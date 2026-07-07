namespace Recorder.Diagnostics;

internal static class AmdRecordingDiagnosticLog
{
    public static bool IsAmdCodec(string? codec)
        => false;

    public static bool IsAmdRelevant(string? codec, string? nativeReason, string? selectedBackendReason = null)
        => false;

    public static void StartSession(
        int sessionId,
        int targetFps,
        int videoBitrate,
        string requestedCodec,
        string encoderPreset,
        bool useHardwareEncoder,
        AudioCaptureMode audioCaptureMode,
        bool includeOverlay,
        VideoOutputScaleMode videoOutputScaleMode,
        bool forceFfmpegRecording,
        bool preferNativeRecorder,
        string selectedBackendReason,
        string? nativeRecorderProbeReason)
    {
    }

    public static void EnableImplicitSession(string reason)
    {
    }

    public static void Write(string component, string message)
    {
    }

    public static void WriteForAmdCodec(string? codec, string component, string message)
    {
    }

    public static void WriteIfEnabledOrAmdText(string component, string message)
    {
    }

    public static void FinishSession(string message)
    {
    }
}
