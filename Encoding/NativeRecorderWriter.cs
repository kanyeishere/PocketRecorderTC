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
    private const int MaxAudioQueueSize = 100;
    private const int NativeCodecH264 = 1;
    private const int NativeCodecHevc = 2;
    private const int FirstFrameRetryMs = 300;

    private readonly int _videoBitrate;
    private readonly string _videoCodec;
    private readonly int _nativeCodec;
    private readonly string _nativeCodecName;
    private readonly NativeRecorderTimingDiagnostics _timingDiagnostics = new();
    private readonly object _mailboxLock = new();
    private NativeRecorderSession? _session;
    private ManualResetEventSlim? _mailboxReady;
    private D3D11SharedTextureMailbox? _mailbox;
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
    private int _realOutputTickDropCount;
    private int _duplicateOutputTickDropCount;
    private int _audioPackets;
    private long _lastSubmittedSourceFrameId;
    private long _maxSubmittedSourceFrameId;
    private long _sourceFrameRepeatCount;
    private long _sourceFrameRegressionCount;
    private long _maxSourceFrameRegressionDistance;
    private long _videoFrameDurationHns;
    private long _videoFrameDurationTicks;
    private long _finalVideoDurationHns;

    public NativeRecorderWriter(int videoBitrate, string videoCodec)
    {
        _videoBitrate = videoBitrate;
        _videoCodec = videoCodec;
        _nativeCodec = ResolveNativeCodec(videoCodec);
        _nativeCodecName = _nativeCodec == NativeCodecH264 ? "H.264" : "HEVC";
    }

    public bool SupportsAudio => _hasAudio;
    public bool IsVideoBackedUp => false;
    public bool IsVideoUnderPressure => false;
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
        _realOutputTickDropCount = 0;
        _duplicateOutputTickDropCount = 0;
        _audioPackets = 0;
        _lastSubmittedSourceFrameId = 0;
        _maxSubmittedSourceFrameId = 0;
        _sourceFrameRepeatCount = 0;
        _sourceFrameRegressionCount = 0;
        _maxSourceFrameRegressionDistance = 0;
        _videoFrameDurationHns = Math.Max(1, 10_000_000L / _videoFps);
        _videoFrameDurationTicks = Math.Max(1, Stopwatch.Frequency / _videoFps);
        _finalVideoDurationHns = -1;
        _firstVideoFrameException = null;
        _firstVideoFrameSubmitted.Reset();
        _timingDiagnostics.Reset(_videoFps);
        ClearMailbox();

        string startMessage = $"starting native writer, video={videoFormat.Describe()}@{_videoFps}, codec={_nativeCodecName}, requested={_videoCodec}, audio={audioFormat != null}, bitrate={_videoBitrate}";
        RecordingDiagnosticLog.WriteNativeEvent("NativeRecorder", startMessage);
        AmdRecordingDiagnosticLog.Write("NativeRecorder", startMessage);

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

        _mailboxReady = new ManualResetEventSlim(false);
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

        Plugin.Log!.Info($"[NativeRecorder] Started native D3D11 texture writer: {videoFormat.Describe()}@{_videoFps}fps, codec={_nativeCodecName}, requested={_videoCodec}, audio={audioFormat != null}, bitrate={_videoBitrate}");
        string timingMessage = $"video timing: CFR output clock sampling the current shared texture, frameDurationHns={_videoFrameDurationHns}; capture notifications are not queued as historical frames.";
        Plugin.Log!.Info($"[NativeRecorder] {timingMessage}");
        RecordingDiagnosticLog.WriteIfEnabled("NativeRecorder", timingMessage);
        AmdRecordingDiagnosticLog.Write("NativeRecorder", timingMessage);
    }

    public void WriteVideoFrame(VideoFrame frame)
    {
        try
        {
            if (_stopped || _mailboxReady == null)
                return;

            if (!frame.IsD3D11Texture || frame.D3D11Mailbox == null)
            {
                Plugin.Log!.Warning($"[NativeRecorder] Dropped non-mailbox D3D11 frame: {frame.PixelFormat}.");
                return;
            }

            lock (_mailboxLock)
            {
                if (_mailbox == null || !ReferenceEquals(_mailbox, frame.D3D11Mailbox))
                    _mailbox = frame.D3D11Mailbox;

                _mailboxReady.Set();
            }

            Interlocked.Increment(ref _inputFrameCount);
        }
        finally
        {
            frame.ReturnBuffer();
        }
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

        D3D11SharedTextureSnapshot retainedSnapshot = default;
        bool hasRetainedSnapshot = false;
        long nextOutputFrameIndex = 0;
        long nextOutputDueTicks = 0;
        long lastSampledSourceFrameId = 0;

        try
        {
            if (!TryGetFirstSnapshot(out D3D11SharedTextureSnapshot firstSnapshot))
            {
                _firstVideoFrameSubmitted.Set();
            }
            else
            {
                retainedSnapshot = firstSnapshot;
                hasRetainedSnapshot = true;
                long firstSampleAgeTicks = Math.Max(0, Stopwatch.GetTimestamp() - firstSnapshot.PublishTicks);
                if (SubmitOutputTick(firstSnapshot, 0, duplicate: false, recordCaptureTiming: true, firstSampleAgeTicks))
                {
                    lastSampledSourceFrameId = firstSnapshot.SourceFrameId;
                    nextOutputFrameIndex = 1;
                    nextOutputDueTicks = Stopwatch.GetTimestamp() + _videoFrameDurationTicks;

                    while (ShouldContinueOutput(nextOutputFrameIndex))
                    {
                        SleepUntilOutputDue(nextOutputDueTicks);
                        if (!ShouldContinueOutput(nextOutputFrameIndex))
                            break;

                        D3D11SharedTextureSnapshot currentSnapshot = retainedSnapshot;
                        bool hasCurrentSnapshot = TryGetCurrentSnapshot(out currentSnapshot);
                        if (!hasCurrentSnapshot && !hasRetainedSnapshot)
                            break;

                        bool duplicate = !hasCurrentSnapshot ||
                                         currentSnapshot.SourceFrameId == lastSampledSourceFrameId;
                        long timestampHns = nextOutputFrameIndex * _videoFrameDurationHns;
                        long sampleAgeTicks = Math.Max(0, Stopwatch.GetTimestamp() - currentSnapshot.PublishTicks);

                        bool accepted = SubmitOutputTick(
                            currentSnapshot,
                            timestampHns,
                            duplicate,
                            recordCaptureTiming: !duplicate,
                            sampleAgeTicks);

                        if (accepted)
                        {
                            retainedSnapshot = currentSnapshot;
                            hasRetainedSnapshot = true;
                            lastSampledSourceFrameId = currentSnapshot.SourceFrameId;
                        }

                        nextOutputFrameIndex++;
                        nextOutputDueTicks += _videoFrameDurationTicks;
                    }
                }
            }
        }
        catch (Exception ex)
        {
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
        }

        if (Volatile.Read(ref _submittedFrameCount) == 0 && _firstVideoFrameException == null)
            _firstVideoFrameSubmitted.Set();

        string timingSummary = _timingDiagnostics.BuildSummary();
        string dropSummary = BuildDropSummary();
        string sourceFrameSummary = BuildSourceFrameSummary();
        Plugin.Log!.Info($"[NativeRecorder] Video writer thread exiting. input={_inputFrameCount}, submitted={_submittedFrameCount}, duplicates={_duplicateFrameCount}, {dropSummary}, {sourceFrameSummary}");
        Plugin.Log!.Info($"[NativeRecorder] Timing diagnostics: {timingSummary}");
        RecordingDiagnosticLog.WriteIfEnabled(
            "NativeRecorder",
            $"video writer exiting, input={_inputFrameCount}, submitted={_submittedFrameCount}, duplicates={_duplicateFrameCount}, {dropSummary}, {sourceFrameSummary}");
        RecordingDiagnosticLog.WriteIfEnabled(
            "NativeRecorder",
            $"timing diagnostics: {timingSummary}");
        AmdRecordingDiagnosticLog.Write(
            "NativeRecorder",
            $"video writer exiting, input={_inputFrameCount}, submitted={_submittedFrameCount}, duplicates={_duplicateFrameCount}, {dropSummary}, {sourceFrameSummary}");
        AmdRecordingDiagnosticLog.Write(
            "NativeRecorder",
            $"timing diagnostics: {timingSummary}");
    }

    private bool TryGetFirstSnapshot(out D3D11SharedTextureSnapshot snapshot)
    {
        while (!_stopped)
        {
            if (TryGetCurrentSnapshot(out snapshot))
                return true;

            _mailboxReady?.Wait(100);
        }

        snapshot = default;
        return false;
    }

    private bool TryGetCurrentSnapshot(out D3D11SharedTextureSnapshot snapshot)
    {
        D3D11SharedTextureMailbox? mailbox;
        lock (_mailboxLock)
        {
            mailbox = _mailbox;
        }

        if (mailbox != null && mailbox.TryGetLatest(out snapshot))
            return true;

        snapshot = default;
        return false;
    }

    private bool ShouldContinueOutput(long nextOutputFrameIndex)
    {
        if (!_stopped)
            return true;

        long targetFrameCount = GetFinalTargetFrameCount();
        return targetFrameCount > nextOutputFrameIndex;
    }

    private long GetFinalTargetFrameCount()
    {
        long finalDurationHns = Volatile.Read(ref _finalVideoDurationHns);
        if (finalDurationHns <= 0)
            return 0;

        return (finalDurationHns + _videoFrameDurationHns - 1) / _videoFrameDurationHns;
    }

    private void SleepUntilOutputDue(long dueTicks)
    {
        while (!_stopped)
        {
            long remainingTicks = dueTicks - Stopwatch.GetTimestamp();
            if (remainingTicks <= 0)
                return;

            SleepForRemainingTicks(remainingTicks, maxSleepMs: 10);
        }
    }

    private static void SleepForRemainingTicks(long remainingTicks, int maxSleepMs)
    {
        long remainingMs = remainingTicks * 1_000 / Stopwatch.Frequency;
        if (remainingMs > 0)
        {
            Thread.Sleep((int)Math.Min(maxSleepMs, remainingMs));
        }
        else
        {
            Thread.SpinWait(64);
        }
    }

    private bool SubmitOutputTick(
        D3D11SharedTextureSnapshot snapshot,
        long timestampHns,
        bool duplicate,
        bool recordCaptureTiming,
        long sampleAgeTicks)
    {
        long submitStartTicks = Stopwatch.GetTimestamp();
        bool accepted = SubmitOneOutputFrame(snapshot, timestampHns, duplicate);
        long submitTicks = Stopwatch.GetTimestamp() - submitStartTicks;
        _timingDiagnostics.RecordSubmitAttempt(submitTicks, accepted);

        if (!accepted)
        {
            int dropped = Interlocked.Increment(ref _droppedFrameCount);
            int realTickDrops = duplicate
                ? Volatile.Read(ref _realOutputTickDropCount)
                : Interlocked.Increment(ref _realOutputTickDropCount);
            int duplicateTickDrops = duplicate
                ? Interlocked.Increment(ref _duplicateOutputTickDropCount)
                : Volatile.Read(ref _duplicateOutputTickDropCount);
            if (dropped <= 5 || dropped % 60 == 0)
            {
                string status = _session?.GetLastStatus() ?? string.Empty;
                string suffix = string.IsNullOrWhiteSpace(status) ? string.Empty : $" lastStatus={status}";
                string tickKind = duplicate ? "duplicate" : "real";
                Plugin.Log!.Info($"[NativeRecorder] Native texture was not ready, dropped one {tickKind} output tick. dropped={dropped}, realTickDrops={realTickDrops}, duplicateTickDrops={duplicateTickDrops}.{suffix}");
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

            return false;
        }

        if (recordCaptureTiming)
            _timingDiagnostics.RecordSubmittedFrame(snapshot.SourceTimestampHns, sampleAgeTicks);

        RecordSubmittedSourceFrame(snapshot.SourceFrameId, timestampHns, duplicate);

        int submitted = Volatile.Read(ref _submittedFrameCount);
        if (submitted % 300 == 0)
            Plugin.Log!.Info($"[NativeRecorder] Submitted {submitted} texture frames (input={_inputFrameCount}, duplicates={_duplicateFrameCount}, {BuildDropSummary()}), audioPackets={_audioPackets}");

        return true;
    }

    private void RecordSubmittedSourceFrame(long sourceFrameId, long timestampHns, bool duplicate)
    {
        long previousSourceFrameId = _lastSubmittedSourceFrameId;
        long previousMaxSourceFrameId = _maxSubmittedSourceFrameId;

        if (previousSourceFrameId <= 0)
        {
            _lastSubmittedSourceFrameId = sourceFrameId;
            _maxSubmittedSourceFrameId = Math.Max(previousMaxSourceFrameId, sourceFrameId);
            return;
        }

        if (sourceFrameId == previousSourceFrameId)
        {
            Interlocked.Increment(ref _sourceFrameRepeatCount);
        }
        else if (sourceFrameId < previousSourceFrameId)
        {
            long regressions = Interlocked.Increment(ref _sourceFrameRegressionCount);
            long distanceFromPrevious = previousSourceFrameId - sourceFrameId;
            long distanceFromMax = Math.Max(0, previousMaxSourceFrameId - sourceFrameId);
            UpdateMaxSourceFrameRegressionDistance(Math.Max(distanceFromPrevious, distanceFromMax));
            Plugin.Log!.Warning(
                $"[NativeRecorder] Source frame id regressed. outputTimestampHns={timestampHns}, sourceFrameId={sourceFrameId}, previousSourceFrameId={previousSourceFrameId}, maxSourceFrameId={previousMaxSourceFrameId}, regressionDistance={distanceFromPrevious}, distanceFromMax={distanceFromMax}, duplicate={duplicate}, regressions={regressions}");
        }

        _lastSubmittedSourceFrameId = sourceFrameId;
        if (sourceFrameId > previousMaxSourceFrameId)
            _maxSubmittedSourceFrameId = sourceFrameId;
    }

    private void UpdateMaxSourceFrameRegressionDistance(long distance)
    {
        long current;
        do
        {
            current = Volatile.Read(ref _maxSourceFrameRegressionDistance);
            if (distance <= current)
                return;
        }
        while (Interlocked.CompareExchange(ref _maxSourceFrameRegressionDistance, distance, current) != current);
    }

    private bool SubmitOneOutputFrame(D3D11SharedTextureSnapshot snapshot, long timestampHns, bool duplicate)
    {
        bool isFirstSubmittedFrame = Volatile.Read(ref _submittedFrameCount) == 0;
        bool accepted = SubmitD3D11TextureWithStartupRetry(snapshot, timestampHns, isFirstSubmittedFrame);
        if (!accepted)
            return false;

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

    private bool SubmitD3D11TextureWithStartupRetry(D3D11SharedTextureSnapshot snapshot, long timestampHns, bool isFirstSubmittedFrame)
    {
        if (!isFirstSubmittedFrame)
            return _session!.SubmitD3D11Texture(snapshot, timestampHns);

        Stopwatch retrySw = Stopwatch.StartNew();
        int attempts = 0;
        while (!_stopped)
        {
            attempts++;
            if (_session!.SubmitD3D11Texture(snapshot, timestampHns))
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

        long finalDurationHns = finalVideoDuration is { } duration
            ? Math.Max(0, duration.Ticks)
            : -1;
        Volatile.Write(ref _finalVideoDurationHns, finalDurationHns);
        _stopped = true;
        string dropSummary = BuildDropSummary();
        Plugin.Log!.Info($"[NativeRecorder] Stopping... input={_inputFrameCount}, submitted={_submittedFrameCount}, duplicates={_duplicateFrameCount}, {dropSummary}, audioPackets={_audioPackets}");
        RecordingDiagnosticLog.WriteIfEnabled(
            "NativeRecorder",
            $"stopping, input={_inputFrameCount}, submitted={_submittedFrameCount}, duplicates={_duplicateFrameCount}, {dropSummary}, audioPackets={_audioPackets}, finalDuration={finalVideoDuration}");
        AmdRecordingDiagnosticLog.Write(
            "NativeRecorder",
            $"stopping, input={_inputFrameCount}, submitted={_submittedFrameCount}, duplicates={_duplicateFrameCount}, {dropSummary}, audioPackets={_audioPackets}, finalDuration={finalVideoDuration}");

        _mailboxReady?.Set();
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
        ClearMailbox();
        _session?.Dispose();
        _session = null;
        try { _mailboxReady?.Dispose(); } catch { }
        _mailboxReady = null;
        try { _audioQueue?.Dispose(); } catch { }
        try { _firstVideoFrameSubmitted.Dispose(); } catch { }
    }

    private void ClearMailbox()
    {
        lock (_mailboxLock)
        {
            _mailbox = null;
            _mailboxReady?.Reset();
        }
    }

    private string BuildDropSummary()
    {
        int realTickDrops = Volatile.Read(ref _realOutputTickDropCount);
        int duplicateTickDrops = Volatile.Read(ref _duplicateOutputTickDropCount);
        return $"dropped={Volatile.Read(ref _droppedFrameCount)}, " +
               $"realTickDrops={realTickDrops}, " +
               $"duplicateTickDrops={duplicateTickDrops}";
    }

    private string BuildSourceFrameSummary()
    {
        return $"sourceFrameRepeats={Volatile.Read(ref _sourceFrameRepeatCount)}, " +
               $"sourceFrameRegressions={Volatile.Read(ref _sourceFrameRegressionCount)}, " +
               $"maxSourceFrameRegressionDistance={Volatile.Read(ref _maxSourceFrameRegressionDistance)}, " +
               $"lastSourceFrameId={Volatile.Read(ref _lastSubmittedSourceFrameId)}, " +
               $"maxSourceFrameId={Volatile.Read(ref _maxSubmittedSourceFrameId)}";
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
