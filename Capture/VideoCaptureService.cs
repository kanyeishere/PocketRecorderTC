using Dalamud.Hooking;
using Dalamud.Interface;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using DXGI_FORMAT = TerraFX.Interop.DirectX.DXGI_FORMAT;

namespace Recorder.Capture;

/// <summary>
/// 视频捕获服务。
/// 主方案：Framework.Update + FFXIVClientStructs BackBuffer（后台可用，不阻塞渲染线程）
/// 备选方案：Hook IDXGISwapChain::Present（在 Present 前捕获）
/// 最后方案：Windows Graphics Capture API
/// </summary>
internal sealed unsafe class VideoCaptureService : IDisposable
{
    private readonly IUiBuilder _uiBuilder;
    private readonly IGameInteropProvider _gameInterop;
    private readonly IFramework _framework;
    private readonly Action<VideoFrame> _onFrame;

    // D3D11 对象
    private ID3D11Device* _device;
    private readonly IntPtr[] _stagingTextures = new IntPtr[StagingTextureCount];
    private uint _stagingWidth;
    private uint _stagingHeight;
    private DXGI_FORMAT _stagingFormat;
    private IntPtr _stagingDevice;
    private int _stagingWriteIndex;
    private int _stagingReadyCount;
    private ID3D11Multithread* _d3d11Multithread;
    private IntPtr _multithreadContext;
    private bool _multithreadUnavailableLogged;

    // Present Hook（备选）
    private Hook<PresentDelegate>? _presentHook;

    // Windows Graphics Capture（最后方案）
    private WindowGraphicsCapture? _wgcCapture;

    private enum CaptureBackend
    {
        FrameworkUpdate,
        PresentHook,
        WindowsGraphicsCapture,
    }

    // 状态
    private bool _capturing;
    private bool _disposed;
    private int _targetFps = 60;
    private long _lastFrameTicks;
    private readonly Stopwatch _sw = new();
    private int _frameCount;
    private int _skipCount;
    private int _errorCount;
    private int _consecutiveBlackFrames;
    private bool _presentHookEnabled;
    private int _diagnosedBackendMask;
    private CaptureBackend _activeBackend = CaptureBackend.FrameworkUpdate;
    private string _captureMethod = "unknown";

    private const int MaxBlackFramesBeforeFallback = 3;
    private const int StagingTextureCount = 3;
    private const int DXGI_ERROR_WAS_STILL_DRAWING = unchecked((int)0x887A000A);

    private static readonly Guid IID_ID3D11Texture2D = new(0x6F15AAF2, 0xD208, 0x4E89, 0x9A, 0xB4, 0x48, 0x95, 0x35, 0xD3, 0x4F, 0x9C);
    private static readonly Guid IID_ID3D11Device = new(0xdb6f6ddb, 0xac77, 0x4e88, 0x82, 0x53, 0x81, 0x9d, 0xf9, 0xbb, 0xf1, 0x40);
    private static readonly Guid IID_ID3D11Multithread = new(0x9B7E4E00, 0x342C, 0x4106, 0xA1, 0x9F, 0x4F, 0x27, 0x04, 0xF6, 0x89, 0xF0);

    public int CurrentWidth { get; private set; }
    public int CurrentHeight { get; private set; }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int PresentDelegate(IntPtr swapChain, uint syncInterval, uint flags);

    public VideoCaptureService(IUiBuilder uiBuilder, IGameInteropProvider gameInterop, IFramework framework, Action<VideoFrame> onFrame)
    {
        _uiBuilder = uiBuilder;
        _gameInterop = gameInterop;
        _framework = framework;
        _onFrame = onFrame;
    }

