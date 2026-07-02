#define PR_NATIVE_RECORDER_BUILD
#include "pocket_recorder_native.h"

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cstdio>
#include <cstdint>
#include <cstring>
#include <exception>
#include <memory>
#include <mutex>
#include <new>
#include <thread>
#include <string>
#include <vector>

#include <windows.h>
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
constexpr DWORD kSharedTextureAcquireTimeoutMs = 5;

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
    char* message = nullptr;
    DWORD flags = FORMAT_MESSAGE_ALLOCATE_BUFFER |
                  FORMAT_MESSAGE_FROM_SYSTEM |
                  FORMAT_MESSAGE_IGNORE_INSERTS;
    DWORD length = FormatMessageA(
        flags,
        nullptr,
        static_cast<DWORD>(hr),
        MAKELANGID(LANG_NEUTRAL, SUBLANG_DEFAULT),
        reinterpret_cast<LPSTR>(&message),
        0,
        nullptr);

    std::string result = "HRESULT 0x" + std::to_string(static_cast<uint32_t>(hr));
    if (length > 0 && message != nullptr)
    {
        result.assign(message, length);
        while (!result.empty() && (result.back() == '\r' || result.back() == '\n' || result.back() == '.'))
            result.pop_back();
        result += " (0x";
        char hex[16]{};
        sprintf_s(hex, "%08X", static_cast<uint32_t>(hr));
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

struct SharedTextureNv12Converter
{
    pr_video_config video{};
    UINT required_vendor_id = kNvidiaVendorId;
    const char* required_vendor_name = "NVIDIA";
    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> device_context;
    ComPtr<ID3D11VideoDevice> video_device;
    ComPtr<ID3D11VideoContext> video_context;
    ComPtr<ID3D11VideoProcessorEnumerator> video_processor_enum;
    ComPtr<ID3D11VideoProcessor> video_processor;
    ComPtr<ID3D11Texture2D> source_copy_texture;
    std::string adapter_name;
    LUID adapter_luid{};
    UINT source_format_support = 0;
    UINT nv12_format_support = 0;
    uint64_t frame_index = 0;
    bool initialized = false;

    int source_width() const { return video.width; }
    int source_height() const { return video.height; }
    int encoded_width() const { return align_to_even(video.width); }
    int encoded_height() const { return align_to_even(video.height); }

    HRESULT initialize(ID3D11Device* source_device, DXGI_FORMAT source_format)
    {
        if (initialized)
            return S_OK;
        if (source_device == nullptr)
            return E_POINTER;
        if (video.width <= 0 || video.height <= 0 || video.fps <= 0)
            return E_INVALIDARG;

        UINT vendor_id = 0;
        ComPtr<IDXGIAdapter> adapter;
        HRESULT hr = get_device_adapter_info(source_device, &vendor_id, &adapter_name, &adapter_luid, &adapter);
        if (FAILED(hr))
            return hr;
        if (vendor_id != required_vendor_id)
        {
            set_last_error(std::string("NativeRecorder source game device is not on a ") +
                required_vendor_name + " adapter; using FFmpeg fallback.");
            return HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED);
        }
        if (!adapter)
            return DXGI_ERROR_NOT_FOUND;

        UINT create_flags = D3D11_CREATE_DEVICE_BGRA_SUPPORT | D3D11_CREATE_DEVICE_VIDEO_SUPPORT;
        D3D_FEATURE_LEVEL feature_levels[] =
        {
            D3D_FEATURE_LEVEL_11_1,
            D3D_FEATURE_LEVEL_11_0,
        };
        D3D_FEATURE_LEVEL feature_level{};
        hr = D3D11CreateDevice(
            adapter.Get(),
            D3D_DRIVER_TYPE_UNKNOWN,
            nullptr,
            create_flags,
            feature_levels,
            _countof(feature_levels),
            D3D11_SDK_VERSION,
            &device,
            &feature_level,
            &device_context);
        if (FAILED(hr))
            return fail_step("D3D11CreateDevice", hr, "adapter=" + adapter_name);

        device->GetImmediateContext(&device_context);
        if (!device_context)
            return E_FAIL;

        hr = device.As(&video_device);
        if (FAILED(hr))
            return fail_step("QueryInterface(ID3D11VideoDevice)", hr);
        hr = device_context.As(&video_context);
        if (FAILED(hr))
            return fail_step("QueryInterface(ID3D11VideoContext)", hr);

        D3D11_VIDEO_PROCESSOR_CONTENT_DESC content{};
        content.InputFrameFormat = D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE;
        content.InputFrameRate.Numerator = static_cast<UINT>(std::max(1, video.fps));
        content.InputFrameRate.Denominator = 1;
        content.InputWidth = static_cast<UINT>(source_width());
        content.InputHeight = static_cast<UINT>(source_height());
        content.OutputFrameRate.Numerator = static_cast<UINT>(std::max(1, video.fps));
        content.OutputFrameRate.Denominator = 1;
        content.OutputWidth = static_cast<UINT>(encoded_width());
        content.OutputHeight = static_cast<UINT>(encoded_height());
        content.Usage = D3D11_VIDEO_USAGE_PLAYBACK_NORMAL;

        hr = video_device->CreateVideoProcessorEnumerator(&content, &video_processor_enum);
        if (FAILED(hr))
            return fail_step("CreateVideoProcessorEnumerator", hr);

        hr = video_processor_enum->CheckVideoProcessorFormat(source_format, &source_format_support);
        if (FAILED(hr))
            return fail_step("CheckVideoProcessorFormat(source)", hr, "format=" + dxgi_format_to_string(source_format));
        if ((source_format_support & D3D11_VIDEO_PROCESSOR_FORMAT_SUPPORT_INPUT) == 0)
        {
            set_last_error("NativeRecorder video processor does not support source texture as input; format=" +
                dxgi_format_to_string(source_format) +
                ", support=" + hex_uint32(source_format_support) +
                ". This GPU/driver likely needs shader NV12 shared texture or direct encoder input.");
            return E_INVALIDARG;
        }

        hr = video_processor_enum->CheckVideoProcessorFormat(DXGI_FORMAT_NV12, &nv12_format_support);
        if (FAILED(hr))
            return fail_step("CheckVideoProcessorFormat(NV12)", hr);
        if ((nv12_format_support & D3D11_VIDEO_PROCESSOR_FORMAT_SUPPORT_OUTPUT) == 0)
        {
            set_last_error("NativeRecorder video processor does not support NV12 output; support=" +
                hex_uint32(nv12_format_support) + ".");
            return E_INVALIDARG;
        }

        hr = video_device->CreateVideoProcessor(video_processor_enum.Get(), 0, &video_processor);
        if (FAILED(hr))
            return fail_step("CreateVideoProcessor", hr);

        RECT source_rect{0, 0, source_width(), source_height()};
        RECT dest_rect{0, 0, source_width(), source_height()};
        RECT output_rect{0, 0, encoded_width(), encoded_height()};
        D3D11_VIDEO_COLOR background{};
        background.RGBA.A = 1.0f;
        video_context->VideoProcessorSetStreamFrameFormat(video_processor.Get(), 0, D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE);
        video_context->VideoProcessorSetStreamSourceRect(video_processor.Get(), 0, TRUE, &source_rect);
        video_context->VideoProcessorSetStreamDestRect(video_processor.Get(), 0, TRUE, &dest_rect);
        video_context->VideoProcessorSetOutputTargetRect(video_processor.Get(), TRUE, &output_rect);
        video_context->VideoProcessorSetOutputBackgroundColor(video_processor.Get(), FALSE, &background);

        initialized = true;
        return S_OK;
    }

    HRESULT ensure_source_copy_texture(const D3D11_TEXTURE2D_DESC& source_desc)
    {
        if (source_copy_texture)
        {
            D3D11_TEXTURE2D_DESC existing{};
            source_copy_texture->GetDesc(&existing);
            constexpr UINT required_bind_flags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET;
            if (existing.Width == source_desc.Width &&
                existing.Height == source_desc.Height &&
                existing.Format == source_desc.Format &&
                (existing.BindFlags & required_bind_flags) == required_bind_flags)
            {
                return S_OK;
            }

            source_copy_texture.Reset();
        }

        D3D11_TEXTURE2D_DESC copy_desc = source_desc;
        copy_desc.Usage = D3D11_USAGE_DEFAULT;
        copy_desc.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET;
        copy_desc.CPUAccessFlags = 0;
        copy_desc.MiscFlags = 0;

        return device->CreateTexture2D(&copy_desc, nullptr, &source_copy_texture);
    }

    HRESULT convert_shared_texture(HANDLE shared_handle, DXGI_FORMAT source_format, ComPtr<ID3D11Texture2D>& nv12_texture)
    {
        if (!initialized)
            return E_UNEXPECTED;
        if (shared_handle == nullptr)
            return E_POINTER;

        ComPtr<ID3D11Texture2D> source_texture;
        HRESULT hr = device->OpenSharedResource(shared_handle, __uuidof(ID3D11Texture2D), &source_texture);
        if (FAILED(hr))
        {
            set_last_error("NativeRecorder failed to open shared texture on the source adapter: " + hresult_to_string(hr));
            return hr;
        }

        D3D11_TEXTURE2D_DESC source_desc{};
        source_texture->GetDesc(&source_desc);
        if (source_desc.Width != static_cast<UINT>(source_width()) ||
            source_desc.Height != static_cast<UINT>(source_height()))
        {
            set_last_error("NativeRecorder source texture size changed; expected=" +
                std::to_string(source_width()) + "x" + std::to_string(source_height()) +
                ", actual=" + texture_desc_to_string(source_desc));
            return E_INVALIDARG;
        }
        if (source_desc.SampleDesc.Count != 1)
        {
            set_last_error("NativeRecorder does not support MSAA swap-chain textures.");
            return HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED);
        }
        if (!is_supported_texture_format(source_desc.Format) || !is_supported_texture_format(source_format))
        {
            set_last_error("NativeRecorder source texture format is not supported.");
            return E_INVALIDARG;
        }

        ComPtr<IDXGIKeyedMutex> keyed_mutex;
        hr = source_texture.As(&keyed_mutex);

        hr = ensure_source_copy_texture(source_desc);
        if (FAILED(hr))
            return fail_step("CreateTexture2D(source copy)", hr, "source=" + texture_desc_to_string(source_desc));

        bool mutex_acquired = false;
        if (keyed_mutex)
        {
            hr = keyed_mutex->AcquireSync(kEncoderReadKey, kSharedTextureAcquireTimeoutMs);
            if (hr == WAIT_TIMEOUT || hr == DXGI_ERROR_WAIT_TIMEOUT)
            {
                set_last_error("NativeRecorder shared texture was not ready; dropping one frame.");
                return DXGI_ERROR_WAS_STILL_DRAWING;
            }
            if (FAILED(hr))
                return fail_step("IDXGIKeyedMutex::AcquireSync", hr, "source=" + texture_desc_to_string(source_desc));

            mutex_acquired = true;
        }

        device_context->CopyResource(source_copy_texture.Get(), source_texture.Get());
        if (keyed_mutex)
        {
            HRESULT release_hr = keyed_mutex->ReleaseSync(kGameWriteKey);
            mutex_acquired = false;
            if (FAILED(release_hr))
                return fail_step("IDXGIKeyedMutex::ReleaseSync", release_hr, "source=" + texture_desc_to_string(source_desc));
        }

        D3D11_TEXTURE2D_DESC output_desc{};
        output_desc.Width = static_cast<UINT>(encoded_width());
        output_desc.Height = static_cast<UINT>(encoded_height());
        output_desc.MipLevels = 1;
        output_desc.ArraySize = 1;
        output_desc.Format = DXGI_FORMAT_NV12;
        output_desc.SampleDesc.Count = 1;
        output_desc.Usage = D3D11_USAGE_DEFAULT;
        output_desc.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;

        hr = device->CreateTexture2D(&output_desc, nullptr, &nv12_texture);
        if (FAILED(hr))
            return fail_step("CreateTexture2D(NV12 output)", hr, "output=" + texture_desc_to_string(output_desc));

        D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC input_view_desc{};
        input_view_desc.FourCC = 0;
        input_view_desc.ViewDimension = D3D11_VPIV_DIMENSION_TEXTURE2D;
        input_view_desc.Texture2D.MipSlice = 0;
        input_view_desc.Texture2D.ArraySlice = 0;

        ComPtr<ID3D11VideoProcessorInputView> input_view;
        hr = video_device->CreateVideoProcessorInputView(source_copy_texture.Get(), video_processor_enum.Get(), &input_view_desc, &input_view);
        if (FAILED(hr))
        {
            if (mutex_acquired && keyed_mutex)
                keyed_mutex->ReleaseSync(kGameWriteKey);
            return fail_step("CreateVideoProcessorInputView", hr, "source=" + texture_desc_to_string(source_desc));
        }

        D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC output_view_desc{};
        output_view_desc.ViewDimension = D3D11_VPOV_DIMENSION_TEXTURE2D;
        output_view_desc.Texture2D.MipSlice = 0;

        ComPtr<ID3D11VideoProcessorOutputView> output_view;
        hr = video_device->CreateVideoProcessorOutputView(nv12_texture.Get(), video_processor_enum.Get(), &output_view_desc, &output_view);
        if (FAILED(hr))
            return fail_step("CreateVideoProcessorOutputView", hr, "output=" + texture_desc_to_string(output_desc));

        D3D11_VIDEO_PROCESSOR_STREAM stream{};
        stream.Enable = TRUE;
        stream.OutputIndex = 0;
        stream.InputFrameOrField = 0;
        stream.PastFrames = 0;
        stream.FutureFrames = 0;
        stream.pInputSurface = input_view.Get();

        hr = video_context->VideoProcessorBlt(
            video_processor.Get(),
            output_view.Get(),
            static_cast<UINT>(frame_index),
            1,
            &stream);
        if (FAILED(hr))
            return fail_step("VideoProcessorBlt", hr, "source=" + texture_desc_to_string(source_desc) + "; output=" + texture_desc_to_string(output_desc));

        ++frame_index;
        return S_OK;
    }

    void reset()
    {
        source_copy_texture.Reset();
        video_processor.Reset();
        video_processor_enum.Reset();
        video_context.Reset();
        video_device.Reset();
        device_context.Reset();
        device.Reset();
        initialized = false;
        frame_index = 0;
    }
};

