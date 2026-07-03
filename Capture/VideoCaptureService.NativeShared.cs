using Recorder.Encoding;
using Recorder.Diagnostics;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using TerraFX.Interop.DirectX;
using DXGI_FORMAT = TerraFX.Interop.DirectX.DXGI_FORMAT;

namespace Recorder.Capture;

internal sealed unsafe partial class VideoCaptureService
{
    private bool TryProcessTextureAsD3D11(
        ID3D11DeviceContext* ctx,
        ID3D11Texture2D* srcTexture,
        uint width,
        uint height,
        DXGI_FORMAT format,
        uint sampleCount,
        long timestampTicks)
    {
        if (!PreferD3D11TextureFrames)
            return false;

        if (_nativeSharedDisabled)
            return false;

        if (_device == null || ctx == null || srcTexture == null)
            return false;

        if (sampleCount != 1)
        {
            _skipCount++;
            string reason = $"Native D3D11 texture path does not support MSAA sampleCount={sampleCount}; falling back to readback.";
            if (_skipCount <= 3)
                Plugin.Log!.Warning($"[Video] {reason}");
            RecordingDiagnosticLog.WriteNativeFailure("Video", reason);
            PreferD3D11TextureFrames = false;
            return false;
        }

        if (!VideoCaptureFormats.IsSupportedReadbackFormat(format))
        {
            string reason = $"Native D3D11 texture path does not support source format={format}; falling back to readback.";
            Plugin.Log!.Warning($"[Video] {reason}");
            RecordingDiagnosticLog.WriteNativeFailure(
                "Video",
                reason);
            PreferD3D11TextureFrames = false;
            return false;
        }

        DXGI_FORMAT sharedFormat = VideoCaptureFormats.GetNativeSharedFormat(format);
        if (!EnsureNativeSharedTextures(width, height, sharedFormat))
            return false;

        DrainNativeSharedDropReleases();

        int slot = FindAvailableNativeSharedSlot();
        if (slot < 0)
        {
            SkipNativeSharedFrameForBusySlot();
            return true;
        }

        IntPtr mutexPtr = _nativeSharedMutexes[slot];
        bool usesKeyedMutex = mutexPtr != IntPtr.Zero;
        int hr = 0;
        if (usesKeyedMutex)
        {
            hr = D3D11InteropHelpers.AcquireKeyedMutex(mutexPtr, NativeGameWriteKey, NativeKeyedMutexTimeoutMs);
            if (hr < 0 || hr == WAIT_TIMEOUT)
            {
                Volatile.Write(ref _nativeSharedSlotStates[slot], NativeSlotAvailable);
                SkipNativeSharedFrameForBusySlot();
                return true;
            }
        }

        bool mutexAcquired = usesKeyedMutex;
        try
        {
            ID3D11Texture2D* sharedTexture = (ID3D11Texture2D*)_nativeSharedTextures[slot];
            ctx->CopyResource((ID3D11Resource*)sharedTexture, (ID3D11Resource*)srcTexture);
            if (usesKeyedMutex)
            {
                hr = D3D11InteropHelpers.ReleaseKeyedMutex(mutexPtr, NativeEncoderReadKey);
                mutexAcquired = false;
                if (hr < 0)
                {
                    DisableNativeSharedPath($"IDXGIKeyedMutex.ReleaseSync failed: 0x{hr:X8}");
                    return false;
                }
            }
            else
            {
                WaitForNativeSharedCopy(ctx);
            }
        }
        finally
        {
            if (mutexAcquired)
                _ = D3D11InteropHelpers.ReleaseKeyedMutex(mutexPtr, NativeGameWriteKey);
        }

        long timestampHns = timestampTicks * 10_000_000L / Stopwatch.Frequency;
        Volatile.Write(ref _nativeSharedSlotStates[slot], NativeSlotInFlight);
        _device->AddRef();
        IntPtr devicePtr = (IntPtr)_device;

        VideoFrame frame = new(
            devicePtr,
            _nativeSharedTextures[slot],
            _nativeSharedHandles[slot],
            (int)sharedFormat,
            (int)width,
            (int)height,
            timestampHns,
            submitted =>
            {
                ReleaseNativeSharedSlot(slot, submitted);
                ((ID3D11Device*)devicePtr)->Release();
            });

        bool delivered = false;
        try
        {
            _onFrame(frame);
            delivered = true;
            _frameCount++;

            if (_frameCount % 300 == 0)
                Plugin.Log!.Info($"[Video] {CurrentWidth}x{CurrentHeight} D3D11 texture frame #{_frameCount}, method={_captureMethod}");

            return true;
        }
        finally
        {
            if (!delivered)
                frame.ReturnBuffer();
        }
    }


