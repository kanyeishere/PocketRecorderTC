using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using Recorder.Telemetry;

namespace Recorder.Diagnostics;

internal static class AmdRecordingDiagnosticLog
{
    private const int MaxLogFiles = 20;
    private const int MaxUploadChars = 120_000;

    private static readonly Regex WindowsPathRegex = new(@"(?i)\b[a-z]:\\[^\r\n\t""'<>|]*", RegexOptions.Compiled);
    private static readonly Regex UncPathRegex = new(@"\\\\(?!\.\\pipe\\)[^\s""'<>|]+", RegexOptions.Compiled);
    private static readonly Regex AudioPipeRegex = new(@"\\\\\.\\pipe\\RecAud_[A-Za-z0-9]+", RegexOptions.Compiled);
    private static readonly Regex DiscordWebhookRegex = new(@"https://discord\.com/api/webhooks/\S+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly object Sync = new();
    private static readonly List<string> SessionLines = [];
    private static string? _logPath;
    private static int? _pendingSessionId;
    private static string[] _pendingHeaderLines = [];
    private static bool _cleanupAttempted;
    private static int _writeFailureLogged;

    public static bool IsAmdCodec(string? codec)
        => !string.IsNullOrWhiteSpace(codec) &&
           codec.EndsWith("_amf", StringComparison.OrdinalIgnoreCase);

    public static bool IsAmdRelevant(string? codec, string? nativeReason, string? selectedBackendReason = null)
        => IsAmdCodec(codec) ||
           ContainsAmdMarker(nativeReason) ||
           ContainsAmdMarker(selectedBackendReason);

    public static void StartSession(
        int sessionId,
        int targetFps,
        int videoBitrate,
        string requestedCodec,
        string encoderPreset,
        bool useHardwareEncoder,
        AudioCaptureMode audioCaptureMode,
        bool includeOverlay,
        bool forceFfmpegFallback,
        bool preferNativeRecorder,
        string selectedBackendReason,
        string? nativeRecorderProbeReason)
    {
        string effectiveNativeProbeReason = string.IsNullOrWhiteSpace(nativeRecorderProbeReason)
            ? selectedBackendReason
            : nativeRecorderProbeReason;

        var headerLines = new List<string>
        {
            $"sessionId={sessionId}",
            $"pluginVersion={Assembly.GetExecutingAssembly().GetName().Version}",
            $"runtime={RuntimeInformation.FrameworkDescription}, os={RuntimeInformation.OSDescription}, processArch={RuntimeInformation.ProcessArchitecture}",
            $"requestedCodec={requestedCodec}, preset={encoderPreset}, bitrate={videoBitrate}, fps={targetFps}",
            $"hardware={useHardwareEncoder}, audio={audioCaptureMode}, preferNative={preferNativeRecorder}",
            $"captureConfig=includeOverlay={includeOverlay}, forceFFmpegFallback={forceFfmpegFallback}",
            $"nativeProbe={effectiveNativeProbeReason}",
        };

        if (!string.Equals(selectedBackendReason, effectiveNativeProbeReason, StringComparison.Ordinal))
            headerLines.Add($"selectedBackendReason={selectedBackendReason}");

        try
        {
            lock (Sync)
            {
                _logPath = null;
                _pendingSessionId = sessionId;
                _pendingHeaderLines = headerLines.ToArray();
                SessionLines.Clear();

                if (!IsAmdRelevant(requestedCodec, effectiveNativeProbeReason, selectedBackendReason))
                    return;

                StartLogNoLock(sessionId, "recording start matched AMD/AMF");
            }
        }
        catch (Exception ex)
        {
            ReportWriteFailure(ex);
        }
    }

    public static void EnableImplicitSession(string reason)
    {
        try
        {
            lock (Sync)
            {
                if (!string.IsNullOrWhiteSpace(_logPath))
                    return;

                StartLogNoLock(_pendingSessionId, reason);
            }
        }
        catch (Exception ex)
        {
            ReportWriteFailure(ex);
        }
    }

    public static void Write(string component, string message)
    {
        try
        {
            lock (Sync)
            {
                if (string.IsNullOrWhiteSpace(_logPath))
                    return;

                AppendLineNoLock($"{component} | {Normalize(message)}");
            }
        }
        catch (Exception ex)
        {
            ReportWriteFailure(ex);
        }
    }

    public static void WriteForAmdCodec(string? codec, string component, string message)
    {
        if (!IsAmdCodec(codec))
            return;

        EnableImplicitSession($"FFmpeg selected AMD codec {codec}");
        Write(component, message);
    }

    public static void WriteIfEnabledOrAmdText(string component, string message)
    {
        try
        {
            lock (Sync)
            {
                if (string.IsNullOrWhiteSpace(_logPath))
                {
                    if (!ContainsAmdMarker(message))
                        return;

                    StartLogNoLock(_pendingSessionId, "native status mentioned AMD/AMF");
                }

                AppendLineNoLock($"{component} | {Normalize(message)}");
            }
        }
        catch (Exception ex)
        {
            ReportWriteFailure(ex);
        }
    }

    public static void FinishSession(string message)
    {
        string? uploadFileName = null;
        string? uploadText = null;

        try
        {
            lock (Sync)
            {
                if (!string.IsNullOrWhiteSpace(_logPath))
                {
                    AppendLineNoLock($"Session | {Normalize(message)}");
                    AppendLineNoLock("=== AMD recorder diagnostics ended ===");
                    uploadFileName = Path.GetFileName(_logPath);
                    uploadText = BuildUploadTextNoLock();
                }

                _logPath = null;
                _pendingSessionId = null;
                _pendingHeaderLines = [];
                SessionLines.Clear();
            }
        }
        catch (Exception ex)
        {
            ReportWriteFailure(ex);
        }

        QueueDiagnosticsUpload(uploadFileName, uploadText);
    }

    private static void StartLogNoLock(int? sessionId, string reason)
    {
        _logPath = CreateLogPathNoLock(sessionId);
        AppendLineNoLock("=== AMD recorder diagnostics started ===");
        AppendLineNoLock($"enableReason={Normalize(reason)}");
        foreach (string line in _pendingHeaderLines)
            AppendLineNoLock(Normalize(line));
    }

    private static string CreateLogPathNoLock(int? sessionId)
    {
        string logDir = Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), "Logs");
        Directory.CreateDirectory(logDir);
        PruneOldLogsNoLock(logDir);