struct LibavPacketHolder
{
    AVPacket* packet = nullptr;

    LibavPacketHolder()
        : packet(av_packet_alloc())
    {
    }

    ~LibavPacketHolder()
    {
        av_packet_free(&packet);
    }

    AVPacket* get() const { return packet; }
};

struct LibavMp4Muxer
{
    pr_video_config video{};
    pr_audio_config audio{};
    std::string output_path_utf8;
    AVFormatContext* format_context = nullptr;
    AVStream* video_stream = nullptr;
    AVStream* audio_stream = nullptr;
    AVCodecContext* audio_encoder = nullptr;
    AVFrame* audio_frame = nullptr;
    SwrContext* swr = nullptr;
    AVChannelLayout input_audio_layout{};
    AVChannelLayout encoder_audio_layout{};
    std::vector<uint8_t> pending_audio;
    bool header_written = false;
    bool trailer_written = false;
    int64_t first_video_timestamp_hns = -1;
    int64_t first_audio_timestamp_hns = -1;
    int64_t last_video_pts = -1;
    int64_t next_audio_pts = 0;
    int audio_input_frame_size_bytes = 0;
    int audio_encoder_frame_size = 0;
    int encoded_width = 0;
    int encoded_height = 0;

    ~LibavMp4Muxer()
    {
        close();
    }

    HRESULT open(
        const std::wstring& output_path,
        const pr_video_config& video_config,
        const pr_audio_config& audio_config,
        const std::vector<uint8_t>& video_extradata)
    {
        video = video_config;
        audio = audio_config;
        encoded_width = align_to_even(video.width);
        encoded_height = align_to_even(video.height);
        output_path_utf8 = wide_to_utf8(output_path.c_str());
        if (output_path_utf8.empty())
            return E_INVALIDARG;

        int ret = avformat_alloc_output_context2(&format_context, nullptr, "mp4", output_path_utf8.c_str());
        if (ret < 0 || format_context == nullptr)
            return fail_ffmpeg("avformat_alloc_output_context2", ret, output_path_utf8);

        video_stream = avformat_new_stream(format_context, nullptr);
        if (video_stream == nullptr)
            return fail_ffmpeg("avformat_new_stream(video)", AVERROR(ENOMEM));

        video_stream->time_base = AVRational{1, 10'000'000};
        video_stream->avg_frame_rate = AVRational{std::max(1, video.fps), 1};
        video_stream->r_frame_rate = video_stream->avg_frame_rate;
        video_stream->codecpar->codec_type = AVMEDIA_TYPE_VIDEO;
        video_stream->codecpar->codec_id = ffmpeg_video_codec_id(video.codec);
        video_stream->codecpar->width = encoded_width;
        video_stream->codecpar->height = encoded_height;
        video_stream->codecpar->format = AV_PIX_FMT_NV12;
        video_stream->codecpar->codec_tag = 0;

        if (!video_extradata.empty())
        {
            video_stream->codecpar->extradata =
                static_cast<uint8_t*>(av_mallocz(video_extradata.size() + AV_INPUT_BUFFER_PADDING_SIZE));
            if (video_stream->codecpar->extradata == nullptr)
                return fail_ffmpeg("av_mallocz(video extradata)", AVERROR(ENOMEM));

            std::memcpy(video_stream->codecpar->extradata, video_extradata.data(), video_extradata.size());
            video_stream->codecpar->extradata_size = static_cast<int>(video_extradata.size());
        }

        if (audio.enabled)
        {
            HRESULT hr = open_audio_encoder();
            if (FAILED(hr))
                return hr;
        }

        if ((format_context->oformat->flags & AVFMT_NOFILE) == 0)
        {
            ret = avio_open(&format_context->pb, output_path_utf8.c_str(), AVIO_FLAG_WRITE);
            if (ret < 0)
                return fail_ffmpeg("avio_open", ret, output_path_utf8);
        }

        ret = avformat_write_header(format_context, nullptr);
        if (ret < 0)
            return fail_ffmpeg("avformat_write_header", ret, output_path_utf8);

        header_written = true;
        return S_OK;
    }

