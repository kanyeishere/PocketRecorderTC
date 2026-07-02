#pragma once

#include <stdint.h>

#ifdef _WIN32
#  ifdef PR_NATIVE_RECORDER_BUILD
#    define PR_API extern "C" __declspec(dllexport)
#  else
#    define PR_API extern "C" __declspec(dllimport)
#  endif
#  define PR_CALL __cdecl
#else
#  define PR_API extern "C"
#  define PR_CALL
#endif

#define PR_ABI_VERSION 9

typedef struct pr_recorder_t pr_recorder_t;

enum pr_result
{
    PR_OK = 0,
    PR_E_NOT_AVAILABLE = 0x70000001,
    PR_E_NOT_IMPLEMENTED = 0x70000002,
    PR_E_INVALID_ARGUMENT = 0x70000003,
    PR_E_INVALID_STATE = 0x70000004,
    PR_E_ENCODER_FAILED = 0x70000005,
};

enum pr_codec
{
    PR_CODEC_H264 = 1,
    PR_CODEC_HEVC = 2,
};

enum pr_pixel_format
{
    PR_PIXEL_FORMAT_UNKNOWN = 0,
    PR_PIXEL_FORMAT_BGRA8 = 1,
    PR_PIXEL_FORMAT_RGBA8 = 2,
    PR_PIXEL_FORMAT_NV12 = 3,
};

typedef struct pr_probe_info
{
    int32_t is_supported_adapter;
    int32_t supports_d3d11_texture_input;
    char adapter_name[128];
    char message[256];
} pr_probe_info;

typedef struct pr_video_config
{
    int32_t width;
    int32_t height;
    int32_t fps;
    int32_t bitrate_bps;
    int32_t codec;
    int32_t pixel_format;
    const wchar_t* output_path;
} pr_video_config;

typedef struct pr_audio_config
{
    int32_t enabled;
    int32_t sample_rate;
    int32_t channels;
    int32_t bits_per_sample;
    int32_t is_float;
} pr_audio_config;

PR_API int32_t PR_CALL pr_get_abi_version(void);
PR_API int32_t PR_CALL pr_probe(pr_probe_info* info);
PR_API int32_t PR_CALL pr_create(const pr_video_config* video, const pr_audio_config* audio, pr_recorder_t** recorder);
PR_API int32_t PR_CALL pr_submit_d3d11_texture(
    pr_recorder_t* recorder,
    void* d3d11_device,
    void* d3d11_texture,
    int32_t dxgi_format,
    int64_t timestamp_hns);
PR_API int32_t PR_CALL pr_submit_d3d11_shared_texture(
    pr_recorder_t* recorder,
    void* d3d11_device,
    void* shared_handle,
    int32_t dxgi_format,
    int64_t timestamp_hns);
PR_API int32_t PR_CALL pr_submit_audio(pr_recorder_t* recorder, const void* data, int32_t byte_count, int64_t timestamp_hns);
PR_API int32_t PR_CALL pr_stop(pr_recorder_t* recorder);
PR_API void PR_CALL pr_destroy(pr_recorder_t* recorder);
PR_API int32_t PR_CALL pr_get_last_error(char* buffer, int32_t buffer_size);