    public void Start(int targetFps)
    {
        _targetFps = targetFps;
        _capturing = true;
        _frameCount = 0;
        _skipCount = 0;
        _errorCount = 0;
        _consecutiveBlackFrames = 0;
        _presentHookEnabled = false;
        _diagnosedBackendMask = 0;
        _activeBackend = CaptureBackend.PresentHook;
        _sw.Restart();

        // 默认用 Present Hook 在 Present 前捕获，避免 Framework.Update 读到渲染中间态。
        if (TryInstallPresentHook(enable: true))
        {
            _captureMethod = "PresentHook";
            Plugin.Log!.Info($"[Video] Capture started, targetFps={targetFps}, method=PresentHook");
            return;
        }

        _activeBackend = CaptureBackend.FrameworkUpdate;
        _framework.Update += OnFrameworkUpdate;
        _captureMethod = "FrameworkUpdate";
        Plugin.Log!.Warning("[Video] Present hook unavailable; capture started with FrameworkUpdate fallback.");
    }

    public void Stop()
    {
        _capturing = false;
        _activeBackend = CaptureBackend.FrameworkUpdate;

        // 取消 Framework.Update 订阅
        _framework.Update -= OnFrameworkUpdate;

        // 停止 WGC 后备
        if (_wgcCapture != null)
        {
            _wgcCapture.Stop();
            _wgcCapture.Dispose();
            _wgcCapture = null;
        }

        // 卸载 hook
        if (_presentHook != null)
        {
            if (_presentHookEnabled)
            {
                try { _presentHook.Disable(); } catch { }
                _presentHookEnabled = false;
            }
            _presentHook.Dispose();
            _presentHook = null;
        }

        _sw.Stop();
        Plugin.Log!.Info($"[Video] Capture stopped. frames={_frameCount}, skipped={_skipCount}, errors={_errorCount}, method={_captureMethod}");
    }

    // ──────────────────────────────────────────────────────────
    //  主方案：Framework.Update + FFXIVClientStructs BackBuffer
    // ──────────────────────────────────────────────────────────

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (!_capturing || _activeBackend != CaptureBackend.FrameworkUpdate) return;

