using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace Recorder.Localization;

internal static class Loc
{
    private static readonly Dictionary<string, string> Empty = new(StringComparer.Ordinal);

    private static Dictionary<string, string> _current = Empty;
    private static Dictionary<string, string> _fallback = Empty;
    private static AppLanguage _effectiveLanguage = AppLanguage.English;

    public static AppLanguage EffectiveLanguage => _effectiveLanguage;

    /// <summary>
    /// Loads locale resources for the given language. When <see cref="AppLanguage.Auto"/>,
    /// <paramref name="autoLanguageCode"/> (typically Dalamud UiLanguage) is used to resolve the effective language.
    /// English is always loaded as the fallback.
    /// </summary>
    public static void Initialize(AppLanguage language, string? autoLanguageCode = null)
    {
        _fallback = LoadResource("en") ?? Empty;

        AppLanguage effective = language == AppLanguage.Auto
            ? ResolveAutoLanguage(autoLanguageCode)
            : language;

        _effectiveLanguage = effective;
        _current = effective == AppLanguage.English
            ? _fallback
            : (LoadResource(GetResourceName(effective)) ?? _fallback);
    }

    /// <summary>Returns the translated string for the given key, or the key itself if not found.</summary>
    public static string T(string key)
    {
        if (_current.TryGetValue(key, out string? value) && !string.IsNullOrEmpty(value))
            return value;

        if (_fallback.TryGetValue(key, out value) && !string.IsNullOrEmpty(value))
            return value;

        return key;
    }

    /// <summary>Returns the formatted translated string for the given key.</summary>
    public static string T(string key, params object[] args)
    {
        string template = T(key);
        try
        {
            return args.Length == 0 ? template : string.Format(template, args);
        }
        catch
        {
            return template;
        }
    }

    /// <summary>Convenience helper for on/off labels.</summary>
    public static string OnOff(bool enabled) => T(enabled ? "General.On" : "General.Off");

    private static AppLanguage ResolveAutoLanguage(string? code)
    {
        return (code ?? string.Empty).ToLowerInvariant() switch
        {
            "ja" => AppLanguage.Japanese,
            "zh" or "zh-cn" => AppLanguage.ChineseSimplified,
            "zh-tw" => AppLanguage.ChineseTraditional,
            _ => AppLanguage.English,
        };
    }

    private static string GetResourceName(AppLanguage language)
    {
        return language switch
        {
            AppLanguage.Japanese => "ja",
            AppLanguage.ChineseSimplified => "zh-CN",
            AppLanguage.ChineseTraditional => "zh-TW",
            _ => "en",
        };
    }

    private static Dictionary<string, string>? LoadResource(string resourceName)
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            string fullResourceName = $"Recorder.Localization.locales.{resourceName}.json";
            using var stream = assembly.GetManifestResourceStream(fullResourceName);
            if (stream == null)
                return null;

            using var reader = new StreamReader(stream);
            string json = reader.ReadToEnd();
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        }
        catch
        {
            return null;
        }
    }
}
