using Recorder.Capture;
using System;

namespace Recorder.Recording;

/// <summary>
/// 输出目标抽象。当前实现为本地 FFmpeg 文件输出。
/// 将来可扩展 WebRTC 推流（WebRtcOutputSink），编码后通过 SIPSorcery 传输 H.264 NAL。
/// </summary>
internal interface IOutputSink : IDisposable
{
    /// <summary>输出链路发生不可恢复错误，录制服务应主动停止。</summary>
    event Action<IOutputSink, string>? FatalError;

    /// <summary>是否支持音频输入。</summary>
    bool SupportsAudio { get; }

    /// <summary>视频写入队列是否积压，需要捕获端暂缓读回。</summary>
    bool IsVideoBackedUp { get; }

    /// <summary>视频写入链路是否处于压力期，需要捕获端临时降低输入帧率。</summary>
    bool IsVideoUnderPressure { get; }

    /// <summary>停止后可上传的最终帧统计摘要。</summary>
    string FinalVideoDiagnostics { get; }

    /// <summary>设置输出路径。</summary>
    void SetOutputPath(string path);

    /// <summary>开始写入。传入视频和音频格式信息。</summary>
    void Start(VideoFormat videoFormat, AudioFormat? audioFormat);

    /// <summary>写入一帧视频画面。</summary>
    void WriteVideoFrame(VideoFrame frame);

    /// <summary>写入一包音频数据。</summary>
    void WriteAudioPacket(AudioPacket packet);

    /// <summary>停止写入并完成输出。</summary>
    void Stop(TimeSpan? finalVideoDuration = null);
}

internal sealed record VideoFormat
{
    public VideoFormat(int width, int height, int fps, VideoPixelFormat pixelFormat)
        : this(width, height, width, height, fps, pixelFormat)
    {
    }

    public VideoFormat(
        int width,
        int height,
        int outputWidth,
        int outputHeight,
        int fps,
        VideoPixelFormat pixelFormat)
    {
        Width = width;
        Height = height;
        OutputWidth = outputWidth;
        OutputHeight = outputHeight;
        Fps = fps;
        PixelFormat = pixelFormat;
    }

    public int Width { get; init; }
    public int Height { get; init; }
    public int OutputWidth { get; init; }
    public int OutputHeight { get; init; }
    public int Fps { get; init; }
    public VideoPixelFormat PixelFormat { get; init; }
    public bool HasScaledOutput => OutputWidth != Width || OutputHeight != Height;

    public string Describe()
        => HasScaledOutput
            ? $"{Width}x{Height}->{OutputWidth}x{OutputHeight}"
            : $"{Width}x{Height}";
}

internal sealed record AudioFormat(int SampleRate, int Channels, int BitsPerSample, bool IsFloat);
