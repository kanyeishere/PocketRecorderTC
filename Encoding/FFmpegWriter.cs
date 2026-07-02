using Recorder.Capture;
using Recorder.Diagnostics;
using Recorder.Recording;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;

namespace Recorder.Encoding;

/// <summary>
/// 通过 FFmpeg 子进程编码视频和音频。
/// 视频帧通过 stdin 以 rawvideo 格式传入，音频通过 Windows 命名管道传入。
/// 所有写入操作异步执行（队列 + 专用线程），不阻塞渲染线程和音频采集线程。
/// 视频使用 wall-clock VFR 时间戳；编码端追不上时丢弃过期帧，而不是复制旧帧补满 CFR。
/// </summary>
internal sealed class FFmpegWriter : IOutputSink
{
    private Process? _process;
    private Stream? _stdin;
    private NamedPipeServerStream? _audioPipe;
    private Thread? _audioConnectThread;
    private string _outputPath = string.Empty;
    private int _videoFps;
    private bool _hasAudio;
    private volatile bool _stopped;
    private int _frameCount;
    private int _inputFrameCount;
    private int _tailDuplicateFrameCount;
    private int _droppedFrameCount;
    private int _staleFrameDropCount;
    private int _nativeManagedCopyFrameCount;
    private int _audioPackets;
    private TimeSpan? _finalVideoDuration;
    private long _managedNativeCopyUntilTicks;

    // 异步写入队列
    private BlockingCollection<VideoFrame>? _videoQueue;
    private Thread? _videoWriterThread;
    private BlockingCollection<byte[]>? _audioQueue;
    private Thread? _audioWriterThread;
    private const int MaxQueueSize = 10; // 限制队列深度，避免内存暴涨
    private const int NativeCopyPressureWindowMs = 1_000;

    private readonly string _ffmpegPath;
    private readonly int _videoBitrate;
    private readonly string _videoCodec;
    private readonly string _preset;
    private readonly VideoPipelinePerfStats _stdinWritePerfStats = new("FFmpeg stdin.Write", "write");
    private readonly VideoPipelinePerfStats _nativeSnapshotPerfStats = new("FFmpeg native frame snapshot", "copy");
    private readonly ManualResetEventSlim _firstVideoFrameWritten = new(false);

    public bool SupportsAudio => _hasAudio;
    public bool IsVideoBackedUp => _videoQueue != null && _videoQueue.Count >= MaxQueueSize / 2;
    public bool IsVideoUnderPressure => IsVideoBackedUp || IsWritePressureActive();
    public event Action<IOutputSink, string>? FatalError;

    public FFmpegWriter(string ffmpegPath, int videoBitrate, string videoCodec, string preset)
    {
        _ffmpegPath = ffmpegPath;
        _videoBitrate = videoBitrate;
        _videoCodec = videoCodec;
        _preset = preset;
    }

    public void SetOutputPath(string path) => _outputPath = path;