    HRESULT open_audio_encoder()
    {
        const AVCodec* codec = avcodec_find_encoder(AV_CODEC_ID_AAC);
        if (codec == nullptr)
            return fail_ffmpeg("avcodec_find_encoder(AAC)", AVERROR_ENCODER_NOT_FOUND);

        audio_encoder = avcodec_alloc_context3(codec);
        if (audio_encoder == nullptr)
            return fail_ffmpeg("avcodec_alloc_context3(AAC)", AVERROR(ENOMEM));

        int channels = std::max(1, audio.channels);
        int sample_rate = std::max(1, audio.sample_rate);
        av_channel_layout_default(&input_audio_layout, channels);
        av_channel_layout_copy(&encoder_audio_layout, &input_audio_layout);

        const void* supported_layouts = nullptr;
        int supported_layout_count = 0;
        int ret = avcodec_get_supported_config(
            nullptr,
            codec,
            AV_CODEC_CONFIG_CHANNEL_LAYOUT,
            0,
            &supported_layouts,
            &supported_layout_count);
        if (ret >= 0 && supported_layouts != nullptr && supported_layout_count > 0)
        {
            const auto* layouts = static_cast<const AVChannelLayout*>(supported_layouts);
            bool exact_layout_supported = false;
            for (int i = 0; i < supported_layout_count; ++i)
            {
                if (layouts[i].nb_channels == channels && av_channel_layout_compare(&layouts[i], &input_audio_layout) == 0)
                {
                    exact_layout_supported = true;
                    break;
                }
            }

            if (!exact_layout_supported)
            {
                av_channel_layout_uninit(&encoder_audio_layout);
                if (channels >= 2)
                    av_channel_layout_default(&encoder_audio_layout, 2);
                else
                    av_channel_layout_default(&encoder_audio_layout, 1);
            }
        }

        AVSampleFormat encoder_sample_format = AV_SAMPLE_FMT_FLTP;
        const void* supported_formats = nullptr;
        int supported_format_count = 0;
        ret = avcodec_get_supported_config(
            nullptr,
            codec,
            AV_CODEC_CONFIG_SAMPLE_FORMAT,
            0,
            &supported_formats,
            &supported_format_count);
        if (ret >= 0 && supported_formats != nullptr && supported_format_count > 0)
        {
            const auto* formats = static_cast<const AVSampleFormat*>(supported_formats);
            encoder_sample_format = formats[0];
            for (int i = 0; i < supported_format_count; ++i)
            {
                if (formats[i] == AV_SAMPLE_FMT_FLTP)
                {
                    encoder_sample_format = AV_SAMPLE_FMT_FLTP;
                    break;
                }
            }
        }

        const void* supported_rates = nullptr;
        int supported_rate_count = 0;
        ret = avcodec_get_supported_config(
            nullptr,
            codec,
            AV_CODEC_CONFIG_SAMPLE_RATE,
            0,
            &supported_rates,
            &supported_rate_count);
        if (ret >= 0 && supported_rates != nullptr && supported_rate_count > 0)
        {
            const auto* rates = static_cast<const int*>(supported_rates);
            bool exact_rate_supported = false;
            for (int i = 0; i < supported_rate_count; ++i)
            {
                if (rates[i] == sample_rate)
                {
                    exact_rate_supported = true;
                    break;
                }
            }
            if (!exact_rate_supported)
                sample_rate = 48'000;
        }

        audio_encoder->sample_rate = sample_rate;
        audio_encoder->sample_fmt = encoder_sample_format;
        audio_encoder->time_base = AVRational{1, sample_rate};
        audio_encoder->bit_rate = 192'000;
        audio_encoder->profile = AV_PROFILE_AAC_LOW;
        if ((format_context->oformat->flags & AVFMT_GLOBALHEADER) != 0)
            audio_encoder->flags |= AV_CODEC_FLAG_GLOBAL_HEADER;

        ret = av_channel_layout_copy(&audio_encoder->ch_layout, &encoder_audio_layout);
        if (ret < 0)
            return fail_ffmpeg("av_channel_layout_copy(audio encoder)", ret);

        AVDictionary* options = nullptr;
        ret = avcodec_open2(audio_encoder, codec, &options);
        av_dict_free(&options);
        if (ret < 0)
            return fail_ffmpeg("avcodec_open2(AAC)", ret);

        audio_stream = avformat_new_stream(format_context, nullptr);
        if (audio_stream == nullptr)
            return fail_ffmpeg("avformat_new_stream(audio)", AVERROR(ENOMEM));

        audio_stream->time_base = audio_encoder->time_base;
        ret = avcodec_parameters_from_context(audio_stream->codecpar, audio_encoder);
        if (ret < 0)
            return fail_ffmpeg("avcodec_parameters_from_context(audio)", ret);
        audio_stream->codecpar->codec_tag = 0;

        AVSampleFormat input_format = audio_sample_format(audio);
        ret = swr_alloc_set_opts2(
            &swr,
            &audio_encoder->ch_layout,
            audio_encoder->sample_fmt,
            audio_encoder->sample_rate,
            &input_audio_layout,
            input_format,
            std::max(1, audio.sample_rate),
            0,
            nullptr);
        if (ret < 0)
            return fail_ffmpeg("swr_alloc_set_opts2", ret);

        ret = swr_init(swr);
        if (ret < 0)
            return fail_ffmpeg("swr_init", ret);

        audio_frame = av_frame_alloc();
        if (audio_frame == nullptr)
            return fail_ffmpeg("av_frame_alloc(audio)", AVERROR(ENOMEM));

        audio_encoder_frame_size = audio_encoder->frame_size > 0 ? audio_encoder->frame_size : 1024;
        audio_frame->nb_samples = audio_encoder_frame_size;
        audio_frame->format = audio_encoder->sample_fmt;
        audio_frame->sample_rate = audio_encoder->sample_rate;
        ret = av_channel_layout_copy(&audio_frame->ch_layout, &audio_encoder->ch_layout);
        if (ret < 0)
            return fail_ffmpeg("av_channel_layout_copy(audio frame)", ret);
        ret = av_frame_get_buffer(audio_frame, 0);
        if (ret < 0)
            return fail_ffmpeg("av_frame_get_buffer(audio)", ret);

        audio_input_frame_size_bytes = audio_encoder_frame_size * input_audio_layout.nb_channels * bytes_per_audio_sample(audio);
        return S_OK;
    }

    HRESULT write_video_packet(const std::vector<uint8_t>& data, bool is_key_frame, int64_t timestamp_hns, int64_t duration_hns)
    {
        if (!header_written || video_stream == nullptr || data.empty())
            return S_OK;

        LibavPacketHolder holder;
        if (holder.get() == nullptr)
            return fail_ffmpeg("av_packet_alloc(video)", AVERROR(ENOMEM));

        AVPacket* packet = holder.get();
        int ret = av_new_packet(packet, static_cast<int>(data.size()));
        if (ret < 0)
            return fail_ffmpeg("av_new_packet(video)", ret);

        std::memcpy(packet->data, data.data(), data.size());
        packet->stream_index = video_stream->index;
        if (is_key_frame)
            packet->flags |= AV_PKT_FLAG_KEY;

        if (first_video_timestamp_hns < 0)
            first_video_timestamp_hns = timestamp_hns;

        int64_t relative_hns = std::max<int64_t>(0, timestamp_hns - first_video_timestamp_hns);
        int64_t pts = hns_to_stream_ticks(relative_hns, video_stream->time_base);
        if (last_video_pts >= 0 && pts <= last_video_pts)
            pts = last_video_pts + std::max<int64_t>(1, hns_to_stream_ticks(static_cast<int64_t>(duration_hns), video_stream->time_base));

        packet->pts = pts;
        packet->dts = pts;
        packet->duration = std::max<int64_t>(1, hns_to_stream_ticks(static_cast<int64_t>(duration_hns), video_stream->time_base));

        ret = av_interleaved_write_frame(format_context, packet);
        if (ret < 0)
        {
            av_packet_unref(packet);
            return fail_ffmpeg("av_interleaved_write_frame(video)", ret);
        }

        last_video_pts = pts;
        return S_OK;
    }