        string suffix = sessionId.HasValue ? $"-s{sessionId.Value}" : "-implicit";
        return Path.Combine(logDir, $"amd-recorder-{DateTime.Now:yyyyMMdd-HHmmss-fff}{suffix}.log");
    }

    private static void PruneOldLogsNoLock(string logDir)
    {
        if (_cleanupAttempted)
            return;

        _cleanupAttempted = true;
        foreach (string file in Directory
                     .GetFiles(logDir, "amd-recorder-*.log")
                     .OrderByDescending(File.GetLastWriteTimeUtc)
                     .Skip(MaxLogFiles))
        {
            try { File.Delete(file); } catch { }
        }
    }

    private static void AppendLineNoLock(string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff zzz} | {Sanitize(message)}{Environment.NewLine}";
        SessionLines.Add(line);
        File.AppendAllText(_logPath!, line, System.Text.Encoding.UTF8);
    }

    private static string BuildUploadTextNoLock()
    {
        string text = string.Concat(SessionLines);
        if (text.Length <= MaxUploadChars)
            return text;

        int headLength = MaxUploadChars * 2 / 3;
        int tailLength = MaxUploadChars - headLength;
        return string.Concat(
            text[..headLength],
            Environment.NewLine,
            "[diagnostics truncated: middle omitted before upload]",
            Environment.NewLine,
            text[^tailLength..]);
    }

    private static void QueueDiagnosticsUpload(string? fileName, string? text)
    {
        if (string.IsNullOrWhiteSpace(fileName) || string.IsNullOrWhiteSpace(text))
            return;

        PocketBackendClient.QueueDiagnostics(
            "amd-recorder",
            fileName,
            text,
            "PocketRecorder AMD recorder diagnostics (sanitized).");
    }

    private static bool ContainsAmdMarker(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return value.Contains("amd", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("amf", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("radeon", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("advanced micro devices", StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string? value)
        => (value ?? string.Empty)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);

    private static string Sanitize(string value)
    {
        string sanitized = DiscordWebhookRegex.Replace(value, "<discord-webhook>");
        sanitized = AudioPipeRegex.Replace(sanitized, @"\\.\pipe\<audio>");
        sanitized = WindowsPathRegex.Replace(sanitized, "<path>");
        sanitized = UncPathRegex.Replace(sanitized, "<unc-path>");
        return sanitized;
    }

    private static void ReportWriteFailure(Exception ex)
    {
        if (Interlocked.Exchange(ref _writeFailureLogged, 1) == 0)
            Plugin.Log?.Warning($"[Diagnostics] Failed to write AMD recorder diagnostics: {ex.Message}");
    }
}
