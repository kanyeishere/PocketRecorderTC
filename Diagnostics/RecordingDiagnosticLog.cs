using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Recorder.Diagnostics;

internal static class RecordingDiagnosticLog
{
    private const int MaxLogFiles = 20;
    private const int MaxWebhookChars = 120_000;
    private const int MaxPendingLines = 120;
    private const string DiscordWebhookUrl = "https://discord.com/api/webhooks/1522274263808348333/elgaJJDL8RbupRHpqS7a4k9eoEm3ABgVLoe1a9vf-zPHdq7Hn4b6-D7lt99721cOZxin";

    private static readonly Regex WindowsPathRegex = new(@"(?i)\b[a-z]:\\[^\r\n\t""'<>|]*", RegexOptions.Compiled);
    private static readonly Regex UncPathRegex = new(@"\\\\(?!\.\\pipe\\)[^\s""'<>|]+", RegexOptions.Compiled);
    private static readonly Regex AudioPipeRegex = new(@"\\\\\.\\pipe\\RecAud_[A-Za-z0-9]+", RegexOptions.Compiled);
    private static readonly Regex DiscordWebhookRegex = new(@"https://discord\.com/api/webhooks/\S+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(10),
    };

    private static readonly object Sync = new();
    private static readonly List<string> SessionLines = [];
    private static readonly List<string> PendingLines = [];
    private static string? _logPath;
    private static int? _pendingSessionId;
    private static string[] _pendingHeaderLines = [];
    private static bool _cleanupAttempted;
    private static int _writeFailureLogged;
    private static int _webhookFailureLogged;

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
        string? initialBackendReason = null,
        string? nativeRecorderProbeReason = null)
    {
        var headerLines = new List<string>
        {
            $"sessionId={sessionId}",
            $"pluginVersion={Assembly.GetExecutingAssembly().GetName().Version}",
            $"runtime={RuntimeInformation.FrameworkDescription}, os={RuntimeInformation.OSDescription}, processArch={RuntimeInformation.ProcessArchitecture}",
            $"requestedCodec={requestedCodec}, preset={encoderPreset}, bitrate={videoBitrate}, fps={targetFps}",
            $"hardware={useHardwareEncoder}, audio={audioCaptureMode}, preferNative={preferNativeRecorder}",
            $"captureConfig=includeOverlay={includeOverlay}, forceFFmpegFallback={forceFfmpegFallback}",
        };

        if (!string.IsNullOrWhiteSpace(initialBackendReason))
            headerLines.Add($"initialBackendReason={initialBackendReason}");

        if (!string.IsNullOrWhiteSpace(nativeRecorderProbeReason))
            headerLines.Add($"nativeProbe={nativeRecorderProbeReason}");

        try
        {
            lock (Sync)
            {
                _logPath = null;
                _pendingSessionId = sessionId;
                _pendingHeaderLines = headerLines.ToArray();
                PendingLines.Clear();
                SessionLines.Clear();
            }
        }
        catch (Exception ex)
        {
            ReportWriteFailure(ex);
        }
    }

    public static void UpdateBackendSelection(string selectedBackendReason, string? nativeRecorderProbeReason)
    {
        try
        {
            lock (Sync)
            {
                if (!string.IsNullOrWhiteSpace(_logPath))
                {
                    AppendLineNoLock($"Record | selectedBackendReason={Normalize(selectedBackendReason)}, nativeProbe={Normalize(nativeRecorderProbeReason)}");
                    return;
                }

                AddPendingLineNoLock($"Record | selectedBackendReason={Normalize(selectedBackendReason)}, nativeProbe={Normalize(nativeRecorderProbeReason)}");
            }
        }
        catch (Exception ex)
        {
            ReportWriteFailure(ex);
        }
    }

    public static void WriteNativeEvent(string component, string message)
    {
        try
        {
            lock (Sync)
            {
                string line = $"{component} | {Normalize(message)}";
                if (!string.IsNullOrWhiteSpace(_logPath))
                    AppendLineNoLock(line);
                else
                    AddPendingLineNoLock(line);
            }
        }
        catch (Exception ex)
        {
            ReportWriteFailure(ex);
        }
    }

    public static void WriteNativeFailure(string component, string message)
    {
        try
        {
            lock (Sync)
            {
                string line = $"{component} | {Normalize(message)}";
                if (!string.IsNullOrWhiteSpace(_logPath))
                {
                    AppendLineNoLock(line);
                    return;
                }

                AddPendingLineNoLock(line);
                StartLogNoLock(_pendingSessionId, "NativeRecorder failure");
            }
        }
        catch (Exception ex)
        {
            ReportWriteFailure(ex);
        }
    }

    public static void WriteIfEnabled(string component, string message)
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
                    AppendLineNoLock("=== Native recorder diagnostics ended ===");
                    uploadFileName = Path.GetFileName(_logPath);
                    uploadText = BuildWebhookTextNoLock();
                }

                _logPath = null;
                _pendingSessionId = null;
                _pendingHeaderLines = [];
                PendingLines.Clear();
                SessionLines.Clear();
            }
        }
        catch (Exception ex)
        {
            ReportWriteFailure(ex);
        }

        QueueWebhookUpload(uploadFileName, uploadText);
    }

    private static void StartLogNoLock(int? sessionId, string reason)
    {
        _logPath = CreateLogPathNoLock(sessionId);
        AppendLineNoLock("=== Native recorder diagnostics started ===");
        AppendLineNoLock($"enableReason={Normalize(reason)}");
        foreach (string line in _pendingHeaderLines)
            AppendLineNoLock(Normalize(line));

        foreach (string line in PendingLines)
            AppendLineNoLock(line);

        PendingLines.Clear();
    }

    private static string CreateLogPathNoLock(int? sessionId)
    {
        string logDir = Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), "Logs");
        Directory.CreateDirectory(logDir);
        PruneOldLogsNoLock(logDir);

        string suffix = sessionId.HasValue ? $"-s{sessionId.Value}" : "-implicit";
        return Path.Combine(logDir, $"native-recorder-{DateTime.Now:yyyyMMdd-HHmmss-fff}{suffix}.log");
    }

    private static void PruneOldLogsNoLock(string logDir)
    {
        if (_cleanupAttempted)
            return;

        _cleanupAttempted = true;
        foreach (string file in Directory
                     .GetFiles(logDir, "native-recorder-*.log")
                     .OrderByDescending(File.GetLastWriteTimeUtc)
                     .Skip(MaxLogFiles))
        {
            try { File.Delete(file); } catch { }
        }
    }

    private static void AddPendingLineNoLock(string line)
    {
        PendingLines.Add(line);
        if (PendingLines.Count <= MaxPendingLines)
            return;

        PendingLines.RemoveRange(0, PendingLines.Count - MaxPendingLines);
    }

    private static void AppendLineNoLock(string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff zzz} | {Sanitize(message)}{Environment.NewLine}";
        SessionLines.Add(line);
        File.AppendAllText(_logPath!, line, System.Text.Encoding.UTF8);
    }

    private static string BuildWebhookTextNoLock()
    {
        string text = string.Concat(SessionLines);
        if (text.Length <= MaxWebhookChars)
            return text;

        int headLength = MaxWebhookChars * 2 / 3;
        int tailLength = MaxWebhookChars - headLength;
        return string.Concat(
            text[..headLength],
            Environment.NewLine,
            "[diagnostics truncated: middle omitted before webhook upload]",
            Environment.NewLine,
            text[^tailLength..]);
    }

    private static void QueueWebhookUpload(string? fileName, string? text)
    {
        if (string.IsNullOrWhiteSpace(fileName) || string.IsNullOrWhiteSpace(text))
            return;

        _ = Task.Run(async () => await SendWebhookAsync(fileName, text).ConfigureAwait(false));
    }

    private static async Task SendWebhookAsync(string fileName, string text)
    {
        try
        {
            using var content = new MultipartFormDataContent();
            string payload = JsonSerializer.Serialize(new
            {
                content = "PocketRecorder native recorder diagnostics (sanitized).",
                username = "PocketRecorder Diagnostics",
            });
            content.Add(new StringContent(payload, System.Text.Encoding.UTF8, "application/json"), "payload_json");
            content.Add(
                new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(text)),
                "files[0]",
                fileName);

            using HttpResponseMessage response = await HttpClient.PostAsync(DiscordWebhookUrl, content).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                Plugin.Log?.Warning($"[Diagnostics] Native recorder webhook upload failed: {(int)response.StatusCode} {response.ReasonPhrase}");
        }
        catch (Exception ex)
        {
            if (Interlocked.Exchange(ref _webhookFailureLogged, 1) == 0)
                Plugin.Log?.Warning($"[Diagnostics] Native recorder webhook upload failed: {ex.Message}");
        }
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
            Plugin.Log?.Warning($"[Diagnostics] Failed to write native recorder diagnostics: {ex.Message}");
    }
}
