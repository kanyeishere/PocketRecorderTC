struct pr_recorder_t
{
    std::mutex backend_mutex;
    std::mutex video_mutex;
    std::mutex audio_mutex;
    std::mutex stop_mutex;
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
        std::lock_guard video_lock(video_mutex);
        NativeRecorderBackend* active_backend = nullptr;
        {
            std::lock_guard backend_lock(backend_mutex);
            HRESULT hr = create_backend(source_device, source_format);
            if (FAILED(hr))
                return hr;
            active_backend = backend.get();
        }

        return active_backend->submit_shared_texture(source_device, shared_handle, source_format, timestamp_hns);
    }

    HRESULT submit_audio(const void* data, int32_t byte_count, int64_t timestamp_hns)
    {
        std::lock_guard audio_lock(audio_mutex);
        NativeRecorderBackend* active_backend = nullptr;
        {
            std::lock_guard backend_lock(backend_mutex);
            active_backend = backend.get();
        }
        if (active_backend == nullptr)
            return S_OK;

        return active_backend->submit_audio(data, byte_count, timestamp_hns);
    }

    HRESULT stop()
    {
        std::lock_guard stop_lock(stop_mutex);
        std::lock_guard video_lock(video_mutex);
        std::lock_guard audio_lock(audio_mutex);
        std::lock_guard backend_lock(backend_mutex);
        if (!backend)
            return S_OK;

        return backend->stop();
    }
};

int32_t fail_export_exception(const char* operation, const std::exception& ex)
{
    std::string message = "NativeRecorder ";
    message += operation != nullptr ? operation : "export";
    message += " threw a C++ exception: ";
    message += ex.what();
    set_last_error(message);
    return PR_E_ENCODER_FAILED;
}

int32_t fail_export_unknown_exception(const char* operation)
{
    std::string message = "NativeRecorder ";
    message += operation != nullptr ? operation : "export";
    message += " threw an unknown C++ exception.";
    set_last_error(message);
    return PR_E_ENCODER_FAILED;
}

void copy_export_exception(pr_probe_info* info, const std::string& message)
{
    if (info != nullptr)
        copy_text(info->message, sizeof(info->message), message.c_str());
    set_last_error(message);
}

PR_API int32_t PR_CALL pr_get_abi_version(void)
{
    return PR_ABI_VERSION;
}

PR_API int32_t PR_CALL pr_probe(pr_probe_info* info)
{
    try
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
    catch (const std::exception& ex)
    {
        std::string message = "NativeRecorder probe threw a C++ exception: ";
        message += ex.what();
        copy_export_exception(info, message);
        return PR_E_ENCODER_FAILED;
    }
    catch (...)
    {
        copy_export_exception(info, "NativeRecorder probe threw an unknown C++ exception.");
        return PR_E_ENCODER_FAILED;
    }
}

PR_API int32_t PR_CALL pr_get_diagnostics_report(char* buffer, int32_t buffer_size)
{
    try
    {
        if (buffer == nullptr || buffer_size <= 0)
            return PR_E_INVALID_ARGUMENT;

        HRESULT hr = ensure_thread_com_initialized();
        std::string report;
        if (FAILED(hr))
            report = "com=failed: " + hresult_to_string(hr) + "; ";
        else
            report = "com=ok; ";

        report += native_runtime_report();
        report += "; ";
        report += dxgi_adapter_report();

        copy_text(buffer, static_cast<size_t>(buffer_size), report.c_str());
        return PR_OK;
    }
    catch (const std::exception& ex)
    {
        return fail_export_exception("diagnostics report", ex);
    }
    catch (...)
    {
        return fail_export_unknown_exception("diagnostics report");
    }
}

PR_API int32_t PR_CALL pr_create(const pr_video_config* video, const pr_audio_config* audio, pr_recorder_t** recorder)
{
    try
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
    catch (const std::exception& ex)
    {
        if (recorder != nullptr)
            *recorder = nullptr;
        return fail_export_exception("create", ex);
    }
    catch (...)
    {
        if (recorder != nullptr)
            *recorder = nullptr;
        return fail_export_unknown_exception("create");
    }
}

