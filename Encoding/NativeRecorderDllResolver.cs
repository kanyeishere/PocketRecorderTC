using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Recorder.Encoding;

internal sealed class NativeRecorderDllResolver
{
    private readonly string[] _fileNames;
    private readonly List<string> _extraSearchDirectories = [];

    public NativeRecorderDllResolver(string[] fileNames)
    {
        _fileNames = fileNames;
    }

    public void ConfigureFromPluginInterface(object? pluginInterface)
    {
        if (pluginInterface == null)
            return;

        AddPathLikeValues(pluginInterface, "plugin interface");

        object? installedPlugins = TryGetPropertyValue(pluginInterface, "InstalledPlugins");
        if (installedPlugins is not IEnumerable plugins)
            return;

        foreach (object? plugin in plugins)
        {
            if (plugin == null || !LooksLikePocketRecorderPlugin(plugin))
                continue;

            AddPathLikeValues(plugin, "installed plugin");
            break;
        }
    }

    public string[] BuildCandidates(Type assemblyAnchor)
        => BuildCandidates(assemblyAnchor, _fileNames);

    public string[] BuildCandidates(Type assemblyAnchor, IReadOnlyList<string> fileNames)
    {
        List<string> directories = [];

        foreach (string directory in _extraSearchDirectories)
            AddDirectory(directories, directory);

        string? assemblyDir = Path.GetDirectoryName(assemblyAnchor.Assembly.Location);
        AddDirectory(directories, assemblyDir);
        AddDirectory(directories, AppContext.BaseDirectory);
        AddDirectory(directories, Environment.CurrentDirectory);

        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (!string.Equals(assembly.GetName().Name, "PocketRecorder", StringComparison.OrdinalIgnoreCase))
                continue;

            string? location = null;
            try { location = assembly.Location; } catch { }
            AddPathValue(directories, location);
        }

        if (directories.Count == 0)
            return fileNames.ToArray();

        return directories
            .SelectMany(directory => fileNames.Select(fileName => Path.Combine(directory, fileName)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void AddPathLikeValues(object source, string label)
    {
        foreach (PropertyInfo property in source.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length != 0)
                continue;

            string name = property.Name;
            if (!IsPathLikeName(name))
                continue;

            object? value;
            try { value = property.GetValue(source); }
            catch { continue; }

            AddPathValue(_extraSearchDirectories, value?.ToString());
        }

        if (_extraSearchDirectories.Count > 0)
        {
            Plugin.Log?.Info($"[NativeRecorder] Native search directories from {label}: {string.Join("; ", _extraSearchDirectories.Distinct(StringComparer.OrdinalIgnoreCase))}");
        }
    }

    private static bool LooksLikePocketRecorderPlugin(object plugin)
    {
        string? internalName = TryGetPropertyValue(plugin, "InternalName")?.ToString();
        string? name = TryGetPropertyValue(plugin, "Name")?.ToString();
        return string.Equals(internalName, "PocketRecorder", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(name, "Pocket Recorder", StringComparison.OrdinalIgnoreCase);
    }

    private static object? TryGetPropertyValue(object source, string propertyName)
    {
        PropertyInfo? property = source.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
        if (property == null || property.GetIndexParameters().Length != 0)
            return null;

        try { return property.GetValue(source); }
        catch { return null; }
    }

    private static bool IsPathLikeName(string name)
        => name.Contains("Path", StringComparison.OrdinalIgnoreCase) ||
           name.Contains("Directory", StringComparison.OrdinalIgnoreCase) ||
           name.Contains("Location", StringComparison.OrdinalIgnoreCase) ||
           name.Contains("Assembly", StringComparison.OrdinalIgnoreCase) ||
           name.Contains("Source", StringComparison.OrdinalIgnoreCase);

    private static void AddPathValue(List<string> directories, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        string expanded = Environment.ExpandEnvironmentVariables(value.Trim());
        if (expanded.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            return;

        try
        {
            if (File.Exists(expanded))
            {
                AddDirectory(directories, Path.GetDirectoryName(expanded));
                return;
            }

            if (Directory.Exists(expanded))
            {
                AddDirectory(directories, expanded);
                return;
            }

            string? directory = Path.GetDirectoryName(expanded);
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
                AddDirectory(directories, directory);
        }
        catch
        {
        }
    }

    private static void AddDirectory(List<string> directories, string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            return;

        string fullPath;
        try { fullPath = Path.GetFullPath(directory); }
        catch { return; }

        if (!Directory.Exists(fullPath))
            return;

        if (!directories.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
            directories.Add(fullPath);
    }
}