    HRESULT write_audio(const void* data, int32_t byte_count, int64_t timestamp_hns)
    {
        if (!audio.enabled || audio_encoder == nullptr || audio_stream == nullptr || data == nullptr || byte_count <= 0)
            return S_OK;

        if (first_audio_timestamp_hns < 0)
        {
            first_audio_timestamp_hns = timestamp_hns;
            next_audio_pts = 0;
        }

        const auto* bytes = static_cast<const uint8_t*>(data);
        pending_audio.insert(pending_audio.end(), bytes, bytes + byte_count);

        while (audio_input_frame_size_bytes > 0 && static_cast<int>(pending_audio.size()) >= audio_input_frame_size_bytes)
        {
            HRESULT hr = encode_audio_frame(pending_audio.data(), audio_input_frame_size_bytes);
            if (FAILED(hr))
                return hr;
            pending_audio.erase(pending_audio.begin(), pending_audio.begin() + audio_input_frame_size_bytes);
        }

        return S_OK;
    }

    HRESULT encode_audio_frame(const uint8_t* input_data, int input_size)
    {
        if (audio_frame == nullptr || swr == nullptr || audio_encoder == nullptr)
            return S_OK;

        audio_frame->nb_samples = audio_encoder_frame_size;
        int ret = av_frame_make_writable(audio_frame);
        if (ret < 0)
            return fail_ffmpeg("av_frame_make_writable(audio)", ret);

        int input_channels = std::max(1, input_audio_layout.nb_channels);
        int input_sample_size = bytes_per_audio_sample(audio);
        int input_samples = input_size / std::max(1, input_channels * input_sample_size);
        const uint8_t* input_planes[1] = { input_data };

        ret = swr_convert(
            swr,
            audio_frame->data,
            audio_frame->nb_samples,
            input_planes,
            input_samples);
        if (ret < 0)
            return fail_ffmpeg("swr_convert(audio)", ret);

        audio_frame->nb_samples = ret;
        audio_frame->pts = next_audio_pts;
        next_audio_pts += ret;

        return send_audio_frame(audio_frame);
    }

    HRESULT flush_audio()
    {
        if (!audio.enabled || audio_encoder == nullptr)
            return S_OK;

        if (!pending_audio.empty() && audio_input_frame_size_bytes > 0)
        {
            pending_audio.resize(static_cast<size_t>(audio_input_frame_size_bytes), 0);
            HRESULT hr = encode_audio_frame(pending_audio.data(), audio_input_frame_size_bytes);
            if (FAILED(hr))
                return hr;
            pending_audio.clear();
        }

        return send_audio_frame(nullptr);
    }

    HRESULT send_audio_frame(AVFrame* frame)
    {
        int ret = avcodec_send_frame(audio_encoder, frame);
        if (ret < 0)
            return fail_ffmpeg("avcodec_send_frame(AAC)", ret);

        LibavPacketHolder holder;
        if (holder.get() == nullptr)
            return fail_ffmpeg("av_packet_alloc(audio)", AVERROR(ENOMEM));

        while (true)
        {
            ret = avcodec_receive_packet(audio_encoder, holder.get());
            if (ret == AVERROR(EAGAIN) || ret == AVERROR_EOF)
                return S_OK;
            if (ret < 0)
                return fail_ffmpeg("avcodec_receive_packet(AAC)", ret);

            av_packet_rescale_ts(holder.get(), audio_encoder->time_base, audio_stream->time_base);
            holder.get()->stream_index = audio_stream->index;
            ret = av_interleaved_write_frame(format_context, holder.get());
            if (ret < 0)
                return fail_ffmpeg("av_interleaved_write_frame(audio)", ret);
            av_packet_unref(holder.get());
        }
    }

    HRESULT close()
    {
        if (trailer_written)
            return S_OK;

        HRESULT result = S_OK;
        if (header_written && format_context != nullptr)
        {
            HRESULT audio_hr = flush_audio();
            if (FAILED(audio_hr))
                result = audio_hr;

            int ret = av_write_trailer(format_context);
            if (ret < 0 && SUCCEEDED(result))
                result = fail_ffmpeg("av_write_trailer", ret);
        }
        trailer_written = true;

        if (format_context != nullptr && (format_context->oformat->flags & AVFMT_NOFILE) == 0)
            avio_closep(&format_context->pb);

        if (audio_frame != nullptr)
            av_frame_free(&audio_frame);
        if (swr != nullptr)
            swr_free(&swr);
        if (audio_encoder != nullptr)
            avcodec_free_context(&audio_encoder);
        if (format_context != nullptr)
            avformat_free_context(format_context);
        format_context = nullptr;
        av_channel_layout_uninit(&input_audio_layout);
        av_channel_layout_uninit(&encoder_audio_layout);
        pending_audio.clear();
        return result;
    }
};
}

struct NvencLibavRecorderBackend final : NativeRecorderBackend
{
    pr_video_config video{};
    pr_audio_config audio{};
    std::wstring output_path;
    SharedTextureNv12Converter converter;
    std::unique_ptr<NvEncoderD3D11> encoder;
    LibavMp4Muxer muxer;
    bool initialized = false;
    bool stopped = false;
    uint64_t submitted_frames = 0;
    uint64_t written_packets = 0;
    uint64_t audio_packets = 0;
    int64_t video_sample_duration_hns = 0;

    NvencLibavRecorderBackend(const pr_video_config& video_config, const pr_audio_config& audio_config, std::wstring output)
        : video(video_config), audio(audio_config), output_path(std::move(output))
    {
        converter.video = video;
    }

    ~NvencLibavRecorderBackend() override
    {
        stop();
    }

    const char* backend_name() const override
    {
        return "NvEncoderD3D11+libavformat";
    }

    HRESULT initialize(ID3D11Device* source_device, DXGI_FORMAT source_format) override
    {
        if (initialized)
            return S_OK;
        if (source_device == nullptr)
            return E_POINTER;
        if (output_path.empty())
            return E_INVALIDARG;
        if (video.width <= 0 || video.height <= 0 || video.fps <= 0)
            return E_INVALIDARG;
        if (!is_supported_recording_codec(video.codec))
            return HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED);
        if (!nvenc_runtime_present())
        {
            set_last_error("NVIDIA NVENC runtime nvEncodeAPI64.dll was not found.");
            return HRESULT_FROM_WIN32(ERROR_NOT_FOUND);
        }

        HRESULT hr = converter.initialize(source_device, source_format);
        if (FAILED(hr))
            return hr;

        const int encoded_width = converter.encoded_width();
        const int encoded_height = converter.encoded_height();
        video_sample_duration_hns = static_cast<int64_t>(make_sample_duration_hns(video.fps));

        try
        {
            encoder = std::make_unique<NvEncoderD3D11>(
                converter.device.Get(),
                static_cast<uint32_t>(encoded_width),
                static_cast<uint32_t>(encoded_height),
                NV_ENC_BUFFER_FORMAT_NV12,
                1);

            NV_ENC_INITIALIZE_PARAMS initialize_params = { NV_ENC_INITIALIZE_PARAMS_VER };
            NV_ENC_CONFIG encode_config = { NV_ENC_CONFIG_VER };
            initialize_params.encodeConfig = &encode_config;
            encoder->CreateDefaultEncoderParams(
                &initialize_params,
                nvenc_codec_guid(video.codec),
                nvenc_preset_guid(),
                NV_ENC_TUNING_INFO_LOW_LATENCY);

            initialize_params.frameRateNum = static_cast<uint32_t>(std::max(1, video.fps));
            initialize_params.frameRateDen = 1;
            initialize_params.encodeWidth = static_cast<uint32_t>(encoded_width);
            initialize_params.encodeHeight = static_cast<uint32_t>(encoded_height);
            initialize_params.darWidth = static_cast<uint32_t>(encoded_width);
            initialize_params.darHeight = static_cast<uint32_t>(encoded_height);
            initialize_params.enablePTD = 1;
#if defined(_WIN32)
            initialize_params.enableEncodeAsync = 0;
#endif

            auto* cfg = initialize_params.encodeConfig;
            cfg->gopLength = static_cast<uint32_t>(std::max(1, video.fps) * 2);
            cfg->frameIntervalP = 1;
            cfg->rcParams.rateControlMode = NV_ENC_PARAMS_RC_CBR;
            cfg->rcParams.averageBitRate = static_cast<uint32_t>(video.bitrate_bps > 0 ? video.bitrate_bps : 12'000'000);
            cfg->rcParams.maxBitRate = cfg->rcParams.averageBitRate;
            cfg->rcParams.vbvBufferSize = std::max<uint32_t>(cfg->rcParams.averageBitRate / std::max(1, video.fps), cfg->rcParams.averageBitRate / 4);
            cfg->rcParams.vbvInitialDelay = cfg->rcParams.vbvBufferSize;
            cfg->rcParams.enableLookahead = 0;
            cfg->rcParams.lookaheadDepth = 0;

            if (video.codec == PR_CODEC_H264)
            {
                cfg->encodeCodecConfig.h264Config.idrPeriod = cfg->gopLength;
                cfg->encodeCodecConfig.h264Config.repeatSPSPPS = 1;
                cfg->encodeCodecConfig.h264Config.outputAUD = 0;
            }
            else
            {
                cfg->encodeCodecConfig.hevcConfig.idrPeriod = cfg->gopLength;
                cfg->encodeCodecConfig.hevcConfig.repeatSPSPPS = 1;
                cfg->encodeCodecConfig.hevcConfig.outputAUD = 0;
            }

            encoder->CreateEncoder(&initialize_params);

            std::vector<uint8_t> sequence_params;
            encoder->GetSequenceParams(sequence_params);
            hr = muxer.open(output_path, video, audio, sequence_params);
            if (FAILED(hr))
                return hr;
        }
        catch (const std::exception& ex)
        {
            return fail_exception("NvEncoderD3D11 initialize", ex,
                "codec=" + std::string(codec_name(video.codec)) +
                ", encoded=" + std::to_string(encoded_width) + "x" + std::to_string(encoded_height));
        }