    public void Start(VideoFormat video, AudioFormat? audio)
    {
        _videoFps = Math.Max(1, video.Fps);
        _hasAudio = audio != null;
        _stopped = false;
        _frameCount = 0;
        _inputFrameCount = 0;
        _tailDuplicateFrameCount = 0;
        _droppedFrameCount = 0;
        _staleFrameDropCount = 0;
        _nativeManagedCopyFrameCount = 0;
        _audioPackets = 0;
        _finalVideoDuration = null;
        _managedNativeCopyUntilTicks = 0;
        _stdinWritePerfStats.Reset();
        _nativeSnapshotPerfStats.Reset();
        _firstVideoFrameWritten.Reset();

        // 确保输出目录
        string? dir = Path.GetDirectoryName(_outputPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        // 构造 FFmpeg 参数
        var args = new System.Collections.Generic.List<string>();
        args.Add("-y"); // 覆盖

        // ── 视频输入：stdin rawvideo ──
        args.Add("-use_wallclock_as_timestamps"); args.Add("1");
        args.Add("-f"); args.Add("rawvideo");
        args.Add("-pix_fmt"); args.Add(GetFFmpegPixelFormat(video.PixelFormat));
        args.Add("-video_size"); args.Add($"{video.Width}x{video.Height}");
        args.Add("-framerate"); args.Add($"{_videoFps}");
        args.Add("-i"); args.Add("-");

        if (audio != null)
        {
            string pipeName = $"RecAud_{Guid.NewGuid():N}"[..31];
            _audioPipe = new NamedPipeServerStream(
                pipeName, PipeDirection.Out, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
                131072, 131072);

            _audioConnectThread = new Thread(() =>
            {
                try
                {
                    Plugin.Log!.Info($"[FFmpeg] Waiting for audio pipe: {pipeName}");
                    _audioPipe.WaitForConnection();
                    Plugin.Log.Info("[FFmpeg] Audio pipe connected.");
                }
                catch (Exception ex)
                {
                    Plugin.Log!.Warning($"[FFmpeg] Audio pipe connect failed: {ex.Message}");
                }
            })
            { IsBackground = true, Name = "FFmpeg-AudioPipe" };
            _audioConnectThread.Start();

            string audioFmt = audio.IsFloat ? "f32le" : $"s{audio.BitsPerSample}le";
            args.Add("-f"); args.Add(audioFmt);
            args.Add("-ar"); args.Add($"{audio.SampleRate}");
            args.Add("-ac"); args.Add($"{audio.Channels}");
            args.Add("-i"); args.Add($"\\\\.\\pipe\\{pipeName}");

            Plugin.Log!.Info($"[FFmpeg] Audio: {audioFmt} {audio.SampleRate}Hz {audio.Channels}ch");
        }

        // ── 视频编码器 ──
        args.Add("-c:v"); args.Add(_videoCodec);
        if (!string.IsNullOrEmpty(_preset))
        {
            args.Add("-preset"); args.Add(_preset);
        }
        args.Add("-b:v"); args.Add($"{_videoBitrate}");
        if (IsSoftwareX26x(_videoCodec))
        {
            args.Add("-pix_fmt"); args.Add("yuv420p");
            args.Add("-threads"); args.Add(GetSoftwareEncoderThreadCount().ToString());
            if (_videoCodec.Equals("libx264", StringComparison.OrdinalIgnoreCase))
            {
                args.Add("-tune"); args.Add("zerolatency");
                args.Add("-x264-params"); args.Add("bframes=0:sync-lookahead=0");
            }
        }
        args.Add("-rtbufsize"); args.Add("200M");
        args.Add("-fps_mode"); args.Add("vfr");

        // ── 音频编码器 ──
        if (audio != null)
        {
            args.Add("-c:a"); args.Add("aac");
            args.Add("-b:a"); args.Add("192k");
            args.Add("-ar"); args.Add("48000");
        }

        // MP4 快速启动
        args.Add("-movflags"); args.Add("+faststart");
        args.Add(_outputPath);

        AmdRecordingDiagnosticLog.WriteForAmdCodec(
            _videoCodec,
            "FFmpeg",
            $"starting process, input={video.Width}x{video.Height}@{_videoFps}, pixelFormat={video.PixelFormat}/{GetFFmpegPixelFormat(video.PixelFormat)}, audio={audio != null}, bitrate={_videoBitrate}, preset={_preset}, args={BuildDiagnosticArguments(args)}");

        // 启动 FFmpeg
        var psi = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        Plugin.Log!.Info($"[FFmpeg] Starting: {_ffmpegPath} {string.Join(" ", args)}");

        _process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start FFmpeg process");

        _stdin = _process.StandardInput.BaseStream;

        _process.ErrorDataReceived += (s, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                Plugin.Log!.Info($"[FFmpeg] {e.Data}");
                AmdRecordingDiagnosticLog.WriteForAmdCodec(_videoCodec, "FFmpeg stderr", e.Data);
            }
        };
        _process.BeginErrorReadLine();

        Plugin.Log!.Info($"[FFmpeg] Process started (PID={_process.Id}), codec={_videoCodec}, pix_fmt={GetFFmpegPixelFormat(video.PixelFormat)}, {video.Width}x{video.Height}@{_videoFps}fps");
        AmdRecordingDiagnosticLog.WriteForAmdCodec(
            _videoCodec,
            "FFmpeg",
            $"process started, pid={_process.Id}, codec={_videoCodec}, pix_fmt={GetFFmpegPixelFormat(video.PixelFormat)}, size={video.Width}x{video.Height}, fps={_videoFps}");

        // 启动异步写入线程
        Plugin.Log!.Info("[FFmpeg] Video timing: rawvideo VFR uses wall-clock timestamps; stale queued frames are dropped instead of synthesized.");
        _videoQueue = new BlockingCollection<VideoFrame>(MaxQueueSize);
        _videoWriterThread = new Thread(VideoWriterLoop)
        {
            IsBackground = true,
            Name = "FFmpeg-VideoWriter",
        };
        _videoWriterThread.Start();

        if (audio != null)
        {
            _audioQueue = new BlockingCollection<byte[]>(100);
            _audioWriterThread = new Thread(AudioWriterLoop)
            {
                IsBackground = true,
                Name = "FFmpeg-AudioWriter",
            };
            _audioWriterThread.Start();
        }
    }

