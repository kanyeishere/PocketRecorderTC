using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using Recorder.Encoding;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using TerraFX.Interop.DirectX;
using DXGI_FORMAT = TerraFX.Interop.DirectX.DXGI_FORMAT;

namespace Recorder.Capture;

/// <summary>
/// 视频捕获服务。
/// 通过 Hook IDXGISwapChain::Present 在 Present 前直接捕获游戏 D3D11 backbuffer。
/// </summary>
internal sealed unsafe partial class VideoCaptureService : IDisposable
{
    private readonly IGameInteropProvider _gameInterop;
    private readonly Action<VideoFrame> _onFrame;
    private readonly Func<bool>? _shouldCaptureFrame;

    // D3D11 对象
    private ID3D11Device* _device;
    private readonly D3D11ReadbackTextureRing _stagingReadback = new(StagingTextureCount);
    private ID3D11Texture2D* _nv12SourceTexture;
    private ID3D11ShaderResourceView* _nv12SourceSrv;
    private ID3D11Buffer* _nv12OutputBuffer;
    private ID3D11UnorderedAccessView* _nv12OutputUav;
    private ID3D11Buffer* _nv12ConstantBuffer;
    private ID3D11ComputeShader* _nv12ComputeShader;
    private IntPtr _nv12ShaderDevice;
    private readonly IntPtr[] _nv12ReadbackBuffers = new IntPtr[StagingTextureCount];
    private readonly int[] _nv12ReadbackSlotStates = new int[StagingTextureCount];
    private readonly IntPtr[] _nativeSharedTextures = new IntPtr[NativeSharedTextureCount];
    private readonly IntPtr[] _nativeSharedMutexes = new IntPtr[NativeSharedTextureCount];
    private readonly IntPtr[] _nativeSharedHandles = new IntPtr[NativeSharedTextureCount];
    private readonly int[] _nativeSharedSlotStates = new int[NativeSharedTextureCount];
    private uint _nativeSharedWidth;
    private uint _nativeSharedHeight;
    private DXGI_FORMAT _nativeSharedFormat;
    private IntPtr _nativeSharedDevice;
    private int _nativeSharedWriteIndex;
    private int _nativeSharedBusySkipCount;
    private int _nativeSharedBusySkipSuppressed;
    private long _lastNativeSharedBusyLogTicks;
    private bool _nativeSharedDisabled;
    private bool _nativeSharedFallbackLogged;
    private uint _nv12SourceWidth;
    private uint _nv12SourceHeight;
    private uint _nv12OutputWidth;
    private uint _nv12OutputHeight;
    private DXGI_FORMAT _nv12Format;
    private IntPtr _nv12Device;
    private int _nv12DataSize;
    private int _nv12WriteIndex;
    private int _nv12ReadyCount;
    private bool _nv12Disabled;
    private bool _nv12FallbackLogged;
    private int _nv12BusySkipCount;
    private int _nv12BusySkipSuppressed;
    private long _lastNv12BusyLogTicks;
    private VideoPixelFormat? _lockedOutputPixelFormat;
    private readonly VideoPipelinePerfStats _nv12PerfStats = new("NV12 capture", "map", "copy", "onFrame", "total");

    // Present Hook
    private Hook<PresentDelegate>? _presentHook;

    // 状态
    private volatile bool _capturing;
    private bool _disposed;
    private int _stopStarted;
    private int _targetFps = 60;
    private readonly VideoCaptureFramePacer _framePacer = new();
    private readonly Stopwatch _sw = new();
    private int _frameCount;
    private int _skipCount;
    private int _backpressureSkipCount;
    private int _errorCount;
    private int _consecutiveBlackFrames;
    private bool _presentHookEnabled;
    private int _nv12StopCleanupRequested;
    private bool _diagnosedFirstFrame;
    private bool _diagnosedReadbackFrame;
    private bool _loggedBackBufferSuccess;
    private string _captureMethod = "unknown";
    private int _presentDetourDepth;

