using Recorder.Capture;

namespace Recorder.Recording;

internal sealed record RecordingRequest(
    int SessionId,
    string OutputPath,
    string FFmpegPath,
    string PluginConfigDirectory,
    int VideoBitrate,
    int TargetFps,
    AudioCaptureMode AudioCaptureMode,
    string VideoCodec,
    string EncoderPreset,
    bool UseHardwareEncoder,
    bool ForceFFmpegFallbackForTesting)
{
    public VideoFormat ToVideoFormat(VideoFrame frame)
        => new(frame.Width, frame.Height, TargetFps, frame.PixelFormat);
}
