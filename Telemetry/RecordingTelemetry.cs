using Recorder.Encoding;
using System;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Recorder.Telemetry;

internal sealed record RecordingTelemetryContext(
    int SessionId,
    int DalamudApiLevel,
    string GpuVendor,
    string GpuAdapter,
    string BackendMode,
    string BackendLabel,
    string RequestedCodec,
    string SelectedBackendReason,
    string? NativeProbeReason,
    string NativeNvencSdk = "");

internal readonly record struct RecordingGpuInfo(string Vendor, string AdapterName);

internal static class RecordingTelemetry
{
    private static readonly Regex AdapterFieldRegex = new(@"(?:^|;\s*)adapter=([^;]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex NativeSourceAdapterRegex = new(@"source\s+(?:NVIDIA|AMD|Intel)\s+adapter=([^,;]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DxgiAdapterRegex = new(@"#\d+:\s*name=""([^""]+)"",\s*vendor=0x([0-9a-fA-F]+)", RegexOptions.Compiled);

    public static int GetDalamudApiLevel()
    {
        try
        {
            object pluginInterface = Plugin.PluginInterface;
            object? manifest = pluginInterface.GetType().GetProperty("Manifest")?.GetValue(pluginInterface);
            object? apiLevel = manifest?.GetType().GetProperty("DalamudApiLevel")?.GetValue(manifest);
            if (apiLevel != null)
                return Convert.ToInt32(apiLevel);
        }
        catch
        {
        }

        try
        {
            string manifestPath = Path.Combine(
                Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty,
                "PocketRecorder.json");
            if (!File.Exists(manifestPath))
                return 0;

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            return document.RootElement.TryGetProperty("DalamudApiLevel", out JsonElement value) &&
                   value.TryGetInt32(out int apiLevel)
                ? apiLevel
                : 0;
        }
        catch
        {
            return 0;
        }
    }

    public static RecordingGpuInfo DetectGpu(
        string? nativeProbeReason,
        string? selectedBackendReason,
        string? backendLabel,
        string? requestedCodec,
        string? gameGraphicsDevice = null)
    {
        string? adapterName = ExtractAdapterName(nativeProbeReason);

        foreach (string? candidate in new[] { gameGraphicsDevice, backendLabel, selectedBackendReason, nativeProbeReason, adapterName, requestedCodec })
        {
            string vendor = DetectVendorFromText(candidate);
            if (!IsUnknown(vendor))
                return new RecordingGpuInfo(vendor, ExtractAdapterName(gameGraphicsDevice) ?? adapterName ?? string.Empty);
        }

        string nativeDiagnostics = NativeRecorderRuntimeManager.GetDiagnosticsReport();
        foreach (Match match in DxgiAdapterRegex.Matches(nativeDiagnostics))
        {
            string vendor = VendorFromId(match.Groups[2].Value);
            if (IsUnknown(vendor))
                continue;

            return new RecordingGpuInfo(vendor, match.Groups[1].Value);
        }

        return new RecordingGpuInfo("unknown", adapterName ?? string.Empty);
    }

    public static string BackendMode(string backendId, string? backendLabel)
    {
        if (backendId.StartsWith("native-", StringComparison.OrdinalIgnoreCase) ||
            ContainsAny(backendLabel, "NativeRecorder", "NVENC", "AMF", "QSV", "oneVPL"))
        {
            return "native";
        }

        return "ffmpeg";
    }

    private static string? ExtractAdapterName(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        Match match = AdapterFieldRegex.Match(text);
        if (match.Success)
        {
            string value = match.Groups[1].Value.Trim();
            return IsNone(value) ? null : value;
        }

        match = NativeSourceAdapterRegex.Match(text);
        if (match.Success)
        {
            string value = match.Groups[1].Value.Trim();
            return IsNone(value) ? null : value;
        }

        string trimmed = text.Trim();
        if (trimmed.Length <= 120 &&
            !trimmed.Contains(';') &&
            !trimmed.Contains('=') &&
            !trimmed.Contains("NativeRecorder", StringComparison.OrdinalIgnoreCase) &&
            !trimmed.Contains("FFmpeg", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        return null;
    }

    private static string DetectVendorFromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "unknown";

        if (ContainsAny(text, "vendor=0x10DE", "nvidia", "nvenc", "geforce"))
            return "nvidia";
        if (ContainsAny(text, "vendor=0x1002", "amd", "amf", "radeon", "advanced micro devices"))
            return "amd";
        if (ContainsAny(text, "vendor=0x8086", "intel", "qsv", "onevpl", "iris", "uhd graphics", "arc graphics"))
            return "intel";

        return "unknown";
    }

    private static string VendorFromId(string vendorId)
        => vendorId.ToUpperInvariant() switch
        {
            "10DE" => "nvidia",
            "1002" => "amd",
            "8086" => "intel",
            _ => "unknown",
        };

    private static bool ContainsAny(string? text, params string[] needles)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        foreach (string needle in needles)
        {
            if (text.Contains(needle, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool IsUnknown(string vendor)
        => string.IsNullOrWhiteSpace(vendor) ||
           string.Equals(vendor, "unknown", StringComparison.OrdinalIgnoreCase);

    private static bool IsNone(string value)
        => value.Length == 0 ||
           string.Equals(value, "<none>", StringComparison.OrdinalIgnoreCase);
}
