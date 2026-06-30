using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using WinRT;
using WinRT.Interop;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace Recorder.Capture;

/// <summary>
/// 使用 Windows Graphics Capture API 捕获游戏窗口。
/// 这是 Windows 10 1903+ 的现代捕获 API（OBS 的窗口捕获也用它）。
/// 直接从 DWM 获取 Direct3D 纹理，GPU 加速、后台可用。
/// </summary>
internal sealed unsafe class WindowGraphicsCapture : IDisposable
{
    private readonly Action<VideoFrame> _onFrame;
    private readonly Func<bool>? _shouldCaptureFrame;
    private readonly int _targetFps;
    private readonly Stopwatch _sw = new();
    private readonly long _minIntervalTicks;

    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private IDirect3DDevice? _d3dDevice;
    private ID3D11Device* _nativeDevice;
    private ID3D11Multithread* _d3d11Multithread;
    private IntPtr _multithreadContext;
    private bool _multithreadUnavailableLogged;

    private int _frameCount;
    private int _skipCount;
    private int _backpressureSkipCount;
    private int _errorCount;
    private int _width;
    private int _height;
    private long _lastFrameTicks;
    private volatile bool _running;
    private static readonly Guid IID_IGraphicsCaptureItemInterop = new(0x3628E81B, 0x3CAC, 0x4C60, 0xB7, 0xF4, 0x23, 0xCE, 0xE0, 0xC3, 0x35, 0x6);
    private static readonly Guid IID_IGraphicsCaptureItem = new(0x79C3F95B, 0x31F7, 0x4EC2, 0xA4, 0x64, 0x63, 0x2E, 0xF5, 0xD3, 0x07, 0x60);
    private static bool _comWrappersPatched;

    /// <summary>
    /// 预置 CsWinRT 的 ComWrappers 缓存，避免与 Dalamud 的 ComWrappers 注册冲突。
    /// Dalamud 启动时已注册了全局 ComWrappers 实例，CsWinRT 首次创建 RCW 时
    /// 会尝试注册自己的实例，导致 InvalidOperationException。
    /// 通过反射直接设置缓存字段，跳过 RegisterForTrackerSupport 调用。
    /// </summary>
    private static void PatchComWrappers()
    {
        if (_comWrappersPatched) return;
        _comWrappersPatched = true;

        try
        {
            var defaultWrappers = TryGetDefaultComWrappers();
            if (defaultWrappers == null)
            {
                Plugin.Log!.Warning("[WGC] DefaultComWrappers singleton not found, WGC may fail.");
                return;
            }

            WinRT.ComWrappersSupport.InitializeComWrappers(defaultWrappers);
            Plugin.Log!.Info("[WGC] ✓ ComWrappers initialized to WinRT.DefaultComWrappers singleton.");
        }
        catch (Exception ex)
        {
            Plugin.Log!.Warning($"[WGC] ComWrappers patch failed: {ex}");
        }
    }

    private static System.Runtime.InteropServices.ComWrappers? TryGetDefaultComWrappers()
    {
        try
        {
            var supportType = typeof(WinRT.ComWrappersSupport);
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

            var prop = supportType.GetProperty("DefaultComWrappersInstance", flags);
            if (prop?.GetValue(null) is System.Runtime.InteropServices.ComWrappers wrappers)
                return wrappers;

            var getter = supportType.GetMethod("get_DefaultComWrappersInstance", flags);
            if (getter?.Invoke(null, null) is System.Runtime.InteropServices.ComWrappers wrappers2)
                return wrappers2;
        }
        catch (Exception ex)
        {
            Plugin.Log!.Warning($"[WGC] Failed to resolve DefaultComWrappers: {ex.Message}");
        }

        return null;
    }

    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice")]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    private static readonly Guid IID_ID3D11Texture2D = new(0x6F15AAF2, 0xD208, 0x4E89, 0x9A, 0xB4, 0x48, 0x95, 0x35, 0xD3, 0x4F, 0x9C);
    private static readonly Guid IID_IDXGIDevice = new(0x54EC77FA, 0x1377, 0x44E6, 0x8C, 0x32, 0x88, 0xFD, 0x5F, 0x44, 0xC8, 0x4C);
    private static readonly Guid IID_ID3D11Multithread = new(0x9B7E4E00, 0x342C, 0x4106, 0xA1, 0x9F, 0x4F, 0x27, 0x04, 0xF6, 0x89, 0xF0);

    public int CurrentWidth => _width;
    public int CurrentHeight => _height;

