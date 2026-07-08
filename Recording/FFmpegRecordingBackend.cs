using Recorder.Capture;
using Recorder.Diagnostics;
using Recorder.Encoding;
using Recorder.Localization;
using System;

namespace Recorder.Recording;

internal sealed class FFmpegRecordingBackend : IRecordingBackend
{
    private readonly IRecorderLogger _log;

    public FFmpegRecordingBackend(IRecorderLogger log)
    {
        _log = log;
    }

    public string Id => "ffmpeg";
    public string DisplayName => "FFmpeg";
    public string PreparingText => Loc.T("Backend.FFmpegPreparing");
    public bool PrefersD3D11TextureFrames => false;

    public RecordingBackendCapabilities Capabilities { get; } = new(
        AcceptsD3D11Texture: false,
        AcceptsNv12: true,
        AcceptsBgra: true,
        SupportsAudio: true);

    public RecordingBackendProbeResult Probe(RecordingRequest request)
        => RecordingBackendProbeResult.Available("FFmpeg fallback available");

    public RecordingBackendStartResult Start(
        RecordingRequest request,
        VideoFrame firstFrame,
        AudioFormat? audioFormat,
        Action<IOutputSink, string> fatalErrorHandler)
    {
        VideoFrame frame = firstFrame;
        bool frameHandedToWriter = false;
        FFmpegWriter? writer = null;

        try
        {
            frame = frame.DetachToManagedCopyIfNative();

            var encoderConfig = new Configuration
            {
                VideoBitrate = request.VideoBitrate,
                VideoCodec = request.VideoCodec,
                EncoderPreset = request.EncoderPreset,
                UseHardwareEncoder = request.UseHardwareEncoder,
            };

            string ffmpegPath = FFmpegBootstrapper.ResolveOrInstall(
                request.FFmpegPath,
                request.PluginConfigDirectory);
            EncoderSelection encoder = FFmpegEncoderSelector.Select(ffmpegPath, encoderConfig);

            AmdRecordingDiagnosticLog.WriteForAmdCodec(
                encoder.Codec,
                "FFmpeg",
                $"selected encoder codec={encoder.Codec}, preset={encoder.Preset}, isHardware={encoder.IsHardware}, reason={encoder.Reason}, diagnostics={encoder.DiagnosticDetails}, ffmpegPath={ffmpegPath}, firstFrame={DescribeFrame(frame)}");

            writer = new FFmpegWriter(
                ffmpegPath,
                request.VideoBitrate,
                encoder.Codec,
                encoder.Preset);
            writer.FatalError += fatalErrorHandler;
            writer.SetOutputPath(request.OutputPath);

            var videoFormat = request.ToVideoFormat(frame);
            writer.Start(videoFormat, audioFormat);
            writer.WriteVideoFrame(frame);
            frameHandedToWriter = true;

            if (!writer.WaitForFirstVideoFrameWritten(1_000))
                _log.Warning("[Record] FFmpeg did not accept the first video frame within 1000ms; starting capture anyway.");

            return new RecordingBackendStartResult(
                writer,
                videoFormat,
                $"FFmpeg {encoder.Codec}",
                CountFirstVideoFrame: true);
        }
        catch
        {
            if (!frameHandedToWriter)
                frame.ReturnBuffer();

            if (writer != null)
            {
                try { writer.Stop(TimeSpan.Zero); } catch { }
                try { writer.Dispose(); } catch { }
            }

            throw;
        }
    }

    private static string DescribeFrame(VideoFrame frame)
    {
        string description = $"{frame.Width}x{frame.Height}, pixelFormat={frame.PixelFormat}, stride={frame.Stride}, dataLength={frame.DataLength}, timestampHns={frame.TimestampHns}";
        if (!frame.IsD3D11Texture)
            return description;

        return $"{description}, dxgiFormat={frame.DxgiFormat}, deviceSet={frame.D3D11DevicePtr != IntPtr.Zero}, textureSet={frame.D3D11TexturePtr != IntPtr.Zero}, sharedHandleSet={frame.D3D11SharedHandle != IntPtr.Zero}";
    }
}