        initialized = true;
        std::string message = "NativeRecorder initialized: source NVIDIA adapter=" + converter.adapter_name +
            ", luid=" + std::to_string(static_cast<uint32_t>(converter.adapter_luid.HighPart)) + ":" +
            std::to_string(converter.adapter_luid.LowPart) +
            ", sourceFormat=" + dxgi_format_to_string(source_format) +
            ", encoded=" + std::to_string(encoded_width) + "x" + std::to_string(encoded_height) +
            ", pad=" + std::to_string(encoded_width - video.width) + "x" + std::to_string(encoded_height - video.height) +
            ", vpSourceSupport=" + hex_uint32(converter.source_format_support) +
            ", vpNv12Support=" + hex_uint32(converter.nv12_format_support) +
            ", output=" + std::string(codec_name(video.codec)) + "/MP4 via NvEncoderD3D11 + libavformat.";
        set_last_error(message);
        return S_OK;
    }

    HRESULT submit_shared_texture(ID3D11Device* source_device, HANDLE shared_handle, DXGI_FORMAT source_format, int64_t timestamp_hns) override
    {
        if (stopped)
            return E_ABORT;
        if (source_device == nullptr || shared_handle == nullptr)
            return E_POINTER;

        HRESULT hr = initialize(source_device, source_format);
        if (FAILED(hr))
            return hr;

        ComPtr<ID3D11Texture2D> nv12_texture;
        hr = converter.convert_shared_texture(shared_handle, source_format, nv12_texture);
        if (FAILED(hr))
            return hr;

        try
        {
            const NvEncInputFrame* input_frame = encoder->GetNextInputFrame();
            auto* input_texture = reinterpret_cast<ID3D11Texture2D*>(input_frame->inputPtr);
            if (input_texture == nullptr)
                return E_POINTER;

            converter.device_context->CopyResource(input_texture, nv12_texture.Get());

            NV_ENC_PIC_PARAMS picture_params = { NV_ENC_PIC_PARAMS_VER };
            picture_params.inputTimeStamp = static_cast<uint64_t>(std::max<int64_t>(0, timestamp_hns));
            if (submitted_frames == 0)
                picture_params.encodePicFlags = NV_ENC_PIC_FLAG_FORCEIDR | NV_ENC_PIC_FLAG_OUTPUT_SPSPPS;

            std::vector<NvEncOutputFrame> packets;
            encoder->EncodeFrame(packets, &picture_params);
            for (const NvEncOutputFrame& packet : packets)
            {
                if (packet.frame.empty())
                    continue;

                int64_t packet_timestamp = static_cast<int64_t>(packet.timeStamp);
                if (packet_timestamp <= 0)
                    packet_timestamp = timestamp_hns;
                hr = muxer.write_video_packet(
                    packet.frame,
                    nvenc_output_is_key_frame(packet.pictureType),
                    packet_timestamp,
                    video_sample_duration_hns);
                if (FAILED(hr))
                    return hr;
                ++written_packets;
            }

            ++submitted_frames;
        }
        catch (const std::exception& ex)
        {
            return fail_exception("NvEncoderD3D11 submit", ex,
                "submitted=" + std::to_string(submitted_frames) +
                ", written=" + std::to_string(written_packets));
        }

        return S_OK;
    }

    HRESULT submit_audio(const void* data, int32_t byte_count, int64_t timestamp_hns) override
    {
        if (!audio.enabled)
            return S_OK;
        if (!initialized)
            return S_OK;

        HRESULT hr = muxer.write_audio(data, byte_count, timestamp_hns);
        if (FAILED(hr))
            return hr;

        ++audio_packets;
        return S_OK;
    }

    HRESULT stop() override
    {
        if (stopped)
            return S_OK;

        stopped = true;
        HRESULT result = S_OK;
        if (encoder)
        {
            try
            {
                std::vector<NvEncOutputFrame> packets;
                encoder->EndEncode(packets);
                for (const NvEncOutputFrame& packet : packets)
                {
                    if (packet.frame.empty())
                        continue;

                    HRESULT hr = muxer.write_video_packet(
                        packet.frame,
                        nvenc_output_is_key_frame(packet.pictureType),
                        static_cast<int64_t>(packet.timeStamp),
                        video_sample_duration_hns);
                    if (FAILED(hr) && SUCCEEDED(result))
                        result = hr;
                    ++written_packets;
                }
                encoder->DestroyEncoder();
            }
            catch (const std::exception& ex)
            {
                if (SUCCEEDED(result))
                    result = fail_exception("NvEncoderD3D11 finalize", ex);
            }
        }

        HRESULT mux_hr = muxer.close();
        if (FAILED(mux_hr) && SUCCEEDED(result))
            result = mux_hr;

        encoder.reset();
        converter.reset();

        if (SUCCEEDED(result))
        {
            set_last_error("NativeRecorder finalized via NvEncoderD3D11 + libavformat. submitted=" +
                std::to_string(submitted_frames) +
                ", packets=" + std::to_string(written_packets) +
                ", audioPackets=" + std::to_string(audio_packets));
        }

        return result;
    }
};

struct AmfLibavRecorderBackend final : NativeRecorderBackend
{
    pr_video_config video{};
    pr_audio_config audio{};
    std::wstring output_path;
    SharedTextureNv12Converter converter;
    amf::AMFContextPtr context;
    amf::AMFComponentPtr encoder;
    LibavMp4Muxer muxer;
    bool initialized = false;
    bool stopped = false;
    bool factory_initialized = false;
    uint64_t submitted_frames = 0;
    uint64_t written_packets = 0;
    uint64_t audio_packets = 0;
    int64_t video_sample_duration_hns = 0;

    AmfLibavRecorderBackend(const pr_video_config& video_config, const pr_audio_config& audio_config, std::wstring output)
        : video(video_config), audio(audio_config), output_path(std::move(output))
    {
        converter.video = video;
        converter.required_vendor_id = kAmdVendorId;
        converter.required_vendor_name = "AMD";
    }

    ~AmfLibavRecorderBackend() override
    {
        stop();
        if (factory_initialized)
        {
            g_AMFFactory.Terminate();
            factory_initialized = false;
        }
    }

    const char* backend_name() const override
    {
        return "AMF+libavformat";
    }

    HRESULT initialize(ID3D11Device* source_device, DXGI_FORMAT source_format) override
    {
        if (initialized)
            return S_OK;
        if (source_device == nullptr)
            return E_POINTER;
        if (output_path.empty())
            return E_INVALIDARG;
        if (video.width <= 0 || video.height <= 0 || video.fps <= 0)
            return E_INVALIDARG;
        if (!is_supported_recording_codec(video.codec))
            return HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED);
        if (!amf_runtime_present())
        {
            set_last_error("AMD AMF runtime amfrt64.dll was not found.");
            return HRESULT_FROM_WIN32(ERROR_NOT_FOUND);
        }

        HRESULT hr = converter.initialize(source_device, source_format);
        if (FAILED(hr))
            return hr;

        const int encoded_width = converter.encoded_width();
        const int encoded_height = converter.encoded_height();
        video_sample_duration_hns = static_cast<int64_t>(make_sample_duration_hns(video.fps));

        AMF_RESULT amf_result = g_AMFFactory.Init();
        if (!amf_result_success(amf_result))
            return fail_amf("AMF factory init", amf_result);
        factory_initialized = true;

        amf_result = g_AMFFactory.GetFactory()->CreateContext(&context);
        if (!amf_result_success(amf_result))
            return fail_amf("AMF CreateContext", amf_result);

