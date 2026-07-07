using System;
using System.IO;

namespace Recorder.Recording;

internal static class RecordingFileNames
{
    public const string StarPrefix = "★";

    public static bool IsStarred(string path)
    {
        string fileName = Path.GetFileName(path);
        return fileName.StartsWith(StarPrefix, StringComparison.Ordinal);
    }

    public static string SetStarred(string path, bool starred)
    {
        if (string.IsNullOrWhiteSpace(path))
            return path;

        return starred ? AddStarPrefix(path) : RemoveStarPrefix(path);
    }

    public static string RenameStarred(string path, bool starred)
    {
        string targetPath = SetStarred(path, starred);
        if (string.Equals(path, targetPath, StringComparison.OrdinalIgnoreCase))
            return path;

        if (File.Exists(targetPath))
            targetPath = AppendCollisionSuffix(targetPath);

        File.Move(path, targetPath);
        return targetPath;
    }

    public static string AppendCollisionSuffix(string path)
    {
        string directory = Path.GetDirectoryName(path) ?? string.Empty;
        string name = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);

        for (int i = 2; i < 1000; i++)
        {
            string candidate = Path.Combine(directory, $"{name}-{i}{ext}");
            if (!File.Exists(candidate))
                return candidate;
        }

        return Path.Combine(directory, $"{name}-{Guid.NewGuid():N}{ext}");
    }

    private static string AddStarPrefix(string path)
    {
        if (IsStarred(path))
            return path;

        string directory = Path.GetDirectoryName(path) ?? string.Empty;
        string fileName = Path.GetFileName(path);
        return Path.Combine(directory, $"{StarPrefix}{fileName}");
    }

    private static string RemoveStarPrefix(string path)
    {
        if (!IsStarred(path))
            return path;

        string directory = Path.GetDirectoryName(path) ?? string.Empty;
        string fileName = Path.GetFileName(path);
        return Path.Combine(directory, fileName[StarPrefix.Length..]);
    }
}
