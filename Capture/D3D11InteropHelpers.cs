using System;
using System.Runtime.InteropServices;

namespace Recorder.Capture;

internal static unsafe class D3D11InteropHelpers
{
    private static readonly Guid IID_ID3D11Device = new(0xDB6F6DDB, 0xAC77, 0x4E88, 0x82, 0x53, 0x81, 0x9D, 0xF9, 0xBB, 0xF1, 0x40);
    private static readonly Guid IID_IDXGIDevice = new(0x54EC77FA, 0x1377, 0x44E6, 0x8C, 0x32, 0x88, 0xFD, 0x5F, 0x44, 0xC8, 0x4C);
    private static readonly Guid IID_IDXGIResource = new(0x035F3AB4, 0x482E, 0x4E50, 0xB4, 0x1F, 0x8A, 0x7F, 0x8B, 0xD8, 0x96, 0x0B);
    private static readonly Guid IID_IDXGIKeyedMutex = new(0x9D8E1289, 0xD7B3, 0x465F, 0x81, 0x26, 0x25, 0x0E, 0x34, 0x9A, 0xF8, 0x5D);

    public static bool TryGetD3D11DeviceFromSwapChain(IntPtr swapChainPtr, out IntPtr devicePtr, out string error)
    {
        devicePtr = IntPtr.Zero;
        error = string.Empty;

        if (swapChainPtr == IntPtr.Zero)
        {
            error = "swapchain pointer is null";
            return false;
        }

        void** vtable = *(void***)swapChainPtr;
        // IDXGISwapChain inherits IDXGIDeviceSubObject; GetDevice is slot 7.
        var getDevice = (delegate* unmanaged[Stdcall]<void*, Guid*, void**, int>)vtable[7];
        Guid iid = IID_ID3D11Device;
        void* device = null;
        int hr = getDevice((void*)swapChainPtr, &iid, &device);
        if (hr < 0 || device == null)
        {
            error = $"IDXGISwapChain::GetDevice(ID3D11Device) failed: 0x{hr:X8}";
            return false;
        }

        devicePtr = (IntPtr)device;
        return true;
    }

    public static bool TryGetAdapterInfoFromD3D11Device(IntPtr devicePtr, out D3D11AdapterInfo adapterInfo, out string error)
    {
        adapterInfo = default;
        error = string.Empty;

        if (devicePtr == IntPtr.Zero)
        {
            error = "D3D11 device pointer is null";
            return false;
        }

        if (!TryQueryInterface(devicePtr, IID_IDXGIDevice, out IntPtr dxgiDevicePtr))
        {
            error = "ID3D11Device::QueryInterface(IDXGIDevice) failed";
            return false;
        }

        IntPtr adapterPtr = IntPtr.Zero;
        try
        {
            void** dxgiDeviceVtable = *(void***)dxgiDevicePtr;
            // IDXGIDevice inherits IDXGIObject; GetAdapter is slot 7.
            var getAdapter = (delegate* unmanaged[Stdcall]<void*, void**, int>)dxgiDeviceVtable[7];
            void* adapter = null;
            int hr = getAdapter((void*)dxgiDevicePtr, &adapter);
            if (hr < 0 || adapter == null)
            {
                error = $"IDXGIDevice::GetAdapter failed: 0x{hr:X8}";
                return false;
            }

            adapterPtr = (IntPtr)adapter;
            void** adapterVtable = *(void***)adapterPtr;
            // IDXGIAdapter::GetDesc is slot 8.
            var getDesc = (delegate* unmanaged[Stdcall]<void*, DxgiAdapterDesc*, int>)adapterVtable[8];
            DxgiAdapterDesc desc = default;
            hr = getDesc((void*)adapterPtr, &desc);
            if (hr < 0)
            {
                error = $"IDXGIAdapter::GetDesc failed: 0x{hr:X8}";
                return false;
            }

            adapterInfo = new D3D11AdapterInfo(
                desc.VendorId,
                desc.DeviceId,
                desc.SubSysId,
                desc.Revision,
                ReadAdapterDescription(desc),
                desc.AdapterLuidHighPart,
                desc.AdapterLuidLowPart);
            return true;
        }
        finally
        {
            if (adapterPtr != IntPtr.Zero)
                Marshal.Release(adapterPtr);
            Marshal.Release(dxgiDevicePtr);
        }
    }