        amf_result = context->InitDX11(converter.device.Get());
        if (!amf_result_success(amf_result))
            return fail_amf("AMF InitDX11", amf_result, "adapter=" + converter.adapter_name);

        amf_result = g_AMFFactory.GetFactory()->CreateComponent(context, amf_codec_id(video.codec), &encoder);
        if (!amf_result_success(amf_result))
            return fail_amf("AMF CreateComponent(encoder)", amf_result, std::string("codec=") + codec_name(video.codec));

        hr = configure_encoder(encoded_width, encoded_height);
        if (FAILED(hr))
            return hr;

        amf_result = encoder->Init(amf::AMF_SURFACE_NV12, encoded_width, encoded_height);
        if (!amf_result_success(amf_result))
            return fail_amf("AMF encoder Init(NV12)", amf_result,
                "encoded=" + std::to_string(encoded_width) + "x" + std::to_string(encoded_height));

        std::vector<uint8_t> sequence_params;
        hr = read_sequence_params(sequence_params);
        if (FAILED(hr))
            return hr;

        hr = muxer.open(output_path, video, audio, sequence_params);
        if (FAILED(hr))
            return hr;

        initialized = true;
        std::string message = "NativeRecorder initialized: source AMD adapter=" + converter.adapter_name +
            ", luid=" + std::to_string(static_cast<uint32_t>(converter.adapter_luid.HighPart)) + ":" +
            std::to_string(converter.adapter_luid.LowPart) +
            ", sourceFormat=" + dxgi_format_to_string(source_format) +
            ", encoded=" + std::to_string(encoded_width) + "x" + std::to_string(encoded_height) +
            ", pad=" + std::to_string(encoded_width - video.width) + "x" + std::to_string(encoded_height - video.height) +
            ", vpSourceSupport=" + hex_uint32(converter.source_format_support) +
            ", vpNv12Support=" + hex_uint32(converter.nv12_format_support) +
            ", output=" + std::string(codec_name(video.codec)) + "/MP4 via AMF + libavformat.";
        set_last_error(message);
        return S_OK;
    }

    HRESULT configure_encoder(int encoded_width, int encoded_height)
    {
        const int fps = std::max(1, video.fps);
        const int64_t bitrate = video.bitrate_bps > 0 ? video.bitrate_bps : 12'000'000;
        AMF_RESULT result = AMF_OK;

        if (video.codec == PR_CODEC_H264)
        {
            result = encoder->SetProperty(AMF_VIDEO_ENCODER_USAGE, amf::AMFVariant(static_cast<amf_int64>(AMF_VIDEO_ENCODER_USAGE_LOW_LATENCY)));
            if (!amf_result_success(result)) return fail_amf("AMF SetProperty(H264 Usage)", result);
            result = encoder->SetProperty(AMF_VIDEO_ENCODER_PROFILE, amf::AMFVariant(static_cast<amf_int64>(AMF_VIDEO_ENCODER_PROFILE_HIGH)));
            if (!amf_result_success(result)) return fail_amf("AMF SetProperty(H264 Profile)", result);
            result = encoder->SetProperty(AMF_VIDEO_ENCODER_FRAMESIZE, amf::AMFVariant(AMFConstructSize(encoded_width, encoded_height)));
            if (!amf_result_success(result)) return fail_amf("AMF SetProperty(H264 FrameSize)", result);
            result = encoder->SetProperty(AMF_VIDEO_ENCODER_FRAMERATE, amf::AMFVariant(AMFConstructRate(fps, 1)));
            if (!amf_result_success(result)) return fail_amf("AMF SetProperty(H264 FrameRate)", result);
            result = encoder->SetProperty(AMF_VIDEO_ENCODER_TARGET_BITRATE, amf::AMFVariant(static_cast<amf_int64>(bitrate)));
            if (!amf_result_success(result)) return fail_amf("AMF SetProperty(H264 TargetBitrate)", result);
            result = encoder->SetProperty(AMF_VIDEO_ENCODER_PEAK_BITRATE, amf::AMFVariant(static_cast<amf_int64>(bitrate)));
            if (!amf_result_success(result)) return fail_amf("AMF SetProperty(H264 PeakBitrate)", result);
            result = encoder->SetProperty(AMF_VIDEO_ENCODER_RATE_CONTROL_METHOD, amf::AMFVariant(static_cast<amf_int64>(AMF_VIDEO_ENCODER_RATE_CONTROL_METHOD_CBR)));
            if (!amf_result_success(result)) return fail_amf("AMF SetProperty(H264 RateControl)", result);
            result = encoder->SetProperty(AMF_VIDEO_ENCODER_QUALITY_PRESET, amf::AMFVariant(static_cast<amf_int64>(AMF_VIDEO_ENCODER_QUALITY_PRESET_SPEED)));
            if (!amf_result_success(result)) return fail_amf("AMF SetProperty(H264 QualityPreset)", result);
            result = encoder->SetProperty(AMF_VIDEO_ENCODER_MAX_CONSECUTIVE_BPICTURES, amf::AMFVariant(static_cast<amf_int64>(0)));
            if (!amf_result_success(result)) return fail_amf("AMF SetProperty(H264 BFrames)", result);
            result = encoder->SetProperty(AMF_VIDEO_ENCODER_IDR_PERIOD, amf::AMFVariant(static_cast<amf_int64>(fps * 2)));
            if (!amf_result_success(result)) return fail_amf("AMF SetProperty(H264 IDRPeriod)", result);
            result = encoder->SetProperty(AMF_VIDEO_ENCODER_HEADER_INSERTION_SPACING, amf::AMFVariant(static_cast<amf_int64>(fps * 2)));
            if (!amf_result_success(result)) return fail_amf("AMF SetProperty(H264 HeaderInsertion)", result);
        }
        else
        {
            result = encoder->SetProperty(AMF_VIDEO_ENCODER_HEVC_USAGE, amf::AMFVariant(static_cast<amf_int64>(AMF_VIDEO_ENCODER_HEVC_USAGE_LOW_LATENCY)));
            if (!amf_result_success(result)) return fail_amf("AMF SetProperty(HEVC Usage)", result);
            result = encoder->SetProperty(AMF_VIDEO_ENCODER_HEVC_PROFILE, amf::AMFVariant(static_cast<amf_int64>(AMF_VIDEO_ENCODER_HEVC_PROFILE_MAIN)));
            if (!amf_result_success(result)) return fail_amf("AMF SetProperty(HEVC Profile)", result);
            result = encoder->SetProperty(AMF_VIDEO_ENCODER_HEVC_FRAMESIZE, amf::AMFVariant(AMFConstructSize(encoded_width, encoded_height)));
            if (!amf_result_success(result)) return fail_amf("AMF SetProperty(HEVC FrameSize)", result);
            result = encoder->SetProperty(AMF_VIDEO_ENCODER_HEVC_FRAMERATE, amf::AMFVariant(AMFConstructRate(fps, 1)));
            if (!amf_result_success(result)) return fail_amf("AMF SetProperty(HEVC FrameRate)", result);
            result = encoder->SetProperty(AMF_VIDEO_ENCODER_HEVC_TARGET_BITRATE, amf::AMFVariant(static_cast<amf_int64>(bitrate)));
            if (!amf_result_success(result)) return fail_amf("AMF SetProperty(HEVC TargetBitrate)", result);
            result = encoder->SetProperty(AMF_VIDEO_ENCODER_HEVC_PEAK_BITRATE, amf::AMFVariant(static_cast<amf_int64>(bitrate)));
            if (!amf_result_success(result)) return fail_amf("AMF SetProperty(HEVC PeakBitrate)", result);
            result = encoder->SetProperty(AMF_VIDEO_ENCODER_HEVC_RATE_CONTROL_METHOD, amf::AMFVariant(static_cast<amf_int64>(AMF_VIDEO_ENCODER_HEVC_RATE_CONTROL_METHOD_CBR)));
            if (!amf_result_success(result)) return fail_amf("AMF SetProperty(HEVC RateControl)", result);
            result = encoder->SetProperty(AMF_VIDEO_ENCODER_HEVC_QUALITY_PRESET, amf::AMFVariant(static_cast<amf_int64>(AMF_VIDEO_ENCODER_HEVC_QUALITY_PRESET_SPEED)));
            if (!amf_result_success(result)) return fail_amf("AMF SetProperty(HEVC QualityPreset)", result);
            result = encoder->SetProperty(AMF_VIDEO_ENCODER_HEVC_GOP_SIZE, amf::AMFVariant(static_cast<amf_int64>(fps * 2)));
            if (!amf_result_success(result)) return fail_amf("AMF SetProperty(HEVC GOP)", result);
            result = encoder->SetProperty(AMF_VIDEO_ENCODER_HEVC_NUM_GOPS_PER_IDR, amf::AMFVariant(static_cast<amf_int64>(1)));
            if (!amf_result_success(result)) return fail_amf("AMF SetProperty(HEVC IDR)", result);
            result = encoder->SetProperty(AMF_VIDEO_ENCODER_HEVC_HEADER_INSERTION_MODE, amf::AMFVariant(static_cast<amf_int64>(AMF_VIDEO_ENCODER_HEVC_HEADER_INSERTION_MODE_IDR_ALIGNED)));
            if (!amf_result_success(result)) return fail_amf("AMF SetProperty(HEVC HeaderInsertion)", result);
        }

        return S_OK;
    }

    HRESULT read_sequence_params(std::vector<uint8_t>& sequence_params)
    {
        amf::AMFVariant extra_data;
        const wchar_t* property = video.codec == PR_CODEC_HEVC
            ? AMF_VIDEO_ENCODER_HEVC_EXTRADATA
            : AMF_VIDEO_ENCODER_EXTRADATA;
        AMF_RESULT result = encoder->GetProperty(property, &extra_data);
        if (!amf_result_success(result))
            return fail_amf("AMF GetProperty(extradata)", result, std::string("codec=") + codec_name(video.codec));

        amf::AMFInterface* extra_interface = AMFVariantGetInterface(&extra_data);
        if (extra_interface == nullptr)
        {
            set_last_error("NativeRecorder AMF encoder returned empty extradata.");
            return E_FAIL;
        }

        amf::AMFBufferPtr buffer(extra_interface);
        if (buffer == nullptr || buffer->GetNative() == nullptr || buffer->GetSize() == 0)
        {
            set_last_error("NativeRecorder AMF encoder returned invalid extradata buffer.");
            return E_FAIL;
        }

        const auto* data = static_cast<const uint8_t*>(buffer->GetNative());
        sequence_params.assign(data, data + buffer->GetSize());
        return S_OK;
    }

    HRESULT submit_shared_texture(ID3D11Device* source_device, HANDLE shared_handle, DXGI_FORMAT source_format, int64_t timestamp_hns) override
    {
        if (stopped)
            return E_ABORT;
        if (source_device == nullptr || shared_handle == nullptr)
            return E_POINTER;

        HRESULT hr = initialize(source_device, source_format);
        if (FAILED(hr))
            return hr;

        ComPtr<ID3D11Texture2D> nv12_texture;
        hr = converter.convert_shared_texture(shared_handle, source_format, nv12_texture);
        if (FAILED(hr))
            return hr;

        amf::AMFSurfacePtr surface;
        AMF_RESULT result = context->CreateSurfaceFromDX11Native(nv12_texture.Get(), &surface, nullptr);
        if (!amf_result_success(result))
            return fail_amf("AMF CreateSurfaceFromDX11Native", result);

        surface->SetPts(static_cast<amf_pts>(std::max<int64_t>(0, timestamp_hns)));
        surface->SetDuration(static_cast<amf_pts>(video_sample_duration_hns));

        if (submitted_frames == 0)
        {
            if (video.codec == PR_CODEC_H264)
            {
                surface->SetProperty(AMF_VIDEO_ENCODER_FORCE_PICTURE_TYPE, amf::AMFVariant(static_cast<amf_int64>(AMF_VIDEO_ENCODER_PICTURE_TYPE_IDR)));
                surface->SetProperty(AMF_VIDEO_ENCODER_INSERT_SPS, amf::AMFVariant(true));
                surface->SetProperty(AMF_VIDEO_ENCODER_INSERT_PPS, amf::AMFVariant(true));
            }
            else
            {
                surface->SetProperty(AMF_VIDEO_ENCODER_HEVC_FORCE_PICTURE_TYPE, amf::AMFVariant(static_cast<amf_int64>(AMF_VIDEO_ENCODER_HEVC_PICTURE_TYPE_IDR)));
                surface->SetProperty(AMF_VIDEO_ENCODER_HEVC_INSERT_HEADER, amf::AMFVariant(true));
            }
        }

        result = encoder->SubmitInput(surface);
        if (result == AMF_INPUT_FULL)
        {
            hr = drain_output(false);
            if (FAILED(hr))
                return hr;
            result = encoder->SubmitInput(surface);
        }
        if (result == AMF_INPUT_FULL)
        {
            set_last_error("NativeRecorder AMF input queue is full; dropping one frame.");
            return DXGI_ERROR_WAS_STILL_DRAWING;
        }
        if (result != AMF_OK && result != AMF_NEED_MORE_INPUT)
            return fail_amf("AMF SubmitInput", result);

        ++submitted_frames;
        return drain_output(false);
    }

    HRESULT drain_output(bool flushing)
    {
        if (!encoder)
            return S_OK;

        int repeat_count = 0;
        while (true)
        {
            amf::AMFDataPtr data;
            AMF_RESULT result = encoder->QueryOutput(&data);
            if (result == AMF_REPEAT)
            {
                if (!flushing || repeat_count++ >= 1000)
                    return S_OK;
                std::this_thread::sleep_for(std::chrono::milliseconds(1));
                continue;
            }
            if (result == AMF_EOF)
                return S_OK;
            if (!amf_result_success(result))
                return fail_amf("AMF QueryOutput", result);
            if (data == nullptr)
            {
                if (!flushing || repeat_count++ >= 1000)
                    return S_OK;
                std::this_thread::sleep_for(std::chrono::milliseconds(1));
                continue;
            }

            amf::AMFBufferPtr buffer(data);
            if (buffer == nullptr || buffer->GetNative() == nullptr || buffer->GetSize() == 0)
                continue;

            const auto* bytes = static_cast<const uint8_t*>(buffer->GetNative());
            std::vector<uint8_t> packet(bytes, bytes + buffer->GetSize());
            bool key_frame = false;
            if (video.codec == PR_CODEC_H264)
            {
                amf_int64 output_type = AMF_VIDEO_ENCODER_OUTPUT_DATA_TYPE_P;
                buffer->GetProperty(AMF_VIDEO_ENCODER_OUTPUT_DATA_TYPE, &output_type);
                key_frame = output_type == AMF_VIDEO_ENCODER_OUTPUT_DATA_TYPE_IDR ||
                    output_type == AMF_VIDEO_ENCODER_OUTPUT_DATA_TYPE_I;
            }
            else
            {
                amf_int64 output_type = AMF_VIDEO_ENCODER_HEVC_OUTPUT_DATA_TYPE_P;
                buffer->GetProperty(AMF_VIDEO_ENCODER_HEVC_OUTPUT_DATA_TYPE, &output_type);
                key_frame = output_type == AMF_VIDEO_ENCODER_HEVC_OUTPUT_DATA_TYPE_IDR ||
                    output_type == AMF_VIDEO_ENCODER_HEVC_OUTPUT_DATA_TYPE_I;
            }

            int64_t timestamp = static_cast<int64_t>(data->GetPts());
            if (timestamp <= 0)
                timestamp = static_cast<int64_t>(submitted_frames) * video_sample_duration_hns;

            HRESULT hr = muxer.write_video_packet(packet, key_frame, timestamp, video_sample_duration_hns);
            if (FAILED(hr))
                return hr;
            ++written_packets;
        }
    }

    HRESULT submit_audio(const void* data, int32_t byte_count, int64_t timestamp_hns) override
    {
        if (!audio.enabled)
            return S_OK;
        if (!initialized)
            return S_OK;

        HRESULT hr = muxer.write_audio(data, byte_count, timestamp_hns);
        if (FAILED(hr))
            return hr;

        ++audio_packets;
        return S_OK;
    }

    HRESULT stop() override
    {
        if (stopped)
            return S_OK;

        stopped = true;
        HRESULT result = S_OK;
        if (encoder)
        {
            AMF_RESULT amf_result = encoder->Drain();
            if (!amf_result_success(amf_result) && amf_result != AMF_INPUT_FULL)
                result = fail_amf("AMF encoder Drain", amf_result);

            if (SUCCEEDED(result))
                result = drain_output(true);

            encoder->Terminate();
            encoder.Release();
        }

        HRESULT mux_hr = muxer.close();
        if (FAILED(mux_hr) && SUCCEEDED(result))
            result = mux_hr;

        context.Release();
        converter.reset();

        if (SUCCEEDED(result))
        {
            set_last_error("NativeRecorder finalized via AMF + libavformat. submitted=" +
                std::to_string(submitted_frames) +
                ", packets=" + std::to_string(written_packets) +
                ", audioPackets=" + std::to_string(audio_packets));
        }

        return result;
    }
};