    private void ReleaseNativeSharedTextures()
    {
        for (int i = 0; i < NativeSharedTextureCount; i++)
        {
            if (_nativeSharedMutexes[i] != IntPtr.Zero)
            {
                Marshal.Release(_nativeSharedMutexes[i]);
                _nativeSharedMutexes[i] = IntPtr.Zero;
            }

            if (_nativeSharedTextures[i] != IntPtr.Zero)
            {
                ((ID3D11Texture2D*)_nativeSharedTextures[i])->Release();
                _nativeSharedTextures[i] = IntPtr.Zero;
            }

            _nativeSharedHandles[i] = IntPtr.Zero;
            Volatile.Write(ref _nativeSharedSlotStates[i], NativeSlotAvailable);
        }

        _nativeSharedWidth = 0;
        _nativeSharedHeight = 0;
        _nativeSharedFormat = 0;
        _nativeSharedDevice = IntPtr.Zero;
        _nativeSharedWriteIndex = 0;
    }

    private bool EnsureNativeSharedTextures(uint width, uint height, DXGI_FORMAT format)
    {
        if (_nativeSharedTextures[0] != IntPtr.Zero &&
            width == _nativeSharedWidth &&
            height == _nativeSharedHeight &&
            format == _nativeSharedFormat &&
            _nativeSharedDevice == (IntPtr)_device)
            return true;

        if (HasOutstandingNativeSharedFrames())
        {
            SkipNativeSharedFrameForBusySlot();
            return false;
        }

        ReleaseNativeSharedTextures();

        for (int i = 0; i < NativeSharedTextureCount; i++)
        {
            if (!TryCreateNativeSharedTexture(width, height, format, i, out ID3D11Texture2D* texture, out IntPtr sharedHandle, out IntPtr mutexPtr, out string error))
            {
                DisableNativeSharedPath(error);
                ReleaseNativeSharedTextures();
                return false;
            }

            if (mutexPtr != IntPtr.Zero)
            {
                int hr = D3D11InteropHelpers.AcquireKeyedMutex(mutexPtr, NativeGameWriteKey, 0);
                if (hr < 0 || hr == WAIT_TIMEOUT)
                {
                    Marshal.Release(mutexPtr);
                    texture->Release();
                    DisableNativeSharedPath($"Initial shared texture mutex acquire failed: 0x{hr:X8}");
                    ReleaseNativeSharedTextures();
                    return false;
                }

                hr = D3D11InteropHelpers.ReleaseKeyedMutex(mutexPtr, NativeGameWriteKey);
                if (hr < 0)
                {
                    Marshal.Release(mutexPtr);
                    texture->Release();
                    DisableNativeSharedPath($"Initial shared texture mutex release failed: 0x{hr:X8}");
                    ReleaseNativeSharedTextures();
                    return false;
                }
            }

            _nativeSharedTextures[i] = (IntPtr)texture;
            _nativeSharedMutexes[i] = mutexPtr;
            _nativeSharedHandles[i] = sharedHandle;
            Volatile.Write(ref _nativeSharedSlotStates[i], NativeSlotAvailable);
        }

        _nativeSharedWidth = width;
        _nativeSharedHeight = height;
        _nativeSharedFormat = format;
        _nativeSharedDevice = (IntPtr)_device;
        _nativeSharedWriteIndex = 0;
        Plugin.Log!.Info($"[Video] Native shared texture ring ready: {width}x{height}, format={format}, slots={NativeSharedTextureCount}");
        return true;
    }

