using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;

namespace Recorder.Recording;

internal sealed class RecordingRetentionCleanupService : IDisposable
{
    public const int MaxRetentionDays = 3650;

    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Period = TimeSpan.FromHours(12);
    private static readonly string OptionalStarPrefix = $"{Regex.Escape(RecordingFileNames.StarPrefix)}?";
    private static readonly Regex ManualRecordingNameRegex = new(
        @$"^{OptionalStarPrefix}FFXIV_\d{{8}}_\d{{6}}(?:-\d+)?\.mp4$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(250));
    private static readonly Regex DutyRecordingNameRegex = new(
        @$"^{OptionalStarPrefix}.+-\d{{8}}-\d{{6}}-\d{{6}}(?:-\d+)?\.mp4$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(250));
    private static readonly Regex TemporaryDutyRecordingNameRegex = new(
        @$"^{OptionalStarPrefix}.+-\d{{8}}-\d{{6}}-recording(?:-\d+)?\.mp4$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(250));

    private readonly Plugin _plugin;
    private readonly IRecorderLogger _log;
    private readonly object _statusSync = new();
    private readonly Timer _timer;

    private CleanupResult? _lastResult;
    private int _cleanupRunning;
    private volatile bool _disposed;

    public RecordingRetentionCleanupService(Plugin plugin, IRecorderEnvironment environment)
    {
        _plugin = plugin;
        _log = environment.Log;
        _timer = new Timer(_ => QueueCleanup("scheduled"), null, InitialDelay, Period);
    }

    public bool IsCleanupRunning => Volatile.Read(ref _cleanupRunning) != 0;

    public string LastStatusText
    {
        get
        {
            if (IsCleanupRunning)
                return "正在清理旧录像...";

            lock (_statusSync)
                return _lastResult?.StatusText ?? string.Empty;
        }
    }

    private bool QueueCleanup(string trigger)
    {
        if (_disposed)
            return false;

        if (Interlocked.CompareExchange(ref _cleanupRunning, 1, 0) != 0)
            return false;

        ThreadPool.QueueUserWorkItem(_ => CleanupWorker(trigger));
        return true;
    }

    private void CleanupWorker(string trigger)
    {
        try
        {
            if (_disposed)
                return;

            CleanupResult? result = RunCleanup(trigger);
            if (result != null)
                SetLastResult(result);
        }
        catch (Exception ex)
        {
            var result = CleanupResult.Create($"清理旧录像失败: {ex.Message}");
            SetLastResult(result);
            _log.Warning($"[Retention] Cleanup failed: {ex}");
        }
        finally
        {
            Volatile.Write(ref _cleanupRunning, 0);
        }
    }

    private CleanupResult? RunCleanup(string trigger)
    {
        int retentionDays = Math.Clamp(_plugin.Config.RecordingRetentionDays, 0, MaxRetentionDays);
        if (retentionDays <= 0)
            return null;

        if (_plugin.RecordingService.Phase != RecordingPhase.Idle)
            return CleanupResult.Create("录制中，已跳过旧录像清理。");

        string configuredDirectory = _plugin.Config.GetEffectiveOutputDirectory(Plugin.PluginInterface);
        if (string.IsNullOrWhiteSpace(configuredDirectory) || !Directory.Exists(configuredDirectory))
            return CleanupResult.Create("输出目录不存在，已跳过旧录像清理。");

        if (!TryGetSafeDirectory(configuredDirectory, out string outputDirectory, out string unsafeReason))
            return CleanupResult.Create(unsafeReason);

        DateTime cutoffUtc = DateTime.UtcNow.AddDays(-retentionDays);
        int deletedFiles = 0;
        int failedFiles = 0;
        long deletedBytes = 0;

        foreach (string filePath in Directory.EnumerateFiles(outputDirectory, "*.mp4", SearchOption.TopDirectoryOnly))
        {
            string fileName = Path.GetFileName(filePath);
            if (!IsPocketRecorderRecordingName(fileName))
                continue;

            try
            {
                var file = new FileInfo(filePath);
                if (!file.Exists || file.LastWriteTimeUtc >= cutoffUtc)
                    continue;

                long length = file.Length;
                file.Delete();
                deletedFiles++;
                deletedBytes += length;
            }
            catch (Exception ex)
            {
                failedFiles++;
                _log.Warning($"[Retention] Failed to delete old recording '{fileName}': {ex.Message}");
            }
        }

        string message = failedFiles > 0
            ? $"已删除 {deletedFiles} 个旧录像，释放 {FormatBytes(deletedBytes)}，失败 {failedFiles} 个。"
            : deletedFiles > 0
                ? $"已删除 {deletedFiles} 个旧录像，释放 {FormatBytes(deletedBytes)}。"
                : "没有需要清理的旧录像。";

        _log.Info($"[Retention] Cleanup {trigger}: retentionDays={retentionDays}, deleted={deletedFiles}, bytes={deletedBytes}, failed={failedFiles}");
        return CleanupResult.Create(message);
    }

    private static bool IsPocketRecorderRecordingName(string fileName)
    {
        return ManualRecordingNameRegex.IsMatch(fileName) ||
               DutyRecordingNameRegex.IsMatch(fileName) ||
               TemporaryDutyRecordingNameRegex.IsMatch(fileName);
    }

    private static bool TryGetSafeDirectory(string directory, out string fullDirectory, out string failureReason)
    {
        fullDirectory = Path.GetFullPath(directory);
        string normalizedDirectory = fullDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string normalizedRoot = (Path.GetPathRoot(fullDirectory) ?? string.Empty)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (!string.IsNullOrWhiteSpace(normalizedRoot) &&
            string.Equals(normalizedDirectory, normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            failureReason = "输出目录是磁盘根目录，已跳过旧录像清理。";
            return false;
        }

        failureReason = string.Empty;
        return true;
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int unitIndex = 0;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return unitIndex == 0
            ? $"{bytes} {units[unitIndex]}"
            : $"{value:0.##} {units[unitIndex]}";
    }

    private void SetLastResult(CleanupResult result)
    {
        lock (_statusSync)
            _lastResult = result;
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Dispose();
    }

    private sealed record CleanupResult(DateTimeOffset FinishedAt, string Message)
    {
        public string StatusText => $"上次清理 {FinishedAt.LocalDateTime:MM-dd HH:mm}: {Message}";

        public static CleanupResult Create(string message)
            => new(DateTimeOffset.Now, message);
    }
}
