using Recorder.Capture;
using Recorder.Diagnostics;
using Recorder.Encoding;
using System;

namespace Recorder.Recording;

internal sealed class NativeRecorderRecordingBackend : IRecordingBackend
{
    public string Id => "native-recorder";
    public string DisplayName => "NativeRecorder";
    public string PreparingText => "NativeRecorder 准备中";
    public bool PrefersD3D11TextureFrames => true;

    public RecordingBackendCapabilities Capabilities { get; } = new(
        AcceptsD3D11Texture: true,
        AcceptsNv12: false,
        AcceptsBgra: false,
        SupportsAudio: true);

    public RecordingBackendProbeResult Probe(RecordingRequest request)
    {
        if (request.ForceFFmpegFallbackForTesting)
            return RecordingBackendProbeResult.Unavailable("FFmpeg fallback forced for local testing");

        if (!request.UseHardwareEncoder)
            return RecordingBackendProbeResult.Unavailable("hardware encoder disabled");

        if (!IsCompatibleCodec(request.VideoCodec))
            return RecordingBackendProbeResult.Unavailable($"codec {request.VideoCodec} is not native HEVC/H.264 compatible");

        NativeRecorderProbeResult probe = NativeRecorderBackend.Probe();
        return probe.IsAvailable
            ? RecordingBackendProbeResult.Available(probe.Message)
            : RecordingBackendProbeResult.Unavailable(probe.Message);
    }

    public RecordingBackendStartResult Start(
        RecordingRequest request,
        VideoFrame firstFrame,
        AudioFormat? audioFormat,
        Action<IOutputSink, string> fatalErrorHandler)
    {
        if (!firstFrame.IsD3D11Texture)
            throw new InvalidOperationException($"NativeRecorder requires D3D11 texture frames, got {firstFrame.PixelFormat}.");

        NativeRecorderWriter? writer = null;
        bool frameHandedToWriter = false;

        try
        {
            AmdRecordingDiagnosticLog.WriteIfEnabledOrAmdText(
                "NativeRecorder",
                $"attempting native writer, firstFrame={DescribeFrame(firstFrame)}, audio={audioFormat != null}, audioMode={request.AudioCaptureMode}");

            writer = new NativeRecorderWriter(request.VideoBitrate, request.VideoCodec);
            writer.FatalError += fatalErrorHandler;
            writer.SetOutputPath(request.OutputPath);

            var videoFormat = new VideoFormat(
                firstFrame.Width,
                firstFrame.Height,
                request.TargetFps,
                VideoPixelFormat.D3D11Texture);
            writer.Start(videoFormat, audioFormat);
            writer.WriteVideoFrame(firstFrame);
            frameHandedToWriter = true;
            writer.WaitForFirstVideoFrameSubmitted(2_000);

            return new RecordingBackendStartResult(
                writer,
                videoFormat,
                $"NativeRecorder D3D11 {GetCodecLabel(request.VideoCodec)}",
                CountFirstVideoFrame: false);
        }
        catch
        {
            if (!frameHandedToWriter)
                firstFrame.ReturnBuffer();

            AmdRecordingDiagnosticLog.WriteIfEnabledOrAmdText(
                "NativeRecorder",
                $"native path failed before start, fallback=FFmpeg rawvideo, lastStatus={NativeRecorderBackend.GetLastStatus()}");

            if (writer != null)
            {
                try { writer.Stop(TimeSpan.Zero); } catch { }
                try { writer.Dispose(); } catch { }
            }

            throw;
        }
    }

    private static bool IsCompatibleCodec(string codec)
    {
        return string.Equals(codec, "auto", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(codec, "hevc_nvenc", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(codec, "hevc_amf", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(codec, "hevc", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(codec, "h265", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(codec, "h264_nvenc", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(codec, "h264_amf", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(codec, "h264", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetCodecLabel(string codec)
    {
        if (string.Equals(codec, "h264", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(codec, "h264_nvenc", StringComparison.OrdinalIgnoreCase))
        {
            return "H.264";
        }

        return "HEVC";
    }

    private static string DescribeFrame(VideoFrame frame)
    {
        string description = $"{frame.Width}x{frame.Height}, pixelFormat={frame.PixelFormat}, stride={frame.Stride}, dataLength={frame.DataLength}, timestampHns={frame.TimestampHns}";
        if (!frame.IsD3D11Texture)
            return description;

        return $"{description}, dxgiFormat={frame.DxgiFormat}, deviceSet={frame.D3D11DevicePtr != IntPtr.Zero}, textureSet={frame.D3D11TexturePtr != IntPtr.Zero}, sharedHandleSet={frame.D3D11SharedHandle != IntPtr.Zero}";
    }
}
