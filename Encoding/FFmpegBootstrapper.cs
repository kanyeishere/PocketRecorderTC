using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;

namespace Recorder.Encoding;

internal static class FFmpegBootstrapper
{
    private const string DownloadUrl = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";
    private const string InstallFolderName = "FFmpeg";
    private static readonly object InstallLock = new();

    public static string ResolveOrInstall(string configuredPath, string pluginConfigDirectory)
    {
        string? configured = ResolveConfiguredPath(configuredPath);
        if (configured != null)
            return configured;

        string installed = GetInstalledFFmpegPath(pluginConfigDirectory);
        if (File.Exists(installed))
            return installed;

        string? fromPath = FindOnPath("ffmpeg.exe");
        if (fromPath != null)
            return fromPath;

        lock (InstallLock)
        {
            if (File.Exists(installed))
                return installed;

            InstallFFmpeg(pluginConfigDirectory, installed, "FFmpeg not found");
            return installed;
        }
    }

    public static string GetInstalledFFmpegPath(string pluginConfigDirectory)
    {
        return Path.Combine(pluginConfigDirectory, "Tools", InstallFolderName, "bin", "ffmpeg.exe");
    }

    public static string InstallOrUpdateBundled(string pluginConfigDirectory)
    {
        string installed = GetInstalledFFmpegPath(pluginConfigDirectory);
        lock (InstallLock)
        {
            InstallFFmpeg(pluginConfigDirectory, installed, "Manual install/update requested");
            return installed;
        }
    }

    private static string? ResolveConfiguredPath(string configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
            return null;

        if (Path.IsPathRooted(configuredPath))
        {
            if (File.Exists(configuredPath))
                return configuredPath;

            Plugin.Log!.Warning($"[FFmpeg] Configured FFmpeg path does not exist: {configuredPath}; trying bundled/PATH fallback.");
            return null;
        }

        string? fromPath = FindOnPath(configuredPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? configuredPath
            : $"{configuredPath}.exe");
        if (fromPath != null)
            return fromPath;

        if (!configuredPath.Equals("ffmpeg", StringComparison.OrdinalIgnoreCase))
            Plugin.Log!.Warning($"[FFmpeg] Configured FFmpeg command was not found on PATH: {configuredPath}; trying bundled fallback.");

        return null;
    }

    private static string? FindOnPath(string executableName)
    {
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathEnv))
            return null;

        foreach (string dir in pathEnv.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir))
                continue;

            try
            {
                string candidate = Path.Combine(dir.Trim(), executableName);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch
            {
                // Ignore malformed PATH entries.
            }
        }

        return null;
    }

    private static void InstallFFmpeg(string pluginConfigDirectory, string installedExePath, string reason)
    {
        string toolsDir = Path.Combine(pluginConfigDirectory, "Tools", InstallFolderName);
        string targetBinDir = Path.GetDirectoryName(installedExePath)!;
        string downloadDir = Path.Combine(pluginConfigDirectory, "Downloads");
        string tempExtractDir = Path.Combine(downloadDir, $"ffmpeg-{Guid.NewGuid():N}");
        string zipPath = Path.Combine(downloadDir, "ffmpeg-latest-win64-gpl.zip");

        Directory.CreateDirectory(downloadDir);
        Directory.CreateDirectory(targetBinDir);

        Plugin.Log!.Info($"[FFmpeg] {reason}. Downloading bundled FFmpeg to plugin config directory: {DownloadUrl}");

        DownloadFile(DownloadUrl, zipPath);

        try
        {
            ZipFile.ExtractToDirectory(zipPath, tempExtractDir, overwriteFiles: true);

            string? extractedFFmpeg = Directory.EnumerateFiles(tempExtractDir, "ffmpeg.exe", SearchOption.AllDirectories)
                .FirstOrDefault();
            if (extractedFFmpeg == null)
                throw new InvalidOperationException("Downloaded FFmpeg archive did not contain ffmpeg.exe.");

            string extractedBinDir = Path.GetDirectoryName(extractedFFmpeg)!;
            foreach (string file in Directory.EnumerateFiles(extractedBinDir))
            {
                string target = Path.Combine(targetBinDir, Path.GetFileName(file));
                File.Copy(file, target, overwrite: true);
            }

            File.WriteAllText(
                Path.Combine(toolsDir, "source.txt"),
                $"Source: {DownloadUrl}{Environment.NewLine}InstalledAt: {DateTimeOffset.Now:O}{Environment.NewLine}");

            Plugin.Log!.Info($"[FFmpeg] Bundled FFmpeg installed: {installedExePath}");
        }
        finally
        {
            TryDeleteFile(zipPath);
            TryDeleteDirectory(tempExtractDir);
        }
    }

    private static void DownloadFile(string url, string destinationPath)
    {
        using var client = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(10),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PocketRecorder-DalamudPlugin/1.0");

        using HttpResponseMessage response = client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();

        long? totalBytes = response.Content.Headers.ContentLength;
        using Stream input = response.Content.ReadAsStream();
        using FileStream output = File.Create(destinationPath);

        byte[] buffer = new byte[1024 * 1024];
        long downloaded = 0;
        long nextLogAt = 10L * 1024 * 1024;

        while (true)
        {
            int read = input.Read(buffer, 0, buffer.Length);
            if (read <= 0)
                break;

            output.Write(buffer, 0, read);
            downloaded += read;

            if (downloaded >= nextLogAt)
            {
                if (totalBytes is > 0)
                    Plugin.Log!.Info($"[FFmpeg] Downloaded {downloaded / 1024 / 1024}MB / {totalBytes.Value / 1024 / 1024}MB...");
                else
                    Plugin.Log!.Info($"[FFmpeg] Downloaded {downloaded / 1024 / 1024}MB...");

                nextLogAt += 10L * 1024 * 1024;
            }
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch { }
    }
}
