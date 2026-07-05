using Recorder.Diagnostics;
using System;
using System.Diagnostics;
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
            RecordingDiagnosticLog.WriteNativeFailure("Video", reason);
            PreferD3D11TextureFrames = false;
            return false;
        }

        DXGI_FORMAT sharedFormat = VideoCaptureFormats.GetNativeSharedFormat(format);
        if (!EnsureNativeSharedTexture(width, height, sharedFormat))
            return false;

        D3D11SharedTextureMailbox? mailbox = _nativeSharedMailbox;
        if (mailbox == null)
            return false;

        ID3D11Texture2D* sharedTexture = (ID3D11Texture2D*)_nativeSharedTexture;
        ctx->CopyResource((ID3D11Resource*)sharedTexture, (ID3D11Resource*)srcTexture);

        long timestampHns = timestampTicks * 10_000_000L / Stopwatch.Frequency;
        mailbox.Publish(timestampHns);

        VideoFrame frame = new(mailbox, timestampHns);
        bool delivered = false;
        try
        {
            _onFrame(frame);
            delivered = true;
            _frameCount++;

            if (_frameCount % 300 == 0)
                Plugin.Log!.Info($"[Video] {CurrentWidth}x{CurrentHeight} D3D11 shared texture update #{_frameCount}, method={_captureMethod}");

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
        _nativeSharedMailbox?.Dispose();
        _nativeSharedMailbox = null;

        if (_nativeSharedTexture != IntPtr.Zero)
        {
            ((ID3D11Texture2D*)_nativeSharedTexture)->Release();
            _nativeSharedTexture = IntPtr.Zero;
        }

        _nativeSharedHandle = IntPtr.Zero;

        if (_nativeSharedDevice != IntPtr.Zero)
        {
            ((ID3D11Device*)_nativeSharedDevice)->Release();
            _nativeSharedDevice = IntPtr.Zero;
        }

        _nativeSharedWidth = 0;
        _nativeSharedHeight = 0;
        _nativeSharedFormat = 0;
    }

    private bool EnsureNativeSharedTexture(uint width, uint height, DXGI_FORMAT format)
    {
        if (_nativeSharedTexture != IntPtr.Zero &&
            _nativeSharedMailbox != null &&
            width == _nativeSharedWidth &&
            height == _nativeSharedHeight &&
            format == _nativeSharedFormat &&
            _nativeSharedDevice == (IntPtr)_device)
        {
            return true;
        }

        ReleaseNativeSharedTextures();

        if (!TryCreateNativeSharedTexture(width, height, format, out ID3D11Texture2D* texture, out IntPtr sharedHandle, out string error))
        {
            DisableNativeSharedPath(error);
            ReleaseNativeSharedTextures();
            return false;
        }

        _device->AddRef();
        _nativeSharedDevice = (IntPtr)_device;
        _nativeSharedTexture = (IntPtr)texture;
        _nativeSharedHandle = sharedHandle;
        _nativeSharedWidth = width;
        _nativeSharedHeight = height;
        _nativeSharedFormat = format;
        _nativeSharedMailbox = new D3D11SharedTextureMailbox(
            _nativeSharedDevice,
            _nativeSharedTexture,
            _nativeSharedHandle,
            (int)format,
            (int)width,
            (int)height);

        Plugin.Log!.Info($"[Video] native shared texture ready: {width}x{height}, format={format}, slots=1, sync=plain-shared, bind=shader-resource");
        return true;
    }

    private bool TryCreateNativeSharedTexture(
        uint width,
        uint height,
        DXGI_FORMAT format,
        out ID3D11Texture2D* texture,
        out IntPtr sharedHandle,
        out string error)
    {
        texture = null;
        sharedHandle = IntPtr.Zero;

        D3D11_TEXTURE2D_DESC desc = default;
        desc.Width = width;
        desc.Height = height;
        desc.MipLevels = 1;
        desc.ArraySize = 1;
        desc.Format = format;
        desc.SampleDesc.Count = 1;
        desc.SampleDesc.Quality = 0;
        desc.Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT;
        desc.BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE;
        desc.CPUAccessFlags = 0;
        desc.MiscFlags = D3D11ResourceMiscShared;

        ID3D11Texture2D* candidate;
        int hr = _device->CreateTexture2D(&desc, null, &candidate);
        if (hr < 0 || candidate == null)
        {
            error = $"CreateTexture2D(shared native texture) failed: 0x{hr:X8}; desc={width}x{height}, format={format}, usage={D3D11_USAGE.D3D11_USAGE_DEFAULT}, bind=0x{desc.BindFlags:X}, misc=0x{desc.MiscFlags:X}, sample=1/0.";
            return false;
        }

        if (!D3D11InteropHelpers.TryGetSharedHandle((IntPtr)candidate, out IntPtr candidateHandle) ||
            candidateHandle == IntPtr.Zero)
        {
            candidate->Release();
            error = $"CreateTexture2D(shared native texture) succeeded but IDXGIResource shared handle was unavailable; desc={width}x{height}, format={format}.";
            return false;
        }

        texture = candidate;
        sharedHandle = candidateHandle;
        error = string.Empty;
        return true;
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
        Plugin.Log!.Info($"[Video] Native shared texture update skipped. busySkips={_nativeSharedBusySkipCount}{suffix}");
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
