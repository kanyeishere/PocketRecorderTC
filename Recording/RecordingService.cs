using Dalamud.Plugin.Services;
using Recorder.Capture;
using Recorder.Diagnostics;
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
    private readonly IRecorderEnvironment _environment;
    private readonly object _sync = new();
    private readonly SoftFpsGovernor _softFps;
    private readonly NativeRecorderStartupGate _nativeStartupGate = new();

    private VideoCaptureService? _videoCapture;
    private AudioCaptureService? _audioCapture;
    private IOutputSink? _writer;
    private RecordingRequest? _request;
    private RecordingBackendPlan? _backendPlan;
    private readonly RecordingBackendSelector _backendSelector;
    private readonly RecordingFinalizer _finalizer;
    private long _lastNvencDriverToastTicks;

    private int _sessionId;
    private RecordingLifecycle _lifecycle = RecordingLifecycle.Idle;
    private DateTime _recordStart;
    private int _frameCount;
    private string? _currentFilePath;
    private string _currentBackend = string.Empty;
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
    public string CurrentBackend => _currentBackend;

    public event Action<bool>? RecordingStateChanged;

    public RecordingService(Plugin plugin, IGameInteropProvider gameInterop, IRecorderEnvironment environment)
    {
        _plugin = plugin;
        _gameInterop = gameInterop;
        _environment = environment;
        _backendSelector = new RecordingBackendSelector(environment.Log);
        _finalizer = new RecordingFinalizer(environment.Log);
        _softFps = new SoftFpsGovernor(message => _environment.Log.Info(message));
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
        RecordingRequest request;
        RecordingBackendPlan backendPlan;
        AudioCaptureService? audioCapture = null;
        VideoCaptureService videoCapture;

        if (!_plugin.IsFFmpegBootstrapComplete)
        {
            _environment.Log.Warning($"[Record] FFmpeg is not ready yet: {_plugin.FFmpegBootstrapStatus}");
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

            request = new RecordingRequest(
                sessionId,
                _currentFilePath,
                config.FFmpegPath,
                _environment.Paths.PluginConfigDirectory,
                config.VideoBitrate,
                _videoFps,
                config.AudioCaptureMode,
                config.VideoCodec,
                config.EncoderPreset,
                config.UseHardwareEncoder,
                config.IncludeOverlay,
                config.VideoOutputScaleMode,
                config.EffectiveForceFFmpegFallbackForTesting);
            RecordingDiagnosticLog.StartSession(
                request.SessionId,
                request.TargetFps,
                request.VideoBitrate,
                request.VideoCodec,
                request.EncoderPreset,
                request.UseHardwareEncoder,
                request.AudioCaptureMode,
                request.IncludeOverlay,
                request.VideoOutputScaleMode,
                request.ForceFFmpegFallbackForTesting,
                !request.ForceFFmpegFallbackForTesting && request.UseHardwareEncoder);
            backendPlan = _backendSelector.SelectInitial(request);
            RecordingDiagnosticLog.UpdateBackendSelection(backendPlan.Reason, backendPlan.NativeRecorderProbeReason);

            _request = request;
            _backendPlan = backendPlan;
            _writer = null;
            _audioCapture = null;
            _videoCapture = null;
            _lifecycle = RecordingLifecycle.Preparing;
            _finishedCallback = finishedCallback;
            _frameCount = 0;
            _currentBackend = backendPlan.PreparingText;
            _videoWidth = 0;
            _videoHeight = 0;
            _videoPixelFormat = VideoPixelFormat.Bgra;
            _nativeStartupGate.Reset();
            _softFps.Reset(log: false);
        }

        AmdRecordingDiagnosticLog.StartSession(
            request.SessionId,
            request.TargetFps,
            request.VideoBitrate,
            request.VideoCodec,
            request.EncoderPreset,
            request.UseHardwareEncoder,
            request.AudioCaptureMode,
            request.IncludeOverlay,
            request.VideoOutputScaleMode,
            request.ForceFFmpegFallbackForTesting,
            backendPlan.PrefersD3D11TextureFrames,
            backendPlan.Reason,
            backendPlan.NativeRecorderProbeReason);

        if (request.AudioCaptureMode != AudioCaptureMode.Off)
        {
            _environment.Log.Info($"[Record] Starting audio capture mode={request.AudioCaptureMode}...");
            audioCapture = new AudioCaptureService(request.AudioCaptureMode, Environment.ProcessId, OnAudioPacket);
            lock (_sync)
            {
                if (IsCurrentSessionNoLock(request.SessionId))
                    _audioCapture = audioCapture;
            }

            audioCapture.Start();
        }

        videoCapture = new VideoCaptureService(
            _gameInterop,
            OnVideoFrame,
            ShouldCaptureVideoFrame);
        videoCapture.IncludeOverlay = request.IncludeOverlay;
        videoCapture.PreferD3D11TextureFrames = backendPlan.PrefersD3D11TextureFrames;

        lock (_sync)
        {
            if (!IsCurrentSessionNoLock(request.SessionId))
            {
                audioCapture?.Stop();
                audioCapture?.Dispose();
                videoCapture.Dispose();
                return false;
            }

            _videoCapture = videoCapture;
        }

        int captureFps = GetInitialCaptureFps(request, backendPlan);
        if (!videoCapture.Start(request.TargetFps, captureFps))
        {
            lock (_sync)
            {
                if (IsCurrentSessionNoLock(request.SessionId))
                {
                    _sessionId++;
                    ClearSessionNoLock(RecordingLifecycle.Idle);
                }
            }

            DisposeVideoCapture(videoCapture);
            StopAndDisposeAudioCapture(audioCapture);

            _environment.Log.Warning("[Record] Video capture could not start; recording aborted.");
            AmdRecordingDiagnosticLog.FinishSession("video capture could not start; recording aborted");
            RecordingDiagnosticLog.FinishSession("video capture could not start; recording aborted");
            RecordingStateChanged?.Invoke(false);
            return false;
        }

        _environment.Log.Info($"[Record] Preparation started -> {request.OutputPath}, startSync={startSw.ElapsedMilliseconds}ms");
        _environment.Log.Info($"[Record] Config: fps={request.TargetFps}, captureFps={captureFps}, bitrate={request.VideoBitrate}, codec={request.VideoCodec}, preset={request.EncoderPreset}, audio={request.AudioCaptureMode}, hw={request.UseHardwareEncoder}, overlay={request.IncludeOverlay}, outputScale={request.VideoOutputScaleMode}, backend={backendPlan.Backend.DisplayName} ({backendPlan.Reason})");
        AmdRecordingDiagnosticLog.Write("Record", $"preparation started, startSyncMs={startSw.ElapsedMilliseconds}");
        RecordingStateChanged?.Invoke(true);
        return true;
    }

    private void OnVideoFrame(VideoFrame frame)
    {
        IOutputSink? writer = null;
        RecordingRequest? request = null;
        RecordingBackendPlan? backendPlan = null;
        bool startWriter = false;
        bool waitForNativeStartupFrame = false;
        bool fallbackToFFmpeg = false;
        int fallbackSessionId = 0;
        string? nativeStartupGateMessage = null;
        string? nativeStartupFallbackReason = null;
        int expectedWidth = 0;
        int expectedHeight = 0;
        VideoPixelFormat expectedPixelFormat = VideoPixelFormat.Bgra;

        lock (_sync)
        {
            if (_request == null || _lifecycle == RecordingLifecycle.Finalizing)
            {
                frame.ReturnBuffer();
                return;
            }

            if (_lifecycle == RecordingLifecycle.StartingWriter)
            {
                frame.ReturnBuffer();
                return;
            }

            writer = _writer;
            if (writer == null)
            {
                request = _request;
                backendPlan = _backendPlan;
                NativeRecorderStartupGateResult startupGate = _nativeStartupGate.Evaluate(backendPlan, frame);
                nativeStartupGateMessage = startupGate.Message;
                if (startupGate.Action != NativeRecorderStartupGateAction.Ready)
                {
                    waitForNativeStartupFrame = true;
                    if (startupGate.Action == NativeRecorderStartupGateAction.FallbackToFFmpeg)
                    {
                        fallbackToFFmpeg = true;
                        fallbackSessionId = request.SessionId;
                        nativeStartupFallbackReason = startupGate.Message;
                    }
                }
                else
                {
                    _lifecycle = RecordingLifecycle.StartingWriter;
                    _videoWidth = frame.Width;
                    _videoHeight = frame.Height;
                    _videoPixelFormat = frame.PixelFormat;
                    startWriter = true;
                    expectedWidth = frame.Width;
                    expectedHeight = frame.Height;
                    expectedPixelFormat = frame.PixelFormat;
                }
            }
            else
            {
                request = null;
                backendPlan = null;
                startWriter = false;
                expectedWidth = _videoWidth;
                expectedHeight = _videoHeight;
                expectedPixelFormat = _videoPixelFormat;
            }
        }

        if (!fallbackToFFmpeg && !string.IsNullOrWhiteSpace(nativeStartupGateMessage))
            LogNativeStartupGate(nativeStartupGateMessage);

        if (fallbackToFFmpeg && fallbackSessionId != 0 && !string.IsNullOrWhiteSpace(nativeStartupFallbackReason))
        {
            LogNativeStartupGateFallback(nativeStartupFallbackReason);
            SwitchToFFmpegFallback(fallbackSessionId, nativeStartupFallbackReason);
        }

        if (waitForNativeStartupFrame)
        {
            frame.ReturnBuffer();
            return;
        }

        if (startWriter)
        {
            StartWriterInBackground(request!, backendPlan!, frame);
            return;
        }

        if (frame.Width != expectedWidth || frame.Height != expectedHeight)
        {
            _environment.Log.Warning($"Frame size changed {frame.Width}x{frame.Height} != {expectedWidth}x{expectedHeight}, skipping.");
            frame.ReturnBuffer();
            return;
        }

        if (frame.PixelFormat != expectedPixelFormat)
        {
            _environment.Log.Warning($"Frame pixel format changed {frame.PixelFormat} != {expectedPixelFormat}, skipping.");
            frame.ReturnBuffer();
            return;
        }

        writer!.WriteVideoFrame(frame);
        Interlocked.Increment(ref _frameCount);
    }

    private void LogNativeStartupGate(string message)
    {
        _environment.Log.Info($"[Record] {message}");
        RecordingDiagnosticLog.WriteNativeEvent("Record", message);
        AmdRecordingDiagnosticLog.WriteIfEnabledOrAmdText("Record", message);
    }

    private void LogNativeStartupGateFallback(string message)
    {
        _environment.Log.Warning($"[Record] {message}");
        RecordingDiagnosticLog.WriteNativeFailure("Record", message);
        AmdRecordingDiagnosticLog.WriteIfEnabledOrAmdText("Record", message);
    }

    private void StartWriterInBackground(RecordingRequest request, RecordingBackendPlan backendPlan, VideoFrame firstFrame)
    {
        var thread = new Thread(() => StartWriterWorker(request, backendPlan, firstFrame))
        {
            IsBackground = true,
            Name = "Recorder-StartWriter",
        };
        thread.Start();
    }

    private void StartWriterWorker(RecordingRequest request, RecordingBackendPlan backendPlan, VideoFrame firstFrame)
    {
        Stopwatch startSw = Stopwatch.StartNew();
        RecordingBackendStartResult? startResult = null;
        bool writerPublished = false;

        try
        {
            if (!IsCurrentSession(request.SessionId))
            {
                firstFrame.ReturnBuffer();
                return;
            }

            AudioFormat? audioFormat = WaitForAudioFormat(request);
            if (!IsCurrentSession(request.SessionId))
            {
                firstFrame.ReturnBuffer();
                return;
            }

            try
            {
                startResult = backendPlan.Backend.Start(request, firstFrame, audioFormat, OnWriterFatalError);
            }
            catch (Exception ex) when (!string.Equals(backendPlan.Backend.Id, "ffmpeg", StringComparison.OrdinalIgnoreCase))
            {
                _environment.Log.Warning($"[{backendPlan.Backend.DisplayName}] Backend failed before start; falling back to FFmpeg stdin rawvideo. {ex.Message}");
                AmdRecordingDiagnosticLog.WriteIfEnabledOrAmdText(
                    backendPlan.Backend.DisplayName,
                    $"backend failed before start, fallback=FFmpeg rawvideo, exception={ex}");
                RecordingDiagnosticLog.WriteIfEnabled(
                    backendPlan.Backend.DisplayName,
                    $"backend failed before start, fallback=FFmpeg rawvideo, exception={ex}");
                NotifyUserForActionableNativeFailure(ex);
                SwitchToFFmpegFallback(request.SessionId, ex.Message);
                return;
            }

            lock (_sync)
            {
                if (!IsCurrentSessionNoLock(request.SessionId) ||
                    _lifecycle != RecordingLifecycle.StartingWriter)
                {
                    return;
                }

                _writer = startResult.Sink;
                _videoWidth = startResult.VideoFormat.Width;
                _videoHeight = startResult.VideoFormat.Height;
                _videoPixelFormat = startResult.VideoFormat.PixelFormat;
                _currentBackend = startResult.BackendLabel;
                writerPublished = true;
                _lifecycle = RecordingLifecycle.Recording;
                _recordStart = DateTime.Now;
            }

            if (startResult.CountFirstVideoFrame)
                Interlocked.Increment(ref _frameCount);

            _environment.Log.Info($"[Record] Recording started: {startResult.VideoFormat.Describe()}@{request.TargetFps}fps, audio={audioFormat != null}, backend={startResult.BackendLabel}, asyncStart={startSw.ElapsedMilliseconds}ms");
            AmdRecordingDiagnosticLog.WriteIfEnabledOrAmdText(
                "Record",
                $"recording started, asyncStartMs={startSw.ElapsedMilliseconds}, backend={startResult.BackendLabel}");
        }
        catch (Exception ex)
        {
            if (startResult == null)
                firstFrame.ReturnBuffer();

            _environment.Log.Error($"[Record] Failed to start writer: {ex}");
            AmdRecordingDiagnosticLog.WriteIfEnabledOrAmdText("Record", $"failed to start writer, exception={ex}");
            AbortStart(request.SessionId);
        }
        finally
        {
            if (startResult?.Sink != null && !writerPublished)
            {
                try { startResult.Sink.Stop(TimeSpan.Zero); } catch { }
                try { startResult.Sink.Dispose(); } catch { }
            }
        }
    }

    private void OnWriterFatalError(IOutputSink sender, string message)
    {
        bool shouldStop;
        lock (_sync)
        {
            shouldStop = ReferenceEquals(_writer, sender) &&
                         _lifecycle is RecordingLifecycle.Recording or RecordingLifecycle.StartingWriter;
        }

        if (!shouldStop)
            return;

        _environment.Log.Warning($"[Record] Writer failed; stopping recording automatically. {message}");
        AmdRecordingDiagnosticLog.WriteIfEnabledOrAmdText("Record", $"writer fatal error, message={message}");
        RecordingDiagnosticLog.WriteIfEnabled("Record", $"writer fatal error, message={message}");
        StopRecording();
    }

    private AudioFormat? WaitForAudioFormat(RecordingRequest request)
    {
        if (request.AudioCaptureMode == AudioCaptureMode.Off)
        {
            _environment.Log.Info("[Record] No audio (disabled), video-only recording.");
            return null;
        }

        Stopwatch sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 500)
        {
            AudioCaptureService? audioCapture;
            lock (_sync)
            {
                if (!IsCurrentSessionNoLock(request.SessionId))
                    return null;

                audioCapture = _audioCapture;
            }

            if (audioCapture == null)
                return null;

            if (audioCapture.Initialized)
            {
                _environment.Log.Info($"[Record] Audio initialized ({request.AudioCaptureMode}): {audioCapture.SampleRate}Hz, {audioCapture.Channels}ch, {audioCapture.BitsPerSample}bit");
                var audioFormat = new AudioFormat(
                    audioCapture.SampleRate,
                    audioCapture.Channels,
                    audioCapture.BitsPerSample,
                    audioCapture.BitsPerSample == 32);
                _environment.Log.Info($"[Record] Audio format: {audioFormat.SampleRate}Hz, {audioFormat.Channels}ch, {audioFormat.BitsPerSample}bit, float={audioFormat.IsFloat}");
                return audioFormat;
            }

            if (!string.IsNullOrEmpty(audioCapture.LastError))
                break;

            Thread.Sleep(25);
        }

        AudioCaptureService? audioToStop = null;
        lock (_sync)
        {
            if (IsCurrentSessionNoLock(request.SessionId))
            {
                audioToStop = _audioCapture;
                _audioCapture = null;
            }
        }

        if (audioToStop != null)
        {
            _environment.Log.Warning($"[Record] Audio init failed or timed out (LastError={audioToStop.LastError}), continuing video-only.");
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
            if (_request == null)
                return false;

            if (_lifecycle == RecordingLifecycle.StartingWriter)
                return _backendPlan?.PrefersD3D11TextureFrames == true;

            if (_writer == null)
            {
                _softFps.Reset();
                return true;
            }

            if (!_writer.IsVideoUnderPressure)
            {
                _softFps.Reset();
                return true;
            }

            if (_writer.IsVideoBackedUp)
                return false;

            return _softFps.ShouldCapture(_videoFps);
        }
    }

    private void SwitchToFFmpegFallback(int sessionId, string reason)
    {
        int targetFps = _videoFps;
        lock (_sync)
        {
            if (!IsCurrentSessionNoLock(sessionId))
                return;

            if (_request != null)
                targetFps = _request.TargetFps;

            if (_request != null)
                _backendPlan = _backendSelector.SelectFFmpeg(_request, reason);

            if (_videoCapture != null)
            {
                _videoCapture.PreferD3D11TextureFrames = false;
                _videoCapture.SetCaptureFps(targetFps, "FFmpeg fallback");
            }

            _writer = null;
            _videoWidth = 0;
            _videoHeight = 0;
            _videoPixelFormat = VideoPixelFormat.Bgra;
            _nativeStartupGate.Reset();
            _currentBackend = _backendPlan?.PreparingText ?? "FFmpeg fallback 准备中";
            _lifecycle = RecordingLifecycle.Preparing;
            _softFps.Reset(log: false);
        }

        AmdRecordingDiagnosticLog.WriteIfEnabledOrAmdText("Record", "switched capture to FFmpeg fallback; D3D11 texture preference disabled");
        RecordingDiagnosticLog.WriteIfEnabled("Record", "switched capture to FFmpeg fallback; D3D11 texture preference disabled");
    }

    private static int GetInitialCaptureFps(RecordingRequest request, RecordingBackendPlan backendPlan)
        => backendPlan.PrefersD3D11TextureFrames
            ? Math.Max(1, request.TargetFps * 2)
            : request.TargetFps;

    private void NotifyUserForActionableNativeFailure(Exception ex)
    {
        string failureText = ex.ToString();
        if (!IsNvencDriverVersionFailure(failureText))
            return;

        ShowNvencDriverUpdateToast(throttle: true);
    }

