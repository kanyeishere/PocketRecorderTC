using System;
using System.Threading;

namespace Recorder.Capture;

/// <summary>一帧视频画面的数据。</summary>
internal sealed unsafe class VideoFrame
{
    private readonly bool _ownsBuffer;
    private readonly Action? _onConsumed;
    private int _bufferReturned;
    private bool _isNative;
    private bool _isD3D11Texture;
    private byte* _dataPtr;

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

    public VideoFrame(
        byte* dataPtr,
        int dataLength,
        int width,
        int height,
        int stride,
        long timestampHns,
        VideoPixelFormat pixelFormat,
        Action onConsumed)
    {
        _isNative = true;
        _dataPtr = dataPtr;
        Data = Array.Empty<byte>();
        DataLength = dataLength;
        Width = width;
        Height = height;
        Stride = stride;
        TimestampHns = timestampHns;
        PixelFormat = pixelFormat;
        _onConsumed = onConsumed;
    }

    public VideoFrame(
        IntPtr d3d11DevicePtr,
        IntPtr d3d11TexturePtr,
        IntPtr d3d11SharedHandle,
        int dxgiFormat,
        int width,
        int height,
        long timestampHns,
        D3D11SharedTextureMailbox? mailbox = null)
    {
        _isD3D11Texture = true;
        Data = Array.Empty<byte>();
        DataLength = 0;
        Width = width;
        Height = height;
        Stride = 0;
        TimestampHns = timestampHns;
        PixelFormat = VideoPixelFormat.D3D11Texture;
        D3D11DevicePtr = d3d11DevicePtr;
        D3D11TexturePtr = d3d11TexturePtr;
        D3D11SharedHandle = d3d11SharedHandle;
        DxgiFormat = dxgiFormat;
        D3D11Mailbox = mailbox;
    }

    public VideoFrame(D3D11SharedTextureMailbox mailbox, long timestampHns)
        : this(
            mailbox.DevicePtr,
            mailbox.TexturePtr,
            mailbox.SharedHandle,
            mailbox.DxgiFormat,
            mailbox.Width,
            mailbox.Height,
            timestampHns,
            mailbox)
    {
    }

    public byte[] Data { get; }
    public int DataLength { get; }
    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public long TimestampHns { get; }
    public VideoPixelFormat PixelFormat { get; }
    public byte* DataPtr => _dataPtr;
    public bool IsNative => _isNative;
    public bool IsD3D11Texture => _isD3D11Texture;
    public IntPtr D3D11DevicePtr { get; }
    public IntPtr D3D11TexturePtr { get; }
    public IntPtr D3D11SharedHandle { get; }
    public int DxgiFormat { get; }
    public D3D11SharedTextureMailbox? D3D11Mailbox { get; }

    public VideoFrame DetachToManagedCopyIfNative()
    {
        if (_isD3D11Texture)
            throw new InvalidOperationException("D3D11 texture frames cannot be detached to managed rawvideo buffers.");

        if (!_isNative)
            return this;

        byte[] buffer = RentBuffer(DataLength);
        try
        {
            new ReadOnlySpan<byte>(_dataPtr, DataLength).CopyTo(buffer.AsSpan(0, DataLength));
            var managedFrame = new VideoFrame(
                buffer,
                DataLength,
                Width,
                Height,
                Stride,
                TimestampHns,
                PixelFormat,
                ownsBuffer: true);
            ReturnBuffer();
            return managedFrame;
        }
        catch
        {
            ReturnBuffer(buffer);
            throw;
        }
    }

    public static byte[] RentBuffer(int minimumLength)
        => System.Buffers.ArrayPool<byte>.Shared.Rent(minimumLength);

    public static void ReturnBuffer(byte[] buffer)
        => System.Buffers.ArrayPool<byte>.Shared.Return(buffer);

    public void ReturnBuffer()
    {
        if (_isD3D11Texture)
        {
            Interlocked.Exchange(ref _bufferReturned, 1);
            return;
        }

        if (_isNative)
        {
            if (Interlocked.Exchange(ref _bufferReturned, 1) == 0)
                _onConsumed?.Invoke();
            return;
        }

        if (!_ownsBuffer)
            return;

        if (Interlocked.Exchange(ref _bufferReturned, 1) == 0)
            ReturnBuffer(Data);
    }
}