    public WindowGraphicsCapture(IntPtr d3d11DeviceHandle, int targetFps, Action<VideoFrame> onFrame, Func<bool>? shouldCaptureFrame = null)
    {
        _nativeDevice = (ID3D11Device*)d3d11DeviceHandle;
        if (_nativeDevice == null)
            throw new ArgumentException("D3D11 device handle is zero.", nameof(d3d11DeviceHandle));

        _nativeDevice->AddRef();
        _targetFps = Math.Max(1, targetFps);
        _onFrame = onFrame;
        _shouldCaptureFrame = shouldCaptureFrame;
        _minIntervalTicks = Stopwatch.Frequency / _targetFps;
    }

    public bool Start(IntPtr hwnd)
    {
        PatchComWrappers();
        try
        {
            // 1. 创建 IDirect3DDevice（从游戏的 D3D11 Device）
            if (!TryCreateDirect3DDevice())
            {
                Plugin.Log!.Error("[WGC] Failed to create IDirect3DDevice.");
                return false;
            }

            // 2. 创建 GraphicsCaptureItem（从窗口句柄）
            _item = TryCreateCaptureItemForWindow(hwnd);
            if (_item == null)
            {
                Plugin.Log!.Error("[WGC] Failed to create GraphicsCaptureItem for window.");
                return false;
            }

            _width = (int)_item.Size.Width;
            _height = (int)_item.Size.Height;
            Plugin.Log!.Info($"[WGC] CaptureItem created: {_width}x{_height}");

            // 3. 创建 FramePool (BGRA8, 2 buffers)
            _framePool = Direct3D11CaptureFramePool.Create(
                _d3dDevice!,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                2,
                new SizeInt32 { Width = _width, Height = _height });

            _framePool.FrameArrived += OnFrameArrived;

            // 4. 创建 Session 并开始
            _session = _framePool.CreateCaptureSession(_item);
            _session.IsCursorCaptureEnabled = false;
            // IsBorderRequired 需要 Win10 2004+，用反射安全调用
            try
            {
                var prop = _session.GetType().GetProperty("IsBorderRequired");
                if (prop != null && prop.CanWrite)
                    prop.SetValue(_session, false);
            }
            catch { }
            _session.StartCapture();

            _running = true;
            _sw.Start();

            Plugin.Log!.Info("[WGC] ✓ Windows Graphics Capture session started.");
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log!.Error($"[WGC] Start failed: {ex}");
            return false;
        }
    }

    public void Stop()
    {
        _running = false;

        try { _session?.Dispose(); } catch { }
        try { _framePool?.Dispose(); } catch { }
        try { _item = null; } catch { }

        _session = null;
        _framePool = null;
        _item = null;

        _sw.Stop();
        Plugin.Log!.Info($"[WGC] Stopped. frames={_frameCount}, skipped={_skipCount}, backpressureSkips={_backpressureSkipCount}, errors={_errorCount}");
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        if (!_running) return;

        try
        {
            long now = _sw.ElapsedTicks;
            if (now - _lastFrameTicks < _minIntervalTicks)
            {
                _skipCount++;
                return;
            }
            _lastFrameTicks = now;
            if (ShouldSkipCaptureForBackpressure()) return;

            using var frame = sender.TryGetNextFrame();
            if (frame == null)
            {
                _skipCount++;
                return;
            }

            var surface = frame.Surface;
            var dpiSurface = surface as IDirect3DDxgiInterfaceAccess;
            if (dpiSurface == null && surface != null)
            {
                // ComWrappers 冲突 fallback：通过 Marshal 获取经典 COM RCW
                try
                {
                    IntPtr unkPtr = Marshal.GetIUnknownForObject(surface);
                    dpiSurface = (IDirect3DDxgiInterfaceAccess)Marshal.GetObjectForIUnknown(unkPtr);
                    Marshal.Release(unkPtr);
                }
                catch { }
            }
            if (dpiSurface == null)
            {
                _skipCount++;
                return;
            }

            // 获取 IDXGISurface → QI ID3D11Texture2D
            Guid iidTex2D = IID_ID3D11Texture2D;
            IntPtr texPtr = dpiSurface.GetInterface(ref iidTex2D);
            if (texPtr == IntPtr.Zero)
            {
                _skipCount++;
                return;
            }

            ID3D11Texture2D* srcTexture = (ID3D11Texture2D*)texPtr;
            try
            {
                ProcessTexture(srcTexture, frame.SystemRelativeTime);
            }
            finally
            {
                // Release the texture
                ((IUnknown*)texPtr)->Release();
            }
        }
        catch (Exception ex)
        {
            _errorCount++;
            if (_errorCount <= 5 || _errorCount % 100 == 0)
                Plugin.Log!.Warning($"[WGC] FrameArrived error (#{_errorCount}): {ex.Message}");
        }
    }

