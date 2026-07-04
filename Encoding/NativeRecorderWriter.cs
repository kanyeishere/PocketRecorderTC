using Recorder.Capture;
using Recorder.Diagnostics;
using Recorder.Recording;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Recorder.Encoding;

internal sealed class NativeRecorderWriter : IOutputSink
{
    private const int MaxVideoQueueSize = 18;
    private const int MaxAudioQueueSize = 100;
    private const int NativeCodecH264 = 1;
    private const int NativeCodecHevc = 2;
    private const int PressureWindowMs = 1_000;
    private const int FirstFrameRetryMs = 300;

    private readonly int _videoBitrate;
    private readonly string _videoCodec;
    private readonly int _nativeCodec;
    private readonly string _nativeCodecName;
    private readonly NativeRecorderTimingDiagnostics _timingDiagnostics = new();
    private readonly NativeRecorderCfrFrameScheduler _cfrScheduler = new();
    private NativeRecorderSession? _session;
    private BoundedMediaQueue<NativeQueuedVideoFrame>? _videoQueue;
    private BoundedMediaQueue<AudioPacket>? _audioQueue;
    private Thread? _videoWriterThread;
    private Thread? _audioWriterThread;
    private readonly ManualResetEventSlim _firstVideoFrameSubmitted = new(false);
    private Exception? _firstVideoFrameException;
    private string _outputPath = string.Empty;
    private volatile bool _stopped;
    private bool _hasAudio;
    private int _videoFps;
    private int _inputFrameCount;
    private int _submittedFrameCount;
    private int _duplicateFrameCount;
    private int _droppedFrameCount;
    private int _audioPackets;
    private long _submitPressureUntilTicks;
    private long _videoFrameDurationHns;
    private TimeSpan? _finalVideoDuration;

    public NativeRecorderWriter(int videoBitrate, string videoCodec)
    {
        _videoBitrate = videoBitrate;
        _videoCodec = videoCodec;
        _nativeCodec = ResolveNativeCodec(videoCodec);
        _nativeCodecName = _nativeCodec == NativeCodecH264 ? "H.264" : "HEVC";
    }

    public bool SupportsAudio => _hasAudio;
    public bool IsVideoBackedUp => _videoQueue != null && _videoQueue.Count >= MaxVideoQueueSize / 2;
    public bool IsVideoUnderPressure => IsVideoBackedUp || IsSubmitPressureActive();
    public event Action<IOutputSink, string>? FatalError;

    public void SetOutputPath(string path) => _outputPath = path;

