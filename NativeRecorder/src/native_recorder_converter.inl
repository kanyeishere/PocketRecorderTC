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
    HANDLE cached_shared_handle = nullptr;
    ComPtr<ID3D11Texture2D> cached_source_texture;
    ComPtr<IDXGIKeyedMutex> cached_keyed_mutex;
    ComPtr<ID3D11Texture2D> source_copy_texture;
    ComPtr<ID3D11VideoProcessorInputView> source_copy_input_view;
    struct OutputViewCacheEntry
    {
        ID3D11Texture2D* texture = nullptr;
        ComPtr<ID3D11VideoProcessorOutputView> view;
    };
    std::vector<OutputViewCacheEntry> output_view_cache;
    std::string adapter_name;
    LUID adapter_luid{};
    UINT source_format_support = 0;
    UINT nv12_format_support = 0;
    uint64_t frame_index = 0;
    bool initialized = false;

    int source_width() const { return video.width; }
    int source_height() const { return video.height; }
    int output_width() const { return video_output_width(video); }
    int output_height() const { return video_output_height(video); }
    int encoded_width() const { return video_encoded_width(video); }
    int encoded_height() const { return video_encoded_height(video); }

    HRESULT initialize(ID3D11Device* source_device, DXGI_FORMAT source_format)
    {
        if (initialized)
            return S_OK;
        if (source_device == nullptr)
            return E_POINTER;
        if (video.width <= 0 || video.height <= 0 || output_width() <= 0 || output_height() <= 0 || video.fps <= 0)
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

        ComPtr<ID3D10Multithread> multithread;
        if (SUCCEEDED(device.As(&multithread)))
            multithread->SetMultithreadProtected(TRUE);

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
        RECT dest_rect{0, 0, output_width(), output_height()};
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
                (existing.BindFlags & required_bind_flags) == required_bind_flags &&
                source_copy_input_view)
            {
                return S_OK;
            }

            source_copy_input_view.Reset();
            source_copy_texture.Reset();
        }

        D3D11_TEXTURE2D_DESC copy_desc = source_desc;
        copy_desc.MipLevels = 1;
        copy_desc.ArraySize = 1;
        copy_desc.Usage = D3D11_USAGE_DEFAULT;
        copy_desc.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET;
        copy_desc.CPUAccessFlags = 0;
        copy_desc.MiscFlags = 0;

        HRESULT hr = device->CreateTexture2D(&copy_desc, nullptr, &source_copy_texture);
        if (FAILED(hr))
            return hr;

        D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC input_view_desc{};
        input_view_desc.FourCC = 0;
        input_view_desc.ViewDimension = D3D11_VPIV_DIMENSION_TEXTURE2D;
        input_view_desc.Texture2D.MipSlice = 0;
        input_view_desc.Texture2D.ArraySlice = 0;

        hr = video_device->CreateVideoProcessorInputView(
            source_copy_texture.Get(),
            video_processor_enum.Get(),
            &input_view_desc,
            &source_copy_input_view);
        if (FAILED(hr))
        {
            source_copy_texture.Reset();
            return hr;
        }

        return S_OK;
    }

    HRESULT ensure_shared_source_texture(HANDLE shared_handle, DXGI_FORMAT source_format, D3D11_TEXTURE2D_DESC& source_desc)
    {
        if (shared_handle == nullptr)
            return E_POINTER;

        if (shared_handle != cached_shared_handle || !cached_source_texture)
        {
            cached_keyed_mutex.Reset();
            cached_source_texture.Reset();
            cached_shared_handle = nullptr;

            HRESULT hr = device->OpenSharedResource(shared_handle, __uuidof(ID3D11Texture2D), &cached_source_texture);
            if (FAILED(hr))
            {
                set_last_error("NativeRecorder failed to open shared texture on the source adapter: " + hresult_to_string(hr));
                return hr;
            }

            cached_shared_handle = shared_handle;
            (void) cached_source_texture.As(&cached_keyed_mutex);
        }

        cached_source_texture->GetDesc(&source_desc);
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

        HRESULT hr = ensure_source_copy_texture(source_desc);
        if (FAILED(hr))
            return fail_step("CreateTexture2D(source copy)", hr, "source=" + texture_desc_to_string(source_desc));

        return S_OK;
    }

    HRESULT ensure_output_view(ID3D11Texture2D* output_texture, ID3D11VideoProcessorOutputView** output_view)
    {
        if (output_texture == nullptr || output_view == nullptr)
            return E_POINTER;

        *output_view = nullptr;
        for (auto& entry : output_view_cache)
        {
            if (entry.texture == output_texture && entry.view)
            {
                *output_view = entry.view.Get();
                return S_OK;
            }
        }

        D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC output_view_desc{};
        output_view_desc.ViewDimension = D3D11_VPOV_DIMENSION_TEXTURE2D;
        output_view_desc.Texture2D.MipSlice = 0;

        OutputViewCacheEntry entry{};
        entry.texture = output_texture;
        HRESULT hr = video_device->CreateVideoProcessorOutputView(
            output_texture,
            video_processor_enum.Get(),
            &output_view_desc,
            &entry.view);
        if (FAILED(hr))
            return hr;

        output_view_cache.push_back(std::move(entry));
        *output_view = output_view_cache.back().view.Get();
        return S_OK;
    }

    HRESULT create_nv12_texture(ComPtr<ID3D11Texture2D>& nv12_texture)
    {
        D3D11_TEXTURE2D_DESC output_desc{};
        output_desc.Width = static_cast<UINT>(encoded_width());
        output_desc.Height = static_cast<UINT>(encoded_height());
        output_desc.MipLevels = 1;
        output_desc.ArraySize = 1;
        output_desc.Format = DXGI_FORMAT_NV12;
        output_desc.SampleDesc.Count = 1;
        output_desc.Usage = D3D11_USAGE_DEFAULT;
        output_desc.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;

        HRESULT hr = device->CreateTexture2D(&output_desc, nullptr, &nv12_texture);
        if (FAILED(hr))
            return fail_step("CreateTexture2D(NV12 output)", hr, "output=" + texture_desc_to_string(output_desc));

        return S_OK;
    }

    HRESULT convert_shared_texture_to(HANDLE shared_handle, DXGI_FORMAT source_format, ID3D11Texture2D* output_texture)
    {
        if (!initialized)
            return E_UNEXPECTED;
        if (output_texture == nullptr)
            return E_POINTER;

        D3D11_TEXTURE2D_DESC source_desc{};
        HRESULT hr = ensure_shared_source_texture(shared_handle, source_format, source_desc);
        if (FAILED(hr))
            return hr;

        bool mutex_acquired = false;
        if (cached_keyed_mutex)
        {
            hr = cached_keyed_mutex->AcquireSync(kEncoderReadKey, kSharedTextureAcquireTimeoutMs);
            if (hr == WAIT_TIMEOUT || hr == DXGI_ERROR_WAIT_TIMEOUT)
            {
                set_last_error("NativeRecorder shared texture was not ready; dropping one frame.");
                return DXGI_ERROR_WAS_STILL_DRAWING;
            }
            if (FAILED(hr))
                return fail_step("IDXGIKeyedMutex::AcquireSync", hr, "source=" + texture_desc_to_string(source_desc));

            mutex_acquired = true;
        }

        device_context->CopyResource(source_copy_texture.Get(), cached_source_texture.Get());
        if (cached_keyed_mutex)
        {
            HRESULT release_hr = cached_keyed_mutex->ReleaseSync(kGameWriteKey);
            mutex_acquired = false;
            if (FAILED(release_hr))
                return fail_step("IDXGIKeyedMutex::ReleaseSync", release_hr, "source=" + texture_desc_to_string(source_desc));
        }

        ID3D11VideoProcessorOutputView* output_view = nullptr;
        hr = ensure_output_view(output_texture, &output_view);
        if (FAILED(hr))
            return fail_step("CreateVideoProcessorOutputView", hr);

        D3D11_VIDEO_PROCESSOR_STREAM stream{};
        stream.Enable = TRUE;
        stream.OutputIndex = 0;
        stream.InputFrameOrField = 0;
        stream.PastFrames = 0;
        stream.FutureFrames = 0;
        stream.pInputSurface = source_copy_input_view.Get();

        hr = video_context->VideoProcessorBlt(
            video_processor.Get(),
            output_view,
            static_cast<UINT>(frame_index),
            1,
            &stream);
        if (FAILED(hr))
            return fail_step("VideoProcessorBlt", hr, "source=" + texture_desc_to_string(source_desc));

        ++frame_index;
        return S_OK;
    }

    void reset()
    {
        output_view_cache.clear();
        source_copy_input_view.Reset();
        source_copy_texture.Reset();
        cached_keyed_mutex.Reset();
        cached_source_texture.Reset();
        cached_shared_handle = nullptr;
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

