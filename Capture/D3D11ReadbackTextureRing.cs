using System;
using TerraFX.Interop.DirectX;
using DXGI_FORMAT = TerraFX.Interop.DirectX.DXGI_FORMAT;

namespace Recorder.Capture;

internal sealed unsafe class D3D11ReadbackTextureRing : IDisposable
{
    private readonly IntPtr[] _textures;
    private uint _width;
    private uint _height;
    private DXGI_FORMAT _format;
    private IntPtr _device;
    private int _writeIndex;
    private int _readyCount;

    public D3D11ReadbackTextureRing(int slotCount)
    {
        _textures = new IntPtr[Math.Max(1, slotCount)];
    }

    public bool Ensure(ID3D11Device* device, uint width, uint height, DXGI_FORMAT format, Action<string> logError)
    {
        if (_textures[0] != IntPtr.Zero &&
            width == _width &&
            height == _height &&
            format == _format &&
            _device == (IntPtr)device)
        {
            return true;
        }

        Release();

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

        for (int i = 0; i < _textures.Length; i++)
        {
            ID3D11Texture2D* newTexture;
            int hr = device->CreateTexture2D(&desc, null, &newTexture);
            if (hr < 0)
            {
                logError($"[Video] CreateTexture2D(staging #{i}) failed: 0x{hr:X8}");
                Release();
                return false;
            }

            _textures[i] = (IntPtr)newTexture;
        }

        _width = width;
        _height = height;
        _format = format;
        _device = (IntPtr)device;
        _writeIndex = 0;
        _readyCount = 0;
        return true;
    }

    public ID3D11Texture2D* GetWriteTexture()
        => (ID3D11Texture2D*)_textures[_writeIndex];

    public ID3D11Texture2D* GetReadTexture()
    {
        return _readyCount >= _textures.Length - 1
            ? (ID3D11Texture2D*)_textures[(_writeIndex + 1) % _textures.Length]
            : null;
    }

    public void MarkWriteSubmitted()
    {
        _writeIndex = (_writeIndex + 1) % _textures.Length;
        if (_readyCount < _textures.Length)
            _readyCount++;
    }

    public void Release()
    {
        for (int i = 0; i < _textures.Length; i++)
        {
            if (_textures[i] == IntPtr.Zero)
                continue;

            ((ID3D11Texture2D*)_textures[i])->Release();
            _textures[i] = IntPtr.Zero;
        }

        _device = IntPtr.Zero;
        _writeIndex = 0;
        _readyCount = 0;
    }

    public void Dispose()
        => Release();
}