    private bool TryCreateNativeSharedTexture(
        uint width,
        uint height,
        DXGI_FORMAT format,
        int slot,
        out ID3D11Texture2D* texture,
        out IntPtr sharedHandle,
        out IntPtr mutexPtr,
        out string error)
    {
        texture = null;
        sharedHandle = IntPtr.Zero;
        mutexPtr = IntPtr.Zero;

        uint shaderResourceBind = (uint)D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE;
        uint renderTargetBind = (uint)D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET;
        (uint MiscFlags, uint BindFlags, bool RequireKeyedMutex, string Name)[] candidates =
        [
            (D3D11ResourceMiscSharedKeyedMutex, 0, true, "keyed-copy-only"),
            (D3D11ResourceMiscSharedKeyedMutex, renderTargetBind, true, "keyed-rtv"),
            (D3D11ResourceMiscSharedKeyedMutex, shaderResourceBind, true, "keyed-srv"),
            (D3D11ResourceMiscSharedKeyedMutex, renderTargetBind | shaderResourceBind, true, "keyed-rtv-srv"),
            (D3D11ResourceMiscShared, shaderResourceBind, false, "obs-shared-srv"),
            (D3D11ResourceMiscShared, 0, false, "shared-copy-only"),
            (D3D11ResourceMiscShared, renderTargetBind, false, "shared-rtv"),
            (D3D11ResourceMiscShared, renderTargetBind | shaderResourceBind, false, "shared-rtv-srv"),
        ];

        var failures = new System.Text.StringBuilder();
        foreach (var candidateInfo in candidates)
        {
            D3D11_TEXTURE2D_DESC desc = default;
            desc.Width = width;
            desc.Height = height;
            desc.MipLevels = 1;
            desc.ArraySize = 1;
            desc.Format = format;
            desc.SampleDesc.Count = 1;
            desc.SampleDesc.Quality = 0;
            desc.Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT;
            desc.BindFlags = candidateInfo.BindFlags;
            desc.CPUAccessFlags = 0;
            desc.MiscFlags = candidateInfo.MiscFlags;

            ID3D11Texture2D* candidate;
            int hr = _device->CreateTexture2D(&desc, null, &candidate);
            if (hr < 0 || candidate == null)
            {
                failures.Append($" {candidateInfo.Name}(bind=0x{candidateInfo.BindFlags:X},misc=0x{candidateInfo.MiscFlags:X})->0x{hr:X8};");
                continue;
            }

            IntPtr texturePtr = (IntPtr)candidate;
            if (!D3D11InteropHelpers.TryGetSharedHandle(texturePtr, out IntPtr candidateHandle) ||
                candidateHandle == IntPtr.Zero)
            {
                candidate->Release();
                failures.Append($" {candidateInfo.Name}->no IDXGIResource shared handle;");
                continue;
            }

            IntPtr candidateMutex = IntPtr.Zero;
            bool hasKeyedMutex = D3D11InteropHelpers.TryQueryKeyedMutex(texturePtr, out candidateMutex);
            if (candidateInfo.RequireKeyedMutex && !hasKeyedMutex)
            {
                candidate->Release();
                failures.Append($" {candidateInfo.Name}->no IDXGIKeyedMutex;");
                continue;
            }

            if (slot == 0)
            {
                string sync = candidateMutex == IntPtr.Zero ? "plain-shared" : "keyed-mutex";
                Plugin.Log!.Info($"[Video] Native shared texture accepted: {width}x{height}, format={format}, mode={candidateInfo.Name}, sync={sync}, bind=0x{desc.BindFlags:X}, misc=0x{desc.MiscFlags:X}");
            }

            texture = candidate;
            sharedHandle = candidateHandle;
            mutexPtr = candidateMutex;
            error = string.Empty;
            return true;
        }

        error =
            $"CreateTexture2D(shared native #{slot}) failed for all shared texture candidates; " +
            $"desc={width}x{height}, format={format}, usage={D3D11_USAGE.D3D11_USAGE_DEFAULT}, " +
            $"sample=1/0; attempts:{failures}";
        return false;
    }

    private int FindAvailableNativeSharedSlot()
    {
        for (int offset = 0; offset < NativeSharedTextureCount; offset++)
        {
            int slot = (_nativeSharedWriteIndex + offset) % NativeSharedTextureCount;
            if (Volatile.Read(ref _nativeSharedSlotStates[slot]) != NativeSlotAvailable)
                continue;

            if (Interlocked.CompareExchange(
                    ref _nativeSharedSlotStates[slot],
                    NativeSlotInFlight,
                    NativeSlotAvailable) == NativeSlotAvailable)
            {
                _nativeSharedWriteIndex = (slot + 1) % NativeSharedTextureCount;
                return slot;
            }
        }

        return -1;
    }

    private void ReleaseNativeSharedSlot(int slot, bool submitted)
    {
        if (slot < 0 || slot >= NativeSharedTextureCount)
            return;

        if (submitted || _nativeSharedMutexes[slot] == IntPtr.Zero)
        {
            Volatile.Write(ref _nativeSharedSlotStates[slot], NativeSlotAvailable);
            return;
        }

        Volatile.Write(ref _nativeSharedSlotStates[slot], NativeSlotPendingDropRelease);
        DrainNativeSharedDropReleases();
    }