struct pr_recorder_t
{
    std::mutex mutex;
    pr_video_config video{};
    pr_audio_config audio{};
    std::wstring output_path;
    std::unique_ptr<NativeRecorderBackend> backend;

    HRESULT create_backend(ID3D11Device* source_device, DXGI_FORMAT source_format)
    {
        if (backend)
            return S_OK;

        UINT vendor_id = 0;
        std::string adapter_name;
        HRESULT info_hr = get_device_adapter_info(source_device, &vendor_id, &adapter_name);
        if (FAILED(info_hr))
            return info_hr;

        if (vendor_id == kAmdVendorId)
        {
            auto amf_backend = std::make_unique<AmfLibavRecorderBackend>(video, audio, output_path);
            HRESULT hr = amf_backend->initialize(source_device, source_format);
            if (SUCCEEDED(hr))
            {
                backend = std::move(amf_backend);
                return S_OK;
            }

            set_last_error("NativeRecorder AMF + libavformat path unavailable; using FFmpeg fallback. " +
                get_last_error_copy());
            return hr;
        }

        if (vendor_id != kNvidiaVendorId)
        {
            set_last_error("NativeRecorder source game device is not on an NVIDIA or AMD adapter; using FFmpeg fallback. adapter=" +
                adapter_name);
            return HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED);
        }

