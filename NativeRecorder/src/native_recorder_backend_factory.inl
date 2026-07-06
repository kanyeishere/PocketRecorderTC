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

    if (vendor_id == kAmdVendorId)
    {
        auto amf_backend = std::make_unique<AmfLibavRecorderBackend>(video, audio, output_path);
        HRESULT hr = amf_backend->initialize(source_device, source_format);
        if (SUCCEEDED(hr))
        {
            backend = std::move(amf_backend);
            return S_OK;
        }

        std::string failure_detail = get_last_error_copy();
        amf_backend.reset();
        set_last_error("NativeRecorder AMF + libavformat path unavailable; using FFmpeg fallback. " +
            failure_detail);
        return hr;
    }

    if (vendor_id == kIntelVendorId)
    {
        auto qsv_backend = std::make_unique<IntelVplLibavRecorderBackend>(video, audio, output_path);
        HRESULT hr = qsv_backend->initialize(source_device, source_format);
        if (SUCCEEDED(hr))
        {
            backend = std::move(qsv_backend);
            return S_OK;
        }

        std::string failure_detail = get_last_error_copy();
        qsv_backend.reset();
        set_last_error("NativeRecorder oneVPL QSV + libavformat path unavailable; using FFmpeg fallback. " +
            failure_detail);
        return hr;
    }

    if (vendor_id != kNvidiaVendorId)
    {
        set_last_error("NativeRecorder source game device is not on an NVIDIA, AMD, or Intel adapter; using FFmpeg fallback. adapter=" +
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

    std::string failure_detail = get_last_error_copy();
    nvenc_backend.reset();
    set_last_error("NativeRecorder NvEncoderD3D11 + libavformat path unavailable; using FFmpeg fallback. " +
        failure_detail);
    return hr;
}