#if DEBUG
    internal void ShowNvencDriverUpdateToastForTesting()
    {
        ShowNvencDriverUpdateToast(throttle: false);
    }
#endif

    private void ShowNvencDriverUpdateToast(bool throttle)
    {
        long nowTicks = Stopwatch.GetTimestamp();
        long previousTicks = Volatile.Read(ref _lastNvencDriverToastTicks);
        if (throttle &&
            previousTicks != 0 &&
            nowTicks - previousTicks < Stopwatch.Frequency * 60)
        {
            return;
        }

        Volatile.Write(ref _lastNvencDriverToastTicks, nowTicks);
        _ = Plugin.Framework.RunOnFrameworkThread(() =>
        {
            try
            {
                Plugin.ChatGui.PrintError("[Pocket Recorder] NVIDIA 驱动版本过旧，无法开启原生录制，已自动降档至 FFmpeg 录制，请更新驱动。");
            }
            catch (Exception notifyEx)
            {
                _environment.Log.Warning($"[Record] Failed to show NVENC driver toast: {notifyEx.Message}");
            }
        });
    }

    private static bool IsNvencDriverVersionFailure(string text)
    {
        return text.Contains("Current Driver Version does not support this NvEncodeAPI version", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("NV_ENC_ERR_INVALID_VERSION", StringComparison.OrdinalIgnoreCase);
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
            _request = null;
            _backendPlan = null;
            _videoCapture = null;
            _audioCapture = null;
            _writer = null;
            _nativeStartupGate.Reset();
            _lifecycle = writer != null || audioCapture != null || videoCapture != null
                ? RecordingLifecycle.Finalizing
                : RecordingLifecycle.Idle;
            _finishedCallback = null;
        }

        _environment.Log.Info($"[Record] Stopping... frames={FrameCount}, duration={finalDuration}");

        try { videoCapture?.RequestStop(); } catch { }
        var job = new RecordingFinalizationJob(
            videoCapture,
            audioCapture,
            writer,
            outputPath,
            finishedCallback,
            finalDuration,
            stopSw,
            MarkFinalized);
        _finalizer.Finalize(job, waitForFinalize);

        RecordingStateChanged?.Invoke(false);
    }

    private void MarkFinalized()
    {
        lock (_sync)
        {
            if (_lifecycle == RecordingLifecycle.Finalizing)
                _lifecycle = RecordingLifecycle.Idle;
        }
    }

    private void AbortStart(int sessionId)
    {
        VideoCaptureService? videoCapture = null;
        AudioCaptureService? audioCapture = null;
        Action<RecordingFinishedEventArgs>? finishedCallback = null;
        string? outputPath = null;

        lock (_sync)
        {
            if (_request?.SessionId != sessionId)
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
        AmdRecordingDiagnosticLog.FinishSession("start aborted");
        RecordingDiagnosticLog.FinishSession("start aborted");

        RecordingStateChanged?.Invoke(false);

        if (outputPath != null)
        {
            try
            {
                finishedCallback?.Invoke(new RecordingFinishedEventArgs(outputPath, TimeSpan.Zero, false));
            }
            catch (Exception ex)
            {
                _environment.Log.Warning($"[Record] Abort callback failed: {ex.Message}");
            }
        }
    }

    private bool HasActiveSessionNoLock()
    {
        return _request != null ||
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
        return _request?.SessionId == sessionId &&
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
        _request = null;
        _backendPlan = null;
        _videoCapture = null;
        _audioCapture = null;
        _writer = null;
        _lifecycle = nextLifecycle;
        _currentBackend = string.Empty;
        _finishedCallback = null;
        _nativeStartupGate.Reset();
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