    private ID3D11Texture2D* _stagingTexture;
    private uint _stagingWidth;
    private uint _stagingHeight;

    private void ProcessTexture(ID3D11Texture2D* srcTexture, TimeSpan systemRelativeTime)
    {
        D3D11_TEXTURE2D_DESC desc;
        srcTexture->GetDesc(&desc);

        uint width = desc.Width;
        uint height = desc.Height;

        // 尺寸变化（窗口调整）
        if (width != _stagingWidth || height != _stagingHeight)
        {
            if (_stagingTexture != null)
            {
                _stagingTexture->Release();
                _stagingTexture = null;
            }

            _stagingWidth = width;
            _stagingHeight = height;
            _width = (int)width;
            _height = (int)height;

            // 通知 FramePool 调整尺寸
            if (_framePool != null)
            {
                try
                {
                    _framePool.Recreate(
                        _d3dDevice!,
                        DirectXPixelFormat.B8G8R8A8UIntNormalized,
                        2,
                        new SizeInt32 { Width = (int)width, Height = (int)height });
                }
                catch { }
            }
        }

        // 创建 staging texture（如果需要）
        if (_stagingTexture == null)
        {
            D3D11_TEXTURE2D_DESC stagingDesc = default;
            stagingDesc.Width = width;
            stagingDesc.Height = height;
            stagingDesc.MipLevels = 1;
            stagingDesc.ArraySize = 1;
            stagingDesc.Format = desc.Format;
            stagingDesc.SampleDesc.Count = 1;
            stagingDesc.SampleDesc.Quality = 0;
            stagingDesc.Usage = D3D11_USAGE.D3D11_USAGE_STAGING;
            stagingDesc.BindFlags = 0;
            stagingDesc.CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ;
            stagingDesc.MiscFlags = 0;

            ID3D11Texture2D* newTex;
            int hr = _nativeDevice->CreateTexture2D(&stagingDesc, null, &newTex);
            if (hr < 0)
            {
                Plugin.Log!.Error($"[WGC] CreateTexture2D(staging) failed: 0x{hr:X8}");
                return;
            }
            _stagingTexture = newTex;
        }

        // 获取 immediate context
        ID3D11DeviceContext* ctx;
        _nativeDevice->GetImmediateContext(&ctx);
        if (ctx == null) { _skipCount++; return; }

        try
        {
            VideoFrame? frame = null;
            EnsureD3D11MultithreadProtection(ctx);
            bool d3dLocked = EnterD3D11Multithread();
            bool mappedOk = false;

            D3D11_MAPPED_SUBRESOURCE mapped;
            try
            {
                ctx->CopySubresourceRegion(
                    (ID3D11Resource*)_stagingTexture,
                    0,
                    0,
                    0,
                    0,
                    (ID3D11Resource*)srcTexture,
                    0,
                    null);

                // Map 到 CPU
                int hr = ctx->Map((ID3D11Resource*)_stagingTexture, 0, D3D11_MAP.D3D11_MAP_READ, 0, &mapped);
                if (hr < 0)
                {
                    _skipCount++;
                    if (_skipCount <= 3)
                        Plugin.Log!.Warning($"[WGC] Map failed: 0x{hr:X8}");
                    return;
                }

                mappedOk = true;

                {
                    int bpp = 4;
                    int srcStride = (int)mapped.RowPitch;
                    int dstStride = (int)width * bpp;
                    int dataSize = dstStride * (int)height;

                    byte[]? rentedBuffer = VideoFrame.RentBuffer(dataSize);
                    byte[] buffer = rentedBuffer;
                    byte* src = (byte*)mapped.pData;

                    try
                    {
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

                        // 首帧诊断
                        if (_frameCount == 0)
                        {
                            Plugin.Log!.Info($"[WGC] First frame: {width}x{height}, format={desc.Format}, " +
                                $"usage={desc.Usage}, bindFlags=0x{desc.BindFlags:X}, cpuAccess=0x{desc.CPUAccessFlags:X}, " +
                                $"miscFlags=0x{desc.MiscFlags:X}, mips={desc.MipLevels}, sample={desc.SampleDesc.Count}/{desc.SampleDesc.Quality}");
                            DiagnosePixels(buffer, (int)width, (int)height, dstStride);
                        }

                        if (IsFrameEmpty(buffer, (int)width, (int)height, dstStride))
                        {
                            _skipCount++;
                            if (_skipCount <= 5 || _skipCount % 300 == 0)
                                Plugin.Log!.Warning($"[WGC] Empty frame skipped. skipped={_skipCount}");
                            return;
                        }

                        // WGC 已经返回 BGRA，无需转换
                        long timestampHns = _frameCount == 0
                            ? 0
                            : (long)(systemRelativeTime.TotalMilliseconds * 10_000);

                        frame = new VideoFrame(buffer, dataSize, (int)width, (int)height, dstStride, timestampHns, VideoPixelFormat.Bgra, ownsBuffer: true);
                        rentedBuffer = null;
                    }
                    finally
                    {
                        if (rentedBuffer != null)
                            VideoFrame.ReturnBuffer(rentedBuffer);
                    }
                }
            }
            finally
            {
                if (mappedOk)
                    ctx->Unmap((ID3D11Resource*)_stagingTexture, 0);

                if (d3dLocked)
                    LeaveD3D11Multithread();
            }

            if (frame != null)
            {
                bool delivered = false;
                try
                {
                    _onFrame(frame);
                    delivered = true;
                }
                finally
                {
                    if (!delivered)
                        frame.ReturnBuffer();
                }

                _frameCount++;

                if (_frameCount % 300 == 0)
                    Plugin.Log!.Info($"[WGC] {width}x{height} frame #{_frameCount}");
            }
        }
        finally
        {
            ctx->Release();
        }
    }

