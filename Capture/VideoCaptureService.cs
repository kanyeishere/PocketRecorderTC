using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using TerraFX.Interop.DirectX;
using DXGI_FORMAT = TerraFX.Interop.DirectX.DXGI_FORMAT;

namespace Recorder.Capture;

/// <summary>
/// 视频捕获服务。
/// 通过 Hook IDXGISwapChain::Present 在 Present 前直接捕获游戏 D3D11 backbuffer。
/// </summary>
internal sealed unsafe class VideoCaptureService : IDisposable
{
    private readonly IGameInteropProvider _gameInterop;
    private readonly Action<VideoFrame> _onFrame;
    private readonly Func<bool>? _shouldCaptureFrame;

    // D3D11 对象
    private ID3D11Device* _device;
    private readonly IntPtr[] _stagingTextures = new IntPtr[StagingTextureCount];
    private uint _stagingWidth;
    private uint _stagingHeight;
    private DXGI_FORMAT _stagingFormat;
    private IntPtr _stagingDevice;
    private int _stagingWriteIndex;
    private int _stagingReadyCount;
    private ID3D11Texture2D* _nv12SourceTexture;
    private ID3D11ShaderResourceView* _nv12SourceSrv;
    private ID3D11Buffer* _nv12OutputBuffer;
    private ID3D11UnorderedAccessView* _nv12OutputUav;
    private ID3D11Buffer* _nv12ConstantBuffer;
    private ID3D11ComputeShader* _nv12ComputeShader;
    private IntPtr _nv12ShaderDevice;
    private readonly IntPtr[] _nv12ReadbackBuffers = new IntPtr[StagingTextureCount];
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
    private VideoPixelFormat? _lockedOutputPixelFormat;

    // Present Hook
    private Hook<PresentDelegate>? _presentHook;

    // 状态
    private bool _capturing;
    private bool _disposed;
    private int _targetFps = 60;
    private long _lastFrameTicks;
    private readonly Stopwatch _sw = new();
    private int _frameCount;
    private int _skipCount;
    private int _backpressureSkipCount;
    private int _errorCount;
    private int _consecutiveBlackFrames;
    private bool _presentHookEnabled;
    private bool _diagnosedFirstFrame;
    private bool _diagnosedReadbackFrame;
    private bool _loggedBackBufferSuccess;
    private string _captureMethod = "unknown";

