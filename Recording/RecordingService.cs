using Dalamud.Plugin.Services;
using Recorder.Capture;
using Recorder.Encoding;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Recorder.Recording;

/// <summary>
/// 录制协调器：管理画面捕获、音频捕获和输出编码的生命周期。
/// </summary>
internal sealed class RecordingService : IDisposable
{
    private readonly Plugin _plugin;
    private readonly IGameInteropProvider _gameInterop;
    private readonly object _sync = new();

    private VideoCaptureService? _videoCapture;
    private AudioCaptureService? _audioCapture;
    private IOutputSink? _writer;
    private RecordingStartOptions? _startOptions;

    private int _sessionId;
    private RecordingLifecycle _lifecycle = RecordingLifecycle.Idle;
    private DateTime _recordStart;
    private int _frameCount;
    private string? _currentFilePath;
    private Action<RecordingFinishedEventArgs>? _finishedCallback;

    // 配置缓存（录制开始时快照）
    private int _videoWidth;
    private int _videoHeight;
    private int _videoFps;
    private VideoPixelFormat _videoPixelFormat;

    public bool IsRecording
    {
        get
        {
            lock (_sync)
                return HasActiveSessionNoLock();
        }
    }

    public RecordingPhase Phase
    {
        get
        {
            lock (_sync)
                return ToPublicPhase(_lifecycle);
        }
    }

    public TimeSpan Elapsed
    {
        get
        {
            lock (_sync)
                return _lifecycle == RecordingLifecycle.Recording ? DateTime.Now - _recordStart : TimeSpan.Zero;
        }
    }

    public int FrameCount => Volatile.Read(ref _frameCount);
    public string? CurrentFilePath => _currentFilePath;

    public event Action<bool>? RecordingStateChanged;

    public RecordingService(Plugin plugin, IGameInteropProvider gameInterop)
    {
        _plugin = plugin;
        _gameInterop = gameInterop;
    }

    public void ToggleRecording()
    {
        var phase = Phase;
        if (phase is RecordingPhase.Recording or RecordingPhase.Preparing)
            StopRecording();
        else if (phase == RecordingPhase.Idle)
            StartRecording();
    }

    public bool StartRecording()
    {
        return StartRecording(null, null);
    }

