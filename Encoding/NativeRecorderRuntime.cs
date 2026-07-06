using Recorder.Capture;
using Recorder.Recording;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace Recorder.Encoding;

internal static unsafe class NativeRecorderRuntimeManager
{
    private static readonly NativeRecorderRuntime Runtime = new(
        "abi13",
        "NativeRecorder ABI13",
        [
            "NativeRecorder.abi13.dll",
            "NativeRecorder.dll",
        ],
        "single ABI13 native DLL; NVENC is built with SDK 12.x for lower driver requirements.");

    public static NativeRecorderRuntime Default => Runtime;

    public static void ConfigureFromPluginInterface(object? pluginInterface)
        => Runtime.ConfigureFromPluginInterface(pluginInterface);

    public static string GetDiagnosticsReport()
        => Runtime.GetDiagnosticsReport(loadIfNeeded: true);

    public static string GetNvencSdkSummary(NativeRecorderRuntime? runtime = null)
        => (runtime ?? Runtime).GetNvencSdkSummary();
}

internal sealed unsafe class NativeRecorderRuntime
{
    private const int ExpectedAbiVersion = 13;
    private const int DXGI_ERROR_WAS_STILL_DRAWING = unchecked((int)0x887A000A);

    private readonly object _sync = new();
    private readonly NativeRecorderDllResolver _dllResolver;
    private readonly string[] _fileNames;
    private readonly string _loadOrderReason;

    private bool _loaded;
    private bool _loadAttempted;
    private IntPtr _library;
    private string? _loadedPath;
    private string? _loadError;

    private PrGetAbiVersion? _getAbiVersion;
    private PrProbe? _probe;
    private PrGetDiagnosticsReport? _getDiagnosticsReport;
    private PrCreate? _create;
    private PrSubmitD3D11SharedTexture? _submitD3D11SharedTexture;
    private PrSubmitAudio? _submitAudio;
    private PrStop? _stop;
    private PrDestroy? _destroy;
    private PrGetLastError? _getLastError;

    public NativeRecorderRuntime(
        string id,
        string displayName,
        string[] fileNames,
        string loadOrderReason)
    {
        Id = id;
        DisplayName = displayName;
        _fileNames = fileNames;
        _loadOrderReason = loadOrderReason;
        _dllResolver = new NativeRecorderDllResolver(fileNames);
    }

    public string Id { get; }
    public string DisplayName { get; }
    public string? LoadedPath => _loadedPath;

    public void ConfigureFromPluginInterface(object? pluginInterface)
    {
        lock (_sync)
            _dllResolver.ConfigureFromPluginInterface(pluginInterface);
    }

    public NativeRecorderProbeResult Probe()
    {
        lock (_sync)
        {
            if (!EnsureLoadedNoLock())
            {
                string reason = _loadError ?? $"{DisplayName} native DLL was not found.";
                return NativeRecorderProbeResult.Unavailable(reason, BuildManagedDiagnostics(reason));
            }

            try
            {
                int abi = _getAbiVersion!.Invoke();
                if (abi != ExpectedAbiVersion)
                {
                    string reason = $"NativeRecorder ABI mismatch: expected {ExpectedAbiVersion}, got {abi}.";
                    return NativeRecorderProbeResult.Unavailable(reason, BuildManagedDiagnostics(reason));
                }

                NativeProbeInfo info = default;
                int hr = _probe!(ref info);
                if (hr != 0)
                {
                    string reason = $"NativeRecorder probe failed: 0x{hr:X8}.";
                    return NativeRecorderProbeResult.Unavailable(reason, BuildProbeDiagnostics(abi, info, reason));
                }

                string adapterName = NativeString(info.AdapterName, 128);
                string nativeMessage = NativeString(info.Message, 256);

                if (info.IsSupportedAdapter == 0 || info.SupportsD3D11TextureInput == 0)
                {
                    string reason = string.IsNullOrWhiteSpace(nativeMessage)
                        ? "NativeRecorder probe reported unavailable."
                        : nativeMessage;
                    return NativeRecorderProbeResult.Unavailable(
                        reason,
                        BuildProbeDiagnostics(abi, info, reason, adapterName, nativeMessage));
                }

                string availableReason = string.IsNullOrWhiteSpace(adapterName)
                    ? $"{DisplayName} D3D11 texture recorder available."
                    : adapterName;
                return NativeRecorderProbeResult.Available(
                    availableReason,
                    BuildProbeDiagnostics(abi, info, nativeMessage, adapterName, nativeMessage));
            }
            catch (Exception ex)
            {
                string reason = $"NativeRecorder probe exception: {ex.Message}";
                return NativeRecorderProbeResult.Unavailable(reason, BuildManagedDiagnostics(reason));
            }
        }
    }