    private const int MaxConsecutiveEmptyFramesBeforeWarning = 3;
    private const int StagingTextureCount = 3;
    private const int Nv12SlotAvailable = 0;
    private const int Nv12SlotReady = 1;
    private const int Nv12SlotMapped = 2;
    private const int Nv12SlotPendingRelease = 3;
    private const int NativeSharedTextureCount = 6;
    private const int NativeSlotAvailable = 0;
    private const int NativeSlotInFlight = 1;
    private const int NativeSlotPendingDropRelease = 2;
    private const ulong NativeGameWriteKey = 0;
    private const ulong NativeEncoderReadKey = 1;
    private const uint NativeKeyedMutexTimeoutMs = 1;
    private const uint D3D11ResourceMiscShared = 0x2;
    private const uint D3D11ResourceMiscSharedKeyedMutex = 0x100;
    private const int DXGI_ERROR_WAS_STILL_DRAWING = unchecked((int)0x887A000A);
    private const int WAIT_TIMEOUT = 0x00000102;
    private static readonly Guid IID_ID3D11Texture2D = new(0x6F15AAF2, 0xD208, 0x4E89, 0x9A, 0xB4, 0x48, 0x95, 0x35, 0xD3, 0x4F, 0x9C);
    private static readonly Guid IID_ID3D11Device = new(0xdb6f6ddb, 0xac77, 0x4e88, 0x82, 0x53, 0x81, 0x9d, 0xf9, 0xbb, 0xf1, 0x40);
    public int CurrentWidth { get; private set; }
    public int CurrentHeight { get; private set; }
    public bool PreferD3D11TextureFrames { get; set; }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int PresentDelegate(IntPtr swapChain, uint syncInterval, uint flags);

    public VideoCaptureService(IGameInteropProvider gameInterop, Action<VideoFrame> onFrame, Func<bool>? shouldCaptureFrame = null)
    {
        _gameInterop = gameInterop;
        _onFrame = onFrame;
        _shouldCaptureFrame = shouldCaptureFrame;
    }

    public bool Start(int targetFps)
    {
        _targetFps = Math.Max(1, targetFps);
        _framePacer.Reset(_targetFps);
        _capturing = false;
        _frameCount = 0;
        _skipCount = 0;
        _backpressureSkipCount = 0;
        _errorCount = 0;
        _consecutiveBlackFrames = 0;
        _presentHookEnabled = false;
        _diagnosedFirstFrame = false;
        _diagnosedReadbackFrame = false;
        _loggedBackBufferSuccess = false;
        _nv12Disabled = false;
        _nv12FallbackLogged = false;
        _nv12BusySkipCount = 0;
        _nv12BusySkipSuppressed = 0;
        _lastNv12BusyLogTicks = 0;
        _nativeSharedBusySkipCount = 0;
        _nativeSharedBusySkipSuppressed = 0;
        _lastNativeSharedBusyLogTicks = 0;
        _nativeSharedDisabled = false;
        _nativeSharedFallbackLogged = false;
        _lockedOutputPixelFormat = null;
        _presentDetourDepth = 0;
        _stopStarted = 0;
        _nv12StopCleanupRequested = 0;
        Array.Clear(_nv12ReadbackSlotStates);
        Array.Clear(_nativeSharedSlotStates);
        _nv12PerfStats.Reset();
        _sw.Restart();

        if (TryInstallPresentHook(enable: true))
        {
            _capturing = true;
            _captureMethod = "PresentHook";
            Plugin.Log!.Info($"[Video] Capture started, targetFps={_targetFps}, method=PresentHook");
            return true;
        }

        _sw.Stop();
        _captureMethod = "Unavailable";
        Plugin.Log!.Error("[Video] Present hook unavailable; capture was not started.");
        return false;
    }

    public void Stop()
    {
        if (Interlocked.Exchange(ref _stopStarted, 1) != 0)
            return;

        RequestStop();
        WaitForNv12StopCleanup();

        // 卸载 hook
        if (_presentHook != null)
        {
            if (_presentHookEnabled)
            {
                try { _presentHook.Disable(); } catch { }
                _presentHookEnabled = false;
            }
        }

        WaitForPresentDetoursToDrain();
        if (_presentHook != null)
        {
            _presentHook.Dispose();
            _presentHook = null;
        }
        _sw.Stop();
        _nv12PerfStats.FlushIfAny();
        Plugin.Log!.Info($"[Video] Capture stopped. frames={_frameCount}, skipped={_skipCount}, backpressureSkips={_backpressureSkipCount}, nativeBusySkips={_nativeSharedBusySkipCount}, nv12BusySkips={_nv12BusySkipCount}, errors={_errorCount}, method={_captureMethod}");
    }

    public void RequestStop()
    {
        _capturing = false;
        RequestNv12StopCleanup();
    }