    private const int MaxConsecutiveEmptyFramesBeforeWarning = 3;
    private const int StagingTextureCount = 3;
    private const int DXGI_ERROR_WAS_STILL_DRAWING = unchecked((int)0x887A000A);
    private const string Nv12ComputeShaderSource = @"
Texture2D<float4> SourceTexture : register(t0);
RWByteAddressBuffer Nv12Output : register(u0);

cbuffer Nv12Constants : register(b0)
{
    uint SourceWidth;
    uint SourceHeight;
    uint OutputWidth;
    uint OutputHeight;
};

float3 LoadRgb(uint2 p)
{
    if (p.x >= SourceWidth || p.y >= SourceHeight)
        return float3(0.0, 0.0, 0.0);

    float4 c = SourceTexture.Load(int3(p, 0));
    return c.rgb;
}

uint PackByte(uint value, uint byteIndex)
{
    return (value & 0xffu) << (byteIndex * 8u);
}

uint Pack4(uint b0, uint b1, uint b2, uint b3)
{
    return PackByte(b0, 0u) | PackByte(b1, 1u) | PackByte(b2, 2u) | PackByte(b3, 3u);
}

uint ToY(float3 rgb)
{
    float y = 16.0 + 219.0 * dot(rgb, float3(0.2126, 0.7152, 0.0722));
    return (uint)clamp(round(y), 16.0, 235.0);
}

uint ToU(float3 rgb)
{
    float u = 128.0 + 224.0 * dot(rgb, float3(-0.114572, -0.385428, 0.500000));
    return (uint)clamp(round(u), 16.0, 240.0);
}

uint ToV(float3 rgb)
{
    float v = 128.0 + 224.0 * dot(rgb, float3(0.500000, -0.454153, -0.045847));
    return (uint)clamp(round(v), 16.0, 240.0);
}

[numthreads(16, 16, 1)]
void CSMain(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint blockX = dispatchThreadId.x * 4u;
    uint blockY = dispatchThreadId.y * 2u;
    if (blockX >= OutputWidth || blockY >= OutputHeight)
        return;

    float3 c00 = LoadRgb(uint2(blockX + 0u, blockY + 0u));
    float3 c10 = LoadRgb(uint2(blockX + 1u, blockY + 0u));
    float3 c20 = LoadRgb(uint2(blockX + 2u, blockY + 0u));
    float3 c30 = LoadRgb(uint2(blockX + 3u, blockY + 0u));
    float3 c01 = LoadRgb(uint2(blockX + 0u, blockY + 1u));
    float3 c11 = LoadRgb(uint2(blockX + 1u, blockY + 1u));
    float3 c21 = LoadRgb(uint2(blockX + 2u, blockY + 1u));
    float3 c31 = LoadRgb(uint2(blockX + 3u, blockY + 1u));

    uint yBase0 = blockY * OutputWidth + blockX;
    uint yBase1 = (blockY + 1u) * OutputWidth + blockX;
    Nv12Output.Store(yBase0, Pack4(ToY(c00), ToY(c10), ToY(c20), ToY(c30)));
    Nv12Output.Store(yBase1, Pack4(ToY(c01), ToY(c11), ToY(c21), ToY(c31)));

    uint uvBase = OutputWidth * OutputHeight + (blockY / 2u) * OutputWidth + blockX;
    float3 avg0 = (c00 + c10 + c01 + c11) * 0.25;
    float3 avg1 = (c20 + c30 + c21 + c31) * 0.25;
    Nv12Output.Store(uvBase, Pack4(ToU(avg0), ToV(avg0), ToU(avg1), ToV(avg1)));
}
";

    private static readonly Guid IID_ID3D11Texture2D = new(0x6F15AAF2, 0xD208, 0x4E89, 0x9A, 0xB4, 0x48, 0x95, 0x35, 0xD3, 0x4F, 0x9C);
    private static readonly Guid IID_ID3D11Device = new(0xdb6f6ddb, 0xac77, 0x4e88, 0x82, 0x53, 0x81, 0x9d, 0xf9, 0xbb, 0xf1, 0x40);

    public int CurrentWidth { get; private set; }
    public int CurrentHeight { get; private set; }

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
        _targetFps = targetFps;
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
        _lockedOutputPixelFormat = null;
        _lastFrameTicks = 0;
        _sw.Restart();

        if (TryInstallPresentHook(enable: true))
        {
            _capturing = true;
            _captureMethod = "PresentHook";
            Plugin.Log!.Info($"[Video] Capture started, targetFps={targetFps}, method=PresentHook");
            return true;
        }

