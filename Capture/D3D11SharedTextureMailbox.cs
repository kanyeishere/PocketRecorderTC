using System;
using System.Diagnostics;
using System.Threading;

namespace Recorder.Capture;

internal sealed class D3D11SharedTextureMailbox : IDisposable
{
    private readonly ManualResetEventSlim _firstFrameReady = new(false);
    private long _latestFrameId;
    private long _latestTimestampHns;
    private long _latestPublishTicks;
    private int _disposed;

    public D3D11SharedTextureMailbox(
        IntPtr devicePtr,
        IntPtr texturePtr,
        IntPtr sharedHandle,
        int dxgiFormat,
        int width,
        int height)
    {
        DevicePtr = devicePtr;
        TexturePtr = texturePtr;
        SharedHandle = sharedHandle;
        DxgiFormat = dxgiFormat;
        Width = width;
        Height = height;
    }

    public IntPtr DevicePtr { get; }
    public IntPtr TexturePtr { get; }
    public IntPtr SharedHandle { get; }
    public int DxgiFormat { get; }
    public int Width { get; }
    public int Height { get; }

    public void Publish(long timestampHns)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        Volatile.Write(ref _latestTimestampHns, Math.Max(0, timestampHns));
        Volatile.Write(ref _latestPublishTicks, Stopwatch.GetTimestamp());
        Interlocked.Increment(ref _latestFrameId);
        _firstFrameReady.Set();
    }

    public bool WaitForFirstFrame(int timeoutMs)
        => _firstFrameReady.Wait(timeoutMs);

    public bool TryGetLatest(out D3D11SharedTextureSnapshot snapshot)
    {
        long frameId = Volatile.Read(ref _latestFrameId);
        if (frameId <= 0 ||
            DevicePtr == IntPtr.Zero ||
            SharedHandle == IntPtr.Zero)
        {
            snapshot = default;
            return false;
        }

        snapshot = new D3D11SharedTextureSnapshot(
            DevicePtr,
            SharedHandle,
            DxgiFormat,
            Width,
            Height,
            Volatile.Read(ref _latestTimestampHns),
            Volatile.Read(ref _latestPublishTicks),
            frameId);
        return true;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _firstFrameReady.Set();
        _firstFrameReady.Dispose();
    }
}

internal readonly record struct D3D11SharedTextureSnapshot(
    IntPtr DevicePtr,
    IntPtr SharedHandle,
    int DxgiFormat,
    int Width,
    int Height,
    long SourceTimestampHns,
    long PublishTicks,
    long SourceFrameId);
