using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Text;

namespace Recorder.Encoding;

internal sealed record EncoderSelection(string Codec, string Preset, bool IsHardware, string Reason);

internal static class FFmpegEncoderSelector
{
    private const int ProbeTimeoutMs = 4_000;
    private static readonly ConcurrentDictionary<string, Lazy<EncoderSelection>> SelectionCache = new();

    public static EncoderSelection Select(string ffmpegPath, Configuration config)
    {
        if (!IsAuto(config.VideoCodec))
        {
            if (IsAv1Codec(config.VideoCodec))
            {
                Plugin.Log!.Warning($"[FFmpeg] AV1 codec '{config.VideoCodec}' is disabled; falling back to auto hardware selection.");
            }
            else
            {
                string preset = ResolvePresetForCodec(config.VideoCodec, config.EncoderPreset);
                return new EncoderSelection(
                    config.VideoCodec,
                    preset,
                    IsHardwareCodec(config.VideoCodec),
                    "manual");
            }
        }

        if (!config.UseHardwareEncoder)
        {
            return new EncoderSelection(
                "libx264",
                ResolvePresetForCodec("libx264", config.EncoderPreset),
                false,
                "software fallback requested");
        }

        string cacheKey = $"{ffmpegPath}|{config.EncoderPreset}|auto-hardware";
        Lazy<EncoderSelection> lazySelection = SelectionCache.GetOrAdd(
            cacheKey,
            _ => new Lazy<EncoderSelection>(
                () => SelectHardwareEncoder(ffmpegPath, config.EncoderPreset),
                LazyThreadSafetyMode.ExecutionAndPublication));

        return lazySelection.Value;
    }

    public static void Warmup(string ffmpegPath, Configuration config)
    {
        if (!IsAuto(config.VideoCodec) || !config.UseHardwareEncoder)
            return;

        _ = Select(ffmpegPath, config);
    }

    public static string ResolvePresetForCodec(string codec, string configuredPreset)
    {
        if (!IsAuto(configuredPreset))
            return NormalizeManualPreset(codec, configuredPreset);

        if (codec.Equals("h264_nvenc", StringComparison.OrdinalIgnoreCase) ||
            codec.Equals("hevc_nvenc", StringComparison.OrdinalIgnoreCase))
            return "p4";

        if (codec.Equals("h264_amf", StringComparison.OrdinalIgnoreCase) ||
            codec.Equals("hevc_amf", StringComparison.OrdinalIgnoreCase))
            return "speed";

        if (codec.Equals("h264_qsv", StringComparison.OrdinalIgnoreCase) ||
            codec.Equals("hevc_qsv", StringComparison.OrdinalIgnoreCase))
            return "veryfast";

        if (codec.StartsWith("libx", StringComparison.OrdinalIgnoreCase))
            return "ultrafast";

        return string.Empty;
    }