    private bool ShouldSkipCaptureForBackpressure()
    {
        if (_shouldCaptureFrame == null)
            return false;

        bool shouldCapture;
        try
        {
            shouldCapture = _shouldCaptureFrame();
        }
        catch (Exception ex)
        {
            if (_backpressureSkipCount == 0)
                Plugin.Log!.Warning($"[WGC] Capture backpressure check failed: {ex.Message}");
            return false;
        }

        if (shouldCapture)
            return false;

        _skipCount++;
        _backpressureSkipCount++;
        if (_backpressureSkipCount <= 3 || _backpressureSkipCount % 300 == 0)
            Plugin.Log!.Info($"[WGC] Encoder queue backed up; skipped capture readback. backpressureSkips={_backpressureSkipCount}");

        return true;
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
                Plugin.Log!.Warning($"[WGC] ID3D11Multithread unavailable: 0x{hr:X8}");
            }

            return;
        }

        multithread->SetMultithreadProtected(new BOOL(1));
        _d3d11Multithread = multithread;
        Plugin.Log!.Info("[WGC] D3D11 multithread protection enabled.");
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

    private bool TryCreateDirect3DDevice()
    {
        try
        {
            // QI ID3D11Device → IDXGIDevice
            Guid iidDxgiDev = IID_IDXGIDevice;
            IDXGIDevice* dxgiDevice = null;
            int hr = _nativeDevice->QueryInterface(&iidDxgiDev, (void**)&dxgiDevice);
            if (hr < 0 || dxgiDevice == null)
            {
                Plugin.Log!.Error($"[WGC] QI(IDXGIDevice) failed: 0x{hr:X8}");
                return false;
            }

            try
            {
                // CreateDirect3D11DeviceFromDXGIDevice 返回 IInspectable*
                hr = CreateDirect3D11DeviceFromDXGIDevice((IntPtr)dxgiDevice, out IntPtr inspectablePtr);
                if (hr < 0 || inspectablePtr == IntPtr.Zero)
                {
                    Plugin.Log!.Error($"[WGC] CreateDirect3D11DeviceFromDXGIDevice failed: 0x{hr:X8}");
                    return false;
                }

                try
                {
                    // 方法 1：尝试 FromAbi（ComWrappers 正确时可用）
                    try
                    {
                        _d3dDevice = MarshalInterface<IDirect3DDevice>.FromAbi(inspectablePtr);
                        Plugin.Log!.Info("[WGC] ✓ IDirect3DDevice created via FromAbi.");
                        return true;
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
                    {
                        Plugin.Log!.Info($"[WGC] FromAbi failed ({ex.GetType().Name}), trying Marshal/FindObject fallback...");
                    }

                    // 方法 2：退回到经典 RCW / FindObject 路径
                    var deviceObj = Marshal.GetObjectForIUnknown(inspectablePtr) as IDirect3DDevice;
                    if (deviceObj != null)
                    {
                        _d3dDevice = deviceObj;
                        Plugin.Log!.Info("[WGC] ✓ IDirect3DDevice created via Marshal.GetObjectForIUnknown().");
                        return true;
                    }

                    try
                    {
                        _d3dDevice = WinRT.ComWrappersSupport.FindObject<IDirect3DDevice>(inspectablePtr);
                        if (_d3dDevice != null)
                        {
                            Plugin.Log!.Info("[WGC] ✓ IDirect3DDevice created via ComWrappersSupport.FindObject().");
                            return true;
                        }
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log!.Info($"[WGC] FindObject fallback failed ({ex.GetType().Name}).");
                    }

                    Plugin.Log!.Error("[WGC] All methods to create IDirect3DDevice failed.");
                    return false;
                }
                finally
                {
                    Marshal.Release(inspectablePtr);
                }
            }
            finally
            {
                dxgiDevice->Release();
            }
        }
        catch (Exception ex)
        {
            Plugin.Log!.Error($"[WGC] CreateDirect3DDevice error: {ex}");
            return false;
        }
    }

    private static GraphicsCaptureItem? TryCreateCaptureItemForWindow(IntPtr hwnd)
    {
        try
        {
            // WinRT 类不能用 CoCreateInstance，必须用 RoGetActivationFactory。
            string className = "Windows.Graphics.Capture.GraphicsCaptureItem";
            WindowsCreateString(className, (uint)className.Length, out IntPtr hString);
            if (hString == IntPtr.Zero)
            {
                Plugin.Log!.Warning("[WGC] WindowsCreateString failed.");
                return null;
            }

            try
            {
                Guid iidInterop = IID_IGraphicsCaptureItemInterop;
                int hr = RoGetActivationFactory(hString, iidInterop, out IntPtr factoryPtr);
                if (hr < 0 || factoryPtr == IntPtr.Zero)
                {
                    Plugin.Log!.Warning($"[WGC] RoGetActivationFactory failed: 0x{hr:X8}");
                    return null;
                }

                try
                {
                    var interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factoryPtr);
                    var itemIid = IID_IGraphicsCaptureItem;
                    interop.CreateForWindow(hwnd, ref itemIid, out IntPtr itemPtr);
                    try
                    {
                        return GraphicsCaptureItem.FromAbi(itemPtr);
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
                    {
                        // ComWrappers 冲突 fallback
                        Plugin.Log!.Info($"[WGC] GraphicsCaptureItem.FromAbi failed ({ex.GetType().Name}), using Marshal fallback...");
                        return (GraphicsCaptureItem)Marshal.GetObjectForIUnknown(itemPtr);
                    }
                }
                finally
                {
                    Marshal.Release(factoryPtr);
                }
            }
            finally
            {
                WindowsDeleteString(hString);
            }
        }
        catch (Exception ex)
        {
            Plugin.Log!.Warning($"[WGC] CreateCaptureItem failed: {ex.Message}");
            return null;
        }
    }

    [DllImport("combase.dll", EntryPoint = "WindowsCreateString", CallingConvention = CallingConvention.StdCall)]
    private static extern int WindowsCreateString(
        [MarshalAs(UnmanagedType.LPWStr)] string sourceString, uint length, out IntPtr hString);

    [DllImport("combase.dll", EntryPoint = "WindowsDeleteString", CallingConvention = CallingConvention.StdCall)]
    private static extern int WindowsDeleteString(IntPtr hString);

    [DllImport("combase.dll", EntryPoint = "RoGetActivationFactory", CallingConvention = CallingConvention.StdCall)]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, [In] Guid iid, out IntPtr factory);

    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        IntPtr GetInterface([In] ref Guid iid);
    }

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        void CreateForWindow([In] IntPtr window, [In] ref Guid iid, out IntPtr item);
        void CreateForMonitor([In] IntPtr monitor, [In] ref Guid iid, out IntPtr item);
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
        sb.Append("[WGC] Pixel diagnostics:");

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
            Plugin.Log.Warning("[WGC] ⚠ ALL sample points are empty (RGBA=0)!");
        else if (nonZeroRgb == 0)
            Plugin.Log.Warning("[WGC] ⚠ ALL sample RGB values are black, but alpha is non-zero.");
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

    public void Dispose()
    {
        Stop();
        if (_stagingTexture != null)
        {
            _stagingTexture->Release();
            _stagingTexture = null;
        }
        _d3dDevice?.Dispose();
        _d3dDevice = null;
        ReleaseD3D11Multithread();
        if (_nativeDevice != null)
        {
            _nativeDevice->Release();
            _nativeDevice = null;
        }
    }
}
