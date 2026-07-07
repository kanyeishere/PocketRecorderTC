using System.Diagnostics;
using System.IO;

namespace Recorder;

internal static class ShellHelpers
{
    public static void OpenDirectory(string directory)
    {
        if (!Directory.Exists(directory))
            Directory.CreateDirectory(directory);

        Process.Start(new ProcessStartInfo
        {
            FileName = directory,
            UseShellExecute = true,
            Verb = "open",
        });
    }

    public static void OpenFile(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException("File does not exist.", filePath);

        Process.Start(new ProcessStartInfo
        {
            FileName = filePath,
            UseShellExecute = true,
            Verb = "open",
        });
    }

    public static void ShowFileInExplorer(string filePath)
    {
        if (!File.Exists(filePath))
        {
            string? directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrWhiteSpace(directory))
                OpenDirectory(directory);
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"/select,\"{filePath}\"",
            UseShellExecute = true,
        });
    }
}