    private static bool IsSoftwareX26x(string codec)
    {
        return string.Equals(codec, "libx264", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(codec, "libx265", StringComparison.OrdinalIgnoreCase);
    }

    private static int GetSoftwareEncoderThreadCount()
    {
        int cpuThreads = Environment.ProcessorCount;
        if (cpuThreads <= 8)
            return Math.Max(2, cpuThreads / 2);

        return Math.Min(8, Math.Max(4, cpuThreads / 3));
    }

    private static string GetFFmpegPixelFormat(VideoPixelFormat pixelFormat)
    {
        return pixelFormat switch
        {
            VideoPixelFormat.Rgba => "rgba",
            VideoPixelFormat.Nv12 => "nv12",
            _ => "bgra",
        };
    }

    private static string BuildDiagnosticArguments(System.Collections.Generic.IReadOnlyList<string> args)
    {
        string[] sanitized = new string[args.Count];
        for (int i = 0; i < args.Count; i++)
        {
            if (i == args.Count - 1)
                sanitized[i] = "<output>";
            else if (args[i].StartsWith(@"\\.\pipe\", StringComparison.OrdinalIgnoreCase))
                sanitized[i] = @"\\.\pipe\<audio>";
            else
                sanitized[i] = args[i];
        }

        return string.Join(" ", sanitized);
    }

    public void WriteVideoFrame(VideoFrame frame)
    {
        if (_stopped || _videoQueue == null)
        {
            frame.ReturnBuffer();
            return;
        }

        if (ShouldDetachNativeFrameBeforeEnqueue(frame))
        {
            VideoFrame originalFrame = frame;
            try
            {
                frame = frame.DetachToManagedCopyIfNative();
                if (!ReferenceEquals(originalFrame, frame))
                {
                    int copied = Interlocked.Increment(ref _nativeManagedCopyFrameCount);
                    if (copied <= 5 || copied % 300 == 0)
                        Plugin.Log!.Info($"[FFmpeg] Detached native frame to managed buffer under writer pressure. nativeCopies={copied}");
                }
            }
            catch (Exception ex)
            {
                originalFrame.ReturnBuffer();
                Plugin.Log!.Warning($"[FFmpeg] Failed to detach native frame under writer pressure: {ex.Message}");
                return;
            }
        }

        bool added = false;
        // 非阻塞入队：如果队列满则丢弃最旧帧（避免阻塞渲染线程）
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

            // 队列满，丢弃一帧
            if (_videoQueue.TryTake(out var droppedFrame))
            {
                droppedFrame.ReturnBuffer();
                int dropped = Interlocked.Increment(ref _droppedFrameCount);
                if (dropped <= 5 || dropped % 60 == 0)
                    Plugin.Log!.Warning($"[FFmpeg] Video queue full, dropped a captured frame. dropped={dropped}");
            }
            else
            {
                break;
            }
        }

        if (added)
        {
            Interlocked.Increment(ref _inputFrameCount);
        }
        else
        {
            frame.ReturnBuffer();
        }
    }

    public void WriteAudioPacket(AudioPacket packet)
    {
        if (_stopped || _audioQueue == null) return;

        // 非阻塞入队
        if (!_audioQueue.TryAdd(packet.Data, 0))
        {
            Plugin.Log!.Warning("[FFmpeg] Audio queue full, dropped a packet.");
        }
    }

    /// <summary>视频写入线程：从队列取帧写入 FFmpeg stdin。</summary>
    private void VideoWriterLoop()
    {
        Plugin.Log!.Info("[FFmpeg] Video writer thread started.");

        using var lastFrame = new LastVideoFrameCache(this);
        foreach (var frame in _videoQueue!.GetConsumingEnumerable())
        {
            VideoFrame frameToWrite = frame;
            bool frameStored = false;
            try
            {
                if (ShouldCoalesceQueuedFrames())
                    frameToWrite = DropStaleQueuedFrames(frameToWrite);

                WriteRawVideoFrame(frameToWrite, duplicate: false);
                lastFrame.Store(frameToWrite);
                frameStored = true;
            }
            catch (Exception ex)
            {
                if (!frameStored)
                    frameToWrite.ReturnBuffer();
                if (_stopped)
                {
                    Plugin.Log!.Info($"[FFmpeg] Video writer stopped while writing: {ex.Message}");
                }
                else
                {
                    Plugin.Log!.Warning($"[FFmpeg] Video write failed: {ex.Message}");
                    AmdRecordingDiagnosticLog.WriteForAmdCodec(_videoCodec, "FFmpeg", $"video write failed, exception={ex}");
                    NotifyFatalError($"FFmpeg video write failed: {ex.Message}");
                }
                break;
            }
        }

        if (_finalVideoDuration is { } finalDuration &&
            finalDuration > TimeSpan.Zero &&
            lastFrame.HasFrame)
        {
            try
            {
                lastFrame.WriteDuplicate(this);
            }
            catch (Exception ex)
            {
                Plugin.Log!.Warning($"[FFmpeg] Final tail frame failed: {ex.Message}");
            }
        }

        DrainQueuedVideoFrames();
        _stdinWritePerfStats.FlushIfAny();
        _nativeSnapshotPerfStats.FlushIfAny();

        Plugin.Log!.Info($"[FFmpeg] Video writer thread exiting. input={_inputFrameCount}, output={_frameCount}, tailDuplicates={_tailDuplicateFrameCount}, dropped={_droppedFrameCount}, staleDrops={_staleFrameDropCount}, nativeCopies={_nativeManagedCopyFrameCount}");
    }

    public bool WaitForFirstVideoFrameWritten(int timeoutMs)
        => _firstVideoFrameWritten.Wait(timeoutMs);

    private void WriteRawVideoFrame(VideoFrame frame, bool duplicate)
    {
        long writeStartTicks = Stopwatch.GetTimestamp();
        if (frame.IsNative)
        {
            unsafe
            {
                _stdin!.Write(new ReadOnlySpan<byte>(frame.DataPtr, frame.DataLength));
            }
            long writeTicks = Stopwatch.GetTimestamp() - writeStartTicks;
            MarkWritePressureIfSlow(writeTicks);
            ReadOnlySpan<long> perfTicks = stackalloc long[] { writeTicks };
            _stdinWritePerfStats.Record(frame.Width, frame.Height, frame.DataLength, frame.PixelFormat, perfTicks);
            RecordWrittenVideoFrame(duplicate);
            return;
        }

        WriteRawVideoFrame(frame.Data.AsSpan(0, frame.DataLength), duplicate, frame.Width, frame.Height, frame.PixelFormat);
    }

    private void WriteRawVideoFrame(ReadOnlySpan<byte> data, bool duplicate, int width, int height, VideoPixelFormat pixelFormat)
    {
        long writeStartTicks = Stopwatch.GetTimestamp();
        _stdin!.Write(data);
        long writeTicks = Stopwatch.GetTimestamp() - writeStartTicks;
        MarkWritePressureIfSlow(writeTicks);
        ReadOnlySpan<long> perfTicks = stackalloc long[] { writeTicks };
        _stdinWritePerfStats.Record(width, height, data.Length, pixelFormat, perfTicks);

        RecordWrittenVideoFrame(duplicate);
    }

    private void RecordWrittenVideoFrame(bool duplicate)
    {
        _frameCount++;
        if (_frameCount == 1)
            _firstVideoFrameWritten.Set();

        if (duplicate)
            _tailDuplicateFrameCount++;

        if (_frameCount % 300 == 0)
            Plugin.Log!.Info($"[FFmpeg] Written {_frameCount} video frames (input={_inputFrameCount}, tailDuplicates={_tailDuplicateFrameCount}, dropped={_droppedFrameCount}, staleDrops={_staleFrameDropCount}, nativeCopies={_nativeManagedCopyFrameCount}), {_audioPackets} audio packets");
    }

    private bool ShouldDetachNativeFrameBeforeEnqueue(VideoFrame frame)
    {
        if (!frame.IsNative || _videoQueue == null)
            return false;

        return _videoQueue.Count > 0 || IsWritePressureActive();
    }

    private bool ShouldCoalesceQueuedFrames()
    {
        if (_videoQueue == null)
            return false;

        return _videoQueue.Count >= MaxQueueSize / 2 || IsWritePressureActive();
    }

    private VideoFrame DropStaleQueuedFrames(VideoFrame currentFrame)
    {
        if (_videoQueue == null)
            return currentFrame;

        int staleDropped = 0;
        while (_videoQueue.TryTake(out var newerFrame))
        {
            currentFrame.ReturnBuffer();
            currentFrame = newerFrame;
            staleDropped++;
        }

        if (staleDropped > 0)
        {
            int dropped = Interlocked.Add(ref _droppedFrameCount, staleDropped);
            int stale = Interlocked.Add(ref _staleFrameDropCount, staleDropped);
            if (stale <= 5 || stale % 60 == 0)
                Plugin.Log!.Warning($"[FFmpeg] Dropped stale queued video frames to catch up. staleDrops={stale}, dropped={dropped}");
        }

        return currentFrame;
    }

    private bool IsWritePressureActive()
    {
        long untilTicks = Volatile.Read(ref _managedNativeCopyUntilTicks);
        return untilTicks > Stopwatch.GetTimestamp();
    }

    private void MarkWritePressureIfSlow(long writeTicks)
    {
        int fps = Math.Max(1, _videoFps);
        long frameBudgetTicks = Math.Max(1, Stopwatch.Frequency / fps);
        if (writeTicks <= frameBudgetTicks)
            return;

        long pressureTicks = Stopwatch.GetTimestamp() +
            (Stopwatch.Frequency * NativeCopyPressureWindowMs / 1_000);
        Volatile.Write(ref _managedNativeCopyUntilTicks, pressureTicks);
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

    private sealed class LastVideoFrameCache : IDisposable
    {
        private readonly FFmpegWriter _writer;
        private VideoFrame? _retainedFrame;
        private byte[]? _snapshotBuffer;

        public LastVideoFrameCache(FFmpegWriter writer)
        {
            _writer = writer;
        }

        public bool HasFrame => _retainedFrame != null || _snapshotBuffer != null;
        public int DataLength { get; private set; }
        public int Width { get; private set; }
        public int Height { get; private set; }
        public VideoPixelFormat PixelFormat { get; private set; }

        public void Store(VideoFrame frame)
        {
            if (frame.IsNative)
            {
                StoreNativeSnapshot(frame);
                return;
            }

            ReleaseRetainedFrame();
            ReleaseSnapshotBuffer();
            _retainedFrame = frame;
            DataLength = frame.DataLength;
            Width = frame.Width;
            Height = frame.Height;
            PixelFormat = frame.PixelFormat;
        }

        public void WriteDuplicate(FFmpegWriter writer)
        {
            if (_retainedFrame != null)
            {
                writer.WriteRawVideoFrame(_retainedFrame, duplicate: true);
                return;
            }

            if (_snapshotBuffer != null)
            {
                writer.WriteRawVideoFrame(
                    _snapshotBuffer.AsSpan(0, DataLength),
                    duplicate: true,
                    Width,
                    Height,
                    PixelFormat);
            }
        }

        private unsafe void StoreNativeSnapshot(VideoFrame frame)
        {
            ReleaseRetainedFrame();
            EnsureSnapshotBuffer(frame.DataLength);
            long copyStartTicks = Stopwatch.GetTimestamp();
            new ReadOnlySpan<byte>(frame.DataPtr, frame.DataLength).CopyTo(_snapshotBuffer!.AsSpan(0, frame.DataLength));
            long copyTicks = Stopwatch.GetTimestamp() - copyStartTicks;
            DataLength = frame.DataLength;
            Width = frame.Width;
            Height = frame.Height;
            PixelFormat = frame.PixelFormat;
            int width = frame.Width;
            int height = frame.Height;
            int dataLength = frame.DataLength;
            VideoPixelFormat pixelFormat = frame.PixelFormat;
            frame.ReturnBuffer();
            ReadOnlySpan<long> perfTicks = stackalloc long[] { copyTicks };
            _writer._nativeSnapshotPerfStats.Record(width, height, dataLength, pixelFormat, perfTicks);
        }

        private void EnsureSnapshotBuffer(int minimumLength)
        {
            if (_snapshotBuffer != null && _snapshotBuffer.Length >= minimumLength)
                return;

            ReleaseSnapshotBuffer();
            _snapshotBuffer = VideoFrame.RentBuffer(minimumLength);
        }

        private void ReleaseRetainedFrame()
        {
            if (_retainedFrame == null)
                return;

            _retainedFrame.ReturnBuffer();
            _retainedFrame = null;
        }

        private void ReleaseSnapshotBuffer()
        {
            if (_snapshotBuffer == null)
                return;

            VideoFrame.ReturnBuffer(_snapshotBuffer);
            _snapshotBuffer = null;
        }

        public void Dispose()
        {
            ReleaseRetainedFrame();
            ReleaseSnapshotBuffer();
        }
    }

    /// <summary>音频写入线程：从队列取包写入命名管道。</summary>
    private void AudioWriterLoop()
    {
        Plugin.Log!.Info("[FFmpeg] Audio writer thread started.");

        // 等待管道连接
        int waitMs = 0;
        while (!_stopped && _audioPipe != null && !_audioPipe.IsConnected)
        {
            Thread.Sleep(50);
            waitMs += 50;
            if (waitMs > 5000)
            {
                Plugin.Log!.Warning("[FFmpeg] Audio pipe not connected after 5s, abandoning audio.");
                return;
            }
        }

        foreach (var audioData in _audioQueue!.GetConsumingEnumerable())
        {
            if (_audioPipe == null || !_audioPipe.IsConnected) break;

            try
            {
                _audioPipe.Write(audioData, 0, audioData.Length);
                Interlocked.Increment(ref _audioPackets);
            }
            catch (Exception)
            {
                // 管道断开等静默
                break;
            }
        }

        Plugin.Log!.Info("[FFmpeg] Audio writer thread exiting.");
    }

    public void Stop(TimeSpan? finalVideoDuration = null)
    {
        if (_stopped) return;
        _finalVideoDuration = finalVideoDuration;
        _stopped = true;

        Plugin.Log!.Info($"[FFmpeg] Stopping... input={Volatile.Read(ref _inputFrameCount)}, output={Volatile.Read(ref _frameCount)}, tailDuplicates={Volatile.Read(ref _tailDuplicateFrameCount)}, dropped={Volatile.Read(ref _droppedFrameCount)}, staleDrops={Volatile.Read(ref _staleFrameDropCount)}, nativeCopies={Volatile.Read(ref _nativeManagedCopyFrameCount)}, audioPackets={Volatile.Read(ref _audioPackets)}");
        AmdRecordingDiagnosticLog.WriteForAmdCodec(
            _videoCodec,
            "FFmpeg",
            $"stopping, input={Volatile.Read(ref _inputFrameCount)}, output={Volatile.Read(ref _frameCount)}, tailDuplicates={Volatile.Read(ref _tailDuplicateFrameCount)}, dropped={Volatile.Read(ref _droppedFrameCount)}, staleDrops={Volatile.Read(ref _staleFrameDropCount)}, nativeCopies={Volatile.Read(ref _nativeManagedCopyFrameCount)}, audioPackets={Volatile.Read(ref _audioPackets)}, finalDuration={finalVideoDuration}");

        // 完成视频队列
        try { _videoQueue?.CompleteAdding(); } catch { }
        // 完成音频队列
        try { _audioQueue?.CompleteAdding(); } catch { }

        // 停止时优先快速收尾；如果 writer 卡在 stdin.Write，则关闭 stdin 解除阻塞。
        bool videoWriterFinished = _videoWriterThread == null || _videoWriterThread.Join(5_000);
        if (!videoWriterFinished)
        {
            Plugin.Log.Warning("[FFmpeg] Video writer did not finish in 5s; closing input to unblock quick stop.");
            AmdRecordingDiagnosticLog.WriteForAmdCodec(_videoCodec, "FFmpeg", "video writer did not finish in 5s; closing stdin");
            CloseStandardInput();
            if (!_videoWriterThread!.Join(2_000))
            {
                Plugin.Log.Warning("[FFmpeg] Video writer still did not exit after stdin close.");
                AmdRecordingDiagnosticLog.WriteForAmdCodec(_videoCodec, "FFmpeg", "video writer still did not exit after stdin close");
            }
        }
        if (_audioWriterThread != null && !_audioWriterThread.Join(5_000))
        {
            Plugin.Log.Warning("[FFmpeg] Audio writer did not finish in 5s; closing input with remaining packets unwritten.");
            AmdRecordingDiagnosticLog.WriteForAmdCodec(_videoCodec, "FFmpeg", "audio writer did not finish in 5s");
        }

        // 关闭 stdin（发送 EOF）
        CloseStandardInput();

        // 关闭音频管道
        try { _audioPipe?.Flush(); _audioPipe?.Close(); } catch { }

        // 等待 FFmpeg 完成
        if (_process != null && !_process.HasExited)
        {
            Plugin.Log.Info("[FFmpeg] Waiting for FFmpeg to finalize...");
            AmdRecordingDiagnosticLog.WriteForAmdCodec(_videoCodec, "FFmpeg", "waiting for process finalize");
            if (!_process.WaitForExit(30_000))
            {
                Plugin.Log.Warning("[FFmpeg] FFmpeg did not exit in 30s, killing.");
                AmdRecordingDiagnosticLog.WriteForAmdCodec(_videoCodec, "FFmpeg", "process did not exit in 30s; killing");
                try { _process.Kill(); } catch { }
            }
        }

        Plugin.Log.Info("[FFmpeg] Process exited.");
        string exitCode = _process is { HasExited: true } ? _process.ExitCode.ToString() : "unknown";
        AmdRecordingDiagnosticLog.WriteForAmdCodec(_videoCodec, "FFmpeg", $"process exited, exitCode={exitCode}");
    }

    private void CloseStandardInput()
    {
        try { _stdin?.Flush(); } catch { }
        try { _stdin?.Close(); } catch { }
    }

    private void NotifyFatalError(string message)
    {
        try { FatalError?.Invoke(this, message); }
        catch (Exception ex)
        {
            Plugin.Log!.Warning($"[FFmpeg] Fatal error callback failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        Stop();
        try { _process?.Dispose(); } catch { }
        try { _audioPipe?.Dispose(); } catch { }
        try { _videoQueue?.Dispose(); } catch { }
        try { _audioQueue?.Dispose(); } catch { }
        try { _firstVideoFrameWritten.Dispose(); } catch { }
    }
}