        try
        {
            CaptureViaFrameworkUpdate();
        }
        catch (Exception ex)
        {
            _errorCount++;
            if (_errorCount <= 5 || _errorCount % 100 == 0)
                Plugin.Log!.Warning($"[Video] Framework.Update capture failed (#{_errorCount}): {ex.Message}");
        }
    }

    private unsafe void CaptureViaFrameworkUpdate()
    {
        long now = _sw.ElapsedTicks;
        long minInterval = Stopwatch.Frequency / _targetFps;
        if (now - _lastFrameTicks < minInterval) return;
        _lastFrameTicks = now;

        Device* gameDevice = Device.Instance();
        if (gameDevice == null) { _skipCount++; return; }

        SwapChain* gameSwapChain = gameDevice->SwapChain;
        if (gameSwapChain == null) { _skipCount++; return; }

        // 获取 BackBuffer 的 D3D11Texture2D
        Texture* backBufferTex = gameSwapChain->BackBuffer;
        if (backBufferTex == null) { _skipCount++; return; }

        ID3D11Texture2D* backBuffer = (ID3D11Texture2D*)backBufferTex->D3D11Texture2D;
        if (backBuffer == null) { _skipCount++; return; }

        // 通过 SwapChain 的 DXGI 接口获取游戏设备
        IDXGISwapChain* dxgiSwapChain = (IDXGISwapChain*)gameSwapChain->DXGISwapChain;
        if (dxgiSwapChain == null) { _skipCount++; return; }

        ID3D11Device* device = null;
        Guid iidDev = IID_ID3D11Device;
        int hr = dxgiSwapChain->GetDevice(&iidDev, (void**)&device);
        if (hr < 0 || device == null)
        {
            _skipCount++;
            if (_skipCount <= 3)
                Plugin.Log!.Warning($"[Video] SwapChain->GetDevice failed: 0x{hr:X8}");
            return;
        }
        _device = device;

        ID3D11DeviceContext* ctx;
        _device->GetImmediateContext(&ctx);
        if (ctx == null) { _skipCount++; _device->Release(); return; }

        try
        {
            // 注意：BackBuffer 不需要 Release（它是 gameSwapChain 拥有的）
            // 但 GetDevice 返回的 device 需要 Release
            ProcessTexture(ctx, backBuffer, now, releaseDevice: true);
        }
        finally
        {
            ctx->Release();
        }
    }

    // ──────────────────────────────────────────────────────────
    //  备选方案：Present Hook
    // ──────────────────────────────────────────────────────────

    private bool TryInstallPresentHook(bool enable)
    {
        try
        {
            if (_presentHook == null)
            {
                Device* gameDevice = Device.Instance();
                if (gameDevice == null)
                {
                    Plugin.Log!.Warning("[Video] Device.Instance() returned null, cannot hook Present.");
                    return false;
                }

                SwapChain* gameSwapChain = gameDevice->SwapChain;
                if (gameSwapChain == null)
                {
                    Plugin.Log!.Warning("[Video] Device->SwapChain is null, cannot hook Present.");
                    return false;
                }

                IDXGISwapChain* dxgiSwapChain = (IDXGISwapChain*)gameSwapChain->DXGISwapChain;
                if (dxgiSwapChain == null)
                {
                    Plugin.Log!.Warning("[Video] SwapChain->DXGISwapChain is null, cannot hook Present.");
                    return false;
                }

                void** vtable = *(void***)dxgiSwapChain;
                void* presentAddr = vtable[8]; // Present = vtable index 8

                Plugin.Log!.Info($"[Video] Game IDXGISwapChain=0x{(long)dxgiSwapChain:X}, Present addr=0x{(long)presentAddr:X}");

                _presentHook = _gameInterop.HookFromAddress<PresentDelegate>(
                    presentAddr,
                    OnPresentDetour,
                    IGameInteropProvider.HookBackend.Automatic);
            }

            if (enable && !_presentHookEnabled)
            {
                _presentHook.Enable();
                _presentHookEnabled = true;
                _captureMethod = "PresentHook";
                Plugin.Log!.Info("[Video] ✓ Present hook enabled.");
            }
            else if (!enable && _presentHookEnabled)
            {
                _presentHook.Disable();
                _presentHookEnabled = false;
                Plugin.Log!.Info("[Video] ✓ Present hook disabled.");
            }
            else if (!enable)
            {
                Plugin.Log!.Info("[Video] ✓ Present hook installed (disabled).");
            }

            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log!.Error($"[Video] Failed to install Present hook: {ex}");
            return false;
        }
    }

    private int OnPresentDetour(IntPtr swapChainPtr, uint syncInterval, uint flags)
    {
        if (_capturing && _activeBackend == CaptureBackend.PresentHook)
        {
            try
            {
                CaptureBeforePresent(swapChainPtr);
            }
            catch (Exception ex)
            {
                _errorCount++;
                if (_errorCount <= 5 || _errorCount % 100 == 0)
                    Plugin.Log!.Warning($"[Video] Capture in Present hook failed (#{_errorCount}): {ex.Message}");
            }
        }

        return _presentHook!.Original(swapChainPtr, syncInterval, flags);
    }

    private unsafe void CaptureBeforePresent(IntPtr swapChainPtr)
    {
        long now = _sw.ElapsedTicks;
        long minInterval = Stopwatch.Frequency / _targetFps;
        if (now - _lastFrameTicks < minInterval) return;
        _lastFrameTicks = now;

        var swapChain = (IDXGISwapChain*)swapChainPtr;

        // ★ 关键修复：从 SwapChain 获取游戏自己的 D3D11 Device
        // 之前用 _uiBuilder.DeviceHandle 获取的是 ImGui 的设备，跨设备 CopyResource 会产生黑帧
        Guid iidDev = IID_ID3D11Device;
        ID3D11Device* gameDevice = null;
        int hrDev = swapChain->GetDevice(&iidDev, (void**)&gameDevice);
        if (hrDev < 0 || gameDevice == null)
        {
            _skipCount++;
            if (_skipCount <= 3)
                Plugin.Log!.Warning($"[Video] Present: SwapChain->GetDevice failed: 0x{hrDev:X8}");
            return;
        }
        _device = gameDevice;

        ID3D11DeviceContext* ctx;
        _device->GetImmediateContext(&ctx);
        if (ctx == null) { _skipCount++; _device->Release(); return; }

        try
        {
            // 尝试 GetBuffer 获取 backbuffer
            ID3D11Texture2D* backBuffer = null;
            Guid iidTex2D = IID_ID3D11Texture2D;
            int hr = swapChain->GetBuffer(0, &iidTex2D, (void**)&backBuffer);
            bool needRelease = hr >= 0;

            if (hr < 0 || backBuffer == null)
            {
                // fallback: FFXIVClientStructs BackBuffer
                Device* kernelDevice = Device.Instance();
                if (kernelDevice != null && kernelDevice->SwapChain != null && kernelDevice->SwapChain->BackBuffer != null)
                {
                    backBuffer = (ID3D11Texture2D*)kernelDevice->SwapChain->BackBuffer->D3D11Texture2D;
                    needRelease = false;
                    if (_frameCount == 0 && backBuffer != null)
                        Plugin.Log!.Info("[Video] Present: GetBuffer(0) failed, using FFXIVClientStructs BackBuffer");
                }
            }
            else
            {
                if (_frameCount == 0)
                    Plugin.Log!.Info("[Video] Present: ✓ GetBuffer(0) succeeded");
            }

            if (backBuffer == null) { _skipCount++; return; }

            try
            {
                ProcessTexture(ctx, backBuffer, now, releaseDevice: true);
            }
            finally
            {
                if (needRelease) backBuffer->Release();
            }
        }
        finally
        {
            ctx->Release();
        }
    }

    // ──────────────────────────────────────────────────────────
    //  共享：纹理处理
    // ──────────────────────────────────────────────────────────

    /// <summary>
    /// 处理纹理：CopyResource → Map → 读取像素 → 回调
    /// </summary>
    /// <param name="releaseDevice">如果 _device 是通过 GetDevice 获取的（AddRef'd），需要在结束时 Release</param>
    private void ProcessTexture(ID3D11DeviceContext* ctx, ID3D11Texture2D* srcTexture, long timestampTicks, bool releaseDevice = false)
    {
        try
        {
            D3D11_TEXTURE2D_DESC desc;
            srcTexture->GetDesc(&desc);

            uint width = desc.Width;
            uint height = desc.Height;
            DXGI_FORMAT format = desc.Format;
            bool diagnoseThisBackend = ShouldDiagnoseBackend(_activeBackend);

            CurrentWidth = (int)width;
            CurrentHeight = (int)height;

            int bpp = 4;
            if (format != DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM &&
                format != DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM_SRGB &&
                format != DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_TYPELESS &&
                format != DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM &&
                format != DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM_SRGB &&
                format != DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_TYPELESS)
            {
                _skipCount++;
                if (_skipCount <= 3)
                    Plugin.Log!.Warning($"[Video] Unexpected format: {format}, skipping.");
                return;
            }

            if (diagnoseThisBackend)
            {
                Plugin.Log!.Info($"[Video] {_activeBackend} first frame: {width}x{height}, format={format}, " +
                    $"usage={desc.Usage}, bindFlags=0x{desc.BindFlags:X}, cpuAccess=0x{desc.CPUAccessFlags:X}, " +
                    $"miscFlags=0x{desc.MiscFlags:X}, mips={desc.MipLevels}, sample={desc.SampleDesc.Count}/{desc.SampleDesc.Quality}");
            }

            if (!EnsureStagingTextures(width, height, format))
                return;

            ID3D11Texture2D* writeTexture = (ID3D11Texture2D*)_stagingTextures[_stagingWriteIndex];
            ID3D11Texture2D* readTexture = _stagingReadyCount >= StagingTextureCount - 1
                ? (ID3D11Texture2D*)_stagingTextures[(_stagingWriteIndex + 1) % StagingTextureCount]
                : null;

            EnsureD3D11MultithreadProtection(ctx);
            bool d3dLocked = EnterD3D11Multithread();
            bool mappedOk = false;
            bool switchBackendAfterReadback = false;
            VideoFrame? frame = null;

            D3D11_MAPPED_SUBRESOURCE mapped;
            try
            {
                // 先把当前 backbuffer 拷到写入槽，再尝试读较早的槽，避免 Copy 后立刻 Map 阻塞 GPU。
                ctx->CopySubresourceRegion(
                    (ID3D11Resource*)writeTexture,
                    0,
                    0,
                    0,
                    0,
                    (ID3D11Resource*)srcTexture,
                    0,
                    null);

                _stagingWriteIndex = (_stagingWriteIndex + 1) % StagingTextureCount;
                if (_stagingReadyCount < StagingTextureCount)
                    _stagingReadyCount++;

                if (readTexture == null)
                    return;

                int hr = ctx->Map(
                    (ID3D11Resource*)readTexture,
                    0,
                    D3D11_MAP.D3D11_MAP_READ,
                    (uint)D3D11_MAP_FLAG.D3D11_MAP_FLAG_DO_NOT_WAIT,
                    &mapped);
                if (hr < 0)
                {
                    _skipCount++;
                    if (hr == DXGI_ERROR_WAS_STILL_DRAWING)
                    {
                        if (_skipCount <= 3 || _skipCount % 300 == 0)
                            Plugin.Log!.Info($"[Video] Readback not ready; skipped without blocking. skipped={_skipCount}");
                    }
                    else if (_skipCount <= 3)
                    {
                        Plugin.Log!.Warning($"[Video] Map failed: 0x{hr:X8}");
                    }

                    return;
                }

                mappedOk = true;

                {
                    int srcStride = (int)mapped.RowPitch;
                    int dstStride = (int)width * bpp;
                    int dataSize = dstStride * (int)height;

                    byte[] buffer = new byte[dataSize];
                    byte* src = (byte*)mapped.pData;

                    if (srcStride == dstStride)
                    {
                        Marshal.Copy((IntPtr)src, buffer, 0, dataSize);
                    }
                    else
                    {
                        for (int y = 0; y < (int)height; y++)
                        {
                            Marshal.Copy((IntPtr)(src + y * srcStride), buffer, y * dstStride, dstStride);
                        }
                    }

                    bool isEmptyFrame = IsFrameEmpty(buffer, (int)width, (int)height, dstStride);

                    if (diagnoseThisBackend)
                    {
                        DiagnosePixels(buffer, (int)width, (int)height, dstStride);
                    }

                    // RGBA → BGRA 转换
                    bool isRGBA = format == DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM ||
                                  format == DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM_SRGB ||
                                  format == DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_TYPELESS;
                    if (isRGBA)
                    {
                        SwapRedBlue(buffer, (int)width, (int)height, dstStride);
                        if (diagnoseThisBackend)
                            Plugin.Log.Info("[Video] Applied RGBA→BGRA swap.");
                    }

                    // 空帧（RGBA 全 0）通常表示 GPU 读回失败；不要把它送进编码器。
                    if (isEmptyFrame)
                    {
                        _consecutiveBlackFrames++;
                        if (_consecutiveBlackFrames >= MaxBlackFramesBeforeFallback)
                        {
                            Plugin.Log!.Warning($"[Video] {_consecutiveBlackFrames} consecutive empty frames on {_captureMethod}! Switching capture backend.");
                            switchBackendAfterReadback = true;
                        }
                    }
                    else
                    {
                        _consecutiveBlackFrames = 0;

                        long timestampHns = timestampTicks * 10_000_000L / Stopwatch.Frequency;
                        frame = new VideoFrame(buffer, (int)width, (int)height, dstStride, timestampHns);
                    }
                }
            }
            finally
            {
                if (mappedOk)
                    ctx->Unmap((ID3D11Resource*)readTexture, 0);

                if (d3dLocked)
                    LeaveD3D11Multithread();
            }

            if (switchBackendAfterReadback)
            {
                HandleBlackFrameFallback();
                return;
            }

            if (frame != null)
            {
                _onFrame(frame);
                _frameCount++;

                if (_frameCount % 300 == 0)
                    Plugin.Log!.Info($"[Video] {CurrentWidth}x{CurrentHeight} frame #{_frameCount}, method={_captureMethod}");
            }
        }
        finally
        {
            if (releaseDevice && _device != null)
            {
                _device->Release();
                _device = null;
            }
        }
    }

    // ──────────────────────────────────────────────────────────
    //  最后方案：Windows Graphics Capture
    // ──────────────────────────────────────────────────────────

    private void HandleBlackFrameFallback()
    {
        try
        {
            if (_activeBackend == CaptureBackend.FrameworkUpdate)
            {
                if (TrySwitchToPresentHook())
                    return;

                if (TrySwitchToWgcFallback())
                    return;
            }
            else if (_activeBackend == CaptureBackend.PresentHook)
            {
                if (TrySwitchToWgcFallback())
                    return;
            }

            Plugin.Log!.Warning("[Video] No fallback backend could be activated; keeping current capture path.");
        }
        finally
        {
            _consecutiveBlackFrames = 0;
        }
    }

    private bool TrySwitchToPresentHook()
    {
        if (_activeBackend != CaptureBackend.FrameworkUpdate)
            return true;

        if (!TryInstallPresentHook(enable: true))
            return false;

        _framework.Update -= OnFrameworkUpdate;
        _activeBackend = CaptureBackend.PresentHook;
        _captureMethod = "PresentHook";
        Plugin.Log!.Info("[Video] Switched to Present Hook fallback.");
        return true;
    }

    private bool TrySwitchToWgcFallback()
    {
        if (_activeBackend == CaptureBackend.WindowsGraphicsCapture)
            return true;

        if (!TryGetDeviceForWgc(out IntPtr deviceHandle, out bool releaseDevice, out string deviceSource))
            return false;

        IntPtr hwnd = _uiBuilder.WindowHandlePtr;

        Plugin.Log!.Info($"[Video] Starting WGC: hwnd=0x{hwnd:X}, device=0x{deviceHandle:X}, source={deviceSource}");

        WindowGraphicsCapture wgcCapture;
        try
        {
            wgcCapture = new WindowGraphicsCapture(deviceHandle, _targetFps, OnWgcFrame);
        }
        finally
        {
            if (releaseDevice)
                ((ID3D11Device*)deviceHandle)->Release();
        }

        if (!wgcCapture.Start(hwnd))
        {
            Plugin.Log!.Error("[Video] WGC start failed. Staying on the current capture backend.");
            wgcCapture.Dispose();
            return false;
        }

        if (_activeBackend == CaptureBackend.FrameworkUpdate)
            _framework.Update -= OnFrameworkUpdate;

        if (_presentHook != null && _presentHookEnabled)
        {
            try { _presentHook.Disable(); } catch { }
            _presentHookEnabled = false;
        }

        _wgcCapture = wgcCapture;
        _activeBackend = CaptureBackend.WindowsGraphicsCapture;
        _captureMethod = "WindowsGraphicsCapture";
        Plugin.Log!.Info("[Video] Switched to Windows Graphics Capture fallback.");
        return true;
    }

    private bool TryGetDeviceForWgc(out IntPtr deviceHandle, out bool releaseDevice, out string source)
    {
        deviceHandle = IntPtr.Zero;
        releaseDevice = false;
        source = "none";

        try
        {
            Device* gameDevice = Device.Instance();
            SwapChain* gameSwapChain = gameDevice != null ? gameDevice->SwapChain : null;
            IDXGISwapChain* dxgiSwapChain = gameSwapChain != null ? (IDXGISwapChain*)gameSwapChain->DXGISwapChain : null;
            if (dxgiSwapChain != null)
            {
                Guid iidDev = IID_ID3D11Device;
                ID3D11Device* d3d11Device = null;
                int hr = dxgiSwapChain->GetDevice(&iidDev, (void**)&d3d11Device);
                if (hr >= 0 && d3d11Device != null)
                {
                    deviceHandle = (IntPtr)d3d11Device;
                    releaseDevice = true;
                    source = "GameSwapChain";
                    return true;
                }

                Plugin.Log!.Warning($"[Video] WGC: SwapChain->GetDevice failed: 0x{hr:X8}, trying UiBuilder device.");
            }
        }
        catch (Exception ex)
        {
            Plugin.Log!.Warning($"[Video] WGC: failed to get game D3D11 device ({ex.Message}), trying UiBuilder device.");
        }

        deviceHandle = _uiBuilder.DeviceHandle;
        if (deviceHandle == IntPtr.Zero)
        {
            Plugin.Log!.Error("[Video] WGC: UiBuilder.DeviceHandle is zero.");
            return false;
        }

        source = "UiBuilder";
        return true;
    }

    private void OnWgcFrame(VideoFrame frame)
    {
        if (!_capturing) return;

        CurrentWidth = frame.Width;
        CurrentHeight = frame.Height;
        _onFrame(frame);
        _frameCount++;

        if (_frameCount % 300 == 0)
            Plugin.Log!.Info($"[Video] {frame.Width}x{frame.Height} frame #{_frameCount} (WGC)");
    }

    // ──────────────────────────────────────────────────────────
    //  工具方法
    // ──────────────────────────────────────────────────────────

    private bool ShouldDiagnoseBackend(CaptureBackend backend)
    {
        int bit = 1 << (int)backend;
        if ((_diagnosedBackendMask & bit) != 0)
            return false;

        _diagnosedBackendMask |= bit;
        return true;
    }

    private static bool IsFrameEmpty(byte[] buffer, int width, int height, int stride)
    {
        int[] xs = { width / 4, width / 2, width * 3 / 4 };
        int[] ys = { height / 4, height / 2, height * 3 / 4 };
        foreach (int y in ys)
        {
            foreach (int x in xs)
            {
                int idx = y * stride + x * 4;
                if (buffer[idx] != 0 || buffer[idx + 1] != 0 || buffer[idx + 2] != 0 || buffer[idx + 3] != 0)
                    return false;
            }
        }
        return true;
    }

    private static void DiagnosePixels(byte[] buffer, int width, int height, int stride)
    {
        (int x, int y)[] pts = new[]
        {
            (width / 4, height / 4),
            (width / 2, height / 4),
            (width * 3 / 4, height / 4),
            (width / 4, height / 2),
            (width / 2, height / 2),
            (width * 3 / 4, height / 2),
            (width / 4, height * 3 / 4),
            (width / 2, height * 3 / 4),
            (width * 3 / 4, height * 3 / 4),
        };

        int nonZeroRgb = 0;
        int nonZeroRgba = 0;
        var sb = new System.Text.StringBuilder();
        sb.Append("[Video] Pixel diagnostics:");

        foreach (var (x, y) in pts)
        {
            int idx = y * stride + x * 4;
            byte b = buffer[idx], g = buffer[idx + 1], r = buffer[idx + 2], a = buffer[idx + 3];
            if (b != 0 || g != 0 || r != 0) nonZeroRgb++;
            if (b != 0 || g != 0 || r != 0 || a != 0) nonZeroRgba++;
            sb.Append($" ({x},{y})=[{r},{g},{b},{a}]");
        }

        sb.Append($" → rgb={nonZeroRgb}/9, rgba={nonZeroRgba}/9 non-zero");
        Plugin.Log!.Info(sb.ToString());

        if (nonZeroRgba == 0)
            Plugin.Log.Warning("[Video] ⚠ ALL sample points are empty (RGBA=0)!");
        else if (nonZeroRgb == 0)
            Plugin.Log.Warning("[Video] ⚠ ALL sample RGB values are black, but alpha is non-zero.");
    }

    private bool EnsureStagingTextures(uint width, uint height, DXGI_FORMAT format)
    {
        if (_stagingTextures[0] != IntPtr.Zero &&
            width == _stagingWidth &&
            height == _stagingHeight &&
            format == _stagingFormat &&
            _stagingDevice == (IntPtr)_device)
            return true;

        ReleaseStagingTextures();

        D3D11_TEXTURE2D_DESC desc = default;
        desc.Width = width;
        desc.Height = height;
        desc.MipLevels = 1;
        desc.ArraySize = 1;
        desc.Format = format;
        desc.SampleDesc.Count = 1;
        desc.SampleDesc.Quality = 0;
        desc.Usage = D3D11_USAGE.D3D11_USAGE_STAGING;
        desc.BindFlags = 0;
        desc.CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ;
        desc.MiscFlags = 0;

        for (int i = 0; i < StagingTextureCount; i++)
        {
            ID3D11Texture2D* newTexture;
            int hr = _device->CreateTexture2D(&desc, null, &newTexture);
            if (hr < 0)
            {
                Plugin.Log!.Error($"[Video] CreateTexture2D(staging #{i}) failed: 0x{hr:X8}");
                ReleaseStagingTextures();
                return false;
            }

            _stagingTextures[i] = (IntPtr)newTexture;
        }

        _stagingWidth = width;
        _stagingHeight = height;
        _stagingFormat = format;
        _stagingDevice = (IntPtr)_device;
        _stagingWriteIndex = 0;
        _stagingReadyCount = 0;
        return true;
    }

    private void ReleaseStagingTextures()
    {
        for (int i = 0; i < _stagingTextures.Length; i++)
        {
            if (_stagingTextures[i] == IntPtr.Zero)
                continue;

            ((ID3D11Texture2D*)_stagingTextures[i])->Release();
            _stagingTextures[i] = IntPtr.Zero;
        }

        _stagingDevice = IntPtr.Zero;
        _stagingWriteIndex = 0;
        _stagingReadyCount = 0;
    }

    private void EnsureD3D11MultithreadProtection(ID3D11DeviceContext* ctx)
    {
        if (ctx == null)
            return;

        IntPtr contextPtr = (IntPtr)ctx;
        if (_multithreadContext == contextPtr)
            return;

        ReleaseD3D11Multithread();
        _multithreadContext = contextPtr;

        Guid iid = IID_ID3D11Multithread;
        ID3D11Multithread* multithread = null;
        int hr = ctx->QueryInterface(&iid, (void**)&multithread);
        if (hr < 0 || multithread == null)
        {
            if (!_multithreadUnavailableLogged)
            {
                _multithreadUnavailableLogged = true;
                Plugin.Log!.Warning($"[Video] ID3D11Multithread unavailable: 0x{hr:X8}");
            }

            return;
        }

        multithread->SetMultithreadProtected(new BOOL(1));
        _d3d11Multithread = multithread;
        Plugin.Log!.Info("[Video] D3D11 multithread protection enabled for capture readback.");
    }

    private bool EnterD3D11Multithread()
    {
        if (_d3d11Multithread == null)
            return false;

        _d3d11Multithread->Enter();
        return true;
    }

    private void LeaveD3D11Multithread()
    {
        if (_d3d11Multithread != null)
            _d3d11Multithread->Leave();
    }

    private void ReleaseD3D11Multithread()
    {
        if (_d3d11Multithread != null)
        {
            _d3d11Multithread->Release();
            _d3d11Multithread = null;
        }

        _multithreadContext = IntPtr.Zero;
    }

    private static void SwapRedBlue(byte[] buffer, int width, int height, int stride)
    {
        int rowEnd = height * stride;
        for (int row = 0; row < rowEnd; row += stride)
        {
            int rowLimit = row + width * 4;
            for (int px = row; px < rowLimit; px += 4)
            {
                (buffer[px], buffer[px + 2]) = (buffer[px + 2], buffer[px]);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Stop();

        ReleaseStagingTextures();

        ReleaseD3D11Multithread();
    }
}

/// <summary>一帧视频画面的数据。</summary>
internal sealed record VideoFrame(byte[] Data, int Width, int Height, int Stride, long TimestampHns);
