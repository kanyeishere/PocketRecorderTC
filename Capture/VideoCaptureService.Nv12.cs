using Recorder.Encoding;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using TerraFX.Interop.DirectX;
using DXGI_FORMAT = TerraFX.Interop.DirectX.DXGI_FORMAT;

namespace Recorder.Capture;

internal sealed unsafe partial class VideoCaptureService
{
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

        if (!VideoCaptureFormats.IsNv12SupportedInput(format))
        {
            DisableNv12Path($"unsupported source for NV12 path: {width}x{height}, format={format}");
            return _lockedOutputPixelFormat == VideoPixelFormat.Nv12 &&
                   SkipLockedNv12Frame("source format or dimensions no longer support NV12 conversion");
        }

        uint outputWidth = VideoCaptureFormats.AlignUp(width, 4);
        uint outputHeight = VideoCaptureFormats.AlignUp(height, 2);
        long perfStartTicks = 0;
        long perfMapTicks = 0;
        long perfCopyTicks = 0;
        long perfOnFrameTicks = 0;

        try
        {
            if (!EnsureNv12Resources(ctx, width, height, outputWidth, outputHeight, format))
            {
                return _lockedOutputPixelFormat == VideoPixelFormat.Nv12 &&
                       SkipLockedNv12Frame("NV12 resources could not be created");
            }

            perfStartTicks = Stopwatch.GetTimestamp();

            DrainNv12ReleasedSlots(ctx);

            int writeSlot = _nv12WriteIndex;
            if (!IsNv12SlotAvailable(writeSlot))
            {
                SkipNv12FrameForBusySlot(writeSlot);
                return true;
            }

            ID3D11Buffer* writeBuffer = _nv12OutputBuffer;
            int readSlot = _nv12ReadyCount >= StagingTextureCount - 1
                ? (_nv12WriteIndex + 1) % StagingTextureCount
                : -1;
            ID3D11Buffer* readBuffer = readSlot >= 0 ? (ID3D11Buffer*)_nv12ReadbackBuffers[readSlot] : null;

            bool mappedOk = false;
            string? disableNv12AfterReadback = null;
            VideoFrame? frame = null;

            D3D11_MAPPED_SUBRESOURCE mapped;
            try
            {
                long copyStartTicks = Stopwatch.GetTimestamp();
                ctx->CopyResource((ID3D11Resource*)_nv12SourceTexture, (ID3D11Resource*)srcTexture);
                DispatchNv12Conversion(ctx, width, height, outputWidth, outputHeight, format);

                ID3D11Buffer* stagingWrite = (ID3D11Buffer*)_nv12ReadbackBuffers[writeSlot];
                ctx->CopyResource((ID3D11Resource*)stagingWrite, (ID3D11Resource*)writeBuffer);
                perfCopyTicks = Stopwatch.GetTimestamp() - copyStartTicks;

                Volatile.Write(ref _nv12ReadbackSlotStates[writeSlot], Nv12SlotReady);
                _nv12WriteIndex = (writeSlot + 1) % StagingTextureCount;
                if (_nv12ReadyCount < StagingTextureCount)
                    _nv12ReadyCount++;

                if (readBuffer == null)
                    return true;

                if (!IsNv12SlotReady(readSlot))
                {
                    SkipNv12FrameForBusySlot(readSlot);
                    return true;
                }

                long mapStartTicks = Stopwatch.GetTimestamp();
                if (!TryMapReadbackResource(ctx, (ID3D11Resource*)readBuffer, "NV12", out mapped))
                {
                    perfMapTicks = Stopwatch.GetTimestamp() - mapStartTicks;
                    return true;
                }

                perfMapTicks = Stopwatch.GetTimestamp() - mapStartTicks;

                mappedOk = true;

                byte* data = (byte*)mapped.pData;
                try
                {
                    bool isEmptyFrame = VideoFrameContentAnalyzer.IsNv12FrameEmpty(data, (int)outputWidth, (int)outputHeight);
                    if (readbackDiagnosticsPending && ClaimReadbackDiagnostic())
                    {
                        Plugin.Log!.Info($"[Video] NV12 path enabled: source={width}x{height}, encoded={outputWidth}x{outputHeight}, bytes={_nv12DataSize}, sourceFormat={format}");
                        DiagnoseNv12PixelsPtr(data, (int)outputWidth, (int)outputHeight);
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
                        Volatile.Write(ref _nv12ReadbackSlotStates[readSlot], Nv12SlotMapped);
                        mappedOk = false;
                        frame = new VideoFrame(
                            data,
                            _nv12DataSize,
                            (int)outputWidth,
                            (int)outputHeight,
                            (int)outputWidth,
                            timestampHns,
                            VideoPixelFormat.Nv12,
                            () => RequestNv12SlotRelease(readSlot));
                    }
                }
                finally
                {
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
                    long onFrameStartTicks = Stopwatch.GetTimestamp();
                    _onFrame(frame);
                    perfOnFrameTicks = Stopwatch.GetTimestamp() - onFrameStartTicks;
                    delivered = true;
                }
                finally
                {
                    if (!delivered)
                    {
                        if (perfOnFrameTicks == 0)
                            perfOnFrameTicks = Stopwatch.GetTimestamp() - perfStartTicks;
                        frame.ReturnBuffer();
                    }
                }

                DrainNv12ReleasedSlots(ctx);

                ReadOnlySpan<long> perfTicks = stackalloc long[]
                {
                    perfMapTicks,
                    perfCopyTicks,
                    perfOnFrameTicks,
                    Stopwatch.GetTimestamp() - perfStartTicks,
                };
                _nv12PerfStats.Record((int)outputWidth, (int)outputHeight, _nv12DataSize, VideoPixelFormat.Nv12, perfTicks);

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


    private bool EnsureNv12Resources(ID3D11DeviceContext* ctx, uint sourceWidth, uint sourceHeight, uint outputWidth, uint outputHeight, DXGI_FORMAT format)
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

        DrainNv12ReleasedSlots(ctx);
        if (HasOutstandingNv12Readbacks())
        {
            DisableNv12Path("NV12 resources changed while readback slots are still mapped.");
            return false;
        }

        ReleaseNv12Resources(ctx);

        if (!EnsureNv12Shader())
            return false;

        D3D11_TEXTURE2D_DESC textureDesc = default;
        textureDesc.Width = sourceWidth;
        textureDesc.Height = sourceHeight;
        textureDesc.MipLevels = 1;
        textureDesc.ArraySize = 1;
        DXGI_FORMAT shaderFormat = VideoCaptureFormats.GetNv12ShaderReadableFormat(format);
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

        byte[] shaderBytes = System.Text.Encoding.ASCII.GetBytes(Nv12ComputeShader.Source);
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

    private void DisableNv12Path(string reason)
    {
        _nv12Disabled = true;
        if (!HasOutstandingNv12Readbacks())
        {
            ReleaseNv12Resources();
        }
        if (!_nv12FallbackLogged)
        {
            _nv12FallbackLogged = true;
            Plugin.Log!.Warning($"[Video] NV12 GPU readback disabled; falling back to BGRA readback. {reason}");
        }
    }

    private bool IsNv12SlotAvailable(int slot)
        => slot >= 0 && slot < StagingTextureCount &&
           Volatile.Read(ref _nv12ReadbackSlotStates[slot]) == Nv12SlotAvailable;

    private bool IsNv12SlotReady(int slot)
        => slot >= 0 && slot < StagingTextureCount &&
           Volatile.Read(ref _nv12ReadbackSlotStates[slot]) == Nv12SlotReady;

    private bool IsNv12SlotPendingRelease(int slot)
        => slot >= 0 && slot < StagingTextureCount &&
           Volatile.Read(ref _nv12ReadbackSlotStates[slot]) == Nv12SlotPendingRelease;

    private void RequestNv12SlotRelease(int slot)
    {
        if (slot < 0 || slot >= StagingTextureCount)
            return;

        int previous = Interlocked.CompareExchange(ref _nv12ReadbackSlotStates[slot], Nv12SlotPendingRelease, Nv12SlotMapped);
        if (previous == Nv12SlotMapped)
            return;

        if (previous == Nv12SlotReady)
        {
            Interlocked.Exchange(ref _nv12ReadbackSlotStates[slot], Nv12SlotAvailable);
        }
    }

    private void DrainNv12ReleasedSlots(ID3D11DeviceContext* ctx)
    {
        for (int i = 0; i < StagingTextureCount; i++)
        {
            if (!IsNv12SlotPendingRelease(i))
                continue;

            ID3D11Buffer* buffer = (ID3D11Buffer*)_nv12ReadbackBuffers[i];
            if (buffer == null)
                continue;

            ctx->Unmap((ID3D11Resource*)buffer, 0);
            Volatile.Write(ref _nv12ReadbackSlotStates[i], Nv12SlotAvailable);
        }
    }

    private void SkipNv12FrameForBusySlot(int slot)
    {
        if (slot < 0)
            return;

        _nv12BusySkipCount++;

        long now = Stopwatch.GetTimestamp();
        bool shouldLog = _nv12BusySkipCount <= 3 ||
                         now - _lastNv12BusyLogTicks >= Stopwatch.Frequency;
        if (!shouldLog)
        {
            _nv12BusySkipSuppressed++;
            return;
        }

        int suppressed = _nv12BusySkipSuppressed;
        _nv12BusySkipSuppressed = 0;
        _lastNv12BusyLogTicks = now;

        string suffix = suppressed > 0 ? $", suppressed={suppressed}" : string.Empty;
        Plugin.Log!.Info($"[Video] NV12 slot busy, skipped frame. slot={slot}, busySkips={_nv12BusySkipCount}{suffix}");
    }

    private static unsafe void DiagnoseNv12PixelsPtr(byte* data, int width, int height)
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
            int yValue = data[y * width + x];
            int uvBase = width * height + (evenY / 2) * width + evenX;
            int uValue = data[uvBase];
            int vValue = data[uvBase + 1];
            sb.Append($" ({x},{y})=[Y{yValue},U{uValue},V{vValue}]");
        }

        Plugin.Log!.Info(sb.ToString());
    }