    public void Start(VideoFormat videoFormat, AudioFormat? audioFormat)
    {
        if (videoFormat.PixelFormat != VideoPixelFormat.D3D11Texture)
            throw new InvalidOperationException($"NativeRecorder requires D3D11 texture frames, got {videoFormat.PixelFormat}.");

        _videoFps = Math.Max(1, videoFormat.Fps);
        _hasAudio = audioFormat != null;
        _stopped = false;
        _inputFrameCount = 0;
        _submittedFrameCount = 0;
        _duplicateFrameCount = 0;
        _droppedFrameCount = 0;
        _audioPackets = 0;
        _submitPressureUntilTicks = 0;
        _videoFrameDurationHns = Math.Max(1, 10_000_000L / _videoFps);
        _finalVideoDuration = null;
        _firstVideoFrameException = null;
        _firstVideoFrameSubmitted.Reset();
        _cfrScheduler.Reset(_videoFps);
        _timingDiagnostics.Reset(_videoFps);

        string startMessage = $"starting native writer, video={videoFormat.Width}x{videoFormat.Height}@{_videoFps}, codec={_nativeCodecName}, requested={_videoCodec}, audio={audioFormat != null}, bitrate={_videoBitrate}";
        RecordingDiagnosticLog.WriteNativeEvent("NativeRecorder", startMessage);
        AmdRecordingDiagnosticLog.Write("NativeRecorder", startMessage);

        // 确保输出目录存在（原生 avio_open 不会自动创建目录）
        string? outputDir = Path.GetDirectoryName(_outputPath);
        if (!string.IsNullOrEmpty(outputDir))
            Directory.CreateDirectory(outputDir);

        _session = NativeRecorderBackend.Create(
            _outputPath,
            videoFormat,
            audioFormat,
            _videoBitrate,
            _nativeCodec);
        LogNativeStatusToDiagnostics("NativeRecorder create status");

        _videoQueue = new BoundedMediaQueue<NativeQueuedVideoFrame>(MaxVideoQueueSize);
        _videoWriterThread = new Thread(VideoWriterLoop)
        {
            IsBackground = true,
            Name = "NativeRecorder-VideoWriter",
        };
        _videoWriterThread.Start();

        if (audioFormat != null)
        {
            _audioQueue = new BoundedMediaQueue<AudioPacket>(MaxAudioQueueSize);
            _audioWriterThread = new Thread(AudioWriterLoop)
            {
                IsBackground = true,
                Name = "NativeRecorder-AudioWriter",
            };
            _audioWriterThread.Start();
        }

        Plugin.Log!.Info($"[NativeRecorder] Started native D3D11 texture writer: {videoFormat.Width}x{videoFormat.Height}@{_videoFps}fps, codec={_nativeCodecName}, requested={_videoCodec}, audio={audioFormat != null}, bitrate={_videoBitrate}");
        string timingMessage = $"video timing: CFR output timestamps with duplicate padding, frameDurationHns={_videoFrameDurationHns}; capture timestamps retained for diagnostics.";
        Plugin.Log!.Info($"[NativeRecorder] {timingMessage}");
        RecordingDiagnosticLog.WriteIfEnabled("NativeRecorder", timingMessage);
        AmdRecordingDiagnosticLog.Write("NativeRecorder", timingMessage);
    }

    public void WriteVideoFrame(VideoFrame frame)
    {
        if (_stopped || _videoQueue == null)
        {
            frame.ReturnBuffer();
            return;
        }

        if (!frame.IsD3D11Texture)
        {
            frame.ReturnBuffer();
            Plugin.Log!.Warning($"[NativeRecorder] Dropped non-D3D11 frame: {frame.PixelFormat}.");
            return;
        }

        NativeQueuedVideoFrame queuedFrame = new(frame, Stopwatch.GetTimestamp());
        if (_videoQueue.TryEnqueueDropOldest(queuedFrame, droppedFrame => droppedFrame.Frame.ReturnBuffer(), out int droppedCount))
        {
            if (droppedCount > 0)
            {
                int dropped = Interlocked.Add(ref _droppedFrameCount, droppedCount);
                if (dropped <= 5 || dropped % 60 == 0)
                    Plugin.Log!.Warning($"[NativeRecorder] Video queue full, dropped a captured texture frame. dropped={dropped}");
            }

            Interlocked.Increment(ref _inputFrameCount);
            return;
        }

        frame.ReturnBuffer();
    }

    public void WriteAudioPacket(AudioPacket packet)
    {
        if (_stopped || _audioQueue == null)
            return;

        if (!_audioQueue.TryEnqueueDropIncoming(packet))
            Plugin.Log!.Warning("[NativeRecorder] Audio queue full, dropped a packet.");
    }

    public void WaitForFirstVideoFrameSubmitted(int timeoutMs)
    {
        if (!_firstVideoFrameSubmitted.Wait(timeoutMs))
            throw new TimeoutException($"NativeRecorder did not accept the first video frame within {timeoutMs}ms.");

        if (_firstVideoFrameException != null)
            throw new InvalidOperationException("NativeRecorder failed to submit the first video frame.", _firstVideoFrameException);
    }