    public NativeRecorderProbeResult ProbeRuntime()
    {
        lock (_sync)
        {
            if (!EnsureLoadedNoLock())
            {
                string reason = _loadError ?? $"{DisplayName} native DLL was not found.";
                return NativeRecorderProbeResult.Unavailable(reason, BuildManagedDiagnostics(reason));
            }

            try
            {
                int abi = _getAbiVersion!.Invoke();
                if (abi != ExpectedAbiVersion)
                {
                    string reason = $"NativeRecorder ABI mismatch: expected {ExpectedAbiVersion}, got {abi}.";
                    return NativeRecorderProbeResult.Unavailable(reason, BuildManagedDiagnostics(reason));
                }

                string diagnostics = GetNativeDiagnosticsReportNoLock();
                return NativeRecorderProbeResult.Available(
                    $"{DisplayName} native DLL loaded.",
                    string.IsNullOrWhiteSpace(diagnostics) ? BuildManagedDiagnostics("diagnostics unavailable") : diagnostics);
            }
            catch (Exception ex)
            {
                string reason = $"NativeRecorder runtime probe exception: {ex.Message}";
                return NativeRecorderProbeResult.Unavailable(reason, BuildManagedDiagnostics(reason));
            }
        }
    }

    public NativeRecorderSession Create(
        string outputPath,
        VideoFormat video,
        AudioFormat? audio,
        int bitrateBps,
        int codec)
    {
        lock (_sync)
        {
            if (!EnsureLoadedNoLock())
                throw new InvalidOperationException(_loadError ?? $"{DisplayName} native DLL was not found.");

            int abi = _getAbiVersion!.Invoke();
            if (abi != ExpectedAbiVersion)
                throw new InvalidOperationException($"NativeRecorder ABI mismatch: expected {ExpectedAbiVersion}, got {abi}.");
        }

        IntPtr outputPathPtr = Marshal.StringToHGlobalUni(outputPath);
        try
        {
            NativeVideoConfig videoConfig = new()
            {
                Width = video.Width,
                Height = video.Height,
                Fps = Math.Max(1, video.Fps),
                BitrateBps = bitrateBps,
                Codec = codec,
                PixelFormat = ToNativePixelFormat(video.PixelFormat),
                OutputWidth = Math.Max(1, video.OutputWidth),
                OutputHeight = Math.Max(1, video.OutputHeight),
                OutputPath = outputPathPtr,
            };

            NativeAudioConfig audioConfig = audio == null
                ? default
                : new NativeAudioConfig
                {
                    Enabled = 1,
                    SampleRate = audio.SampleRate,
                    Channels = audio.Channels,
                    BitsPerSample = audio.BitsPerSample,
                    IsFloat = audio.IsFloat ? 1 : 0,
                };

            IntPtr handle;
            int hr = _create!(ref videoConfig, ref audioConfig, out handle);
            ThrowIfFailed(hr, "NativeRecorder create failed");
            if (handle == IntPtr.Zero)
                throw new InvalidOperationException("NativeRecorder create returned a null handle.");

            return new NativeRecorderSession(this, handle);
        }
        finally
        {
            Marshal.FreeHGlobal(outputPathPtr);
        }
    }

    public string GetDiagnosticsReport(bool loadIfNeeded = true)
    {
        lock (_sync)
        {
            if (loadIfNeeded)
            {
                if (!EnsureLoadedNoLock())
                    return BuildManagedDiagnostics(_loadError ?? $"{DisplayName} native DLL was not found.");
            }
            else if (!_loaded)
            {
                return string.Empty;
            }

            try
            {
                int abi = _getAbiVersion!.Invoke();
                if (abi != ExpectedAbiVersion)
                    return BuildManagedDiagnostics($"NativeRecorder ABI mismatch: expected {ExpectedAbiVersion}, got {abi}.");

                return GetNativeDiagnosticsReportNoLock();
            }
            catch (Exception ex)
            {
                return BuildManagedDiagnostics($"NativeRecorder diagnostics exception: {ex.Message}");
            }
        }
    }

    public string GetNvencSdkSummary()
    {
        string report = GetDiagnosticsReport();
        string buildSdk = ExtractReportField(report, "nvencBuildSdk");
        string buildApi = ExtractReportField(report, "nvencBuildApi");
        string driverApi = ExtractReportField(report, "nvencDriverApi");
        string loadedDll = Path.GetFileName(_loadedPath) ?? string.Empty;

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(loadedDll))
            parts.Add($"dll={loadedDll}");
        if (!string.IsNullOrWhiteSpace(buildSdk))
            parts.Add($"sdk={buildSdk}");
        if (!string.IsNullOrWhiteSpace(buildApi))
            parts.Add($"api={buildApi}");
        if (!string.IsNullOrWhiteSpace(driverApi))
            parts.Add($"driverApi={driverApi}");