    public bool StartRecording(string? outputPath, Action<RecordingFinishedEventArgs>? finishedCallback = null)
    {
        Stopwatch startSw = Stopwatch.StartNew();
        RecordingStartOptions options;
        AudioCaptureService? audioCapture = null;
        VideoCaptureService videoCapture;

        if (!_plugin.IsFFmpegBootstrapComplete)
        {
            Plugin.Log.Warning($"[Record] FFmpeg is not ready yet: {_plugin.FFmpegBootstrapStatus}");
            return false;
        }

        lock (_sync)
        {
            if (HasActiveSessionNoLock())
                return false;

            var config = _plugin.Config;
            int sessionId = ++_sessionId;
            _videoFps = Math.Max(1, config.TargetFps);

            string dir = config.GetEffectiveOutputDirectory(Plugin.PluginInterface);
            _currentFilePath = string.IsNullOrWhiteSpace(outputPath)
                ? Path.Combine(dir, $"FFXIV_{DateTime.Now:yyyyMMdd_HHmmss}.mp4")
                : outputPath;

            options = new RecordingStartOptions(
                sessionId,
                _currentFilePath,
                config.FFmpegPath,
                Plugin.PluginInterface.GetPluginConfigDirectory(),
                config.VideoBitrate,
                _videoFps,
                config.CaptureAudio,
                config.VideoCodec,
                config.EncoderPreset,
                config.UseHardwareEncoder);

            _startOptions = options;
            _writer = null;
            _audioCapture = null;
            _videoCapture = null;
            _lifecycle = RecordingLifecycle.Preparing;
            _finishedCallback = finishedCallback;
            _frameCount = 0;
            _videoWidth = 0;
            _videoHeight = 0;
            _videoPixelFormat = VideoPixelFormat.Bgra;
        }

        if (options.CaptureAudio)
        {
            Plugin.Log.Info("[Record] Starting audio capture...");
            audioCapture = new AudioCaptureService(OnAudioPacket);
            lock (_sync)
            {
                if (IsCurrentSessionNoLock(options.SessionId))
                    _audioCapture = audioCapture;
            }

            audioCapture.Start();
        }

        videoCapture = new VideoCaptureService(
            _gameInterop,
            OnVideoFrame,
            ShouldCaptureVideoFrame);

        lock (_sync)
        {
            if (!IsCurrentSessionNoLock(options.SessionId))
            {
                audioCapture?.Stop();
                audioCapture?.Dispose();
                videoCapture.Dispose();
                return false;
            }

            _videoCapture = videoCapture;
        }

        if (!videoCapture.Start(options.TargetFps))
        {
            lock (_sync)
            {
                if (IsCurrentSessionNoLock(options.SessionId))
                {
                    _sessionId++;
                    ClearSessionNoLock(RecordingLifecycle.Idle);
                }
            }

            DisposeVideoCapture(videoCapture);
            StopAndDisposeAudioCapture(audioCapture);

            Plugin.Log.Warning("[Record] Video capture could not start; recording aborted.");
            RecordingStateChanged?.Invoke(false);
            return false;
        }

        Plugin.Log.Info($"[Record] Preparation started -> {options.OutputPath}, startSync={startSw.ElapsedMilliseconds}ms");
        Plugin.Log.Info($"[Record] Config: fps={options.TargetFps}, bitrate={options.VideoBitrate}, codec={options.VideoCodec}, preset={options.EncoderPreset}, audio={options.CaptureAudio}, hw={options.UseHardwareEncoder}");
        RecordingStateChanged?.Invoke(true);
        return true;
    }

    private void OnVideoFrame(VideoFrame frame)
    {
        IOutputSink? writer;
        RecordingStartOptions? options;
        bool startWriter;
        int expectedWidth;
        int expectedHeight;
        VideoPixelFormat expectedPixelFormat;

        lock (_sync)
        {
            if (_startOptions == null || _lifecycle == RecordingLifecycle.Finalizing)
            {
                frame.ReturnBuffer();
                return;
            }

            writer = _writer;
            if (writer == null)
            {
                if (_lifecycle == RecordingLifecycle.StartingWriter)
                {
                    frame.ReturnBuffer();
                    return;
                }

                _lifecycle = RecordingLifecycle.StartingWriter;
                _videoWidth = frame.Width;
                _videoHeight = frame.Height;
                _videoPixelFormat = frame.PixelFormat;
                options = _startOptions;
                startWriter = true;
                expectedWidth = frame.Width;
                expectedHeight = frame.Height;
                expectedPixelFormat = frame.PixelFormat;
            }
            else
            {
                options = null;
                startWriter = false;
                expectedWidth = _videoWidth;
                expectedHeight = _videoHeight;
                expectedPixelFormat = _videoPixelFormat;
            }
        }

        if (startWriter)
        {
            StartWriterInBackground(options!, frame);
            return;
        }

        if (frame.Width != expectedWidth || frame.Height != expectedHeight)
        {
            Plugin.Log!.Warning($"Frame size changed {frame.Width}x{frame.Height} != {expectedWidth}x{expectedHeight}, skipping.");
            frame.ReturnBuffer();
            return;
        }

        if (frame.PixelFormat != expectedPixelFormat)
        {
            Plugin.Log!.Warning($"Frame pixel format changed {frame.PixelFormat} != {expectedPixelFormat}, skipping.");
            frame.ReturnBuffer();
            return;
        }

        writer!.WriteVideoFrame(frame);
        Interlocked.Increment(ref _frameCount);
    }