    private void VideoWriterLoop()
    {
        Plugin.Log!.Info("[NativeRecorder] Video writer thread started.");

        VideoFrame? retainedFrame = null;
        bool fatalSubmitFailure = false;
        foreach (var queuedFrame in _videoQueue!.GetConsumingEnumerable())
        {
            VideoFrame frame = queuedFrame.Frame;
            bool retainCurrentFrame = false;
            try
            {
                long dequeueTicks = Stopwatch.GetTimestamp();
                long queueWaitTicks = Math.Max(0, dequeueTicks - queuedFrame.EnqueueTicks);
                NativeRecorderCfrFramePlan framePlan = _cfrScheduler.PlanFrame(frame.TimestampHns);
                long submitStartTicks = Stopwatch.GetTimestamp();
                bool accepted = SubmitCfrFramePlan(retainedFrame, frame, framePlan);
                long submitTicks = Stopwatch.GetTimestamp() - submitStartTicks;
                _timingDiagnostics.RecordSubmitAttempt(submitTicks, accepted);
                if (!accepted)
                {
                    int dropped = Interlocked.Increment(ref _droppedFrameCount);
                    if (dropped <= 5 || dropped % 60 == 0)
                    {
                        string status = _session?.GetLastStatus() ?? string.Empty;
                        string suffix = string.IsNullOrWhiteSpace(status) ? string.Empty : $" lastStatus={status}";
                        Plugin.Log!.Info($"[NativeRecorder] Native texture was not ready, dropped one frame. dropped={dropped}.{suffix}");
                    }

                    if (Volatile.Read(ref _submittedFrameCount) == 0)
                    {
                        string status = _session?.GetLastStatus() ?? string.Empty;
                        _firstVideoFrameException = new TimeoutException(
                            string.IsNullOrWhiteSpace(status)
                                ? "NativeRecorder did not accept the startup texture."
                                : $"NativeRecorder did not accept the startup texture. {status}");
                        _firstVideoFrameSubmitted.Set();
                    }

                    continue;
                }

                _timingDiagnostics.RecordSubmittedFrame(frame.TimestampHns, queueWaitTicks);
                _cfrScheduler.Commit(framePlan);
                retainedFrame?.ReturnBuffer();
                retainedFrame = frame;
                retainCurrentFrame = true;
                int submitted = Volatile.Read(ref _submittedFrameCount);
                if (submitted > 1)
                    MarkSubmitPressureIfSlow(submitTicks);

                if (submitted % 300 == 0)
                    Plugin.Log!.Info($"[NativeRecorder] Submitted {submitted} texture frames (input={_inputFrameCount}, duplicates={_duplicateFrameCount}, dropped={_droppedFrameCount}), audioPackets={_audioPackets}");
            }
            catch (Exception ex)
            {
                fatalSubmitFailure = true;
                if (Volatile.Read(ref _submittedFrameCount) == 0)
                {
                    _firstVideoFrameException = ex;
                    _firstVideoFrameSubmitted.Set();
                }

                if (_stopped)
                {
                    Plugin.Log!.Info($"[NativeRecorder] Video writer stopped while submitting: {ex.Message}");
                }
                else
                {
                    Plugin.Log!.Warning($"[NativeRecorder] Video submit failed: {ex.Message}");
                    RecordingDiagnosticLog.WriteNativeFailure(
                        "NativeRecorder",
                        $"video submit failed, exception={ex}, lastStatus={_session?.GetLastStatus()}");
                    AmdRecordingDiagnosticLog.WriteIfEnabledOrAmdText(
                        "NativeRecorder",
                        $"video submit failed, exception={ex}, lastStatus={_session?.GetLastStatus()}");
                    if (Volatile.Read(ref _submittedFrameCount) > 0)
                        NotifyFatalError($"NativeRecorder video submit failed: {ex.Message}");
                }

                break;
            }
            finally
            {
                if (!retainCurrentFrame)
                    frame.ReturnBuffer();
            }
        }

        if (!fatalSubmitFailure)
            SubmitFinalTailDuplicates(retainedFrame);

        retainedFrame?.ReturnBuffer();

        if (Volatile.Read(ref _submittedFrameCount) == 0 && _firstVideoFrameException == null)
            _firstVideoFrameSubmitted.Set();

        DrainQueuedVideoFrames();
        string timingSummary = _timingDiagnostics.BuildSummary();
        Plugin.Log!.Info($"[NativeRecorder] Video writer thread exiting. input={_inputFrameCount}, submitted={_submittedFrameCount}, duplicates={_duplicateFrameCount}, dropped={_droppedFrameCount}");
        Plugin.Log!.Info($"[NativeRecorder] Timing diagnostics: {timingSummary}");
        RecordingDiagnosticLog.WriteIfEnabled(
            "NativeRecorder",
            $"video writer exiting, input={_inputFrameCount}, submitted={_submittedFrameCount}, duplicates={_duplicateFrameCount}, dropped={_droppedFrameCount}");
        RecordingDiagnosticLog.WriteIfEnabled(
            "NativeRecorder",
            $"timing diagnostics: {timingSummary}");
        AmdRecordingDiagnosticLog.Write(
            "NativeRecorder",
            $"video writer exiting, input={_inputFrameCount}, submitted={_submittedFrameCount}, duplicates={_duplicateFrameCount}, dropped={_droppedFrameCount}");
        AmdRecordingDiagnosticLog.Write(
            "NativeRecorder",
            $"timing diagnostics: {timingSummary}");
    }

