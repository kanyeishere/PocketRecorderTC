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
    private bool _stopped;
    private int _frameCount;
    private int _audioPackets;

    // 异步写入队列
    private BlockingCollection<byte[]>? _videoQueue;
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
        _videoFps = video.Fps;
        _hasAudio = audio != null;

        // 确保输出目录
        string? dir = Path.GetDirectoryName(_outputPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        // 构造 FFmpeg 参数
        var args = new System.Collections.Generic.List<string>();
        args.Add("-y"); // 覆盖

        // ── 视频输入：stdin rawvideo ──
        // FFmpeg 的 bgra = B8G8R8A8，内存顺序 B,G,R,A
        // Dalamud backbuffer 是 R8G8B8A8，VideoCaptureService 已做 RGBA→BGRA 交换
        args.Add("-f"); args.Add("rawvideo");
        args.Add("-pix_fmt"); args.Add("bgra");
        args.Add("-s"); args.Add($"{video.Width}x{video.Height}");
        args.Add("-r"); args.Add($"{video.Fps}");
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

        Plugin.Log!.Info($"[FFmpeg] Process started (PID={_process.Id}), codec={_videoCodec}, {video.Width}x{video.Height}@{video.Fps}fps");

        // 启动异步写入线程
        _videoQueue = new BlockingCollection<byte[]>(MaxQueueSize);
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

    public void WriteVideoFrame(VideoFrame frame)
    {
        if (_stopped || _videoQueue == null) return;

        // 非阻塞入队：如果队列满则丢弃最旧帧（避免阻塞渲染线程）
        while (!_videoQueue.TryAdd(frame.Data, 0))
        {
            // 队列满，丢弃一帧
            if (_videoQueue.TryTake(out _))
            {
                Plugin.Log!.Warning("[FFmpeg] Video queue full, dropped a frame.");
            }
            else
            {
                break;
            }
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

        foreach (var frameData in _videoQueue!.GetConsumingEnumerable())
        {
            if (_stopped) break;

            try
            {
                _stdin!.Write(frameData, 0, frameData.Length);
                _frameCount++;

                if (_frameCount % 300 == 0)
                    Plugin.Log!.Info($"[FFmpeg] Written {_frameCount} frames, {_audioPackets} audio packets");
            }
            catch (Exception ex)
            {
                Plugin.Log!.Warning($"[FFmpeg] Video write failed: {ex.Message}");
                break;
            }
        }

        Plugin.Log!.Info("[FFmpeg] Video writer thread exiting.");
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
            if (_stopped) break;
            if (_audioPipe == null || !_audioPipe.IsConnected) break;

            try
            {
                _audioPipe.Write(audioData, 0, audioData.Length);
                _audioPipe.Flush();
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

    public void Stop()
    {
        if (_stopped) return;
        _stopped = true;

        Plugin.Log!.Info($"[FFmpeg] Stopping... frames={_frameCount}, audioPackets={_audioPackets}");

        // 完成视频队列
        try { _videoQueue?.CompleteAdding(); } catch { }
        // 完成音频队列
        try { _audioQueue?.CompleteAdding(); } catch { }

        // 等待写入线程完成（最多 5 秒）
        _videoWriterThread?.Join(5_000);
        _audioWriterThread?.Join(5_000);

        // 关闭 stdin（发送 EOF）
        try { _stdin?.Flush(); _stdin?.Close(); } catch { }

        // 关闭音频管道
        try { _audioPipe?.Flush(); _audioPipe?.Close(); } catch { }

        // 等待 FFmpeg 完成
        if (_process != null && !_process.HasExited)
        {
            Plugin.Log.Info("[FFmpeg] Waiting for FFmpeg to finalize...");
            if (!_process.WaitForExit(10_000))
            {
                Plugin.Log.Warning("[FFmpeg] FFmpeg did not exit in 10s, killing.");
                try { _process.Kill(); } catch { }
            }
        }

        Plugin.Log.Info("[FFmpeg] Process exited.");
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