PR_API int32_t PR_CALL pr_submit_d3d11_texture(
    pr_recorder_t* recorder,
    void* d3d11_device,
    void* d3d11_texture,
    int32_t dxgi_format,
    int64_t timestamp_hns)
{
    try
    {
        (void)recorder;
        (void)d3d11_device;
        (void)d3d11_texture;
        (void)dxgi_format;
        (void)timestamp_hns;
        set_last_error("Direct D3D11 texture pointer submission is disabled; use shared texture ABI v5.");
        return PR_E_NOT_IMPLEMENTED;
    }
    catch (const std::exception& ex)
    {
        return fail_export_exception("direct texture submit", ex);
    }
    catch (...)
    {
        return fail_export_unknown_exception("direct texture submit");
    }
}

PR_API int32_t PR_CALL pr_submit_d3d11_shared_texture(
    pr_recorder_t* recorder,
    void* d3d11_device,
    void* shared_handle,
    int32_t dxgi_format,
    int64_t timestamp_hns)
{
    try
    {
        if (recorder == nullptr || d3d11_device == nullptr || shared_handle == nullptr)
            return PR_E_INVALID_ARGUMENT;

        HRESULT hr = ensure_thread_com_initialized();
        if (FAILED(hr))
            return fail_hr("COM initialization failed", hr);

        hr = recorder->submit_shared_texture(
            static_cast<ID3D11Device*>(d3d11_device),
            static_cast<HANDLE>(shared_handle),
            static_cast<DXGI_FORMAT>(dxgi_format),
            timestamp_hns);
        if (FAILED(hr))
            return fail_hr("NativeRecorder texture submit failed", hr);

        return PR_OK;
    }
    catch (const std::exception& ex)
    {
        return fail_export_exception("shared texture submit", ex);
    }
    catch (...)
    {
        return fail_export_unknown_exception("shared texture submit");
    }
}

PR_API int32_t PR_CALL pr_submit_audio(pr_recorder_t* recorder, const void* data, int32_t byte_count, int64_t timestamp_hns)
{
    try
    {
        if (recorder == nullptr)
            return PR_E_INVALID_ARGUMENT;

        HRESULT hr = recorder->submit_audio(data, byte_count, timestamp_hns);
        if (FAILED(hr))
            return fail_hr("NativeRecorder audio submit failed", hr);

        return PR_OK;
    }
    catch (const std::exception& ex)
    {
        return fail_export_exception("audio submit", ex);
    }
    catch (...)
    {
        return fail_export_unknown_exception("audio submit");
    }
}

PR_API int32_t PR_CALL pr_stop(pr_recorder_t* recorder)
{
    try
    {
        if (recorder == nullptr)
            return PR_E_INVALID_ARGUMENT;

        HRESULT hr = recorder->stop();

        if (FAILED(hr))
            return fail_hr("NativeRecorder finalize failed", hr);

        if (get_last_error_copy().empty())
            set_last_error("NativeRecorder finalized.");
        return PR_OK;
    }
    catch (const std::exception& ex)
    {
        return fail_export_exception("stop", ex);
    }
    catch (...)
    {
        return fail_export_unknown_exception("stop");
    }
}

PR_API void PR_CALL pr_destroy(pr_recorder_t* recorder)
{
    try
    {
        delete recorder;
    }
    catch (const std::exception& ex)
    {
        (void)fail_export_exception("destroy", ex);
    }
    catch (...)
    {
        (void)fail_export_unknown_exception("destroy");
    }
}

PR_API int32_t PR_CALL pr_get_last_error(char* buffer, int32_t buffer_size)
{
    try
    {
        if (buffer == nullptr || buffer_size <= 0)
            return PR_E_INVALID_ARGUMENT;

        std::lock_guard lock(g_error_mutex);
        copy_text(buffer, static_cast<size_t>(buffer_size), g_last_error.c_str());
        return PR_OK;
    }
    catch (const std::exception& ex)
    {
        return fail_export_exception("get last error", ex);
    }
    catch (...)
    {
        return fail_export_unknown_exception("get last error");
    }
}