    private bool SubmitCfrFramePlan(VideoFrame? previousFrame, VideoFrame currentFrame, NativeRecorderCfrFramePlan framePlan)
    {
        if (framePlan.DuplicateCount > 0 && previousFrame != null)
        {
            for (long i = 0; i < framePlan.DuplicateCount; i++)
            {
                long timestampHns = framePlan.FirstDuplicateTimestampHns + (i * _videoFrameDurationHns);
                if (!SubmitOneOutputFrame(previousFrame, timestampHns, duplicate: true))
                    return false;
            }
        }

        return SubmitOneOutputFrame(currentFrame, framePlan.CurrentTimestampHns, duplicate: false);
    }

    private void SubmitFinalTailDuplicates(VideoFrame? retainedFrame)
    {
        if (retainedFrame == null)
            return;

        NativeRecorderCfrFramePlan tailPlan = _cfrScheduler.PlanTail(_finalVideoDuration);
        if (tailPlan.DuplicateCount <= 0)
            return;

        try
        {
            long submittedDuplicates = 0;
            for (long i = 0; i < tailPlan.DuplicateCount; i++)
            {
                long timestampHns = tailPlan.FirstDuplicateTimestampHns + (i * _videoFrameDurationHns);
                if (!SubmitOneOutputFrame(retainedFrame, timestampHns, duplicate: true))
                    break;
                submittedDuplicates++;
            }

            if (submittedDuplicates > 0)
            {
                long lastSubmittedIndex = _cfrScheduler.NextOutputFrameIndex + submittedDuplicates - 1;
                _cfrScheduler.Commit(new NativeRecorderCfrFramePlan(
                    tailPlan.FirstDuplicateTimestampHns,
                    submittedDuplicates,
                    lastSubmittedIndex * _videoFrameDurationHns,
                    lastSubmittedIndex));
            }
        }
        catch (Exception ex)
        {
            Plugin.Log!.Warning($"[NativeRecorder] Final tail duplicate submit failed: {ex.Message}");
            RecordingDiagnosticLog.WriteIfEnabled("NativeRecorder", $"final tail duplicate submit failed: {ex}");
            AmdRecordingDiagnosticLog.Write("NativeRecorder", $"final tail duplicate submit failed: {ex}");
        }
    }

    private bool SubmitOneOutputFrame(VideoFrame frame, long timestampHns, bool duplicate)
    {
        bool isFirstSubmittedFrame = Volatile.Read(ref _submittedFrameCount) == 0;
        bool accepted = SubmitD3D11TextureWithStartupRetry(frame, timestampHns, isFirstSubmittedFrame);
        if (!accepted)
            return false;

        frame.MarkD3D11TextureSubmitted();
        int submitted = Interlocked.Increment(ref _submittedFrameCount);
        if (duplicate)
            Interlocked.Increment(ref _duplicateFrameCount);

        if (submitted == 1)
        {
            LogNativeStatus("Native backend status");
            LogNativeStatusToDiagnostics("First texture submit status");
            _firstVideoFrameSubmitted.Set();
        }

        return true;
    }

