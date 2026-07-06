using NativeBackendCreator = std::unique_ptr<NativeRecorderBackend>(*)(
    const pr_video_config& video,
    const pr_audio_config& audio,
    const std::wstring& output_path);

struct NativeBackendDescriptor
{
    UINT vendor_id;
    const char* vendor_name;
    const char* path_name;
    const char* unavailable_message;
    NativeBackendCreator create;
};

std::unique_ptr<NativeRecorderBackend> create_nvenc_backend(
    const pr_video_config& video,
    const pr_audio_config& audio,
    const std::wstring& output_path)
{
    return std::make_unique<NvencLibavRecorderBackend>(video, audio, output_path);
}

std::unique_ptr<NativeRecorderBackend> create_amf_backend(
    const pr_video_config& video,
    const pr_audio_config& audio,
    const std::wstring& output_path)
{
    return std::make_unique<AmfLibavRecorderBackend>(video, audio, output_path);
}

std::unique_ptr<NativeRecorderBackend> create_qsv_backend(
    const pr_video_config& video,
    const pr_audio_config& audio,
    const std::wstring& output_path)
{
    return std::make_unique<IntelVplLibavRecorderBackend>(video, audio, output_path);
}

const NativeBackendDescriptor* find_native_backend_descriptor(UINT vendor_id)
{
    static const NativeBackendDescriptor descriptors[] =
    {
        {
            kNvidiaVendorId,
            "NVIDIA",
            "NativeRecorder NvEncoderD3D11 + libavformat",
            "NativeRecorder NvEncoderD3D11 + libavformat path unavailable; using FFmpeg fallback. ",
            create_nvenc_backend,
        },
        {
            kAmdVendorId,
            "AMD",
            "NativeRecorder AMF + libavformat",
            "NativeRecorder AMF + libavformat path unavailable; using FFmpeg fallback. ",
            create_amf_backend,
        },
        {
            kIntelVendorId,
            "Intel",
            "NativeRecorder oneVPL QSV + libavformat",
            "NativeRecorder oneVPL QSV + libavformat path unavailable; using FFmpeg fallback. ",
            create_qsv_backend,
        },
    };

    for (const NativeBackendDescriptor& descriptor : descriptors)
    {
        if (descriptor.vendor_id == vendor_id)
            return &descriptor;
    }

    return nullptr;
}

HRESULT initialize_native_backend(
    const NativeBackendDescriptor& descriptor,
    ID3D11Device* source_device,
    DXGI_FORMAT source_format,
    const pr_video_config& video,
    const pr_audio_config& audio,
    const std::wstring& output_path,
    std::unique_ptr<NativeRecorderBackend>& backend)
{
    std::unique_ptr<NativeRecorderBackend> candidate = descriptor.create(video, audio, output_path);
    HRESULT hr = candidate->initialize(source_device, source_format);
    if (SUCCEEDED(hr))
    {
        backend = std::move(candidate);
        return S_OK;
    }

    std::string failure_detail = get_last_error_copy();
    candidate.reset();
    set_last_error(std::string(descriptor.unavailable_message) + failure_detail);
    return hr;
}

HRESULT create_native_recorder_backend(
    ID3D11Device* source_device,
    DXGI_FORMAT source_format,
    const pr_video_config& video,
    const pr_audio_config& audio,
    const std::wstring& output_path,
    std::unique_ptr<NativeRecorderBackend>& backend)
{
    if (backend)
        return S_OK;

    UINT vendor_id = 0;
    std::string adapter_name;
    HRESULT info_hr = get_device_adapter_info(source_device, &vendor_id, &adapter_name);
    if (FAILED(info_hr))
        return info_hr;

    const NativeBackendDescriptor* descriptor = find_native_backend_descriptor(vendor_id);
    if (descriptor == nullptr)
    {
        set_last_error("NativeRecorder source game device is not on an NVIDIA, AMD, or Intel adapter; using FFmpeg fallback. adapter=" +
            adapter_name);
        return HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED);
    }

    return initialize_native_backend(
        *descriptor,
        source_device,
        source_format,
        video,
        audio,
        output_path,
        backend);
}
