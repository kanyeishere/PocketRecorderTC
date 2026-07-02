using Recorder.Capture;
using Recorder.Diagnostics;
using Recorder.Recording;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;

namespace Recorder.Encoding;

internal sealed class NativeRecorderWriter : IOutputSink
{
    private const int MaxVideoQueueSize = 6;
    private const int MaxAudioQueueSize = 100;
    private const int NativeCodecH264 = 1;
    private const int NativeCodecHevc = 2;
    private const int PressureWindowMs = 1_000;

    private readonly int _videoBitrate;
    private readonly string _videoCodec;
    private readonly int _nativeCodec;
    private readonly string _nativeCodecName;
    private NativeRecorderSession? _session;
    private BlockingCollection<VideoFrame>? _videoQueue;
    private BlockingCollection<AudioPacket>? _audioQueue;
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
    private int _droppedFrameCount;
    private int _audioPackets;
    private long _submitPressureUntilTicks;

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
        _droppedFrameCount = 0;
        _audioPackets = 0;
        _submitPressureUntilTicks = 0;
        _firstVideoFrameException = null;
        _firstVideoFrameSubmitted.Reset();

        AmdRecordingDiagnosticLog.Write(
            "NativeRecorder",
            $"starting native writer, video={videoFormat.Width}x{videoFormat.Height}@{_videoFps}, codec={_nativeCodecName}, requested={_videoCodec}, audio={audioFormat != null}, bitrate={_videoBitrate}");

        _session = NativeRecorderBackend.Create(
            _outputPath,
            videoFormat,
            audioFormat,
            _videoBitrate,
            _nativeCodec);
        LogNativeStatusToDiagnostics("NativeRecorder create status");

        _videoQueue = new BlockingCollection<VideoFrame>(MaxVideoQueueSize);
        _videoWriterThread = new Thread(VideoWriterLoop)
        {
            IsBackground = true,
            Name = "NativeRecorder-VideoWriter",
        };
        _videoWriterThread.Start();

        if (audioFormat != null)
        {
            _audioQueue = new BlockingCollection<AudioPacket>(MaxAudioQueueSize);
            _audioWriterThread = new Thread(AudioWriterLoop)
            {
                IsBackground = true,
                Name = "NativeRecorder-AudioWriter",
            };
            _audioWriterThread.Start();
        }

        Plugin.Log!.Info($"[NativeRecorder] Started native D3D11 texture writer: {videoFormat.Width}x{videoFormat.Height}@{_videoFps}fps, codec={_nativeCodecName}, requested={_videoCodec}, audio={audioFormat != null}, bitrate={_videoBitrate}");
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

        bool added = false;
        while (!added)
        {
            try
            {
                added = _videoQueue.TryAdd(frame, 0);
            }
            catch (InvalidOperationException)
            {
                frame.ReturnBuffer();
                return;
            }

            if (added)
                break;

            if (_videoQueue.TryTake(out var droppedFrame))
            {
                droppedFrame.ReturnBuffer();
                int dropped = Interlocked.Increment(ref _droppedFrameCount);
                if (dropped <= 5 || dropped % 60 == 0)
                    Plugin.Log!.Warning($"[NativeRecorder] Video queue full, dropped a captured texture frame. dropped={dropped}");
            }
            else
            {
                break;
            }
        }

        if (added)
            Interlocked.Increment(ref _inputFrameCount);
        else
            frame.ReturnBuffer();
    }

    public void WriteAudioPacket(AudioPacket packet)
    {
        if (_stopped || _audioQueue == null)
            return;

        if (!_audioQueue.TryAdd(packet, 0))
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

        foreach (var frame in _videoQueue!.GetConsumingEnumerable())
        {
            try
            {
                long submitStartTicks = Stopwatch.GetTimestamp();
                bool accepted = _session!.SubmitD3D11Texture(frame);
                if (!accepted)
                {
                    int dropped = Interlocked.Increment(ref _droppedFrameCount);
                    if (dropped <= 5 || dropped % 60 == 0)
                        Plugin.Log!.Info($"[NativeRecorder] Native texture was not ready, dropped one frame. dropped={dropped}");
                    continue;
                }

                frame.MarkD3D11TextureSubmitted();
                long submitTicks = Stopwatch.GetTimestamp() - submitStartTicks;
                MarkSubmitPressureIfSlow(submitTicks);

                int submitted = Interlocked.Increment(ref _submittedFrameCount);
                if (submitted == 1)
                {
                    LogNativeStatus("Native backend status");
                    LogNativeStatusToDiagnostics("First texture submit status");
                    _firstVideoFrameSubmitted.Set();
                }

                if (submitted % 300 == 0)
                    Plugin.Log!.Info($"[NativeRecorder] Submitted {submitted} texture frames (input={_inputFrameCount}, dropped={_droppedFrameCount}), audioPackets={_audioPackets}");
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
                frame.ReturnBuffer();
            }
        }

        if (Volatile.Read(ref _submittedFrameCount) == 0 && _firstVideoFrameException == null)
            _firstVideoFrameSubmitted.Set();

        DrainQueuedVideoFrames();
        Plugin.Log!.Info($"[NativeRecorder] Video writer thread exiting. input={_inputFrameCount}, submitted={_submittedFrameCount}, dropped={_droppedFrameCount}");
        AmdRecordingDiagnosticLog.Write(
            "NativeRecorder",
            $"video writer exiting, input={_inputFrameCount}, submitted={_submittedFrameCount}, dropped={_droppedFrameCount}");
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

        _stopped = true;
        Plugin.Log!.Info($"[NativeRecorder] Stopping... input={_inputFrameCount}, submitted={_submittedFrameCount}, dropped={_droppedFrameCount}, audioPackets={_audioPackets}");
        AmdRecordingDiagnosticLog.Write(
            "NativeRecorder",
            $"stopping, input={_inputFrameCount}, submitted={_submittedFrameCount}, dropped={_droppedFrameCount}, audioPackets={_audioPackets}");

        try { _videoQueue?.CompleteAdding(); } catch { }
        try { _audioQueue?.CompleteAdding(); } catch { }

        if (_videoWriterThread != null && !_videoWriterThread.Join(5_000))
        {
            Plugin.Log!.Warning("[NativeRecorder] Video writer did not finish in 5s.");
            AmdRecordingDiagnosticLog.Write("NativeRecorder", "video writer did not finish in 5s");
        }

        if (_audioWriterThread != null && !_audioWriterThread.Join(5_000))
        {
            Plugin.Log!.Warning("[NativeRecorder] Audio writer did not finish in 5s.");
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

        int drained = 0;
        while (_videoQueue.TryTake(out var pendingFrame))
        {
            pendingFrame.ReturnBuffer();
            drained++;
        }

        return drained;
    }

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
