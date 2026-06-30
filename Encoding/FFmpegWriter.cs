using Recorder.Capture;
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
    private int _duplicatedFrameCount;
    private int _droppedFrameCount;
    private int _audioPackets;
    private TimeSpan? _finalVideoDuration;

    // 异步写入队列
    private BlockingCollection<VideoFrame>? _videoQueue;
    private Thread? _videoWriterThread;
    private BlockingCollection<byte[]>? _audioQueue;
    private Thread? _audioWriterThread;
    private const int MaxQueueSize = 10; // 限制队列深度，避免内存暴涨

    private readonly string _ffmpegPath;
    private readonly int _videoBitrate;
    private readonly string _videoCodec;
    private readonly string _preset;
    private readonly bool _useHardware;

    public bool SupportsAudio => _hasAudio;
    public bool IsVideoBackedUp => _videoQueue != null && _videoQueue.Count >= MaxQueueSize / 2;

    public FFmpegWriter(string ffmpegPath, int videoBitrate, string videoCodec, string preset, bool useHardware)
    {
        _ffmpegPath = ffmpegPath;
        _videoBitrate = videoBitrate;
        _videoCodec = videoCodec;
        _preset = preset;
        _useHardware = useHardware;
    }

    public void SetOutputPath(string path) => _outputPath = path;

    public void Start(VideoFormat video, AudioFormat? audio)
    {
        _videoFps = Math.Max(1, video.Fps);
        _hasAudio = audio != null;
        _stopped = false;
        _frameCount = 0;
        _inputFrameCount = 0;
        _duplicatedFrameCount = 0;
        _droppedFrameCount = 0;
        _audioPackets = 0;
        _finalVideoDuration = null;

        // 确保输出目录
        string? dir = Path.GetDirectoryName(_outputPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        // 构造 FFmpeg 参数
        var args = new System.Collections.Generic.List<string>();
        args.Add("-y"); // 覆盖

        // ── 视频输入：stdin rawvideo ──
        args.Add("-f"); args.Add("rawvideo");
        args.Add("-pix_fmt"); args.Add(GetFFmpegPixelFormat(video.PixelFormat));
        args.Add("-video_size"); args.Add($"{video.Width}x{video.Height}");
        args.Add("-framerate"); args.Add($"{_videoFps}");
        args.Add("-i"); args.Add("-");

        // ── 音频输入：命名管道 ──
        string pipeName = $"RecAud_{Guid.NewGuid():N}"[..31];
        if (audio != null)
        {
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
        if (IsSoftwareH264(_videoCodec))
        {
            args.Add("-pix_fmt"); args.Add("yuv420p");
            args.Add("-tune"); args.Add("zerolatency");
            args.Add("-threads"); args.Add(GetSoftwareEncoderThreadCount().ToString());
            args.Add("-x264-params"); args.Add("bframes=0:sync-lookahead=0");
        }
        args.Add("-rtbufsize"); args.Add("200M");
        args.Add("-fps_mode"); args.Add("cfr");

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
                Plugin.Log!.Info($"[FFmpeg] {e.Data}");
        };
        _process.BeginErrorReadLine();

        Plugin.Log!.Info($"[FFmpeg] Process started (PID={_process.Id}), codec={_videoCodec}, pix_fmt={GetFFmpegPixelFormat(video.PixelFormat)}, {video.Width}x{video.Height}@{_videoFps}fps");

        // 启动异步写入线程
        Plugin.Log!.Info($"[FFmpeg] Video timing: rawvideo CFR stream synthesized from capture timestamps at {_videoFps}fps.");
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

    private static bool IsSoftwareH264(string codec)
    {
        return string.Equals(codec, "libx264", StringComparison.OrdinalIgnoreCase);
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
        return pixelFormat == VideoPixelFormat.Rgba ? "rgba" : "bgra";
    }

    public void WriteVideoFrame(VideoFrame frame)
    {
        if (_stopped || _videoQueue == null)
        {
            frame.ReturnBuffer();
            return;
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

        long? firstTimestampHns = null;
        long nextOutputFrameIndex = 0;
        VideoFrame? lastFrame = null;

        foreach (var frame in _videoQueue!.GetConsumingEnumerable())
        {
            try
            {
                if (_stopped)
                {
                    frame.ReturnBuffer();
                    break;
                }

                if (firstTimestampHns == null)
                {
                    firstTimestampHns = frame.TimestampHns;
                    lastFrame = frame;
                    WriteRawVideoFrame(frame, duplicate: false);
                    nextOutputFrameIndex = 1;
                    continue;
                }

                long relativeHns = Math.Max(0, frame.TimestampHns - firstTimestampHns.Value);
                long targetFrameIndex = relativeHns * _videoFps / 10_000_000L;

                while (!_stopped && lastFrame != null && nextOutputFrameIndex < targetFrameIndex)
                {
                    WriteRawVideoFrame(lastFrame, duplicate: true);
                    nextOutputFrameIndex++;
                }

                if (_stopped)
                {
                    frame.ReturnBuffer();
                    break;
                }

                if (nextOutputFrameIndex <= targetFrameIndex)
                {
                    WriteRawVideoFrame(frame, duplicate: false);
                    nextOutputFrameIndex++;
                }

                if (!ReferenceEquals(lastFrame, frame))
                    lastFrame?.ReturnBuffer();
                lastFrame = frame;
            }
            catch (Exception ex)
            {
                if (!ReferenceEquals(lastFrame, frame))
                    frame.ReturnBuffer();
                if (_stopped)
                    Plugin.Log!.Info($"[FFmpeg] Video writer stopped while writing: {ex.Message}");
                else
                    Plugin.Log!.Warning($"[FFmpeg] Video write failed: {ex.Message}");
                break;
            }
        }

        if (!_stopped && _finalVideoDuration is { } finalDuration && lastFrame != null)
        {
            long desiredFrameCount = Math.Max(1, (finalDuration.Ticks * _videoFps + TimeSpan.TicksPerSecond - 1) / TimeSpan.TicksPerSecond);
            while (nextOutputFrameIndex < desiredFrameCount)
            {
                try
                {
                    WriteRawVideoFrame(lastFrame, duplicate: true);
                    nextOutputFrameIndex++;
                }
                catch (Exception ex)
                {
                    Plugin.Log!.Warning($"[FFmpeg] Final video padding failed: {ex.Message}");
                    break;
                }
            }
        }

        lastFrame?.ReturnBuffer();
        DrainQueuedVideoFrames();

        Plugin.Log!.Info($"[FFmpeg] Video writer thread exiting. input={_inputFrameCount}, output={_frameCount}, duplicated={_duplicatedFrameCount}, dropped={_droppedFrameCount}");
    }

    private void WriteRawVideoFrame(VideoFrame frame, bool duplicate)
    {
        _stdin!.Write(frame.Data, 0, frame.DataLength);
        _frameCount++;
        if (duplicate)
            _duplicatedFrameCount++;

        if (_frameCount % 300 == 0)
            Plugin.Log!.Info($"[FFmpeg] Written {_frameCount} video frames (input={_inputFrameCount}, duplicated={_duplicatedFrameCount}, dropped={_droppedFrameCount}), {_audioPackets} audio packets");
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
        _stopped = true;
        _finalVideoDuration = null;

        Plugin.Log!.Info($"[FFmpeg] Stopping... input={_inputFrameCount}, output={_frameCount}, duplicated={_duplicatedFrameCount}, dropped={_droppedFrameCount}, audioPackets={_audioPackets}");

        // 完成视频队列
        try { _videoQueue?.CompleteAdding(); } catch { }
        // 完成音频队列
        try { _audioQueue?.CompleteAdding(); } catch { }

        int stopDroppedFrames = DrainQueuedVideoFrames();
        if (stopDroppedFrames > 0)
            Plugin.Log!.Info($"[FFmpeg] Dropped {stopDroppedFrames} queued video frame(s) during quick stop.");

        // 停止时优先快速收尾；如果 writer 卡在 stdin.Write，则关闭 stdin 解除阻塞。
        bool videoWriterFinished = _videoWriterThread == null || _videoWriterThread.Join(5_000);
        if (!videoWriterFinished)
        {
            Plugin.Log.Warning("[FFmpeg] Video writer did not finish in 5s; closing input to unblock quick stop.");
            CloseStandardInput();
            if (!_videoWriterThread!.Join(2_000))
                Plugin.Log.Warning("[FFmpeg] Video writer still did not exit after stdin close.");
        }
        if (_audioWriterThread != null && !_audioWriterThread.Join(5_000))
            Plugin.Log.Warning("[FFmpeg] Audio writer did not finish in 5s; closing input with remaining packets unwritten.");

        // 关闭 stdin（发送 EOF）
        CloseStandardInput();

        // 关闭音频管道
        try { _audioPipe?.Flush(); _audioPipe?.Close(); } catch { }

        // 等待 FFmpeg 完成
        if (_process != null && !_process.HasExited)
        {
            Plugin.Log.Info("[FFmpeg] Waiting for FFmpeg to finalize...");
            if (!_process.WaitForExit(30_000))
            {
                Plugin.Log.Warning("[FFmpeg] FFmpeg did not exit in 30s, killing.");
                try { _process.Kill(); } catch { }
            }
        }

        Plugin.Log.Info("[FFmpeg] Process exited.");
    }

    private void CloseStandardInput()
    {
        try { _stdin?.Flush(); } catch { }
        try { _stdin?.Close(); } catch { }
    }

    public void Dispose()
    {
        Stop();
        try { _process?.Dispose(); } catch { }
        try { _audioPipe?.Dispose(); } catch { }
        try { _videoQueue?.Dispose(); } catch { }
        try { _audioQueue?.Dispose(); } catch { }
    }
}
