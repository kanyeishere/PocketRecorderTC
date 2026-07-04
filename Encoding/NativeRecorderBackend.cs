using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Recorder.Capture;
using Recorder.Recording;

namespace Recorder.Encoding;

internal static unsafe class NativeRecorderBackend
{
    private const int ExpectedAbiVersion = 12;
    private const int DXGI_ERROR_WAS_STILL_DRAWING = unchecked((int)0x887A000A);
    private static readonly string[] NativeDllFileNames = ["NativeRecorder.abi12.dll", "NativeRecorder.abi11.dll", "NativeRecorder.abi10.dll", "NativeRecorder.abi9.dll", "NativeRecorder.abi8.dll", "NativeRecorder.abi7.dll", "NativeRecorder.abi6.dll", "NativeRecorder.abi5.dll", "NativeRecorder.abi4.dll", "NativeRecorder.abi3.dll", "NativeRecorder.abi2.dll", "NativeRecorder.dll"];
    private static readonly object Sync = new();
    private static readonly NativeRecorderDllResolver DllResolver = new(NativeDllFileNames);
    private static bool _loaded;
    private static bool _loadAttempted;
    private static IntPtr _library;
    private static string? _loadError;

    private static PrGetAbiVersion? _getAbiVersion;
    private static PrProbe? _probe;
    private static PrGetDiagnosticsReport? _getDiagnosticsReport;
    private static PrCreate? _create;
    private static PrSubmitD3D11SharedTexture? _submitD3D11SharedTexture;
    private static PrSubmitAudio? _submitAudio;
    private static PrStop? _stop;
    private static PrDestroy? _destroy;
    private static PrGetLastError? _getLastError;

    public static void ConfigureFromPluginInterface(object? pluginInterface)
    {
        lock (Sync)
        {
            DllResolver.ConfigureFromPluginInterface(pluginInterface);
        }
    }

    public static NativeRecorderProbeResult Probe()
    {
        lock (Sync)
        {
            if (!EnsureLoadedNoLock())
            {
                string reason = _loadError ?? "NativeRecorder.dll was not found.";
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
                    ? "NativeRecorder D3D11 texture recorder available."
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

    public static NativeRecorderSession Create(
        string outputPath,
        VideoFormat video,
        AudioFormat? audio,
        int bitrateBps,
        int codec)
    {
        lock (Sync)
        {
            if (!EnsureLoadedNoLock())
                throw new InvalidOperationException(_loadError ?? "NativeRecorder.dll was not found.");

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

            return new NativeRecorderSession(handle);
        }
        finally
        {
            Marshal.FreeHGlobal(outputPathPtr);
        }
    }

    public static bool SubmitD3D11SharedTexture(
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

    public static void SubmitAudio(IntPtr recorder, byte[] data, int byteCount, long timestampHns)
    {
        fixed (byte* dataPtr = data)
        {
            int hr = _submitAudio!(recorder, dataPtr, byteCount, timestampHns);
            ThrowIfFailed(hr, "NativeRecorder audio submit failed");
        }
    }

    public static void Stop(IntPtr recorder)
    {
        if (recorder == IntPtr.Zero)
            return;

        int hr = _stop!(recorder);
        ThrowIfFailed(hr, "NativeRecorder stop failed");
    }

    public static void Destroy(IntPtr recorder)
    {
        if (recorder == IntPtr.Zero)
            return;

        _destroy!(recorder);
    }

    private static bool EnsureLoadedNoLock()
    {
        if (_loaded)
            return true;

        if (_loadAttempted)
            return false;

        _loadAttempted = true;

        string[] candidates = DllResolver.BuildCandidates(typeof(NativeRecorderBackend));
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
                _loadError = $"Failed to load NativeRecorder.dll: {candidate}. {ex.Message}";
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
            _loadError = null;
            TryGetExport("pr_get_diagnostics_report", out _getDiagnosticsReport);
            Plugin.Log?.Info($"[NativeRecorder] Loaded native DLL: {candidate} (ABI {ExpectedAbiVersion}, NvEncoderD3D11/libavformat preferred, AMF/libavformat AMD path available)");
            return true;
        }

        if (failedCandidates.Count > 0)
        {
            _loadError ??= "Failed to load NativeRecorder.dll. " +
                $"Failed candidates: {string.Join("; ", failedCandidates)}";
        }
        else
        {
            _loadError = "NativeRecorder.dll was not found. " +
                $"Searched: {string.Join("; ", missingCandidates.Count > 0 ? missingCandidates : candidates)}";
        }
        return false;
    }

    private static bool TryGetExport<TDelegate>(string name, out TDelegate? value)
        where TDelegate : Delegate
    {
        value = null;
        if (!NativeLibrary.TryGetExport(_library, name, out IntPtr proc) || proc == IntPtr.Zero)
            return false;

        value = Marshal.GetDelegateForFunctionPointer<TDelegate>(proc);
        return true;
    }

    private static string NativeString(byte* bytes, int capacity)
    {
        int length = 0;
        while (length < capacity && bytes[length] != 0)
            length++;

        return System.Text.Encoding.UTF8.GetString(bytes, length);
    }

    private static string BuildProbeDiagnostics(
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

    private static string BuildManagedDiagnostics(string reason)
    {
        var parts = new List<string>
        {
            $"abi=not-loaded/{ExpectedAbiVersion}",
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

    private static string GetNativeDiagnosticsReportNoLock()
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

    private static void ThrowIfFailed(int hr, string prefix)
    {
        if (hr == 0)
            return;

        string detail = GetLastStatus();
        if (!string.IsNullOrWhiteSpace(detail))
            throw new InvalidOperationException($"{prefix}: 0x{hr:X8}. {detail}");

        throw new InvalidOperationException($"{prefix}: 0x{hr:X8}.");
    }

    public static string GetLastStatus()
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

}
