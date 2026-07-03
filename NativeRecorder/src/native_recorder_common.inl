#include <algorithm>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstdio>
#include <cstdint>
#include <cstring>
#include <deque>
#include <exception>
#include <memory>
#include <mutex>
#include <new>
#include <thread>
#include <string>
#include <utility>
#include <vector>

#include <windows.h>
#include <d3d10_1.h>
#include <d3d11.h>
#include <dxgi1_2.h>
#include <wrl/client.h>

#include "NvEncoder/NvEncoderD3D11.h"
#include "common/AMFFactory.h"
#include "include/components/VideoEncoderVCE.h"
#include "include/components/VideoEncoderHEVC.h"

extern "C"
{
#include <libavcodec/avcodec.h>
#include <libavcodec/defs.h>
#include <libavformat/avformat.h>
#include <libavutil/avutil.h>
#include <libavutil/channel_layout.h>
#include <libavutil/dict.h>
#include <libavutil/error.h>
#include <libavutil/frame.h>
#include <libavutil/mathematics.h>
#include <libavutil/mem.h>
#include <libavutil/opt.h>
#include <libavutil/samplefmt.h>
#include <libswresample/swresample.h>
}

using Microsoft::WRL::ComPtr;

namespace
{
constexpr UINT kNvidiaVendorId = 0x10DE;
constexpr UINT kAmdVendorId = 0x1002;
constexpr DWORD kInvalidStream = 0xFFFFFFFFu;
constexpr UINT64 kGameWriteKey = 0;
constexpr UINT64 kEncoderReadKey = 1;
constexpr DWORD kSharedTextureAcquireTimeoutMs = 16;
constexpr size_t kNativeNv12ConversionPoolSize = 8;
constexpr size_t kAmfEncoderInputPoolSize = 8;
constexpr size_t kMaxNativeVideoQueueItems = 8;
constexpr size_t kMaxMuxQueueItems = 512;

std::mutex g_error_mutex;
std::string g_last_error;

void set_last_error(const std::string& message)
{
    std::lock_guard lock(g_error_mutex);
    g_last_error = message;
}

void set_last_error(const char* message)
{
    set_last_error(std::string(message != nullptr ? message : ""));
}

std::string get_last_error_copy()
{
    std::lock_guard lock(g_error_mutex);
    return g_last_error;
}

void copy_text(char* destination, size_t capacity, const char* source)
{
    if (destination == nullptr || capacity == 0)
        return;

    const char* text = source != nullptr ? source : "";
    size_t length = std::min(capacity - 1, std::strlen(text));
    std::memcpy(destination, text, length);
    destination[length] = '\0';
}

std::string wide_to_utf8(const wchar_t* text)
{
    if (text == nullptr || *text == L'\0')
        return {};

    int length = WideCharToMultiByte(CP_UTF8, 0, text, -1, nullptr, 0, nullptr, nullptr);
    if (length <= 1)
        return {};

    std::string result(static_cast<size_t>(length - 1), '\0');
    WideCharToMultiByte(CP_UTF8, 0, text, -1, result.data(), length, nullptr, nullptr);
    return result;
}

std::string hresult_to_string(HRESULT hr)
{
    wchar_t* message = nullptr;
    DWORD flags = FORMAT_MESSAGE_ALLOCATE_BUFFER |
                  FORMAT_MESSAGE_FROM_SYSTEM |
                  FORMAT_MESSAGE_IGNORE_INSERTS;
    DWORD length = FormatMessageW(
        flags,
        nullptr,
        static_cast<DWORD>(hr),
        MAKELANGID(LANG_NEUTRAL, SUBLANG_DEFAULT),
        reinterpret_cast<LPWSTR>(&message),
        0,
        nullptr);

    char hex[16]{};
    sprintf_s(hex, "%08X", static_cast<uint32_t>(hr));
    std::string result = "HRESULT 0x";
    result += hex;
    if (length > 0 && message != nullptr)
    {
        result = wide_to_utf8(message);
        while (!result.empty() && (result.back() == '\r' || result.back() == '\n' || result.back() == '.'))
            result.pop_back();
        result += " (0x";
        result += hex;
        result += ")";
    }

    if (message != nullptr)
        LocalFree(message);

    return result;
}

std::string ffmpeg_error_to_string(int error)
{
    char buffer[AV_ERROR_MAX_STRING_SIZE]{};
    av_strerror(error, buffer, sizeof(buffer));
    return std::string(buffer) + " (" + std::to_string(error) + ")";
}

HRESULT fail_ffmpeg(const char* operation, int error, const std::string& details = {})
{
    std::string message = "NativeRecorder ";
    message += operation != nullptr ? operation : "FFmpeg operation";
    message += " failed: ";
    message += ffmpeg_error_to_string(error);
    if (!details.empty())
    {
        message += "; ";
        message += details;
    }
    set_last_error(message);
    return E_FAIL;
}

const char* dxgi_format_name(DXGI_FORMAT format)
{
    switch (format)
    {
    case DXGI_FORMAT_B8G8R8A8_UNORM:
        return "DXGI_FORMAT_B8G8R8A8_UNORM";
    case DXGI_FORMAT_B8G8R8A8_UNORM_SRGB:
        return "DXGI_FORMAT_B8G8R8A8_UNORM_SRGB";
    case DXGI_FORMAT_B8G8R8A8_TYPELESS:
        return "DXGI_FORMAT_B8G8R8A8_TYPELESS";
    case DXGI_FORMAT_R8G8B8A8_UNORM:
        return "DXGI_FORMAT_R8G8B8A8_UNORM";
    case DXGI_FORMAT_R8G8B8A8_UNORM_SRGB:
        return "DXGI_FORMAT_R8G8B8A8_UNORM_SRGB";
    case DXGI_FORMAT_R8G8B8A8_TYPELESS:
        return "DXGI_FORMAT_R8G8B8A8_TYPELESS";
    case DXGI_FORMAT_NV12:
        return "DXGI_FORMAT_NV12";
    default:
        return "DXGI_FORMAT_UNKNOWN";
    }
}

std::string dxgi_format_to_string(DXGI_FORMAT format)
{
    return std::string(dxgi_format_name(format)) + "(" + std::to_string(static_cast<int>(format)) + ")";
}

std::string hex_uint32(uint32_t value)
{
    char buffer[32]{};
    sprintf_s(buffer, "0x%X", value);
    return buffer;
}

bool nvenc_runtime_present();
bool amf_runtime_present();

std::string memory_mb(size_t bytes)
{
    return std::to_string(static_cast<unsigned long long>(bytes / (1024ull * 1024ull))) + "MB";
}

std::string dxgi_adapter_report()
{
    ComPtr<IDXGIFactory1> factory;
    HRESULT hr = CreateDXGIFactory1(IID_PPV_ARGS(&factory));
    if (FAILED(hr))
        return "dxgiAdapters=CreateDXGIFactory1 failed: " + hresult_to_string(hr);

    std::string report = "dxgiAdapters=[";
    bool first = true;

    for (UINT index = 0;; ++index)
    {
        ComPtr<IDXGIAdapter1> adapter;
        hr = factory->EnumAdapters1(index, &adapter);
        if (hr == DXGI_ERROR_NOT_FOUND)
            break;
        if (FAILED(hr))
        {
            if (!first)
                report += " | ";
            report += "#" + std::to_string(index) + ": EnumAdapters1 failed: " + hresult_to_string(hr);
            break;
        }

        DXGI_ADAPTER_DESC1 desc{};
        hr = adapter->GetDesc1(&desc);
        if (FAILED(hr))
        {
            if (!first)
                report += " | ";
            report += "#" + std::to_string(index) + ": GetDesc1 failed: " + hresult_to_string(hr);
            first = false;
            continue;
        }

        UINT output_count = 0;
        for (;; ++output_count)
        {
            ComPtr<IDXGIOutput> output;
            HRESULT output_hr = adapter->EnumOutputs(output_count, &output);
            if (output_hr == DXGI_ERROR_NOT_FOUND)
                break;
            if (FAILED(output_hr))
                break;
        }

        if (!first)
            report += " | ";
        first = false;

        report += "#" + std::to_string(index) +
            ": name=\"" + wide_to_utf8(desc.Description) + "\"" +
            ", vendor=" + hex_uint32(desc.VendorId) +
            ", device=" + hex_uint32(desc.DeviceId) +
            ", subsys=" + hex_uint32(desc.SubSysId) +
            ", rev=" + std::to_string(desc.Revision) +
            ", dedicatedVideo=" + memory_mb(desc.DedicatedVideoMemory) +
            ", dedicatedSystem=" + memory_mb(desc.DedicatedSystemMemory) +
            ", sharedSystem=" + memory_mb(desc.SharedSystemMemory) +
            ", outputs=" + std::to_string(output_count) +
            ", flags=" + hex_uint32(desc.Flags);
    }

    if (first)
        report += "<none>";

    report += "]";
    return report;
}

std::string native_runtime_report()
{
    return std::string("nativeRuntime={") +
        "abi=" + std::to_string(PR_ABI_VERSION) +
        ", nvencRuntime=" + (nvenc_runtime_present() ? "present" : "missing") +
        ", amfRuntime=" + (amf_runtime_present() ? "present" : "missing") +
        ", avformat=" + std::to_string(avformat_version()) +
        ", avcodec=" + std::to_string(avcodec_version()) +
        ", avutil=" + std::to_string(avutil_version()) +
        "}";
}

std::string texture_desc_to_string(const D3D11_TEXTURE2D_DESC& desc)
{
    return std::to_string(desc.Width) + "x" + std::to_string(desc.Height) +
        ", format=" + dxgi_format_to_string(desc.Format) +
        ", usage=" + std::to_string(static_cast<uint32_t>(desc.Usage)) +
        ", bind=" + hex_uint32(desc.BindFlags) +
        ", cpu=" + hex_uint32(desc.CPUAccessFlags) +
        ", misc=" + hex_uint32(desc.MiscFlags) +
        ", sample=" + std::to_string(desc.SampleDesc.Count) + "/" + std::to_string(desc.SampleDesc.Quality);
}

HRESULT fail_step(const char* operation, HRESULT hr, const std::string& details = {})
{
    std::string message = "NativeRecorder ";
    message += operation != nullptr ? operation : "operation";
    message += " failed: ";
    message += hresult_to_string(hr);
    if (!details.empty())
    {
        message += "; ";
        message += details;
    }
    set_last_error(message);
    return hr;
}

int32_t fail_hr(const char* operation, HRESULT hr)
{
    std::string previous = get_last_error_copy();
    std::string message = operation != nullptr ? operation : "Operation failed";
    message += ": ";
    message += hresult_to_string(hr);
    if (!previous.empty())
    {
        message += ". ";
        message += previous;
    }
    set_last_error(message);
    return static_cast<int32_t>(hr);
}

HRESULT fail_exception(const char* operation, const std::exception& ex, const std::string& details = {})
{
    std::string message = "NativeRecorder ";
    message += operation != nullptr ? operation : "operation";
    message += " failed: ";
    message += ex.what();
    if (!details.empty())
    {
        message += "; ";
        message += details;
    }
    set_last_error(message);
    return E_FAIL;
}

HRESULT ensure_thread_com_initialized()
{
    thread_local bool initialized = false;
    if (initialized)
        return S_OK;

    HRESULT hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    if (SUCCEEDED(hr))
    {
        initialized = true;
        return S_OK;
    }

    if (hr == RPC_E_CHANGED_MODE)
        return S_OK;

    return hr;
}

bool nvenc_runtime_present()
{
    HMODULE module = LoadLibraryW(L"nvEncodeAPI64.dll");
    if (module == nullptr)
        return false;

    FreeLibrary(module);
    return true;
}

bool amf_runtime_present()
{
    HMODULE module = LoadLibraryW(AMF_DLL_NAME);
    if (module == nullptr)
        return false;

    FreeLibrary(module);
    return true;
}

HRESULT find_adapter_by_vendor(UINT vendor_id, std::string* adapter_name, IDXGIAdapter1** adapter_out = nullptr)
{
    if (adapter_out != nullptr)
        *adapter_out = nullptr;

    ComPtr<IDXGIFactory1> factory;
    HRESULT hr = CreateDXGIFactory1(IID_PPV_ARGS(&factory));
    if (FAILED(hr))
        return hr;

    for (UINT index = 0;; ++index)
    {
        ComPtr<IDXGIAdapter1> adapter;
        hr = factory->EnumAdapters1(index, &adapter);
        if (hr == DXGI_ERROR_NOT_FOUND)
            break;
        if (FAILED(hr))
            return hr;

        DXGI_ADAPTER_DESC1 desc{};
        hr = adapter->GetDesc1(&desc);
        if (FAILED(hr))
            return hr;

        if (desc.VendorId == vendor_id)
        {
            if (adapter_name != nullptr)
                *adapter_name = wide_to_utf8(desc.Description);
            if (adapter_out != nullptr)
                *adapter_out = adapter.Detach();
            return S_OK;
        }
    }

    return DXGI_ERROR_NOT_FOUND;
}

HRESULT find_nvidia_adapter(std::string* adapter_name, IDXGIAdapter1** adapter_out = nullptr)
{
    return find_adapter_by_vendor(kNvidiaVendorId, adapter_name, adapter_out);
}

HRESULT find_amd_adapter(std::string* adapter_name, IDXGIAdapter1** adapter_out = nullptr)
{
    return find_adapter_by_vendor(kAmdVendorId, adapter_name, adapter_out);
}

HRESULT get_device_adapter_info(
    ID3D11Device* device,
    UINT* vendor_id,
    std::string* adapter_name,
    LUID* adapter_luid = nullptr,
    IDXGIAdapter** adapter_out = nullptr)
{
    if (adapter_out != nullptr)
        *adapter_out = nullptr;

    if (device == nullptr)
        return E_POINTER;

    ComPtr<IDXGIDevice> dxgi_device;
    HRESULT hr = device->QueryInterface(IID_PPV_ARGS(&dxgi_device));
    if (FAILED(hr))
        return hr;

    ComPtr<IDXGIAdapter> adapter;
    hr = dxgi_device->GetAdapter(&adapter);
    if (FAILED(hr))
        return hr;

    DXGI_ADAPTER_DESC desc{};
    hr = adapter->GetDesc(&desc);
    if (FAILED(hr))
        return hr;

    if (vendor_id != nullptr)
        *vendor_id = desc.VendorId;
    if (adapter_name != nullptr)
        *adapter_name = wide_to_utf8(desc.Description);
    if (adapter_luid != nullptr)
        *adapter_luid = desc.AdapterLuid;
    if (adapter_out != nullptr)
        *adapter_out = adapter.Detach();

    return S_OK;
}

bool is_supported_texture_format(DXGI_FORMAT format)
{
    switch (format)
    {
    case DXGI_FORMAT_B8G8R8A8_UNORM:
    case DXGI_FORMAT_B8G8R8A8_UNORM_SRGB:
    case DXGI_FORMAT_B8G8R8A8_TYPELESS:
    case DXGI_FORMAT_R8G8B8A8_UNORM:
    case DXGI_FORMAT_R8G8B8A8_UNORM_SRGB:
    case DXGI_FORMAT_R8G8B8A8_TYPELESS:
        return true;
    default:
        return false;
    }
}

bool is_supported_recording_codec(int32_t codec)
{
    return codec == PR_CODEC_H264 || codec == PR_CODEC_HEVC;
}

AVCodecID ffmpeg_video_codec_id(int32_t codec)
{
    return codec == PR_CODEC_HEVC ? AV_CODEC_ID_HEVC : AV_CODEC_ID_H264;
}

const GUID& nvenc_codec_guid(int32_t codec)
{
    return codec == PR_CODEC_HEVC ? NV_ENC_CODEC_HEVC_GUID : NV_ENC_CODEC_H264_GUID;
}

GUID nvenc_preset_guid()
{
    return NV_ENC_PRESET_P4_GUID;
}

bool nvenc_output_is_key_frame(NV_ENC_PIC_TYPE picture_type)
{
    return picture_type == NV_ENC_PIC_TYPE_IDR || picture_type == NV_ENC_PIC_TYPE_I;
}

const wchar_t* amf_codec_id(int32_t codec)
{
    return codec == PR_CODEC_HEVC ? AMFVideoEncoder_HEVC : AMFVideoEncoderVCE_AVC;
}

std::string amf_result_to_string(AMF_RESULT result)
{
    return "AMF_RESULT " + std::to_string(static_cast<int>(result));
}

HRESULT fail_amf(const char* operation, AMF_RESULT result, const std::string& details = {})
{
    std::string message = "NativeRecorder ";
    message += operation != nullptr ? operation : "AMF operation";
    message += " failed: ";
    message += amf_result_to_string(result);
    if (!details.empty())
    {
        message += "; ";
        message += details;
    }
    set_last_error(message);
    return E_FAIL;
}

bool amf_result_success(AMF_RESULT result)
{
    return result == AMF_OK;
}

const char* codec_name(int32_t codec)
{
    switch (codec)
    {
    case PR_CODEC_H264:
        return "H264";
    case PR_CODEC_HEVC:
        return "HEVC";
    default:
        return "unknown";
    }
}

uint64_t make_sample_duration_hns(int fps)
{
    int safe_fps = std::max(1, fps);
    return 10'000'000ull / static_cast<uint64_t>(safe_fps);
}

int64_t hns_to_stream_ticks(int64_t hns, AVRational time_base)
{
    return av_rescale_q(std::max<int64_t>(0, hns), AVRational{1, 10'000'000}, time_base);
}

AVSampleFormat audio_sample_format(const pr_audio_config& audio)
{
    if (audio.is_float)
        return AV_SAMPLE_FMT_FLT;

    return audio.bits_per_sample <= 16 ? AV_SAMPLE_FMT_S16 : AV_SAMPLE_FMT_S32;
}

int bytes_per_audio_sample(const pr_audio_config& audio)
{
    return std::max(1, audio.bits_per_sample / 8);
}

int align_to_even(int value)
{
    int safe_value = std::max(1, value);
    return (safe_value + 1) & ~1;
}

struct NativeRecorderBackend
{
    virtual ~NativeRecorderBackend() = default;
    virtual HRESULT initialize(ID3D11Device* source_device, DXGI_FORMAT source_format) = 0;
    virtual HRESULT submit_shared_texture(ID3D11Device* source_device, HANDLE shared_handle, DXGI_FORMAT source_format, int64_t timestamp_hns) = 0;
    virtual HRESULT submit_audio(const void* data, int32_t byte_count, int64_t timestamp_hns) = 0;
    virtual HRESULT stop() = 0;
    virtual const char* backend_name() const = 0;
};