    public static bool TryGetSharedHandle(IntPtr texturePtr, out IntPtr sharedHandle)
    {
        sharedHandle = IntPtr.Zero;
        if (!TryQueryInterface(texturePtr, IID_IDXGIResource, out IntPtr resourcePtr))
            return false;

        try
        {
            void** vtable = *(void***)resourcePtr;
            // IDXGIResource inherits IDXGIDeviceSubObject; GetSharedHandle is slot 8.
            var getSharedHandle = (delegate* unmanaged[Stdcall]<void*, IntPtr*, int>)vtable[8];
            IntPtr handle = IntPtr.Zero;
            int hr = getSharedHandle((void*)resourcePtr, &handle);
            if (hr < 0)
                return false;

            sharedHandle = handle;
            return true;
        }
        finally
        {
            Marshal.Release(resourcePtr);
        }
    }

    public static bool TryQueryKeyedMutex(IntPtr texturePtr, out IntPtr mutexPtr)
        => TryQueryInterface(texturePtr, IID_IDXGIKeyedMutex, out mutexPtr);

    public static int AcquireKeyedMutex(IntPtr mutexPtr, ulong key, uint milliseconds)
    {
        if (mutexPtr == IntPtr.Zero)
            return unchecked((int)0x80004003);

        void** vtable = *(void***)mutexPtr;
        var acquireSync = (delegate* unmanaged[Stdcall]<void*, ulong, uint, int>)vtable[8];
        return acquireSync((void*)mutexPtr, key, milliseconds);
    }

    public static int ReleaseKeyedMutex(IntPtr mutexPtr, ulong key)
    {
        if (mutexPtr == IntPtr.Zero)
            return unchecked((int)0x80004003);

        void** vtable = *(void***)mutexPtr;
        var releaseSync = (delegate* unmanaged[Stdcall]<void*, ulong, int>)vtable[9];
        return releaseSync((void*)mutexPtr, key);
    }

    private static bool TryQueryInterface(IntPtr unknownPtr, Guid iid, out IntPtr interfacePtr)
    {
        interfacePtr = IntPtr.Zero;
        if (unknownPtr == IntPtr.Zero)
            return false;

        int hr = Marshal.QueryInterface(unknownPtr, in iid, out interfacePtr);
        return hr >= 0 && interfacePtr != IntPtr.Zero;
    }

    private static string ReadAdapterDescription(DxgiAdapterDesc desc)
    {
        char[] chars = new char[128];
        int length = 0;
        while (length < chars.Length)
        {
            char c = desc.Description[length];
            if (c == '\0')
                break;

            chars[length] = c;
            length++;
        }

        return new string(chars, 0, length);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DxgiAdapterDesc
    {
        public fixed char Description[128];
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public uint AdapterLuidLowPart;
        public int AdapterLuidHighPart;
    }
}

internal readonly record struct D3D11AdapterInfo(
    uint VendorId,
    uint DeviceId,
    uint SubSysId,
    uint Revision,
    string AdapterName,
    int AdapterLuidHighPart,
    uint AdapterLuidLowPart)
{
    public string Vendor => VendorId switch
    {
        0x10DE => "nvidia",
        0x1002 => "amd",
        0x8086 => "intel",
        _ => "unknown",
    };

    public string DiagnosticSummary
        => $"vendor={Vendor}, vendorId=0x{VendorId:X4}, deviceId=0x{DeviceId:X4}, adapter={ValueOrNone(AdapterName)}, luid={AdapterLuidHighPart}:{AdapterLuidLowPart}";

    private static string ValueOrNone(string? value)
        => string.IsNullOrWhiteSpace(value) ? "<none>" : value;
}