    private void DrainNativeSharedDropReleases()
    {
        for (int i = 0; i < NativeSharedTextureCount; i++)
        {
            if (Volatile.Read(ref _nativeSharedSlotStates[i]) != NativeSlotPendingDropRelease)
                continue;

            IntPtr mutexPtr = _nativeSharedMutexes[i];
            if (mutexPtr == IntPtr.Zero)
            {
                Volatile.Write(ref _nativeSharedSlotStates[i], NativeSlotAvailable);
                continue;
            }

            int hr = D3D11InteropHelpers.AcquireKeyedMutex(mutexPtr, NativeEncoderReadKey, 0);
            ulong releaseKey = NativeGameWriteKey;
            if (hr < 0 || hr == WAIT_TIMEOUT)
            {
                hr = D3D11InteropHelpers.AcquireKeyedMutex(mutexPtr, NativeGameWriteKey, 0);
                if (hr < 0 || hr == WAIT_TIMEOUT)
                    continue;

                releaseKey = NativeGameWriteKey;
            }

            _ = D3D11InteropHelpers.ReleaseKeyedMutex(mutexPtr, releaseKey);
            Volatile.Write(ref _nativeSharedSlotStates[i], NativeSlotAvailable);
        }
    }

    private bool HasOutstandingNativeSharedFrames()
    {
        for (int i = 0; i < _nativeSharedSlotStates.Length; i++)
        {
            if (Volatile.Read(ref _nativeSharedSlotStates[i]) != NativeSlotAvailable)
                return true;
        }

        return false;
    }

    private static void WaitForNativeSharedCopy(ID3D11DeviceContext* ctx)
    {
        if (ctx == null)
            return;

        ID3D11Device* device = null;
        ctx->GetDevice(&device);
        if (device == null)
        {
            ctx->Flush();
            return;
        }

        ID3D11Query* query = null;
        try
        {
            D3D11_QUERY_DESC desc = default;
            desc.Query = D3D11_QUERY.D3D11_QUERY_EVENT;
            int hr = device->CreateQuery(&desc, &query);
            if (hr < 0 || query == null)
            {
                ctx->Flush();
                return;
            }

            ctx->End((ID3D11Asynchronous*)query);
            ctx->Flush();

            Stopwatch waitSw = Stopwatch.StartNew();
            while (waitSw.ElapsedMilliseconds < 2)
            {
                hr = ctx->GetData((ID3D11Asynchronous*)query, null, 0, (uint)D3D11_ASYNC_GETDATA_FLAG.D3D11_ASYNC_GETDATA_DONOTFLUSH);
                if (hr == 0)
                    return;

                Thread.SpinWait(64);
            }
        }
        finally
        {
            if (query != null)
                query->Release();
            device->Release();
        }

        ctx->Flush();
    }

    private void SkipNativeSharedFrameForBusySlot()
    {
        _skipCount++;
        _nativeSharedBusySkipCount++;

        long now = Stopwatch.GetTimestamp();
        bool shouldLog = _nativeSharedBusySkipCount <= 3 ||
                         now - _lastNativeSharedBusyLogTicks >= Stopwatch.Frequency;
        if (!shouldLog)
        {
            _nativeSharedBusySkipSuppressed++;
            return;
        }

        int suppressed = _nativeSharedBusySkipSuppressed;
        _nativeSharedBusySkipSuppressed = 0;
        _lastNativeSharedBusyLogTicks = now;

        string suffix = suppressed > 0 ? $", suppressed={suppressed}" : string.Empty;
        Plugin.Log!.Info($"[Video] Native shared texture ring busy; skipped frame. busySkips={_nativeSharedBusySkipCount}{suffix}");
    }

    private void DisableNativeSharedPath(string reason)
    {
        _nativeSharedDisabled = true;
        PreferD3D11TextureFrames = false;
        ReleaseNativeSharedTextures();

        if (_nativeSharedFallbackLogged)
            return;

        _nativeSharedFallbackLogged = true;
        Plugin.Log!.Warning($"[Video] Native D3D11 shared texture path disabled; falling back to readback. {reason}");
        RecordingDiagnosticLog.WriteNativeFailure(
            "Video",
            $"Native D3D11 shared texture path disabled; fallback=readback. {reason}");
    }


}