        return parts.Count == 0 ? string.Empty : string.Join(", ", parts);
    }

    public bool SubmitD3D11SharedTexture(
        IntPtr recorder,
        IntPtr d3d11Device,
        IntPtr sharedHandle,
        int dxgiFormat,
        long timestampHns)
    {
        int hr = _submitD3D11SharedTexture!(recorder, d3d11Device, sharedHandle, dxgiFormat, timestampHns);
        if (hr == DXGI_ERROR_WAS_STILL_DRAWING)
            return false;

        ThrowIfFailed(hr, "NativeRecorder texture submit failed");
        return true;
    }

    public void SubmitAudio(IntPtr recorder, byte[] data, int byteCount, long timestampHns)
    {
        fixed (byte* dataPtr = data)
        {
            int hr = _submitAudio!(recorder, dataPtr, byteCount, timestampHns);
            ThrowIfFailed(hr, "NativeRecorder audio submit failed");
        }
    }

    public void Stop(IntPtr recorder)
    {
        if (recorder == IntPtr.Zero)
            return;

        int hr = _stop!(recorder);
        ThrowIfFailed(hr, "NativeRecorder stop failed");
    }

    public void Destroy(IntPtr recorder)
    {
        if (recorder == IntPtr.Zero)
            return;

        _destroy!(recorder);
    }

    public string GetLastStatus()
    {
        if (_getLastError == null)
            return string.Empty;

        byte[] buffer = new byte[8192];
        fixed (byte* bufferPtr = buffer)
        {
            if (_getLastError(bufferPtr, buffer.Length) != 0)
                return string.Empty;
        }

        int length = Array.IndexOf(buffer, (byte)0);
        if (length < 0)
            length = buffer.Length;

        return System.Text.Encoding.UTF8.GetString(buffer, 0, length);
    }

    private bool EnsureLoadedNoLock()
    {
        if (_loaded)
            return true;

        if (_loadAttempted)
            return false;

        _loadAttempted = true;

        string[] candidates = _dllResolver.BuildCandidates(typeof(NativeRecorderRuntime), _fileNames);
        List<string> missingCandidates = [];
        List<string> failedCandidates = [];

        foreach (string candidate in candidates)
        {
            if (!File.Exists(candidate))
            {
                missingCandidates.Add(candidate);
                continue;
            }

            try
            {
                _library = NativeLibrary.Load(candidate);
            }
            catch (Exception ex)
            {
                failedCandidates.Add($"{candidate} ({ex.Message})");
                _loadError = $"Failed to load {DisplayName}: {candidate}. {ex.Message}";
                continue;
            }

            if (!TryGetExport("pr_get_abi_version", out _getAbiVersion) ||
                !TryGetExport("pr_probe", out _probe) ||
                !TryGetExport("pr_create", out _create) ||
                !TryGetExport("pr_submit_d3d11_shared_texture", out _submitD3D11SharedTexture) ||
                !TryGetExport("pr_submit_audio", out _submitAudio) ||
                !TryGetExport("pr_stop", out _stop) ||
                !TryGetExport("pr_destroy", out _destroy) ||
                !TryGetExport("pr_get_last_error", out _getLastError))
            {
                NativeLibrary.Free(_library);
                _library = IntPtr.Zero;
                _loadError = $"NativeRecorder exports are incomplete in {candidate}.";
                continue;
            }

            _loaded = true;
            _loadedPath = candidate;
            _loadError = null;
            TryGetExport("pr_get_diagnostics_report", out _getDiagnosticsReport);
            Plugin.Log?.Info($"[NativeRecorder] Loaded {DisplayName}: {candidate} (ABI {ExpectedAbiVersion}, {_loadOrderReason}, NvEncoderD3D11/libavformat preferred, AMF/libavformat AMD path available, oneVPL/libavformat Intel path available)");
            return true;
        }

        if (failedCandidates.Count > 0)
        {
            _loadError ??= $"Failed to load {DisplayName}. " +
                $"Failed candidates: {string.Join("; ", failedCandidates)}";
        }
        else
        {
            _loadError = $"{DisplayName} native DLL was not found. " +
                $"Searched: {string.Join("; ", missingCandidates.Count > 0 ? missingCandidates : candidates)}";
        }
        return false;
    }

    private bool TryGetExport<TDelegate>(string name, out TDelegate? value)
        where TDelegate : Delegate
    {
        value = null;
        if (!NativeLibrary.TryGetExport(_library, name, out IntPtr proc) || proc == IntPtr.Zero)
            return false;

        value = Marshal.GetDelegateForFunctionPointer<TDelegate>(proc);
        return true;
    }

    private string BuildProbeDiagnostics(
        int abi,
        NativeProbeInfo info,
        string reason,
        string? adapterName = null,
        string? nativeMessage = null)
    {
        adapterName ??= NativeString(info.AdapterName, 128);
        nativeMessage ??= NativeString(info.Message, 256);

        var parts = new List<string>
        {
            $"abi={abi}/{ExpectedAbiVersion}",
            $"runtime={Id}",
            $"loadedDll={ValueOrNone(Path.GetFileName(_loadedPath))}",
            $"nativeDllOrder={ValueOrNone(_loadOrderReason)}",
            $"adapter={ValueOrNone(adapterName)}",
            $"isSupportedAdapter={info.IsSupportedAdapter}",
            $"supportsD3D11TextureInput={info.SupportsD3D11TextureInput}",
            $"message={ValueOrNone(nativeMessage)}",
        };

        string nativeReport = GetNativeDiagnosticsReportNoLock();
        if (!string.IsNullOrWhiteSpace(nativeReport))
            parts.Add($"nativeReport={nativeReport}");

        string lastStatus = GetLastStatus();
        if (!string.IsNullOrWhiteSpace(lastStatus) &&
            !string.Equals(lastStatus, nativeMessage, StringComparison.Ordinal))
        {
            parts.Add($"lastStatus={lastStatus}");
        }

        if (!string.IsNullOrWhiteSpace(reason) &&
            !string.Equals(reason, nativeMessage, StringComparison.Ordinal))
        {
            parts.Add($"reason={reason}");
        }

        return string.Join("; ", parts);
    }

    private string BuildManagedDiagnostics(string reason)
    {
        var parts = new List<string>
        {
            $"abi=not-loaded/{ExpectedAbiVersion}",
            $"runtime={Id}",
            $"nativeDllOrder={ValueOrNone(_loadOrderReason)}",
            $"reason={ValueOrNone(reason)}",
        };

        string nativeReport = GetNativeDiagnosticsReportNoLock();
        if (!string.IsNullOrWhiteSpace(nativeReport))
            parts.Add($"nativeReport={nativeReport}");

        string lastStatus = GetLastStatus();
        if (!string.IsNullOrWhiteSpace(lastStatus))
            parts.Add($"lastStatus={lastStatus}");

        return string.Join("; ", parts);
    }

    private string GetNativeDiagnosticsReportNoLock()
    {
        if (_getDiagnosticsReport == null)
            return string.Empty;

        byte[] buffer = new byte[4096];
        fixed (byte* bufferPtr = buffer)
        {
            if (_getDiagnosticsReport(bufferPtr, buffer.Length) != 0)
                return string.Empty;
        }

        int length = Array.IndexOf(buffer, (byte)0);
        if (length < 0)
            length = buffer.Length;

        return System.Text.Encoding.UTF8.GetString(buffer, 0, length);
    }

    private static string NativeString(byte* bytes, int capacity)
    {
        int length = 0;
        while (length < capacity && bytes[length] != 0)
            length++;

        return System.Text.Encoding.UTF8.GetString(bytes, length);
    }

    private static string ExtractReportField(string report, string name)
    {
        if (string.IsNullOrWhiteSpace(report) || string.IsNullOrWhiteSpace(name))
            return string.Empty;

        string needle = name + "=";
        int start = report.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            return string.Empty;

        start += needle.Length;
        int end = start;
        while (end < report.Length && report[end] != ',' && report[end] != ';' && report[end] != '}')
            end++;

        return report[start..end].Trim();
    }

    private static string ValueOrNone(string? value)
        => string.IsNullOrWhiteSpace(value) ? "<none>" : value;

    private static int ToNativePixelFormat(VideoPixelFormat pixelFormat)
    {
        return pixelFormat switch
        {
            VideoPixelFormat.Bgra => 1,
            VideoPixelFormat.Rgba => 2,
            VideoPixelFormat.Nv12 => 3,
            _ => 0,
        };
    }

    private void ThrowIfFailed(int hr, string prefix)
    {
        if (hr == 0)
            return;

        string detail = GetLastStatus();
        if (!string.IsNullOrWhiteSpace(detail))
            throw new InvalidOperationException($"{prefix}: 0x{hr:X8}. {detail}");

        throw new InvalidOperationException($"{prefix}: 0x{hr:X8}.");
    }
}
