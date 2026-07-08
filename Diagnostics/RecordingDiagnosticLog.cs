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

internal static class RecordingDiagnosticLog
{
    private const int MaxLogFiles = 20;
    private const int MaxUploadChars = 120_000;
    private const int MaxPendingLines = 120;

    private static readonly Regex WindowsPathRegex = new(@"(?i)\b[a-z]:\\[^\r\n\t""'<>|]*", RegexOptions.Compiled);
    private static readonly Regex UncPathRegex = new(@"\\\\(?!\.\\pipe\\)[^\s""'<>|]+", RegexOptions.Compiled);
    private static readonly Regex AudioPipeRegex = new(@"\\\\\.\\pipe\\RecAud_[A-Za-z0-9]+", RegexOptions.Compiled);
    private static readonly Regex DiscordWebhookRegex = new(@"https://discord\.com/api/webhooks/\S+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly object Sync = new();
    private static readonly List<string> SessionLines = [];
    private static readonly List<string> PendingLines = [];
    private static string? _logPath;
    private static int? _pendingSessionId;
    private static string[] _pendingHeaderLines = [];
    private static RecordingTelemetryContext? _context;
    private static string _finalFrameDiagnostics = string.Empty;
    private static bool _cleanupAttempted;
    private static int _writeFailureLogged;

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
        string? gameGraphicsDevice = null,
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
            $"captureConfig=includeOverlay={includeOverlay}, outputScale={videoOutputScaleMode}, forceFFmpegRecording={forceFfmpegRecording}",
        };

        if (!string.IsNullOrWhiteSpace(gameGraphicsDevice))
            headerLines.Add($"gameGraphicsDevice={gameGraphicsDevice}");

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
                _context = null;
                _finalFrameDiagnostics = string.Empty;
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

    public static void UpdateRecordingContext(RecordingTelemetryContext context)
    {
        try
        {
            lock (Sync)
            {
                _context = context;
                string line =
                    $"Record | gpuVendor={Normalize(context.GpuVendor)}, gpuAdapter={Normalize(context.GpuAdapter)}, " +
                    $"backendMode={Normalize(context.BackendMode)}, backendLabel={Normalize(context.BackendLabel)}, " +
                    $"dalamudApiLevel={context.DalamudApiLevel}, nativeNvencSdk={Normalize(context.NativeNvencSdk)}, " +
                    $"cpuName={Normalize(context.CpuName)}, totalMemoryMB={context.TotalMemoryMB}";
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

    public static void FinishSession(string message, string? finalFrameDiagnostics = null)
    {
        string? uploadFileName = null;
        string? uploadText = null;
        RecordingTelemetryContext? uploadContext = null;
        string uploadFinalFrameDiagnostics = string.Empty;

        try
        {
            lock (Sync)
            {
                if (!string.IsNullOrWhiteSpace(finalFrameDiagnostics))
                    _finalFrameDiagnostics = Normalize(finalFrameDiagnostics);

                if (!string.IsNullOrWhiteSpace(_logPath))
                {
                    if (!string.IsNullOrWhiteSpace(_finalFrameDiagnostics))
                        AppendLineNoLock($"FinalFrames | {_finalFrameDiagnostics}");
                    AppendLineNoLock($"Session | {Normalize(message)}");
                    AppendLineNoLock("=== Native recorder diagnostics ended ===");
                    uploadFileName = Path.GetFileName(_logPath);
                    uploadText = BuildUploadTextNoLock();
                    uploadContext = _context;
                    uploadFinalFrameDiagnostics = _finalFrameDiagnostics;
                }

                _logPath = null;
                _pendingSessionId = null;
                _pendingHeaderLines = [];
                _context = null;
                _finalFrameDiagnostics = string.Empty;
                PendingLines.Clear();
                SessionLines.Clear();
            }
        }
        catch (Exception ex)
        {
            ReportWriteFailure(ex);
        }

        QueueDiagnosticsUpload(uploadFileName, uploadText, uploadContext, uploadFinalFrameDiagnostics);
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

    private static void QueueDiagnosticsUpload(
        string? fileName,
        string? text,
        RecordingTelemetryContext? context,
        string finalFrameDiagnostics)
    {
        if (string.IsNullOrWhiteSpace(fileName) || string.IsNullOrWhiteSpace(text))
            return;

        PocketBackendClient.QueueDiagnostics(
            "native-recorder",
            fileName,
            text,
            "PocketRecorder native recorder diagnostics (sanitized).",
            context == null
                ? null
                : new
                {
                    context.SessionId,
                    context.DalamudApiLevel,
                    context.GpuVendor,
                    context.GpuAdapter,
                    context.BackendMode,
                    context.BackendLabel,
                    context.RequestedCodec,
                    context.SelectedBackendReason,
                    context.NativeProbeReason,
                    context.NativeNvencSdk,
                    context.CpuName,
                    context.TotalMemoryMB,
                    FinalFrameDiagnostics = finalFrameDiagnostics,
                });
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
