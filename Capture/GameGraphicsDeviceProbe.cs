using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using System;
using System.Runtime.InteropServices;

namespace Recorder.Capture;

internal static unsafe class GameGraphicsDeviceProbe
{
    public static GameGraphicsDeviceProbeResult Probe(string reason)
    {
        IntPtr swapChainPtr;
        try
        {
            Device* gameDevice = Device.Instance();
            if (gameDevice == null)
                return GameGraphicsDeviceProbeResult.Unavailable("Device.Instance() returned null", reason);

            SwapChain* gameSwapChain = gameDevice->SwapChain;
            if (gameSwapChain == null)
                return GameGraphicsDeviceProbeResult.Unavailable("Device->SwapChain is null", reason);

            swapChainPtr = (IntPtr)gameSwapChain->DXGISwapChain;
            if (swapChainPtr == IntPtr.Zero)
                return GameGraphicsDeviceProbeResult.Unavailable("SwapChain->DXGISwapChain is null", reason);
        }
        catch (Exception ex)
        {
            return GameGraphicsDeviceProbeResult.Unavailable($"game swapchain probe exception: {ex.Message}", reason);
        }

        IntPtr devicePtr = IntPtr.Zero;
        try
        {
            if (!D3D11InteropHelpers.TryGetD3D11DeviceFromSwapChain(swapChainPtr, out devicePtr, out string deviceError))
                return GameGraphicsDeviceProbeResult.Unavailable(deviceError, reason, swapChainPtr);

            if (!D3D11InteropHelpers.TryGetAdapterInfoFromD3D11Device(devicePtr, out D3D11AdapterInfo adapterInfo, out string adapterError))
                return GameGraphicsDeviceProbeResult.Unavailable(adapterError, reason, swapChainPtr);

            return GameGraphicsDeviceProbeResult.Success(adapterInfo, reason, swapChainPtr);
        }
        catch (Exception ex)
        {
            return GameGraphicsDeviceProbeResult.Unavailable($"D3D11 device probe exception: {ex.Message}", reason, swapChainPtr);
        }
        finally
        {
            if (devicePtr != IntPtr.Zero)
                Marshal.Release(devicePtr);
        }
    }
}

internal readonly record struct GameGraphicsDeviceProbeResult(
    bool Available,
    D3D11AdapterInfo Adapter,
    string Reason,
    string ProbeReason,
    long SwapChainAddress)
{
    public string Vendor => Available ? Adapter.Vendor : "unknown";

    public string DiagnosticSummary
    {
        get
        {
            string swapChain = SwapChainAddress == 0 ? "<none>" : $"0x{SwapChainAddress:X}";
            return Available
                ? $"available=true, {Adapter.DiagnosticSummary}, swapChain={swapChain}, reason={ValueOrNone(ProbeReason)}"
                : $"available=false, reason={ValueOrNone(Reason)}, swapChain={swapChain}, probeReason={ValueOrNone(ProbeReason)}";
        }
    }

    public static GameGraphicsDeviceProbeResult Success(D3D11AdapterInfo adapter, string probeReason, IntPtr swapChainPtr)
        => new(true, adapter, "ok", probeReason, swapChainPtr.ToInt64());

    public static GameGraphicsDeviceProbeResult Unavailable(string reason, string probeReason, IntPtr swapChainPtr = default)
        => new(false, default, reason, probeReason, swapChainPtr.ToInt64());

    private static string ValueOrNone(string? value)
        => string.IsNullOrWhiteSpace(value) ? "<none>" : value;
}
