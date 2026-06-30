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
    private readonly IFramework _framework;
    private readonly object _sync = new();

    private VideoCaptureService? _videoCapture;
    private AudioCaptureService? _audioCapture;
    private FFmpegWriter? _writer;
    private RecordingStartOptions? _startOptions;

    private int _sessionId;
    private bool _isRecording;
    private bool _isStartingWriter;
    private bool _isFinalizing;
    private bool _stopRequested;
    private DateTime _recordStart;
    private int _frameCount;
    private string? _currentFilePath;
    private Action<RecordingFinishedEventArgs>? _finishedCallback;

    // 配置缓存（录制开始时快照）
    private int _videoWidth;
    private int _videoHeight;
    private int _videoFps;

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
            {
                if (_isFinalizing)
                    return RecordingPhase.Finalizing;

                if (_isRecording)
                    return RecordingPhase.Recording;

                if (_startOptions != null || _videoCapture != null || _audioCapture != null || _isStartingWriter)
                    return RecordingPhase.Preparing;

                return RecordingPhase.Idle;
            }
        }
    }

    public TimeSpan Elapsed
    {
        get
        {
            lock (_sync)
                return _isRecording ? DateTime.Now - _recordStart : TimeSpan.Zero;
        }
    }

    public int FrameCount => Volatile.Read(ref _frameCount);
    public string? CurrentFilePath => _currentFilePath;

    public event Action<bool>? RecordingStateChanged;

    public RecordingService(Plugin plugin, IGameInteropProvider gameInterop, IFramework framework)
    {
        _plugin = plugin;
        _gameInterop = gameInterop;
        _framework = framework;
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
            _isRecording = false;
            _isStartingWriter = false;
            _isFinalizing = false;
            _stopRequested = false;
            _finishedCallback = finishedCallback;
            _frameCount = 0;
            _videoWidth = 0;
            _videoHeight = 0;
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
            Plugin.PluginInterface.UiBuilder,
            _gameInterop,
            _framework,
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

        videoCapture.Start(options.TargetFps);

        Plugin.Log.Info($"[Record] Preparation started -> {options.OutputPath}, startSync={startSw.ElapsedMilliseconds}ms");
        Plugin.Log.Info($"[Record] Config: fps={options.TargetFps}, bitrate={options.VideoBitrate}, codec={options.VideoCodec}, preset={options.EncoderPreset}, audio={options.CaptureAudio}, hw={options.UseHardwareEncoder}");
        RecordingStateChanged?.Invoke(true);
        return true;
    }

    private void OnVideoFrame(VideoFrame frame)
    {
        FFmpegWriter? writer;
        RecordingStartOptions? options;
        bool startWriter;
        int expectedWidth;
        int expectedHeight;

        lock (_sync)
        {
            if (_stopRequested || _startOptions == null)
            {
                frame.ReturnBuffer();
                return;
            }

            writer = _writer;
            if (writer == null)
            {
                if (_isStartingWriter)
                {
                    frame.ReturnBuffer();
                    return;
                }

                _isStartingWriter = true;
                _videoWidth = frame.Width;
                _videoHeight = frame.Height;
                options = _startOptions;
                startWriter = true;
                expectedWidth = frame.Width;
                expectedHeight = frame.Height;
            }
            else
            {
                options = null;
                startWriter = false;
                expectedWidth = _videoWidth;
                expectedHeight = _videoHeight;
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
        FFmpegWriter? writer = null;
        FFmpegWriter? startedWriter = null;
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
                encoder.Preset,
                encoder.IsHardware);
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
                _isRecording = true;
                _isStartingWriter = false;
                _recordStart = DateTime.Now;
                _videoWidth = firstFrame.Width;
                _videoHeight = firstFrame.Height;
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
            try { audioToStop.Stop(); } catch { }
            try { audioToStop.Dispose(); } catch { }
        }

        return null;
    }

    private void OnAudioPacket(AudioPacket packet)
    {
        FFmpegWriter? writer;
        lock (_sync)
        {
            if (_stopRequested)
                return;

            writer = _writer;
        }

        writer?.WriteAudioPacket(packet);
    }

    private bool ShouldCaptureVideoFrame()
    {
        lock (_sync)
        {
            if (_stopRequested || _startOptions == null || _isStartingWriter)
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
        FFmpegWriter? writer;
        string? outputPath;
        Action<RecordingFinishedEventArgs>? finishedCallback;
        TimeSpan finalDuration;
        Stopwatch stopSw = Stopwatch.StartNew();

        lock (_sync)
        {
            if (!HasActiveSessionNoLock())
                return;

            finalDuration = _isRecording ? DateTime.Now - _recordStart : TimeSpan.Zero;
            outputPath = _currentFilePath;
            finishedCallback = _finishedCallback;
            videoCapture = _videoCapture;
            audioCapture = _audioCapture;
            writer = _writer;

            _stopRequested = true;
            _sessionId++;
            _startOptions = null;
            _videoCapture = null;
            _audioCapture = null;
            _writer = null;
            _isRecording = false;
            _isStartingWriter = false;
            _isFinalizing = writer != null || audioCapture != null || videoCapture != null;
            _finishedCallback = null;
        }

        Plugin.Log.Info($"[Record] Stopping... frames={FrameCount}, duration={finalDuration}");

        try { videoCapture?.Stop(); } catch { }
        Plugin.Log.Info($"[Record] Capture stopped synchronously in {stopSw.ElapsedMilliseconds}ms; finalizing writer in background.");

        void FinalizeRecording()
        {
            Stopwatch finalizeSw = Stopwatch.StartNew();
            try { videoCapture?.Dispose(); } catch { }
            try { audioCapture?.Stop(); } catch { }
            try { audioCapture?.Dispose(); } catch { }

            try { writer?.Stop(finalDuration); } catch (Exception ex) { Plugin.Log.Warning($"[Record] Writer stop failed: {ex.Message}"); }
            try { writer?.Dispose(); } catch { }

            lock (_sync)
            {
                _isFinalizing = false;
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
            _startOptions = null;
            _videoCapture = null;
            _audioCapture = null;
            _writer = null;
            _isRecording = false;
            _isStartingWriter = false;
            _isFinalizing = false;
            _stopRequested = true;
            _finishedCallback = null;
        }

        try { videoCapture?.Stop(); } catch { }
        try { videoCapture?.Dispose(); } catch { }
        try { audioCapture?.Stop(); } catch { }
        try { audioCapture?.Dispose(); } catch { }

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
               _isRecording ||
               _isStartingWriter ||
               _isFinalizing;
    }

    private bool IsCurrentSession(int sessionId)
    {
        lock (_sync)
            return IsCurrentSessionNoLock(sessionId);
    }

    private bool IsCurrentSessionNoLock(int sessionId)
    {
        return !_stopRequested && _startOptions?.SessionId == sessionId;
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

internal enum RecordingPhase
{
    Idle,
    Preparing,
    Recording,
    Finalizing,
}