        auto nvenc_backend = std::make_unique<NvencLibavRecorderBackend>(video, audio, output_path);
        HRESULT hr = nvenc_backend->initialize(source_device, source_format);
        if (SUCCEEDED(hr))
        {
            backend = std::move(nvenc_backend);
            return S_OK;
        }

        set_last_error("NativeRecorder NvEncoderD3D11 + libavformat path unavailable; using FFmpeg fallback. " +
            get_last_error_copy());
        return hr;
    }

    HRESULT submit_shared_texture(ID3D11Device* source_device, HANDLE shared_handle, DXGI_FORMAT source_format, int64_t timestamp_hns)
    {
        HRESULT hr = create_backend(source_device, source_format);
        if (FAILED(hr))
            return hr;

        return backend->submit_shared_texture(source_device, shared_handle, source_format, timestamp_hns);
    }

    HRESULT submit_audio(const void* data, int32_t byte_count, int64_t timestamp_hns)
    {
        if (!backend)
            return S_OK;

        return backend->submit_audio(data, byte_count, timestamp_hns);
    }

    HRESULT stop()
    {
        if (!backend)
            return S_OK;

        return backend->stop();
    }
};

PR_API int32_t PR_CALL pr_get_abi_version(void)
{
    return PR_ABI_VERSION;
}

PR_API int32_t PR_CALL pr_probe(pr_probe_info* info)
{
    if (info == nullptr)
        return PR_E_INVALID_ARGUMENT;

    std::memset(info, 0, sizeof(*info));

    HRESULT hr = ensure_thread_com_initialized();
    if (FAILED(hr))
    {
        std::string message = "COM initialization failed: " + hresult_to_string(hr);
        copy_text(info->message, sizeof(info->message), message.c_str());
        set_last_error(message);
        return PR_OK;
    }

    std::string adapter_name;
    hr = find_nvidia_adapter(&adapter_name);
    if (SUCCEEDED(hr))
    {
        if (!nvenc_runtime_present())
        {
            copy_text(info->adapter_name, sizeof(info->adapter_name), adapter_name.c_str());
            copy_text(info->message, sizeof(info->message), "NVIDIA adapter was found, but nvEncodeAPI64.dll was not found.");
            info->is_supported_adapter = 1;
            set_last_error(info->message);
            return PR_OK;
        }

        copy_text(info->adapter_name, sizeof(info->adapter_name), adapter_name.c_str());
        copy_text(info->message, sizeof(info->message), "NativeRecorder NVIDIA D3D11 texture path is available (NvEncoderD3D11 + libavformat, FFmpeg fallback on runtime failure).");
        info->is_supported_adapter = 1;
        info->supports_d3d11_texture_input = 1;
        set_last_error(info->message);
        return PR_OK;
    }

    hr = find_amd_adapter(&adapter_name);
    if (FAILED(hr))
    {
        copy_text(info->message, sizeof(info->message), "No NVIDIA or AMD DXGI adapter was found.");
        set_last_error(info->message);
        return PR_OK;
    }

    if (!amf_runtime_present())
    {
        copy_text(info->adapter_name, sizeof(info->adapter_name), adapter_name.c_str());
        copy_text(info->message, sizeof(info->message), "AMD adapter was found, but amfrt64.dll was not found.");
        info->is_supported_adapter = 0;
        set_last_error(info->message);
        return PR_OK;
    }

    copy_text(info->adapter_name, sizeof(info->adapter_name), adapter_name.c_str());
    copy_text(info->message, sizeof(info->message), "NativeRecorder AMD D3D11 texture path is available (AMF + libavformat preferred, FFmpeg fallback on runtime failure).");
    info->is_supported_adapter = 1;
    info->supports_d3d11_texture_input = 1;
    set_last_error(info->message);
    return PR_OK;
}

PR_API int32_t PR_CALL pr_create(const pr_video_config* video, const pr_audio_config* audio, pr_recorder_t** recorder)
{
    if (video == nullptr || recorder == nullptr)
        return PR_E_INVALID_ARGUMENT;

    *recorder = nullptr;
    if (video->output_path == nullptr || video->width <= 0 || video->height <= 0 || video->fps <= 0)
    {
        set_last_error("Invalid NativeRecorder video configuration.");
        return PR_E_INVALID_ARGUMENT;
    }

    if (!is_supported_recording_codec(video->codec))
    {
        set_last_error("NativeRecorder currently supports H.264 and HEVC only.");
        return PR_E_NOT_AVAILABLE;
    }

    auto* instance = new (std::nothrow) pr_recorder_t();
    if (instance == nullptr)
    {
        set_last_error("Failed to allocate NativeRecorder instance.");
        return E_OUTOFMEMORY;
    }

    instance->video = *video;
    if (audio != nullptr)
        instance->audio = *audio;
    instance->output_path = video->output_path;

    *recorder = instance;
    set_last_error("NativeRecorder instance created; waiting for first D3D11 texture.");
    return PR_OK;
}

PR_API int32_t PR_CALL pr_submit_d3d11_texture(
    pr_recorder_t* recorder,
    void* d3d11_device,
    void* d3d11_texture,
    int32_t dxgi_format,
    int64_t timestamp_hns)
{
    (void)recorder;
    (void)d3d11_device;
    (void)d3d11_texture;
    (void)dxgi_format;
    (void)timestamp_hns;
    set_last_error("Direct D3D11 texture pointer submission is disabled; use shared texture ABI v5.");
    return PR_E_NOT_IMPLEMENTED;
}

PR_API int32_t PR_CALL pr_submit_d3d11_shared_texture(
    pr_recorder_t* recorder,
    void* d3d11_device,
    void* shared_handle,
    int32_t dxgi_format,
    int64_t timestamp_hns)
{
    if (recorder == nullptr || d3d11_device == nullptr || shared_handle == nullptr)
        return PR_E_INVALID_ARGUMENT;

    HRESULT hr = ensure_thread_com_initialized();
    if (FAILED(hr))
        return fail_hr("COM initialization failed", hr);

    std::lock_guard lock(recorder->mutex);
    hr = recorder->submit_shared_texture(
        static_cast<ID3D11Device*>(d3d11_device),
        static_cast<HANDLE>(shared_handle),
        static_cast<DXGI_FORMAT>(dxgi_format),
        timestamp_hns);
    if (FAILED(hr))
        return fail_hr("NativeRecorder texture submit failed", hr);

    return PR_OK;
}

PR_API int32_t PR_CALL pr_submit_audio(pr_recorder_t* recorder, const void* data, int32_t byte_count, int64_t timestamp_hns)
{
    if (recorder == nullptr)
        return PR_E_INVALID_ARGUMENT;

    std::lock_guard lock(recorder->mutex);
    HRESULT hr = recorder->submit_audio(data, byte_count, timestamp_hns);
    if (FAILED(hr))
        return fail_hr("NativeRecorder audio submit failed", hr);

    return PR_OK;
}

PR_API int32_t PR_CALL pr_stop(pr_recorder_t* recorder)
{
    if (recorder == nullptr)
        return PR_E_INVALID_ARGUMENT;

    HRESULT hr;
    {
        std::lock_guard lock(recorder->mutex);
        hr = recorder->stop();
    }

    if (FAILED(hr))
        return fail_hr("NativeRecorder finalize failed", hr);

    if (get_last_error_copy().empty())
        set_last_error("NativeRecorder finalized.");
    return PR_OK;
}

PR_API void PR_CALL pr_destroy(pr_recorder_t* recorder)
{
    delete recorder;
}

PR_API int32_t PR_CALL pr_get_last_error(char* buffer, int32_t buffer_size)
{
    if (buffer == nullptr || buffer_size <= 0)
        return PR_E_INVALID_ARGUMENT;

    std::lock_guard lock(g_error_mutex);
    copy_text(buffer, static_cast<size_t>(buffer_size), g_last_error.c_str());
    return PR_OK;
}
