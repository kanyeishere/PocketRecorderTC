using Dalamud.Plugin;
using Recorder.Encoding;
using System;

namespace Recorder;

internal static class ConfigurationMigrator
{
    public static void Migrate(Configuration config, IDalamudPluginInterface pi)
    {
        if (config.Version < 3)
        {
            config.UseHardwareEncoder = true;
            if (config.VideoCodec == "auto" &&
                (string.IsNullOrEmpty(config.EncoderPreset) ||
                 config.EncoderPreset.Equals("veryfast", StringComparison.OrdinalIgnoreCase) ||
                 config.EncoderPreset.Equals("ultrafast", StringComparison.OrdinalIgnoreCase)))
            {
                config.EncoderPreset = "p4";
            }

            SaveVersion(config, pi, 3);
        }

        if (config.Version < 4)
        {
            if (config.VideoCodec == "auto")
                config.EncoderPreset = "auto";

            SaveVersion(config, pi, 4);
        }

        if (config.Version < 5)
        {
            config.ShowFloatingRecordButton = true;
            config.AutoRecordEightPlayerDuty = true;
            SaveVersion(config, pi, 5);
        }

        if (config.Version < 6)
        {
            if (string.IsNullOrWhiteSpace(config.ToggleCommand) ||
                config.ToggleCommand.Equals("/record", StringComparison.OrdinalIgnoreCase))
            {
                config.ToggleCommand = "/pocketrecorder";
            }

            SaveVersion(config, pi, 6);
        }

        if (config.Version < 7)
        {
            if (config.VideoBitrate == 8_000_000)
                config.VideoBitrate = 32_000_000;

            if (config.TargetFps == 30)
                config.TargetFps = 60;

            SaveVersion(config, pi, 7);
        }

        if (config.Version < 8)
        {
            if (config.VideoBitrate == 32_000_000)
                config.VideoBitrate = 12_000_000;

            if (config.TargetFps == 60)
                config.TargetFps = 30;

            SaveVersion(config, pi, 8);
        }

        if (config.Version < 9)
        {
            config.EnableNativeRecorderExperimental = false;
            SaveVersion(config, pi, 9);
        }

        if (config.Version < 10)
        {
            config.EnableNativeRecorderExperimental = true;
            SaveVersion(config, pi, 10);
        }

        if (config.Version < 11)
        {
            config.EnableNativeRecorderExperimental = true;

            if (config.VideoBitrate == 12_000_000)
                config.VideoBitrate = 24_000_000;

            if (config.TargetFps == 30)
                config.TargetFps = 60;

            SaveVersion(config, pi, 11);
        }

        if (config.Version < 12)
        {
            if (config.VideoBitrate == 24_000_000)
                config.VideoBitrate = 32_000_000;

            SaveVersion(config, pi, 12);
        }

        if (config.Version < 13)
        {
            if (FFmpegEncoderSelector.IsAv1Codec(config.VideoCodec))
            {
                config.VideoCodec = "auto";
                config.EncoderPreset = "auto";
                config.UseHardwareEncoder = true;
            }

            SaveVersion(config, pi, 13);
        }

        if (config.Version < 14)
        {
            config.AudioCaptureMode = config.CaptureAudio
                ? AudioCaptureMode.System
                : AudioCaptureMode.Off;

            SaveVersion(config, pi, 14);
        }

        if (config.Version < 15)
        {
            config.ForceFFmpegFallbackForTesting = false;
            SaveVersion(config, pi, 15);
        }

        if (config.Version < 16)
        {
            if (string.IsNullOrWhiteSpace(config.InstallId))
                config.InstallId = Guid.NewGuid().ToString("N");

            config.EnablePocketBackendTelemetry = true;
            SaveVersion(config, pi, 16);
        }

        if (config.Version < 17)
        {
            SaveVersion(config, pi, 17);
        }

        config.CaptureAudio = config.AudioCaptureMode != AudioCaptureMode.Off;

        if (string.IsNullOrWhiteSpace(config.InstallId))
        {
            config.InstallId = Guid.NewGuid().ToString("N");
            config.Save(pi);
        }

        if (!config.EnableNativeRecorderExperimental)
        {
            config.EnableNativeRecorderExperimental = true;
            config.Save(pi);
        }
    }

    private static void SaveVersion(Configuration config, IDalamudPluginInterface pi, int version)
    {
        config.Version = version;
        config.Save(pi);
    }
}
