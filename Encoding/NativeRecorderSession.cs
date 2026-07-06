using Recorder.Capture;
using Recorder.Recording;
using System;
using System.Threading;

namespace Recorder.Encoding;

internal sealed class NativeRecorderSession : IDisposable
{
    private readonly NativeRecorderRuntime _runtime;
    private IntPtr _handle;
    private int _disposed;

    public NativeRecorderSession(NativeRecorderRuntime runtime, IntPtr handle)
    {
        _runtime = runtime;
        _handle = handle;
    }

    public bool SubmitD3D11Texture(VideoFrame frame, long timestampHns)
    {
        if (_handle == IntPtr.Zero)
            throw new ObjectDisposedException(nameof(NativeRecorderSession));

        if (frame.D3D11SharedHandle == IntPtr.Zero)
            throw new InvalidOperationException("NativeRecorder requires a D3D11 shared texture handle.");
        if (frame.D3D11DevicePtr == IntPtr.Zero)
            throw new InvalidOperationException("NativeRecorder requires the source D3D11 device for adapter matching.");

        return _runtime.SubmitD3D11SharedTexture(
            _handle,
            frame.D3D11DevicePtr,
            frame.D3D11SharedHandle,
            frame.DxgiFormat,
            timestampHns);
    }

    public bool SubmitD3D11Texture(D3D11SharedTextureSnapshot snapshot, long timestampHns)
    {
        if (_handle == IntPtr.Zero)
            throw new ObjectDisposedException(nameof(NativeRecorderSession));

        if (snapshot.SharedHandle == IntPtr.Zero)
            throw new InvalidOperationException("NativeRecorder requires a D3D11 shared texture handle.");
        if (snapshot.DevicePtr == IntPtr.Zero)
            throw new InvalidOperationException("NativeRecorder requires the source D3D11 device for adapter matching.");

        return _runtime.SubmitD3D11SharedTexture(
            _handle,
            snapshot.DevicePtr,
            snapshot.SharedHandle,
            snapshot.DxgiFormat,
            timestampHns);
    }

    public void SubmitAudio(AudioPacket packet)
    {
        if (_handle == IntPtr.Zero)
            throw new ObjectDisposedException(nameof(NativeRecorderSession));

        _runtime.SubmitAudio(_handle, packet.Data, packet.Data.Length, packet.TimestampHns);
    }

    public void Stop()
    {
        IntPtr handle = _handle;
        if (handle == IntPtr.Zero)
            return;

        _runtime.Stop(handle);
    }

    public string GetLastStatus()
        => _runtime.GetLastStatus();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        IntPtr handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
        _runtime.Destroy(handle);
    }
}

internal readonly record struct NativeRecorderProbeResult(
    bool IsAvailable,
    string Message,
    string? DiagnosticDetails = null)
{
    public static NativeRecorderProbeResult Available(string message, string? diagnosticDetails = null)
        => new(true, message, diagnosticDetails);

    public static NativeRecorderProbeResult Unavailable(string reason, string? diagnosticDetails = null)
        => new(false, reason, diagnosticDetails);
}
