using System;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Recorder.Telemetry;

internal static class PocketBackendClient
{
    private const string BackendBaseUrl = "http://129.226.146.196:9898";

    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(5),
    };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static Configuration? _configuration;

    public static void Configure(Configuration configuration)
    {
        _configuration = configuration;
    }

    public static void QueueHeartbeat(string eventName, object? metadata = null)
    {
        Configuration? configuration = _configuration;
        if (!CanSendHeartbeat(configuration))
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                await PostJsonAsync(configuration!, "/api/heartbeat", new
                {
                    app = "recorder",
                    installId = configuration!.InstallId,
                    version = GetVersion(),
                    @event = eventName,
                    metadata,
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                SuppressTelemetryFailure(ex);
            }
        });
    }

    public static void QueueDiagnostics(string kind, string fileName, string report, string message, object? metadata = null)
    {
        Configuration? configuration = _configuration;
        if (!CanSendDiagnostics(configuration))
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                await PostJsonAsync(configuration!, "/api/diagnostics", new
                {
                    app = "recorder",
                    installId = configuration!.InstallId,
                    version = GetVersion(),
                    kind,
                    fileName,
                    message,
                    report,
                    metadata,
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                SuppressTelemetryFailure(ex);
            }
        });
    }

    public static void QueueRecordingFinished(
        RecordingTelemetryContext context,
        TimeSpan duration,
        bool saved,
        string finalFrameDiagnostics)
    {
        Configuration? configuration = _configuration;
        if (!CanSendDiagnostics(configuration))
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                await PostJsonAsync(configuration!, "/api/recordings", new
                {
                    app = "recorder",
                    installId = configuration!.InstallId,
                    version = GetVersion(),
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
                    cpuName = context.CpuName,
                    totalMemoryMB = context.TotalMemoryMB,
                    recordingBackendMode = context.RecordingBackendMode,
                    videoBitrate = context.VideoBitrate,
                    targetFps = context.TargetFps,
                    sourceWidth = context.SourceWidth,
                    sourceHeight = context.SourceHeight,
                    outputWidth = context.OutputWidth,
                    outputHeight = context.OutputHeight,
                    videoOutputScaleMode = context.VideoOutputScaleMode,
                    includeOverlay = context.IncludeOverlay,
                    useHardwareEncoder = context.UseHardwareEncoder,
                    encoderPreset = context.EncoderPreset,
                    audioCaptureMode = context.AudioCaptureMode,
                    saved,
                    durationMs = (long)Math.Max(0, duration.TotalMilliseconds),
                    finalFrameDiagnostics,
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                SuppressTelemetryFailure(ex);
            }
        });
    }

    private static bool CanSendHeartbeat(Configuration? configuration)
        => configuration is not null &&
           !string.IsNullOrWhiteSpace(configuration.InstallId);

    private static bool CanSendDiagnostics(Configuration? configuration)
        => configuration is { EnablePocketBackendTelemetry: true } &&
           !string.IsNullOrWhiteSpace(configuration.InstallId);

    private static async Task PostJsonAsync(Configuration configuration, string path, object payload)
    {
        Uri endpoint = BuildEndpoint(path);
        string json = JsonSerializer.Serialize(payload, JsonOptions);
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await HttpClient.PostAsync(endpoint, content).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    private static Uri BuildEndpoint(string path)
    {
        string normalizedBase = BackendBaseUrl.EndsWith("/", StringComparison.Ordinal) ? BackendBaseUrl : BackendBaseUrl + "/";
        return new Uri(new Uri(normalizedBase), path.TrimStart('/'));
    }

    private static string GetVersion()
        => Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";

    private static void SuppressTelemetryFailure(Exception _)
    {
        // Telemetry is best-effort and must not leave traces in plugin logs.
    }
}