    private void StartWriterInBackground(RecordingStartOptions options, VideoFrame firstFrame)
    {
        var thread = new Thread(() => StartWriterWorker(options, firstFrame))
        {
            IsBackground = true,
            Name = "Recorder-StartWriter",
        };
        thread.Start();
    }

    private void StartWriterWorker(RecordingStartOptions options, VideoFrame firstFrame)
    {
        Stopwatch startSw = Stopwatch.StartNew();
        IOutputSink? writer = null;
        IOutputSink? startedWriter = null;
        bool frameHandedToWriter = false;

        try
        {
            AudioFormat? audioFormat = WaitForAudioFormat(options);
            if (!IsCurrentSession(options.SessionId))
            {
                firstFrame.ReturnBuffer();
                return;
            }

            var encoderConfig = new Configuration
            {
                VideoBitrate = options.VideoBitrate,
                VideoCodec = options.VideoCodec,
                EncoderPreset = options.EncoderPreset,
                UseHardwareEncoder = options.UseHardwareEncoder,
            };

            string ffmpegPath = FFmpegBootstrapper.ResolveOrInstall(options.FFmpegPath, options.PluginConfigDirectory);
            EncoderSelection encoder = FFmpegEncoderSelector.Select(ffmpegPath, encoderConfig);
            if (!IsCurrentSession(options.SessionId))
            {
                firstFrame.ReturnBuffer();
                return;
            }

            writer = new FFmpegWriter(
                ffmpegPath,
                options.VideoBitrate,
                encoder.Codec,
                encoder.Preset);
            writer.SetOutputPath(options.OutputPath);

            writer.Start(
                new VideoFormat(firstFrame.Width, firstFrame.Height, options.TargetFps, firstFrame.PixelFormat),
                audioFormat);

            lock (_sync)
            {
                if (!IsCurrentSessionNoLock(options.SessionId))
                {
                    firstFrame.ReturnBuffer();
                    return;
                }

                _writer = writer;
                startedWriter = writer;
                writer = null;
                _lifecycle = RecordingLifecycle.Recording;
                _recordStart = DateTime.Now;
                _videoWidth = firstFrame.Width;
                _videoHeight = firstFrame.Height;
                _videoPixelFormat = firstFrame.PixelFormat;
            }

            startedWriter!.WriteVideoFrame(firstFrame);
            frameHandedToWriter = true;
            Interlocked.Increment(ref _frameCount);

            Plugin.Log!.Info($"[Record] Recording started: {firstFrame.Width}x{firstFrame.Height}@{options.TargetFps}fps, audio={audioFormat != null}, codec={encoder.Codec}, preset={encoder.Preset}, hw={encoder.IsHardware}, encoderReason={encoder.Reason}, asyncStart={startSw.ElapsedMilliseconds}ms");
        }
        catch (Exception ex)
        {
            if (!frameHandedToWriter)
                firstFrame.ReturnBuffer();

            Plugin.Log!.Error($"[Record] Failed to start writer: {ex}");
            AbortStart(options.SessionId);
        }
        finally
        {
            if (writer != null)
            {
                try { writer.Stop(TimeSpan.Zero); } catch { }
                try { writer.Dispose(); } catch { }
            }
        }
    }

    private AudioFormat? WaitForAudioFormat(RecordingStartOptions options)
    {
        if (!options.CaptureAudio)
        {
            Plugin.Log.Info("[Record] No audio (disabled), video-only recording.");
            return null;
        }

        Stopwatch sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 500)
        {
            AudioCaptureService? audioCapture;
            lock (_sync)
            {
                if (!IsCurrentSessionNoLock(options.SessionId))
                    return null;

                audioCapture = _audioCapture;
            }

            if (audioCapture == null)
                return null;

            if (audioCapture.Initialized)
            {
                Plugin.Log.Info($"[Record] Audio initialized: {audioCapture.SampleRate}Hz, {audioCapture.Channels}ch, {audioCapture.BitsPerSample}bit");
                var audioFormat = new AudioFormat(
                    audioCapture.SampleRate,
                    audioCapture.Channels,
                    audioCapture.BitsPerSample,
                    audioCapture.BitsPerSample == 32);
                Plugin.Log.Info($"[Record] Audio format: {audioFormat.SampleRate}Hz, {audioFormat.Channels}ch, {audioFormat.BitsPerSample}bit, float={audioFormat.IsFloat}");
                return audioFormat;
            }

            if (!string.IsNullOrEmpty(audioCapture.LastError))
                break;

            Thread.Sleep(25);
        }