        _sw.Stop();
        _captureMethod = "Unavailable";
        Plugin.Log!.Error("[Video] Present hook unavailable; capture was not started.");
        return false;
    }

    public void Stop()
    {
        _capturing = false;

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
        Plugin.Log!.Info($"[Video] Capture stopped. frames={_frameCount}, skipped={_skipCount}, backpressureSkips={_backpressureSkipCount}, errors={_errorCount}, method={_captureMethod}");
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

        return _presentHook!.Original(swapChainPtr, syncInterval, flags);
    }

    private void CaptureBeforePresent(IntPtr swapChainPtr)
    {
        long now = _sw.ElapsedTicks;
        long minInterval = Stopwatch.Frequency / _targetFps;
        if (now - _lastFrameTicks < minInterval) return;
        _lastFrameTicks = now;
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

    // ──────────────────────────────────────────────────────────
    //  共享：纹理处理
    // ──────────────────────────────────────────────────────────

    private bool TryProcessTextureAsNv12(
        ID3D11DeviceContext* ctx,
        ID3D11Texture2D* srcTexture,
        uint width,
        uint height,
        DXGI_FORMAT format,
        long timestampTicks,
        bool readbackDiagnosticsPending)
    {
        if (_nv12Disabled)
            return _lockedOutputPixelFormat == VideoPixelFormat.Nv12 &&
                   SkipLockedNv12Frame("NV12 path is disabled");

        if (_lockedOutputPixelFormat is { } lockedFormat && lockedFormat != VideoPixelFormat.Nv12)
            return false;

        if (!IsNv12SupportedInput(format))
        {
            DisableNv12Path($"unsupported source for NV12 path: {width}x{height}, format={format}");
            return _lockedOutputPixelFormat == VideoPixelFormat.Nv12 &&
                   SkipLockedNv12Frame("source format or dimensions no longer support NV12 conversion");
        }

        uint outputWidth = AlignUp(width, 4);
        uint outputHeight = AlignUp(height, 2);

        try
        {
            if (!EnsureNv12Resources(width, height, outputWidth, outputHeight, format))
            {
                return _lockedOutputPixelFormat == VideoPixelFormat.Nv12 &&
                       SkipLockedNv12Frame("NV12 resources could not be created");
            }

            ID3D11Buffer* writeBuffer = _nv12OutputBuffer;
            ID3D11Buffer* readBuffer = _nv12ReadyCount >= StagingTextureCount - 1
                ? (ID3D11Buffer*)_nv12ReadbackBuffers[(_nv12WriteIndex + 1) % StagingTextureCount]
                : null;

            bool mappedOk = false;
            string? disableNv12AfterReadback = null;
            VideoFrame? frame = null;

            D3D11_MAPPED_SUBRESOURCE mapped;
            try
            {
                ctx->CopyResource((ID3D11Resource*)_nv12SourceTexture, (ID3D11Resource*)srcTexture);
                DispatchNv12Conversion(ctx, width, height, outputWidth, outputHeight, format);

                ID3D11Buffer* stagingWrite = (ID3D11Buffer*)_nv12ReadbackBuffers[_nv12WriteIndex];
                ctx->CopyResource((ID3D11Resource*)stagingWrite, (ID3D11Resource*)writeBuffer);

                _nv12WriteIndex = (_nv12WriteIndex + 1) % StagingTextureCount;
                if (_nv12ReadyCount < StagingTextureCount)
                    _nv12ReadyCount++;

                if (readBuffer == null)
                    return true;

                if (!TryMapReadbackResource(ctx, (ID3D11Resource*)readBuffer, "NV12", out mapped))
                    return true;

                mappedOk = true;

                byte[]? rentedBuffer = VideoFrame.RentBuffer(_nv12DataSize);
                byte[] buffer = rentedBuffer;
                try
                {
                    Marshal.Copy((IntPtr)mapped.pData, buffer, 0, _nv12DataSize);

                    bool isEmptyFrame = IsNv12FrameEmpty(buffer, (int)outputWidth, (int)outputHeight);
                    if (readbackDiagnosticsPending && ClaimReadbackDiagnostic())
                    {
                        Plugin.Log!.Info($"[Video] NV12 path enabled: source={width}x{height}, encoded={outputWidth}x{outputHeight}, bytes={_nv12DataSize}, sourceFormat={format}");
                        DiagnoseNv12Pixels(buffer, (int)outputWidth, (int)outputHeight);
                    }

                    if (isEmptyFrame)
                    {
                        _consecutiveBlackFrames++;
                        LogConsecutiveEmptyFrames("NV12");
                        if (_lockedOutputPixelFormat == null &&
                            _consecutiveBlackFrames >= MaxConsecutiveEmptyFramesBeforeWarning)
                        {
                            disableNv12AfterReadback = "NV12 path produced consecutive empty frames before the encoder locked its input format.";
                        }
                    }
                    else
                    {
                        _consecutiveBlackFrames = 0;
                        long timestampHns = timestampTicks * 10_000_000L / Stopwatch.Frequency;
                        _lockedOutputPixelFormat ??= VideoPixelFormat.Nv12;
                        frame = new VideoFrame(buffer, _nv12DataSize, (int)outputWidth, (int)outputHeight, (int)outputWidth, timestampHns, VideoPixelFormat.Nv12, ownsBuffer: true);
                        rentedBuffer = null;
                    }
                }
                finally
                {
                    if (rentedBuffer != null)
                        VideoFrame.ReturnBuffer(rentedBuffer);
                }
            }
            finally
            {
                if (mappedOk)
                    ctx->Unmap((ID3D11Resource*)readBuffer, 0);
            }

            if (disableNv12AfterReadback != null)
            {
                DisableNv12Path(disableNv12AfterReadback);
                return false;
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
                    Plugin.Log!.Info($"[Video] {CurrentWidth}x{CurrentHeight} NV12 frame #{_frameCount}, method={_captureMethod}");
            }

            return true;
        }
        catch (Exception ex)
        {
            DisableNv12Path($"NV12 GPU conversion failed: {ex.Message}");
            if (_lockedOutputPixelFormat == VideoPixelFormat.Nv12)
                return SkipLockedNv12Frame("NV12 GPU conversion failed after the encoder locked its input format");

            return false;
        }
    }

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
            if (!IsSupportedReadbackFormat(format))
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

            if (TryProcessTextureAsNv12(ctx, srcTexture, width, height, format, timestampTicks, readbackDiagnosticsPending))
                return;

            if (!EnsureStagingTextures(width, height, format))
                return;

            ID3D11Texture2D* writeTexture = (ID3D11Texture2D*)_stagingTextures[_stagingWriteIndex];
            ID3D11Texture2D* readTexture = _stagingReadyCount >= StagingTextureCount - 1
                ? (ID3D11Texture2D*)_stagingTextures[(_stagingWriteIndex + 1) % StagingTextureCount]
                : null;

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

                _stagingWriteIndex = (_stagingWriteIndex + 1) % StagingTextureCount;
                if (_stagingReadyCount < StagingTextureCount)
                    _stagingReadyCount++;

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

                        bool isEmptyFrame = IsFrameEmpty(buffer, (int)width, (int)height, dstStride);

                        if (readbackDiagnosticsPending && ClaimReadbackDiagnostic())
                        {
                            DiagnosePixels(buffer, (int)width, (int)height, dstStride);
                        }

                        VideoPixelFormat pixelFormat = GetReadbackPixelFormat(format);

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

    private bool EnsureNv12Resources(uint sourceWidth, uint sourceHeight, uint outputWidth, uint outputHeight, DXGI_FORMAT format)
    {
        int dataSize = checked((int)(outputWidth * outputHeight * 3 / 2));
        if (_nv12OutputBuffer != null &&
            sourceWidth == _nv12SourceWidth &&
            sourceHeight == _nv12SourceHeight &&
            outputWidth == _nv12OutputWidth &&
            outputHeight == _nv12OutputHeight &&
            format == _nv12Format &&
            _nv12Device == (IntPtr)_device &&
            _nv12DataSize == dataSize)
            return true;

        ReleaseNv12Resources();

        if (!EnsureNv12Shader())
            return false;

        D3D11_TEXTURE2D_DESC textureDesc = default;
        textureDesc.Width = sourceWidth;
        textureDesc.Height = sourceHeight;
        textureDesc.MipLevels = 1;
        textureDesc.ArraySize = 1;
        DXGI_FORMAT shaderFormat = GetNv12ShaderReadableFormat(format);
        textureDesc.Format = shaderFormat;
        textureDesc.SampleDesc.Count = 1;
        textureDesc.SampleDesc.Quality = 0;
        textureDesc.Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT;
        textureDesc.BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE;
        textureDesc.CPUAccessFlags = 0;
        textureDesc.MiscFlags = 0;

        ID3D11Texture2D* sourceTexture;
        int hr = _device->CreateTexture2D(&textureDesc, null, &sourceTexture);
        if (hr < 0 || sourceTexture == null)
        {
            DisableNv12Path($"CreateTexture2D(NV12 source) failed: 0x{hr:X8}");
            return false;
        }
        _nv12SourceTexture = sourceTexture;

        D3D11_SHADER_RESOURCE_VIEW_DESC srvDesc = default;
        srvDesc.Format = shaderFormat;
        srvDesc.ViewDimension = D3D_SRV_DIMENSION.D3D_SRV_DIMENSION_TEXTURE2D;
        srvDesc.Texture2D.MipLevels = 1;
        srvDesc.Texture2D.MostDetailedMip = 0;

        ID3D11ShaderResourceView* sourceSrv;
        hr = _device->CreateShaderResourceView((ID3D11Resource*)_nv12SourceTexture, &srvDesc, &sourceSrv);
        if (hr < 0 || sourceSrv == null)
        {
            DisableNv12Path($"CreateShaderResourceView(NV12 source) failed: 0x{hr:X8}");
            return false;
        }
        _nv12SourceSrv = sourceSrv;

        D3D11_BUFFER_DESC outputDesc = default;
        outputDesc.ByteWidth = (uint)dataSize;
        outputDesc.Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT;
        outputDesc.BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_UNORDERED_ACCESS;
        outputDesc.CPUAccessFlags = 0;
        outputDesc.MiscFlags = (uint)D3D11_RESOURCE_MISC_FLAG.D3D11_RESOURCE_MISC_BUFFER_ALLOW_RAW_VIEWS;
        outputDesc.StructureByteStride = 0;

        ID3D11Buffer* outputBuffer;
        hr = _device->CreateBuffer(&outputDesc, null, &outputBuffer);
        if (hr < 0 || outputBuffer == null)
        {
            DisableNv12Path($"CreateBuffer(NV12 output) failed: 0x{hr:X8}");
            return false;
        }
        _nv12OutputBuffer = outputBuffer;

        D3D11_UNORDERED_ACCESS_VIEW_DESC uavDesc = default;
        uavDesc.Format = DXGI_FORMAT.DXGI_FORMAT_R32_TYPELESS;
        uavDesc.ViewDimension = D3D11_UAV_DIMENSION.D3D11_UAV_DIMENSION_BUFFER;
        uavDesc.Buffer.FirstElement = 0;
        uavDesc.Buffer.NumElements = (uint)((dataSize + 3) / 4);
        uavDesc.Buffer.Flags = (uint)D3D11_BUFFER_UAV_FLAG.D3D11_BUFFER_UAV_FLAG_RAW;

        ID3D11UnorderedAccessView* outputUav;
        hr = _device->CreateUnorderedAccessView((ID3D11Resource*)_nv12OutputBuffer, &uavDesc, &outputUav);
        if (hr < 0 || outputUav == null)
        {
            DisableNv12Path($"CreateUnorderedAccessView(NV12 output) failed: 0x{hr:X8}");
            return false;
        }
        _nv12OutputUav = outputUav;

        D3D11_BUFFER_DESC cbDesc = default;
        cbDesc.ByteWidth = 16;
        cbDesc.Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT;
        cbDesc.BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_CONSTANT_BUFFER;

        ID3D11Buffer* constantBuffer;
        hr = _device->CreateBuffer(&cbDesc, null, &constantBuffer);
        if (hr < 0 || constantBuffer == null)
        {
            DisableNv12Path($"CreateBuffer(NV12 constants) failed: 0x{hr:X8}");
            return false;
        }
        _nv12ConstantBuffer = constantBuffer;

        D3D11_BUFFER_DESC stagingDesc = default;
        stagingDesc.ByteWidth = (uint)dataSize;
        stagingDesc.Usage = D3D11_USAGE.D3D11_USAGE_STAGING;
        stagingDesc.BindFlags = 0;
        stagingDesc.CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ;
        stagingDesc.MiscFlags = 0;
        stagingDesc.StructureByteStride = 0;

        for (int i = 0; i < StagingTextureCount; i++)
        {
            ID3D11Buffer* readbackBuffer;
            hr = _device->CreateBuffer(&stagingDesc, null, &readbackBuffer);
            if (hr < 0 || readbackBuffer == null)
            {
                DisableNv12Path($"CreateBuffer(NV12 readback #{i}) failed: 0x{hr:X8}");
                return false;
            }

            _nv12ReadbackBuffers[i] = (IntPtr)readbackBuffer;
        }

        _nv12SourceWidth = sourceWidth;
        _nv12SourceHeight = sourceHeight;
        _nv12OutputWidth = outputWidth;
        _nv12OutputHeight = outputHeight;
        _nv12Format = format;
        _nv12Device = (IntPtr)_device;
        _nv12DataSize = dataSize;
        _nv12WriteIndex = 0;
        _nv12ReadyCount = 0;
        return true;
    }

    private bool EnsureNv12Shader()
    {
        if (_nv12ComputeShader != null && _nv12ShaderDevice == (IntPtr)_device)
            return true;

        if (_nv12ComputeShader != null)
            ReleaseNv12Shader();

        byte[] shaderBytes = System.Text.Encoding.ASCII.GetBytes(Nv12ComputeShaderSource);
        IntPtr entryPoint = Marshal.StringToHGlobalAnsi("CSMain");
        IntPtr target = Marshal.StringToHGlobalAnsi("cs_5_0");
        fixed (byte* shaderSource = shaderBytes)
        {
            ID3DBlob* shaderBlob = null;
            ID3DBlob* errorBlob = null;
            int hr = DirectX.D3DCompile(
                shaderSource,
                (nuint)shaderBytes.Length,
                null,
                null,
                null,
                (sbyte*)entryPoint,
                (sbyte*)target,
                0,
                0,
                &shaderBlob,
                &errorBlob);
            try
            {
                if (hr < 0 || shaderBlob == null)
                {
                    string error = errorBlob != null
                        ? Marshal.PtrToStringAnsi((IntPtr)errorBlob->GetBufferPointer(), (int)errorBlob->GetBufferSize()) ?? string.Empty
                        : string.Empty;
                    DisableNv12Path($"D3DCompile(NV12) failed: 0x{hr:X8} {error}");
                    return false;
                }

                ID3D11ComputeShader* computeShader;
                hr = _device->CreateComputeShader(shaderBlob->GetBufferPointer(), shaderBlob->GetBufferSize(), null, &computeShader);
                if (hr < 0 || computeShader == null)
                {
                    DisableNv12Path($"CreateComputeShader(NV12) failed: 0x{hr:X8}");
                    return false;
                }
                _nv12ComputeShader = computeShader;
                _nv12ShaderDevice = (IntPtr)_device;

                return true;
            }
            finally
            {
                if (shaderBlob != null) shaderBlob->Release();
                if (errorBlob != null) errorBlob->Release();
                Marshal.FreeHGlobal(entryPoint);
                Marshal.FreeHGlobal(target);
            }
        }
    }

    private void DispatchNv12Conversion(
        ID3D11DeviceContext* ctx,
        uint sourceWidth,
        uint sourceHeight,
        uint outputWidth,
        uint outputHeight,
        DXGI_FORMAT format)
    {
        uint[] constants =
        {
            sourceWidth,
            sourceHeight,
            outputWidth,
            outputHeight,
        };

        fixed (uint* constantData = constants)
            ctx->UpdateSubresource((ID3D11Resource*)_nv12ConstantBuffer, 0, null, constantData, 0, 0);

        ID3D11ShaderResourceView* srv = _nv12SourceSrv;
        ID3D11UnorderedAccessView* uav = _nv12OutputUav;
        ID3D11Buffer* cb = _nv12ConstantBuffer;

        ID3D11ComputeShader* oldShader = null;
        ID3D11ClassInstance** oldClassInstances = stackalloc ID3D11ClassInstance*[256];
        uint oldClassInstanceCount = 256;
        ID3D11Buffer* oldCb = null;
        ID3D11ShaderResourceView* oldSrv = null;
        ID3D11UnorderedAccessView* oldUav = null;
        ctx->CSGetShader(&oldShader, oldClassInstances, &oldClassInstanceCount);
        ctx->CSGetConstantBuffers(0, 1, &oldCb);
        ctx->CSGetShaderResources(0, 1, &oldSrv);
        ctx->CSGetUnorderedAccessViews(0, 1, &oldUav);

        try
        {
            ctx->CSSetShader(_nv12ComputeShader, null, 0);
            ctx->CSSetConstantBuffers(0, 1, &cb);
            ctx->CSSetShaderResources(0, 1, &srv);
            ctx->CSSetUnorderedAccessViews(0, 1, &uav, null);
            ctx->Dispatch((outputWidth / 4 + 15) / 16, (outputHeight / 2 + 15) / 16, 1);
        }
        finally
        {
            ID3D11ShaderResourceView* nullSrv = null;
            ID3D11UnorderedAccessView* nullUav = null;
            ID3D11Buffer* nullCb = null;
            ctx->CSSetShaderResources(0, 1, &nullSrv);
            ctx->CSSetUnorderedAccessViews(0, 1, &nullUav, null);
            ctx->CSSetConstantBuffers(0, 1, &nullCb);

            ctx->CSSetShader(oldShader, oldClassInstances, oldClassInstanceCount);
            ctx->CSSetConstantBuffers(0, 1, &oldCb);
            ctx->CSSetShaderResources(0, 1, &oldSrv);
            ctx->CSSetUnorderedAccessViews(0, 1, &oldUav, null);

            if (oldShader != null) oldShader->Release();
            if (oldCb != null) oldCb->Release();
            if (oldSrv != null) oldSrv->Release();
            if (oldUav != null) oldUav->Release();
            for (uint i = 0; i < oldClassInstanceCount; i++)
            {
                if (oldClassInstances[i] != null)
                    oldClassInstances[i]->Release();
            }
        }
    }

    private static bool IsSupportedReadbackFormat(DXGI_FORMAT format)
        => format == DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM ||
           format == DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM_SRGB ||
           format == DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_TYPELESS ||
           format == DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM ||
           format == DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM_SRGB ||
           format == DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_TYPELESS;

    private static bool IsNv12SupportedInput(DXGI_FORMAT format) => IsSupportedReadbackFormat(format);

    private static bool IsRgbaFormat(DXGI_FORMAT format)
        => format == DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM ||
           format == DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM_SRGB ||
           format == DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_TYPELESS;

    private static VideoPixelFormat GetReadbackPixelFormat(DXGI_FORMAT format)
        => IsRgbaFormat(format) ? VideoPixelFormat.Rgba : VideoPixelFormat.Bgra;

    private static DXGI_FORMAT GetNv12ShaderReadableFormat(DXGI_FORMAT format)
        => format switch
        {
            DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM_SRGB or
            DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_TYPELESS => DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
            DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM_SRGB or
            DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_TYPELESS => DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM,
            _ => format,
        };

    private static uint AlignUp(uint value, uint alignment)
        => (value + alignment - 1) / alignment * alignment;

    private void DisableNv12Path(string reason)
    {
        _nv12Disabled = true;
        ReleaseNv12Resources();
        if (!_nv12FallbackLogged)
        {
            _nv12FallbackLogged = true;
            Plugin.Log!.Warning($"[Video] NV12 GPU readback disabled; falling back to BGRA readback. {reason}");
        }
    }

    private static bool IsNv12FrameEmpty(byte[] buffer, int width, int height)
    {
        int[] xs = { width / 4, width / 2, width * 3 / 4 };
        int[] ys = { height / 4, height / 2, height * 3 / 4 };
        foreach (int y in ys)
        {
            foreach (int x in xs)
            {
                int evenX = x & ~1;
                int evenY = y & ~1;
                int uvBase = width * height + (evenY / 2) * width + evenX;
                if (buffer[y * width + x] != 0 || buffer[uvBase] != 0 || buffer[uvBase + 1] != 0)
                    return false;
            }
        }

        return true;
    }

    private static void DiagnoseNv12Pixels(byte[] buffer, int width, int height)
    {
        (int x, int y)[] pts =
        {
            (width / 4, height / 4),
            (width / 2, height / 2),
            (width * 3 / 4, height * 3 / 4),
        };

        var sb = new System.Text.StringBuilder();
        sb.Append("[Video] NV12 diagnostics:");
        foreach (var (x, y) in pts)
        {
            int evenX = x & ~1;
            int evenY = y & ~1;
            int yValue = buffer[y * width + x];
            int uvBase = width * height + (evenY / 2) * width + evenX;
            int uValue = buffer[uvBase];
            int vValue = buffer[uvBase + 1];
            sb.Append($" ({x},{y})=[Y{yValue},U{uValue},V{vValue}]");
        }

        Plugin.Log!.Info(sb.ToString());
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

    private void ReleaseNv12Resources()
    {
        if (_nv12SourceSrv != null)
        {
            _nv12SourceSrv->Release();
            _nv12SourceSrv = null;
        }

        if (_nv12SourceTexture != null)
        {
            _nv12SourceTexture->Release();
            _nv12SourceTexture = null;
        }

        if (_nv12OutputUav != null)
        {
            _nv12OutputUav->Release();
            _nv12OutputUav = null;
        }

        if (_nv12OutputBuffer != null)
        {
            _nv12OutputBuffer->Release();
            _nv12OutputBuffer = null;
        }

        if (_nv12ConstantBuffer != null)
        {
            _nv12ConstantBuffer->Release();
            _nv12ConstantBuffer = null;
        }

        for (int i = 0; i < _nv12ReadbackBuffers.Length; i++)
        {
            if (_nv12ReadbackBuffers[i] == IntPtr.Zero)
                continue;

            ((ID3D11Buffer*)_nv12ReadbackBuffers[i])->Release();
            _nv12ReadbackBuffers[i] = IntPtr.Zero;
        }

        _nv12SourceWidth = 0;
        _nv12SourceHeight = 0;
        _nv12OutputWidth = 0;
        _nv12OutputHeight = 0;
        _nv12Format = 0;
        _nv12Device = IntPtr.Zero;
        _nv12DataSize = 0;
        _nv12WriteIndex = 0;
        _nv12ReadyCount = 0;
    }

    private void ReleaseNv12Shader()
    {
        if (_nv12ComputeShader != null)
        {
            _nv12ComputeShader->Release();
            _nv12ComputeShader = null;
        }

        _nv12ShaderDevice = IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Stop();

        ReleaseStagingTextures();
        ReleaseNv12Resources();
        ReleaseNv12Shader();
    }
}

/// <summary>一帧视频画面的数据。</summary>
internal enum VideoPixelFormat
{
    Bgra,
    Rgba,
    Nv12,
}

internal sealed class VideoFrame
{
    private readonly bool _ownsBuffer;
    private int _bufferReturned;

    public VideoFrame(
        byte[] data,
        int dataLength,
        int width,
        int height,
        int stride,
        long timestampHns,
        VideoPixelFormat pixelFormat,
        bool ownsBuffer = false)
    {
        Data = data;
        DataLength = dataLength;
        Width = width;
        Height = height;
        Stride = stride;
        TimestampHns = timestampHns;
        PixelFormat = pixelFormat;
        _ownsBuffer = ownsBuffer;
    }

    public byte[] Data { get; }
    public int DataLength { get; }
    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public long TimestampHns { get; }
    public VideoPixelFormat PixelFormat { get; }

    public static byte[] RentBuffer(int minimumLength)
        => System.Buffers.ArrayPool<byte>.Shared.Rent(minimumLength);

    public static void ReturnBuffer(byte[] buffer)
        => System.Buffers.ArrayPool<byte>.Shared.Return(buffer);

    public void ReturnBuffer()
    {
        if (!_ownsBuffer)
            return;

        if (System.Threading.Interlocked.Exchange(ref _bufferReturned, 1) == 0)
            ReturnBuffer(Data);
    }
}