    private void ReleaseNv12Resources(ID3D11DeviceContext* drainContext = null)
    {
        if (drainContext != null)
            DrainNv12ReleasedSlots(drainContext);

        int pendingMappedSlots = CountOutstandingNv12Readbacks();
        if (pendingMappedSlots > 0)
            Plugin.Log!.Warning($"[Video] Releasing NV12 resources with {pendingMappedSlots} mapped readback slot(s); skipping cross-thread Unmap.");

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
            _nv12ReadbackSlotStates[i] = Nv12SlotAvailable;
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

    private void DrainDeferredNv12Resources(ID3D11DeviceContext* ctx)
    {
        if (!_nv12Disabled)
            return;

        DrainNv12ReleasedSlots(ctx);
        if (!HasOutstandingNv12Readbacks())
            ReleaseNv12Resources();
    }

    private void RequestNv12StopCleanup()
    {
        if (HasOutstandingNv12Readbacks())
            Volatile.Write(ref _nv12StopCleanupRequested, 1);
    }

    private void WaitForNv12StopCleanup()
    {
        if (Volatile.Read(ref _nv12StopCleanupRequested) == 0)
            return;

        Stopwatch waitSw = Stopwatch.StartNew();
        while (Volatile.Read(ref _nv12StopCleanupRequested) != 0 && waitSw.ElapsedMilliseconds < 250)
            Thread.Sleep(1);

        if (Volatile.Read(ref _nv12StopCleanupRequested) != 0)
            Plugin.Log!.Warning($"[Video] NV12 stop cleanup did not run on the render thread within {waitSw.ElapsedMilliseconds}ms; pending mapped slots will not be unmapped from the finalize thread.");
    }

    private void TryDrainNv12StopCleanup(IntPtr swapChainPtr)
    {
        if (Volatile.Read(ref _nv12StopCleanupRequested) == 0)
            return;

        var swapChain = (IDXGISwapChain*)swapChainPtr;
        Guid iidDev = IID_ID3D11Device;
        ID3D11Device* device = null;
        int hrDev = swapChain->GetDevice(&iidDev, (void**)&device);
        if (hrDev < 0 || device == null)
            return;

        ID3D11DeviceContext* ctx = null;
        device->GetImmediateContext(&ctx);
        try
        {
            if (ctx == null)
                return;

            DrainNv12ReleasedSlots(ctx);
            if (!HasOutstandingNv12Readbacks())
            {
                ReleaseNv12Resources(ctx);
                Volatile.Write(ref _nv12StopCleanupRequested, 0);
            }
        }
        finally
        {
            if (ctx != null)
                ctx->Release();
            device->Release();
        }
    }

    private bool HasOutstandingNv12Readbacks()
    {
        for (int i = 0; i < _nv12ReadbackSlotStates.Length; i++)
        {
            int state = Volatile.Read(ref _nv12ReadbackSlotStates[i]);
            if (state == Nv12SlotMapped || state == Nv12SlotPendingRelease)
                return true;
        }

        return false;
    }

    private int CountOutstandingNv12Readbacks()
    {
        int count = 0;
        for (int i = 0; i < _nv12ReadbackSlotStates.Length; i++)
        {
            int state = Volatile.Read(ref _nv12ReadbackSlotStates[i]);
            if (state == Nv12SlotMapped || state == Nv12SlotPendingRelease)
                count++;
        }

        return count;
    }

}
