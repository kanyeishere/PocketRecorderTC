using Recorder.Encoding;
using System;

namespace Recorder.Recording;

internal static class RecordingBackendSelector
{
    public static bool ShouldPreferNativeRecorder(Configuration config, out string reason)
    {
        if (!config.UseHardwareEncoder)
        {
            reason = "hardware encoder disabled";
            return false;
        }

        if (!IsNativeRecorderCompatibleCodec(config.VideoCodec))
        {
            reason = $"codec {config.VideoCodec} is not native HEVC/H.264 compatible";
            return false;
        }

        NativeRecorderProbeResult probe = NativeRecorderBackend.Probe();
        reason = probe.Message;
        return probe.IsAvailable;
    }

    private static bool IsNativeRecorderCompatibleCodec(string codec)
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
}