    private bool SubmitD3D11TextureWithStartupRetry(VideoFrame frame, long timestampHns, bool isFirstSubmittedFrame)
    {
        if (!isFirstSubmittedFrame)
            return _session!.SubmitD3D11Texture(frame, timestampHns);

        Stopwatch retrySw = Stopwatch.StartNew();
        int attempts = 0;
        while (!_stopped)
        {
            attempts++;
            if (_session!.SubmitD3D11Texture(frame, timestampHns))
            {
                if (attempts > 1)
                    Plugin.Log!.Info($"[NativeRecorder] First texture accepted after retry. attempts={attempts}, retryMs={retrySw.ElapsedMilliseconds}");
                return true;
            }

            if (retrySw.ElapsedMilliseconds >= FirstFrameRetryMs)
            {
                string status = _session.GetLastStatus();
                string suffix = string.IsNullOrWhiteSpace(status) ? string.Empty : $" lastStatus={status}";
                Plugin.Log!.Warning($"[NativeRecorder] First texture was not ready after retry. attempts={attempts}, retryMs={retrySw.ElapsedMilliseconds}.{suffix}");
                RecordingDiagnosticLog.WriteNativeFailure(
                    "NativeRecorder",
                    $"first texture was not ready after retry, attempts={attempts}, retryMs={retrySw.ElapsedMilliseconds}, lastStatus={status}");
                return false;
            }

            Thread.Sleep(2);
        }

        return false;
    }

    private void AudioWriterLoop()
    {
        Plugin.Log!.Info("[NativeRecorder] Audio writer thread started.");

        foreach (var packet in _audioQueue!.GetConsumingEnumerable())
        {
            try
            {
                _session!.SubmitAudio(packet);
                Interlocked.Increment(ref _audioPackets);
            }
            catch (Exception ex)
            {
                if (_stopped)
                    Plugin.Log!.Info($"[NativeRecorder] Audio writer stopped while submitting: {ex.Message}");
                else
                {
                    Plugin.Log!.Warning($"[NativeRecorder] Audio submit failed: {ex.Message}");
                    RecordingDiagnosticLog.WriteNativeFailure(
                        "NativeRecorder",
                        $"audio submit failed, exception={ex}, lastStatus={_session?.GetLastStatus()}");
                    AmdRecordingDiagnosticLog.WriteIfEnabledOrAmdText(
                        "NativeRecorder",
                        $"audio submit failed, exception={ex}, lastStatus={_session?.GetLastStatus()}");
                }
                break;
            }
        }

        Plugin.Log!.Info("[NativeRecorder] Audio writer thread exiting.");
    }

    public void Stop(TimeSpan? finalVideoDuration = null)
    {
        if (_stopped)
            return;

        _finalVideoDuration = finalVideoDuration;
        _stopped = true;
        Plugin.Log!.Info($"[NativeRecorder] Stopping... input={_inputFrameCount}, submitted={_submittedFrameCount}, duplicates={_duplicateFrameCount}, dropped={_droppedFrameCount}, audioPackets={_audioPackets}");
        RecordingDiagnosticLog.WriteIfEnabled(
            "NativeRecorder",
            $"stopping, input={_inputFrameCount}, submitted={_submittedFrameCount}, duplicates={_duplicateFrameCount}, dropped={_droppedFrameCount}, audioPackets={_audioPackets}, finalDuration={finalVideoDuration}");
        AmdRecordingDiagnosticLog.Write(
            "NativeRecorder",
            $"stopping, input={_inputFrameCount}, submitted={_submittedFrameCount}, duplicates={_duplicateFrameCount}, dropped={_droppedFrameCount}, audioPackets={_audioPackets}, finalDuration={finalVideoDuration}");

        _videoQueue?.CompleteAdding();
        _audioQueue?.CompleteAdding();

        if (_videoWriterThread != null && !_videoWriterThread.Join(5_000))
        {
            Plugin.Log!.Warning("[NativeRecorder] Video writer did not finish in 5s.");
            RecordingDiagnosticLog.WriteNativeFailure("NativeRecorder", "video writer did not finish in 5s");
            AmdRecordingDiagnosticLog.Write("NativeRecorder", "video writer did not finish in 5s");
        }

        if (_audioWriterThread != null && !_audioWriterThread.Join(5_000))
        {
            Plugin.Log!.Warning("[NativeRecorder] Audio writer did not finish in 5s.");
            RecordingDiagnosticLog.WriteNativeFailure("NativeRecorder", "audio writer did not finish in 5s");
            AmdRecordingDiagnosticLog.Write("NativeRecorder", "audio writer did not finish in 5s");
        }

        _session?.Stop();
        LogNativeStatus("Native writer finalized");
        LogNativeStatusToDiagnostics("Native writer finalized");
    }