    // ──────────────────────────────────────────────────────────
    //  Present Hook 捕获
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
        Interlocked.Increment(ref _presentDetourDepth);
        if (_capturing)
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
        else
        {
            TryDrainNv12StopCleanup(swapChainPtr);
        }

        try
        {
            return _presentHook!.Original(swapChainPtr, syncInterval, flags);
        }
        finally
        {
            Interlocked.Decrement(ref _presentDetourDepth);
        }
    }

    private void CaptureBeforePresent(IntPtr swapChainPtr)
    {
        long now = _sw.ElapsedTicks;
        if (!ShouldCaptureThisPresent(now)) return;
        if (ShouldSkipCaptureForBackpressure()) return;

        var swapChain = (IDXGISwapChain*)swapChainPtr;

        // 从 SwapChain 获取游戏自己的 D3D11 Device，避免跨设备 CopyResource 造成黑帧。
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
            DrainDeferredNv12Resources(ctx);

            ID3D11Texture2D* backBuffer = null;
            Guid iidTex2D = IID_ID3D11Texture2D;
            int hr = swapChain->GetBuffer(0, &iidTex2D, (void**)&backBuffer);
            if (hr < 0 || backBuffer == null)
            {
                _skipCount++;
                if (_skipCount <= 3)
                    Plugin.Log!.Warning($"[Video] Present: GetBuffer(0) failed: 0x{hr:X8}");
                return;
            }

            if (!_loggedBackBufferSuccess)
            {
                _loggedBackBufferSuccess = true;
                Plugin.Log!.Info("[Video] Present: GetBuffer(0) succeeded.");
            }

            try
            {
                ProcessTexture(ctx, backBuffer, now, releaseDevice: true);
            }
            finally
            {
                backBuffer->Release();
            }
        }
        finally
        {
            ctx->Release();
        }
    }

    private bool ShouldCaptureThisPresent(long now)
        => _framePacer.ShouldCapture(now);

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
            bool diagnoseTexture = ClaimFirstFrameDiagnostic();
            bool readbackDiagnosticsPending = !_diagnosedReadbackFrame;

            CurrentWidth = (int)width;
            CurrentHeight = (int)height;

            int bpp = 4;
            if (!VideoCaptureFormats.IsSupportedReadbackFormat(format))
            {
                _skipCount++;
                if (_skipCount <= 3)
                    Plugin.Log!.Warning($"[Video] Unexpected format: {format}, skipping.");
                return;
            }

            if (diagnoseTexture)
            {
                Plugin.Log!.Info($"[Video] PresentHook first frame: {width}x{height}, format={format}, " +
                    $"usage={desc.Usage}, bindFlags=0x{desc.BindFlags:X}, cpuAccess=0x{desc.CPUAccessFlags:X}, " +
                    $"miscFlags=0x{desc.MiscFlags:X}, mips={desc.MipLevels}, sample={desc.SampleDesc.Count}/{desc.SampleDesc.Quality}");
            }

            if (TryProcessTextureAsD3D11(ctx, srcTexture, width, height, format, desc.SampleDesc.Count, timestampTicks))
                return;

            if (TryProcessTextureAsNv12(ctx, srcTexture, width, height, format, timestampTicks, readbackDiagnosticsPending))
                return;

            if (!_stagingReadback.Ensure(_device, width, height, format, message => Plugin.Log!.Error(message)))
                return;

            ID3D11Texture2D* writeTexture = _stagingReadback.GetWriteTexture();
            ID3D11Texture2D* readTexture = _stagingReadback.GetReadTexture();

            bool mappedOk = false;
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

                _stagingReadback.MarkWriteSubmitted();

                if (readTexture == null)
                    return;

                if (!TryMapReadbackResource(ctx, (ID3D11Resource*)readTexture, "BGRA", out mapped))
                    return;

                mappedOk = true;

                {
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

                        bool isEmptyFrame = VideoFrameContentAnalyzer.IsBgraFrameEmpty(buffer, (int)width, (int)height, dstStride);

                        if (readbackDiagnosticsPending && ClaimReadbackDiagnostic())
                        {
                            DiagnosePixels(buffer, (int)width, (int)height, dstStride);
                        }

                        VideoPixelFormat pixelFormat = VideoCaptureFormats.GetReadbackPixelFormat(format);

                        // 空帧（RGBA 全 0）通常表示 GPU 读回失败；不要把它送进编码器。
                        if (isEmptyFrame)
                        {
                            _consecutiveBlackFrames++;
                            LogConsecutiveEmptyFrames(pixelFormat.ToString());
                        }
                        else
                        {
                            _consecutiveBlackFrames = 0;

                            long timestampHns = timestampTicks * 10_000_000L / Stopwatch.Frequency;
                            if (_lockedOutputPixelFormat is { } lockedFormat && lockedFormat != pixelFormat)
                            {
                                _skipCount++;
                                if (_skipCount <= 3 || _skipCount % 300 == 0)
                                    Plugin.Log!.Warning($"[Video] Skipping {pixelFormat} frame because recording input is locked to {lockedFormat}.");
                            }
                            else
                            {
                                _lockedOutputPixelFormat ??= pixelFormat;
                                frame = new VideoFrame(buffer, dataSize, (int)width, (int)height, dstStride, timestampHns, pixelFormat, ownsBuffer: true);
                                rentedBuffer = null;
                            }
                        }
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
                    ctx->Unmap((ID3D11Resource*)readTexture, 0);
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
    //  工具方法
    // ──────────────────────────────────────────────────────────

    private bool ClaimFirstFrameDiagnostic()
    {
        if (_diagnosedFirstFrame)
            return false;

        _diagnosedFirstFrame = true;
        return true;
    }

    private bool ClaimReadbackDiagnostic()
    {
        if (_diagnosedReadbackFrame)
            return false;

        _diagnosedReadbackFrame = true;
        return true;
    }

    private void LogConsecutiveEmptyFrames(string format)
    {
        if (_consecutiveBlackFrames == MaxConsecutiveEmptyFramesBeforeWarning ||
            _consecutiveBlackFrames % 300 == 0)
        {
            Plugin.Log!.Warning($"[Video] {_consecutiveBlackFrames} consecutive empty {format} frames on {_captureMethod}; skipping them.");
        }
    }

    private bool SkipLockedNv12Frame(string reason)
    {
        _skipCount++;
        if (_skipCount <= 3 || _skipCount % 300 == 0)
            Plugin.Log!.Warning($"[Video] Skipping frame because recording input is locked to NV12. {reason}.");

        return true;
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
                Plugin.Log!.Warning($"[Video] Capture backpressure check failed: {ex.Message}");
            return false;
        }

        if (shouldCapture)
            return false;

        _skipCount++;
        _backpressureSkipCount++;
        if (_backpressureSkipCount <= 3 || _backpressureSkipCount % 300 == 0)
            Plugin.Log!.Info($"[Video] Encoder queue backed up; skipped capture readback. backpressureSkips={_backpressureSkipCount}");

        return true;
    }

    private bool TryMapReadbackResource(
        ID3D11DeviceContext* ctx,
        ID3D11Resource* resource,
        string label,
        out D3D11_MAPPED_SUBRESOURCE mapped)
    {
        D3D11_MAPPED_SUBRESOURCE mappedLocal = default;
        int hr = ctx->Map(
            resource,
            0,
            D3D11_MAP.D3D11_MAP_READ,
            (uint)D3D11_MAP_FLAG.D3D11_MAP_FLAG_DO_NOT_WAIT,
            &mappedLocal);
        mapped = mappedLocal;

        if (hr >= 0)
            return true;

        _skipCount++;
        if (hr == DXGI_ERROR_WAS_STILL_DRAWING)
        {
            if (_skipCount <= 3 || _skipCount % 300 == 0)
                Plugin.Log!.Info($"[Video] {label} readback not ready; skipped without blocking. skipped={_skipCount}");
        }
        else if (_skipCount <= 3)
        {
            Plugin.Log!.Warning($"[Video] {label} Map failed: 0x{hr:X8}");
        }

        return false;
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

    private void WaitForPresentDetoursToDrain()
    {
        if (Volatile.Read(ref _presentDetourDepth) == 0)
            return;

        Stopwatch waitSw = Stopwatch.StartNew();
        while (Volatile.Read(ref _presentDetourDepth) > 0 && waitSw.ElapsedMilliseconds < 250)
            Thread.Sleep(1);

        if (Volatile.Read(ref _presentDetourDepth) > 0)
            Plugin.Log!.Warning($"[Video] Present detours did not drain within {waitSw.ElapsedMilliseconds}ms.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Stop();

        ReleaseNativeSharedTextures();
        _stagingReadback.Release();
        ReleaseNv12Resources();
        ReleaseNv12Shader();
    }
}