    public static bool IsHardwareCodec(string codec)
    {
        return codec.EndsWith("_nvenc", StringComparison.OrdinalIgnoreCase) ||
               codec.EndsWith("_amf", StringComparison.OrdinalIgnoreCase) ||
               codec.EndsWith("_qsv", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsAuto(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ||
               value.Equals("auto", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsAv1Codec(string? codec)
    {
        return !string.IsNullOrWhiteSpace(codec) &&
               codec.Contains("av1", StringComparison.OrdinalIgnoreCase);
    }

    private static EncoderSelection SelectHardwareEncoder(string ffmpegPath, string configuredPreset)
    {
        try
        {
            HashSet<string> encoders = GetAvailableEncoders(ffmpegPath);
            string[] candidates =
            {
                "hevc_nvenc",
                "hevc_amf",
                "hevc_qsv",
                "h264_nvenc",
                "h264_amf",
                "h264_qsv",
            };
            var failures = new List<string>();

            foreach (string codec in candidates)
            {
                if (encoders.Count > 0 && !encoders.Contains(codec))
                {
                    failures.Add($"{codec}: not listed by FFmpeg");
                    continue;
                }

                string preset = ResolvePresetForCodec(codec, configuredPreset);
                if (CanEncodeOneFrame(ffmpegPath, codec, preset, out string error))
                {
                    Plugin.Log!.Info($"[FFmpeg] Auto encoder selected: {codec}, preset={preset}");
                    return new EncoderSelection(codec, preset, true, "auto hardware probe");
                }

                failures.Add($"{codec}: {TrimForLog(error)}");
            }

            string fallbackPreset = ResolvePresetForCodec("libx264", configuredPreset);
            Plugin.Log!.Warning($"[FFmpeg] No hardware encoder passed probe; falling back to libx264. {string.Join(" | ", failures)}");
            return new EncoderSelection("libx264", fallbackPreset, false, "hardware probe failed");
        }
        catch (Exception ex)
        {
            string fallbackPreset = ResolvePresetForCodec("libx264", configuredPreset);
            Plugin.Log!.Warning($"[FFmpeg] Hardware encoder probe failed unexpectedly; falling back to libx264. {TrimForLog(ex.Message)}");
            return new EncoderSelection("libx264", fallbackPreset, false, "hardware probe exception");
        }
    }

    private static HashSet<string> GetAvailableEncoders(string ffmpegPath)
    {
        var args = new[] { "-hide_banner", "-encoders" };
        if (!RunFFmpeg(ffmpegPath, args, ProbeTimeoutMs, out string output, out _))
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in output.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || !trimmed.StartsWith("V", StringComparison.Ordinal))
                continue;

            string[] parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
                names.Add(parts[1]);
        }

        return names;
    }

    private static bool CanEncodeOneFrame(string ffmpegPath, string codec, string preset, out string error)
    {
        var args = new List<string>
        {
            "-nostdin",
            "-hide_banner",
            "-loglevel", "error",
            "-f", "lavfi",
            "-i", "color=c=black:s=640x360:r=1:d=0.1",
            "-frames:v", "1",
            "-vf", "format=nv12",
            "-c:v", codec,
        };

        if (!string.IsNullOrWhiteSpace(preset))
        {
            args.Add("-preset");
            args.Add(preset);
        }

        args.Add("-f");
        args.Add("null");
        args.Add("-");

        bool ok = RunFFmpeg(ffmpegPath, args, ProbeTimeoutMs, out string output, out int exitCode);
        error = output;
        return ok && exitCode == 0;
    }

    private static bool RunFFmpeg(string ffmpegPath, IEnumerable<string> args, int timeoutMs, out string output, out int exitCode)
    {
        output = string.Empty;
        exitCode = -1;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            foreach (string arg in args)
                psi.ArgumentList.Add(arg);

            using Process process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start FFmpeg.");

            var outputBuilder = new StringBuilder();
            process.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    outputBuilder.AppendLine(e.Data);
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    outputBuilder.AppendLine(e.Data);
            };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            if (!process.WaitForExit(timeoutMs))
            {
                try { process.Kill(); } catch { }
                output = "probe timed out";
                return false;
            }

            exitCode = process.ExitCode;
            process.WaitForExit();
            output = outputBuilder.ToString();
            return true;
        }
        catch (Exception ex)
        {
            output = ex.Message;
            return false;
        }
    }

    private static string NormalizeManualPreset(string codec, string preset)
    {
        if (codec.StartsWith("libx", StringComparison.OrdinalIgnoreCase) &&
            IsNvencPreset(preset))
            return "ultrafast";

        if ((codec.EndsWith("_nvenc", StringComparison.OrdinalIgnoreCase) ||
             codec.EndsWith("_amf", StringComparison.OrdinalIgnoreCase) ||
             codec.EndsWith("_qsv", StringComparison.OrdinalIgnoreCase)) &&
            (preset.Equals("ultrafast", StringComparison.OrdinalIgnoreCase) ||
             preset.Equals("veryfast", StringComparison.OrdinalIgnoreCase)))
        {
            return ResolvePresetForCodec(codec, "auto");
        }

        return preset;
    }

    private static bool IsNvencPreset(string preset)
    {
        return preset.Length == 2 &&
               preset[0] is 'p' or 'P' &&
               preset[1] >= '1' &&
               preset[1] <= '7';
    }

    private static string TrimForLog(string text)
    {
        text = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return text.Length <= 180 ? text : text[..180] + "...";
    }
}