    public void Dispose()
    {
        try { Stop(); } catch { }
        _session?.Dispose();
        _session = null;
        try { _videoQueue?.Dispose(); } catch { }
        try { _audioQueue?.Dispose(); } catch { }
        try { _firstVideoFrameSubmitted.Dispose(); } catch { }
    }

    private int DrainQueuedVideoFrames()
    {
        if (_videoQueue == null)
            return 0;

        return _videoQueue.Drain(pendingFrame => pendingFrame.Frame.ReturnBuffer());
    }

    private readonly record struct NativeQueuedVideoFrame(VideoFrame Frame, long EnqueueTicks);

    private bool IsSubmitPressureActive()
    {
        long untilTicks = Volatile.Read(ref _submitPressureUntilTicks);
        return untilTicks > Stopwatch.GetTimestamp();
    }

    private void MarkSubmitPressureIfSlow(long submitTicks)
    {
        int fps = Math.Max(1, _videoFps);
        long frameBudgetTicks = Math.Max(1, Stopwatch.Frequency / fps);
        if (submitTicks <= frameBudgetTicks)
            return;

        long pressureTicks = Stopwatch.GetTimestamp() +
            (Stopwatch.Frequency * PressureWindowMs / 1_000);
        Volatile.Write(ref _submitPressureUntilTicks, pressureTicks);
    }

    private void LogNativeStatus(string prefix)
    {
        string status = _session?.GetLastStatus() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(status))
            Plugin.Log!.Info($"[NativeRecorder] {prefix}.");
        else
            Plugin.Log!.Info($"[NativeRecorder] {prefix}: {status}");
    }

    private void LogNativeStatusToDiagnostics(string prefix)
    {
        string status = _session?.GetLastStatus() ?? string.Empty;
        RecordingDiagnosticLog.WriteNativeEvent(
            "NativeRecorder",
            string.IsNullOrWhiteSpace(status) ? prefix : $"{prefix}: {status}");
        AmdRecordingDiagnosticLog.WriteIfEnabledOrAmdText(
            "NativeRecorder",
            string.IsNullOrWhiteSpace(status) ? prefix : $"{prefix}: {status}");
    }

    private void NotifyFatalError(string message)
    {
        try { FatalError?.Invoke(this, message); }
        catch (Exception ex)
        {
            Plugin.Log!.Warning($"[NativeRecorder] Fatal error callback failed: {ex.Message}");
        }
    }

    private static int ResolveNativeCodec(string codec)
    {
        if (string.IsNullOrWhiteSpace(codec) ||
            codec.Equals("auto", StringComparison.OrdinalIgnoreCase) ||
            codec.Equals("hevc", StringComparison.OrdinalIgnoreCase) ||
            codec.Equals("h265", StringComparison.OrdinalIgnoreCase) ||
            codec.Equals("hevc_amf", StringComparison.OrdinalIgnoreCase) ||
            codec.Equals("hevc_nvenc", StringComparison.OrdinalIgnoreCase))
        {
            return NativeCodecHevc;
        }

        if (codec.Equals("h264", StringComparison.OrdinalIgnoreCase) ||
            codec.Equals("h264_amf", StringComparison.OrdinalIgnoreCase) ||
            codec.Equals("h264_nvenc", StringComparison.OrdinalIgnoreCase))
        {
            return NativeCodecH264;
        }

        throw new InvalidOperationException($"NativeRecorder does not support codec '{codec}'.");
    }
}