        AudioCaptureService? audioToStop = null;
        lock (_sync)
        {
            if (IsCurrentSessionNoLock(options.SessionId))
            {
                audioToStop = _audioCapture;
                _audioCapture = null;
            }
        }

        if (audioToStop != null)
        {
            Plugin.Log.Warning($"[Record] Audio init failed or timed out (LastError={audioToStop.LastError}), continuing video-only.");
            StopAndDisposeAudioCapture(audioToStop);
        }

        return null;
    }

    private void OnAudioPacket(AudioPacket packet)
    {
        IOutputSink? writer;
        lock (_sync)
        {
            if (_lifecycle is RecordingLifecycle.Idle or RecordingLifecycle.Finalizing)
                return;

            writer = _writer;
        }

        writer?.WriteAudioPacket(packet);
    }

    private bool ShouldCaptureVideoFrame()
    {
        lock (_sync)
        {
            if (_startOptions == null || _lifecycle == RecordingLifecycle.StartingWriter)
                return false;

            return _writer == null || !_writer.IsVideoBackedUp;
        }
    }

    public void StopRecording()
    {
        StopRecording(waitForFinalize: false);
    }

    private void StopRecording(bool waitForFinalize)
    {
        VideoCaptureService? videoCapture;
        AudioCaptureService? audioCapture;
        IOutputSink? writer;
        string? outputPath;
        Action<RecordingFinishedEventArgs>? finishedCallback;
        TimeSpan finalDuration;
        Stopwatch stopSw = Stopwatch.StartNew();

        lock (_sync)
        {
            if (!HasActiveSessionNoLock())
                return;

            finalDuration = _lifecycle == RecordingLifecycle.Recording ? DateTime.Now - _recordStart : TimeSpan.Zero;
            outputPath = _currentFilePath;
            finishedCallback = _finishedCallback;
            videoCapture = _videoCapture;
            audioCapture = _audioCapture;
            writer = _writer;

            _sessionId++;
            _startOptions = null;
            _videoCapture = null;
            _audioCapture = null;
            _writer = null;
            _lifecycle = writer != null || audioCapture != null || videoCapture != null
                ? RecordingLifecycle.Finalizing
                : RecordingLifecycle.Idle;
            _finishedCallback = null;
        }

        Plugin.Log.Info($"[Record] Stopping... frames={FrameCount}, duration={finalDuration}");

        try { videoCapture?.Stop(); } catch { }
        Plugin.Log.Info($"[Record] Capture stopped synchronously in {stopSw.ElapsedMilliseconds}ms; finalizing writer in background.");

        void FinalizeRecording()
        {
            Stopwatch finalizeSw = Stopwatch.StartNew();
            DisposeVideoCapture(videoCapture);
            StopAndDisposeAudioCapture(audioCapture);

            try { writer?.Stop(finalDuration); } catch (Exception ex) { Plugin.Log.Warning($"[Record] Writer stop failed: {ex.Message}"); }
            try { writer?.Dispose(); } catch { }

            lock (_sync)
            {
                if (_lifecycle == RecordingLifecycle.Finalizing)
                    _lifecycle = RecordingLifecycle.Idle;
            }

            Plugin.Log.Info($"[Record] Saved: {outputPath}, finalize={finalizeSw.ElapsedMilliseconds}ms");
            if (outputPath != null)
            {
                try
                {
                    finishedCallback?.Invoke(new RecordingFinishedEventArgs(outputPath, finalDuration, writer != null));
                }
                catch (Exception ex)
                {
                    Plugin.Log.Warning($"[Record] Finished callback failed: {ex.Message}");
                }
            }
        }

        if (waitForFinalize)
        {
            FinalizeRecording();
        }
        else
        {
            var thread = new Thread(FinalizeRecording)
            {
                IsBackground = true,
                Name = "Recorder-Finalize",
            };
            thread.Start();
        }

        RecordingStateChanged?.Invoke(false);
    }

    private void AbortStart(int sessionId)
    {
        VideoCaptureService? videoCapture = null;
        AudioCaptureService? audioCapture = null;
        Action<RecordingFinishedEventArgs>? finishedCallback = null;
        string? outputPath = null;

        lock (_sync)
        {
            if (_startOptions?.SessionId != sessionId)
                return;

            videoCapture = _videoCapture;
            audioCapture = _audioCapture;
            finishedCallback = _finishedCallback;
            outputPath = _currentFilePath;

            _sessionId++;
            ClearSessionNoLock(RecordingLifecycle.Idle);
        }

        try { videoCapture?.Stop(); } catch { }
        DisposeVideoCapture(videoCapture);
        StopAndDisposeAudioCapture(audioCapture);

        RecordingStateChanged?.Invoke(false);

        if (outputPath != null)
        {
            try
            {
                finishedCallback?.Invoke(new RecordingFinishedEventArgs(outputPath, TimeSpan.Zero, false));
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning($"[Record] Abort callback failed: {ex.Message}");
            }
        }
    }

    private bool HasActiveSessionNoLock()
    {
        return _startOptions != null ||
               _videoCapture != null ||
               _audioCapture != null ||
               _writer != null ||
               _lifecycle != RecordingLifecycle.Idle;
    }

    private bool IsCurrentSession(int sessionId)
    {
        lock (_sync)
            return IsCurrentSessionNoLock(sessionId);
    }

    private bool IsCurrentSessionNoLock(int sessionId)
    {
        return _startOptions?.SessionId == sessionId &&
               _lifecycle is RecordingLifecycle.Preparing or RecordingLifecycle.StartingWriter or RecordingLifecycle.Recording;
    }

    private static RecordingPhase ToPublicPhase(RecordingLifecycle lifecycle)
    {
        return lifecycle switch
        {
            RecordingLifecycle.Preparing or RecordingLifecycle.StartingWriter => RecordingPhase.Preparing,
            RecordingLifecycle.Recording => RecordingPhase.Recording,
            RecordingLifecycle.Finalizing => RecordingPhase.Finalizing,
            _ => RecordingPhase.Idle,
        };
    }

    private void ClearSessionNoLock(RecordingLifecycle nextLifecycle)
    {
        _startOptions = null;
        _videoCapture = null;
        _audioCapture = null;
        _writer = null;
        _lifecycle = nextLifecycle;
        _finishedCallback = null;
    }

    private static void DisposeVideoCapture(VideoCaptureService? videoCapture)
    {
        try { videoCapture?.Dispose(); } catch { }
    }

    private static void StopAndDisposeAudioCapture(AudioCaptureService? audioCapture)
    {
        try { audioCapture?.Stop(); } catch { }
        try { audioCapture?.Dispose(); } catch { }
    }

    public void Dispose()
    {
        StopRecording(waitForFinalize: true);
    }

    private sealed record RecordingStartOptions(
        int SessionId,
        string OutputPath,
        string FFmpegPath,
        string PluginConfigDirectory,
        int VideoBitrate,
        int TargetFps,
        bool CaptureAudio,
        string VideoCodec,
        string EncoderPreset,
        bool UseHardwareEncoder);
}

internal enum RecordingLifecycle
{
    Idle,
    Preparing,
    StartingWriter,
    Recording,
    Finalizing,
}

internal enum RecordingPhase
{
    Idle,
    Preparing,
    Recording,
    Finalizing,
}
