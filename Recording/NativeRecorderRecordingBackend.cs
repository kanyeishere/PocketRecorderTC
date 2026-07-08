using Recorder.Capture;
using Recorder.Diagnostics;
using Recorder.Encoding;
using Recorder.Localization;
using System;

namespace Recorder.Recording;

internal enum NativeRecordingBackendKind
{
    Nvenc,
    Amf,
    Qsv,
}

internal sealed class NativeRecorderRecordingBackend : IRecordingBackend
{
    private readonly NativeRecordingBackendKind _kind;
    private readonly NativeRecorderRuntime _runtime;
    private readonly string _requiredVendor;
    private readonly string _runtimeRequirementField;
    private readonly string _runtimeRequirementName;

    public NativeRecorderRecordingBackend(NativeRecordingBackendKind kind)
    {
        _kind = kind;
        _runtime = NativeRecorderRuntimeManager.Default;

        (Id, DisplayName, _preparingTextKey, _requiredVendor, _runtimeRequirementField, _runtimeRequirementName) = kind switch
        {
            NativeRecordingBackendKind.Nvenc => ("native-nvenc", "Native NVENC", "Backend.NativeNvencPreparing", "nvidia", "nvencRuntime", "NVENC"),
            NativeRecordingBackendKind.Amf => ("native-amf", "Native AMF", "Backend.NativeAmfPreparing", "amd", "amfRuntime", "AMF"),
            NativeRecordingBackendKind.Qsv => ("native-qsv", "Native QSV", "Backend.NativeQsvPreparing", "intel", "oneVplRuntime", "oneVPL"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
    }

    public string Id { get; }
    public string DisplayName { get; }
    private readonly string _preparingTextKey;
    public string PreparingText => Loc.T(_preparingTextKey);
    public NativeRecorderRuntime Runtime => _runtime;
    public bool PrefersD3D11TextureFrames => true;

    public RecordingBackendCapabilities Capabilities { get; } = new(
        AcceptsD3D11Texture: true,
        AcceptsNv12: false,
        AcceptsBgra: false,
        SupportsAudio: true);

    public RecordingBackendProbeResult Probe(RecordingRequest request)
    {
        if (request.ForceFFmpegRecording)
        {
            RecordingDiagnosticLog.WriteNativeEvent(DisplayName, "probe skipped: FFmpeg recording selected");
            return RecordingBackendProbeResult.Unavailable("FFmpeg recording selected");
        }

        if (!request.UseHardwareEncoder)
        {
            RecordingDiagnosticLog.WriteNativeEvent(DisplayName, "probe skipped: hardware encoder disabled");
            return RecordingBackendProbeResult.Unavailable("hardware encoder disabled");
        }

        if (!IsCompatibleCodec(request.VideoCodec))
        {
            RecordingDiagnosticLog.WriteNativeEvent(DisplayName, $"probe skipped: codec {request.VideoCodec} is not native HEVC/H.264 compatible");
            return RecordingBackendProbeResult.Unavailable($"codec {request.VideoCodec} is not native HEVC/H.264 compatible");
        }

        if (!request.GameGraphicsDevice.Available)
        {
            string reason = $"game D3D11 device preflight unavailable: {request.GameGraphicsDevice.Reason}";
            RecordingDiagnosticLog.WriteNativeEvent(DisplayName, $"probe skipped: {reason}");
            return RecordingBackendProbeResult.Unavailable(reason, request.GameGraphicsDevice.DiagnosticSummary);
        }

        if (!string.Equals(request.GameGraphicsDevice.Vendor, _requiredVendor, StringComparison.OrdinalIgnoreCase))
        {
            string reason = $"game D3D11 device vendor is {request.GameGraphicsDevice.Vendor}, expected {_requiredVendor}";
            RecordingDiagnosticLog.WriteNativeEvent(DisplayName, $"probe skipped: {reason}; {request.GameGraphicsDevice.DiagnosticSummary}");
            return RecordingBackendProbeResult.Unavailable(reason, request.GameGraphicsDevice.DiagnosticSummary);
        }

        NativeRecorderProbeResult probe = _kind == NativeRecordingBackendKind.Nvenc
            ? _runtime.Probe()
            : ProbeVendorRuntime();

        if (probe.IsAvailable)
        {
            RecordingDiagnosticLog.WriteNativeEvent(
                DisplayName,
                $"probe available: reason={probe.Message}, diagnostics={probe.DiagnosticDetails}, gameDevice={request.GameGraphicsDevice.DiagnosticSummary}");
        }
        else
        {
            RecordingDiagnosticLog.WriteNativeFailure(
                DisplayName,
                $"probe unavailable: reason={probe.Message}, diagnostics={probe.DiagnosticDetails}, gameDevice={request.GameGraphicsDevice.DiagnosticSummary}");
        }

        return probe.IsAvailable
            ? RecordingBackendProbeResult.Available(probe.Message, CombineDiagnostics(probe.DiagnosticDetails, request.GameGraphicsDevice.DiagnosticSummary))
            : RecordingBackendProbeResult.Unavailable(probe.Message, CombineDiagnostics(probe.DiagnosticDetails, request.GameGraphicsDevice.DiagnosticSummary));
    }

    public RecordingBackendStartResult Start(
        RecordingRequest request,
        VideoFrame firstFrame,
        AudioFormat? audioFormat,
        Action<IOutputSink, string> fatalErrorHandler)
    {
        if (!firstFrame.IsD3D11Texture)
        {
            RecordingDiagnosticLog.WriteNativeFailure(
                DisplayName,
                $"start rejected first frame: {DescribeFrame(firstFrame)}");
            throw new InvalidOperationException($"{DisplayName} requires D3D11 texture frames, got {firstFrame.PixelFormat}.");
        }

        if (!TryValidateFirstFrameDevice(firstFrame, out string deviceDiagnostic))
        {
            RecordingDiagnosticLog.WriteNativeFailure(DisplayName, $"start rejected first frame device: {deviceDiagnostic}, firstFrame={DescribeFrame(firstFrame)}");
            throw new InvalidOperationException($"{DisplayName} rejected first frame device: {deviceDiagnostic}");
        }

        NativeRecorderWriter? writer = null;
        bool frameHandedToWriter = false;

        try
        {
            AmdRecordingDiagnosticLog.WriteIfEnabledOrAmdText(
                DisplayName,
                $"attempting native writer, firstFrame={DescribeFrame(firstFrame)}, firstFrameDevice={deviceDiagnostic}, audio={audioFormat != null}, audioMode={request.AudioCaptureMode}");
            RecordingDiagnosticLog.WriteNativeEvent(
                DisplayName,
                $"attempting native writer, firstFrame={DescribeFrame(firstFrame)}, firstFrameDevice={deviceDiagnostic}, audio={audioFormat != null}, audioMode={request.AudioCaptureMode}");

            writer = new NativeRecorderWriter(_runtime, request.VideoBitrate, request.VideoCodec);
            writer.FatalError += fatalErrorHandler;
            writer.SetOutputPath(request.OutputPath);

            VideoOutputDimensions output = request.GetOutputDimensions(firstFrame.Width, firstFrame.Height);
            var videoFormat = new VideoFormat(
                firstFrame.Width,
                firstFrame.Height,
                output.Width,
                output.Height,
                request.TargetFps,
                VideoPixelFormat.D3D11Texture);
            writer.Start(videoFormat, audioFormat);
            writer.WriteVideoFrame(firstFrame);
            frameHandedToWriter = true;
            writer.WaitForFirstVideoFrameSubmitted(2_000);

            return new RecordingBackendStartResult(
                writer,
                videoFormat,
                $"{DisplayName} D3D11 {GetCodecLabel(request.VideoCodec)}",
                CountFirstVideoFrame: false);
        }
        catch (Exception ex)
        {
            if (!frameHandedToWriter)
                firstFrame.ReturnBuffer();

            RecordingDiagnosticLog.WriteNativeFailure(
                DisplayName,
                $"native path failed before start, exception={ex}, lastStatus={_runtime.GetLastStatus()}");
            AmdRecordingDiagnosticLog.WriteIfEnabledOrAmdText(
                DisplayName,
                $"native path failed before start, lastStatus={_runtime.GetLastStatus()}");

            if (writer != null)
            {
                try { writer.Stop(TimeSpan.Zero); } catch { }
                try { writer.Dispose(); } catch { }
            }

            throw;
        }
    }

    private NativeRecorderProbeResult ProbeVendorRuntime()
    {
        NativeRecorderProbeResult runtimeProbe = _runtime.ProbeRuntime();
        if (!runtimeProbe.IsAvailable)
            return runtimeProbe;

        string diagnostics = runtimeProbe.DiagnosticDetails ?? string.Empty;
        if (!RuntimeFieldIsPresent(diagnostics, _runtimeRequirementField))
        {
            string reason = $"{_runtimeRequirementName} runtime is missing.";
            return NativeRecorderProbeResult.Unavailable(reason, CombineDiagnostics(diagnostics, reason));
        }

        return NativeRecorderProbeResult.Available(
            $"{DisplayName} D3D11 texture recorder available.",
            diagnostics);
    }

    private bool TryValidateFirstFrameDevice(VideoFrame firstFrame, out string diagnostic)
    {
        diagnostic = string.Empty;
        if (firstFrame.D3D11DevicePtr == IntPtr.Zero)
        {
            diagnostic = "first frame D3D11 device pointer is null";
            return false;
        }

        if (!D3D11InteropHelpers.TryGetAdapterInfoFromD3D11Device(firstFrame.D3D11DevicePtr, out D3D11AdapterInfo adapter, out string error))
        {
            diagnostic = error;
            return false;
        }

        diagnostic = adapter.DiagnosticSummary;
        return string.Equals(adapter.Vendor, _requiredVendor, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCompatibleCodec(string codec)
    {
        return string.Equals(codec, "auto", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(codec, "hevc_nvenc", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(codec, "hevc_amf", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(codec, "hevc_qsv", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(codec, "hevc", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(codec, "h265", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(codec, "h264_nvenc", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(codec, "h264_amf", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(codec, "h264_qsv", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(codec, "h264", StringComparison.OrdinalIgnoreCase);
    }

    private static bool RuntimeFieldIsPresent(string diagnostics, string fieldName)
    {
        string needle = fieldName + "=present";
        return diagnostics.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetCodecLabel(string codec)
    {
        if (string.Equals(codec, "h264", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(codec, "h264_nvenc", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(codec, "h264_amf", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(codec, "h264_qsv", StringComparison.OrdinalIgnoreCase))
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

    private static string CombineDiagnostics(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first))
            return second ?? string.Empty;
        if (string.IsNullOrWhiteSpace(second))
            return first;

        return $"{first}; {second}";
    }
}
