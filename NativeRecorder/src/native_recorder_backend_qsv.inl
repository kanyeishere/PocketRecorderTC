struct IntelVplD3D11Allocator
{
    struct MemId
    {
        ID3D11Texture2D* texture = nullptr;
    };

    ComPtr<ID3D11Device> device;

    void initialize(ID3D11Device* d3d_device)
    {
        device = d3d_device;
    }

    static DXGI_FORMAT dxgi_format_from_fourcc(mfxU32 fourcc)
    {
        switch (fourcc)
        {
        case MFX_FOURCC_NV12:
            return DXGI_FORMAT_NV12;
        case MFX_FOURCC_P010:
            return DXGI_FORMAT_P010;
        case MFX_FOURCC_RGB4:
            return DXGI_FORMAT_B8G8R8A8_UNORM;
        default:
            return DXGI_FORMAT_UNKNOWN;
        }
    }

    static mfxStatus MFX_CDECL alloc(mfxHDL pthis, mfxFrameAllocRequest* request, mfxFrameAllocResponse* response)
    {
        auto* self = static_cast<IntelVplD3D11Allocator*>(pthis);
        if (self == nullptr || request == nullptr || response == nullptr)
            return MFX_ERR_NULL_PTR;
        if (!self->device)
            return MFX_ERR_NOT_INITIALIZED;
        if ((request->Type & MFX_MEMTYPE_SYSTEM_MEMORY) != 0)
            return MFX_ERR_UNSUPPORTED;
        if (request->NumFrameSuggested == 0)
            return MFX_ERR_MEMORY_ALLOC;

        DXGI_FORMAT format = dxgi_format_from_fourcc(request->Info.FourCC);
        if (format == DXGI_FORMAT_UNKNOWN)
            return MFX_ERR_UNSUPPORTED;

        auto* mids = static_cast<mfxMemId*>(std::calloc(request->NumFrameSuggested, sizeof(mfxMemId)));
        if (mids == nullptr)
            return MFX_ERR_MEMORY_ALLOC;

        D3D11_TEXTURE2D_DESC desc{};
        desc.Width = request->Info.Width;
        desc.Height = request->Info.Height;
        desc.MipLevels = 1;
        desc.ArraySize = 1;
        desc.Format = format;
        desc.SampleDesc.Count = 1;
        desc.Usage = D3D11_USAGE_DEFAULT;
        desc.BindFlags = D3D11_BIND_DECODER;
        desc.CPUAccessFlags = 0;
        desc.MiscFlags = 0;

        if ((request->Type & MFX_MEMTYPE_VIDEO_MEMORY_PROCESSOR_TARGET) != 0)
            desc.BindFlags = D3D11_BIND_RENDER_TARGET;

        for (mfxU16 i = 0; i < request->NumFrameSuggested; ++i)
        {
            auto* mem = new (std::nothrow) MemId();
            if (mem == nullptr)
            {
                free_allocated_mids(mids, i);
                return MFX_ERR_MEMORY_ALLOC;
            }

            HRESULT hr = self->device->CreateTexture2D(&desc, nullptr, &mem->texture);
            if (FAILED(hr))
            {
                delete mem;
                free_allocated_mids(mids, i);
                return MFX_ERR_MEMORY_ALLOC;
            }

            mids[i] = reinterpret_cast<mfxMemId>(mem);
        }

        response->mids = mids;
        response->NumFrameActual = request->NumFrameSuggested;
        return MFX_ERR_NONE;
    }

    static mfxStatus MFX_CDECL lock(mfxHDL, mfxMemId, mfxFrameData*)
    {
        return MFX_ERR_UNSUPPORTED;
    }

    static mfxStatus MFX_CDECL unlock(mfxHDL, mfxMemId, mfxFrameData*)
    {
        return MFX_ERR_NONE;
    }

    static mfxStatus MFX_CDECL get_hdl(mfxHDL, mfxMemId mid, mfxHDL* handle)
    {
        if (mid == nullptr || handle == nullptr)
            return MFX_ERR_NULL_PTR;

        auto* mem = reinterpret_cast<MemId*>(mid);
        if (mem->texture == nullptr)
            return MFX_ERR_INVALID_HANDLE;

        auto* pair = reinterpret_cast<mfxHDLPair*>(handle);
        pair->first = mem->texture;
        pair->second = nullptr;
        return MFX_ERR_NONE;
    }

    static mfxStatus MFX_CDECL free(mfxHDL, mfxFrameAllocResponse* response)
    {
        if (response == nullptr)
            return MFX_ERR_NULL_PTR;

        free_allocated_mids(response->mids, response->NumFrameActual);
        response->mids = nullptr;
        response->NumFrameActual = 0;
        return MFX_ERR_NONE;
    }

    static ID3D11Texture2D* texture_from_mem_id(mfxMemId mid)
    {
        auto* mem = reinterpret_cast<MemId*>(mid);
        return mem != nullptr ? mem->texture : nullptr;
    }

private:
    static void free_allocated_mids(mfxMemId* mids, mfxU16 count)
    {
        if (mids == nullptr)
            return;

        for (mfxU16 i = 0; i < count; ++i)
        {
            auto* mem = reinterpret_cast<MemId*>(mids[i]);
            if (mem != nullptr)
            {
                if (mem->texture != nullptr)
                    mem->texture->Release();
                delete mem;
            }
        }

        std::free(mids);
    }
};

struct IntelVplLibavRecorderBackend final : NativeD3D11LibavRecorderBackend
{
    static constexpr mfxU64 kTimestampUnknown = static_cast<mfxU64>(MFX_TIMESTAMP_UNKNOWN);

    struct AvFreeDeleter
    {
        void operator()(uint8_t* ptr) const
        {
            av_free(ptr);
        }
    };

    struct Task
    {
        mfxBitstream bitstream{};
        mfxEncodeCtrl ctrl{};
        mfxSyncPoint syncp = nullptr;
        std::unique_ptr<uint8_t, AvFreeDeleter> buffer;
    };

    mfxLoader loader = nullptr;
    mfxSession session = nullptr;
    mfxFrameAllocator frame_allocator{};
    IntelVplD3D11Allocator allocator;
    mfxVideoParam encode_params{};
    mfxFrameAllocResponse surface_response{};
    std::vector<std::unique_ptr<mfxFrameSurface1>> surfaces;
    std::vector<Task> tasks;
    size_t first_sync_task = 0;
    mfxExtCodingOption2 coding_option2{};
    std::vector<mfxExtBuffer*> encode_ext_buffers;
    std::vector<uint8_t> sequence_params;
    mfxVersion vpl_version{};
    bool encoder_initialized = false;
    bool surfaces_allocated = false;

    IntelVplLibavRecorderBackend(const pr_video_config& video_config, const pr_audio_config& audio_config, std::wstring output)
        : NativeD3D11LibavRecorderBackend(video_config, audio_config, std::move(output), "QSV")
    {
        converter.required_vendor_id = kIntelVendorId;
        converter.required_vendor_name = "Intel";
    }

    ~IntelVplLibavRecorderBackend() override
    {
        stop();
    }

    const char* backend_name() const override
    {
        return "oneVPL QSV+libavformat";
    }

    HRESULT initialize(ID3D11Device* source_device, DXGI_FORMAT source_format) override
    {
        if (initialized)
            return S_OK;
        if (source_device == nullptr)
            return E_POINTER;
        if (output_path.empty())
            return E_INVALIDARG;
        if (video.width <= 0 || video.height <= 0 || video_output_width(video) <= 0 || video_output_height(video) <= 0 || video.fps <= 0)
            return E_INVALIDARG;
        if (!is_supported_recording_codec(video.codec))
            return HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED);
        if (!onevpl_runtime_present())
        {
            set_last_error("Intel oneVPL runtime libvpl.dll was not found.");
            return HRESULT_FROM_WIN32(ERROR_NOT_FOUND);
        }

        HRESULT hr = converter.initialize(source_device, source_format);
        if (FAILED(hr))
            return hr;

        const int encoded_width = converter.encoded_width();
        const int encoded_height = converter.encoded_height();
        video_sample_duration_hns = static_cast<int64_t>(make_sample_duration_hns(video.fps));

        hr = initialize_session();
        if (FAILED(hr))
            return hr;

        hr = configure_encoder(encoded_width, encoded_height);
        if (FAILED(hr))
            return hr;

        hr = allocate_surfaces();
        if (FAILED(hr))
            return hr;

        mfxStatus sts = MFXVideoENCODE_Init(session, &encode_params);
        if (!vpl_status_success(sts))
            return fail_vpl("oneVPL Encode Init", sts, "codec=" + std::string(codec_name(video.codec)));
        encoder_initialized = true;

        sts = MFXVideoENCODE_GetVideoParam(session, &encode_params);
        if (!vpl_status_success(sts))
            return fail_vpl("oneVPL GetVideoParam", sts);

        hr = initialize_tasks();
        if (FAILED(hr))
            return hr;

        hr = read_sequence_params(sequence_params);
        if (FAILED(hr))
            return hr;

        hr = muxer.open(output_path, video, audio, sequence_params);
        if (FAILED(hr))
            return hr;

        hr = ensure_conversion_pool();
        if (FAILED(hr))
            return hr;

        initialized = true;
        start_video_worker();

        std::string message = "NativeRecorder initialized: source Intel adapter=" + converter.adapter_name +
            ", luid=" + std::to_string(static_cast<uint32_t>(converter.adapter_luid.HighPart)) + ":" +
            std::to_string(converter.adapter_luid.LowPart) +
            ", source=" + std::to_string(video.width) + "x" + std::to_string(video.height) +
            ", output=" + std::to_string(video_output_width(video)) + "x" + std::to_string(video_output_height(video)) +
            ", sourceFormat=" + dxgi_format_to_string(source_format) +
            ", encoded=" + std::to_string(encoded_width) + "x" + std::to_string(encoded_height) +
            ", vplSurface=" + std::to_string(encode_params.mfx.FrameInfo.Width) + "x" + std::to_string(encode_params.mfx.FrameInfo.Height) +
            ", surfaces=" + std::to_string(surfaces.size()) +
            ", tasks=" + std::to_string(tasks.size()) +
            ", asyncDepth=" + std::to_string(encode_params.AsyncDepth) +
            ", vplVersion=" + std::to_string(vpl_version.Major) + "." + std::to_string(vpl_version.Minor) +
            ", vpSourceSupport=" + hex_uint32(converter.source_format_support) +
            ", vpNv12Support=" + hex_uint32(converter.nv12_format_support) +
            ", qsvInput=shared conversion pool -> oneVPL-owned DX11 NV12 surfaces" +
            ", output=" + std::string(codec_name(video.codec)) + "/MP4 via oneVPL QSV + libavformat.";
        set_last_error(message);
        return S_OK;
    }

    HRESULT initialize_session()
    {
        loader = MFXLoad();
        if (loader == nullptr)
        {
            set_last_error("NativeRecorder oneVPL MFXLoad returned null.");
            return E_FAIL;
        }

        mfxConfig cfg = MFXCreateConfig(loader);
        if (cfg == nullptr)
        {
            set_last_error("NativeRecorder oneVPL MFXCreateConfig returned null.");
            return E_FAIL;
        }

        HRESULT hr = set_filter_u32(cfg, "mfxImplDescription.Impl", MFX_IMPL_TYPE_HARDWARE);
        if (FAILED(hr)) return hr;
        hr = set_filter_u32(cfg, "mfxImplDescription.VendorID", kIntelVendorId);
        if (FAILED(hr)) return hr;
        hr = set_filter_u32(cfg, "mfxImplDescription.AccelerationMode", MFX_ACCEL_MODE_VIA_D3D11);
        if (FAILED(hr)) return hr;

        mfxStatus sts = MFXCreateSession(loader, 0, &session);
        if (!vpl_status_success(sts))
            return fail_vpl("oneVPL CreateSession", sts);

        sts = MFXQueryVersion(session, &vpl_version);
        if (!vpl_status_success(sts))
            return fail_vpl("oneVPL QueryVersion", sts);

        sts = MFXVideoCORE_SetHandle(session, MFX_HANDLE_D3D11_DEVICE, converter.device.Get());
        if (!vpl_status_success(sts))
            return fail_vpl("oneVPL SetHandle(D3D11)", sts, "adapter=" + converter.adapter_name);

        allocator.initialize(converter.device.Get());
        frame_allocator.pthis = &allocator;
        frame_allocator.Alloc = IntelVplD3D11Allocator::alloc;
        frame_allocator.Lock = IntelVplD3D11Allocator::lock;
        frame_allocator.Unlock = IntelVplD3D11Allocator::unlock;
        frame_allocator.GetHDL = IntelVplD3D11Allocator::get_hdl;
        frame_allocator.Free = IntelVplD3D11Allocator::free;

        sts = MFXVideoCORE_SetFrameAllocator(session, &frame_allocator);
        if (!vpl_status_success(sts))
            return fail_vpl("oneVPL SetFrameAllocator", sts);

        return S_OK;
    }

    static HRESULT set_filter_u32(mfxConfig cfg, const char* name, mfxU32 value)
    {
        mfxVariant variant{};
        variant.Type = MFX_VARIANT_TYPE_U32;
        variant.Data.U32 = value;
        mfxStatus sts = MFXSetConfigFilterProperty(cfg, reinterpret_cast<const mfxU8*>(name), variant);
        if (!vpl_status_success(sts))
            return fail_vpl("oneVPL SetConfigFilterProperty", sts, name != nullptr ? name : "");
        return S_OK;
    }

    HRESULT configure_encoder(int encoded_width, int encoded_height)
    {
        std::memset(&encode_params, 0, sizeof(encode_params));
        std::memset(&coding_option2, 0, sizeof(coding_option2));
        encode_ext_buffers.clear();

        const int fps = std::max(1, video.fps);
        const int bitrate_bps = video.bitrate_bps > 0 ? video.bitrate_bps : 12'000'000;
        const mfxU16 gop_size = static_cast<mfxU16>(std::min<int>(std::max(1, fps * 2), UINT16_MAX));
        const mfxU16 target_kbps = static_cast<mfxU16>(std::min<int>(std::max(1, bitrate_bps / 1000), UINT16_MAX));

        encode_params.mfx.CodecId = vpl_codec_id(video.codec);
        encode_params.mfx.CodecProfile = static_cast<mfxU16>(
            video.codec == PR_CODEC_HEVC ? MFX_PROFILE_HEVC_MAIN : MFX_PROFILE_AVC_HIGH);
        encode_params.mfx.TargetUsage = MFX_TARGETUSAGE_BEST_SPEED;
        encode_params.mfx.RateControlMethod = MFX_RATECONTROL_CBR;
        encode_params.mfx.TargetKbps = target_kbps;
        encode_params.mfx.MaxKbps = target_kbps;
        encode_params.mfx.BufferSizeInKB = std::max<mfxU16>(1, static_cast<mfxU16>((target_kbps / 8) * 2));
        encode_params.mfx.InitialDelayInKB = std::max<mfxU16>(1, encode_params.mfx.BufferSizeInKB / 2);
        encode_params.mfx.FrameInfo.FourCC = MFX_FOURCC_NV12;
        encode_params.mfx.FrameInfo.ChromaFormat = MFX_CHROMAFORMAT_YUV420;
        encode_params.mfx.FrameInfo.PicStruct = MFX_PICSTRUCT_PROGRESSIVE;
        encode_params.mfx.FrameInfo.FrameRateExtN = static_cast<mfxU32>(fps);
        encode_params.mfx.FrameInfo.FrameRateExtD = 1;
        encode_params.mfx.FrameInfo.CropX = 0;
        encode_params.mfx.FrameInfo.CropY = 0;
        encode_params.mfx.FrameInfo.CropW = static_cast<mfxU16>(encoded_width);
        encode_params.mfx.FrameInfo.CropH = static_cast<mfxU16>(encoded_height);
        encode_params.mfx.FrameInfo.Width = static_cast<mfxU16>(align_to_16(encoded_width));
        encode_params.mfx.FrameInfo.Height = static_cast<mfxU16>(align_to_16(encoded_height));
        encode_params.mfx.GopPicSize = gop_size;
        encode_params.mfx.GopRefDist = 1;
        encode_params.mfx.GopOptFlag = MFX_GOP_CLOSED;
        encode_params.mfx.IdrInterval = 1;
        encode_params.mfx.NumSlice = 1;
        encode_params.mfx.LowPower = MFX_CODINGOPTION_ON;
        encode_params.AsyncDepth = kIntelVplAsyncDepth;
        encode_params.IOPattern = MFX_IOPATTERN_IN_VIDEO_MEMORY;

        coding_option2.Header.BufferId = MFX_EXTBUFF_CODING_OPTION2;
        coding_option2.Header.BufferSz = sizeof(coding_option2);
        coding_option2.RepeatPPS = MFX_CODINGOPTION_ON;
        coding_option2.BRefType = MFX_B_REF_OFF;
        coding_option2.LookAheadDepth = 0;
        encode_ext_buffers.push_back(reinterpret_cast<mfxExtBuffer*>(&coding_option2));
        encode_params.ExtParam = encode_ext_buffers.data();
        encode_params.NumExtParam = static_cast<mfxU16>(encode_ext_buffers.size());

        mfxStatus sts = MFXVideoENCODE_Query(session, &encode_params, &encode_params);
        if (sts == MFX_ERR_UNSUPPORTED || sts == MFX_ERR_UNDEFINED_BEHAVIOR)
        {
            encode_params.mfx.LowPower = MFX_CODINGOPTION_OFF;
            sts = MFXVideoENCODE_Query(session, &encode_params, &encode_params);
        }
        if (sts == MFX_WRN_INCOMPATIBLE_VIDEO_PARAM)
            sts = MFX_ERR_NONE;
        if (!vpl_status_success(sts))
        {
            return fail_vpl(
                "oneVPL Encode Query",
                sts,
                "codec=" + std::string(codec_name(video.codec)) +
                ", encoded=" + std::to_string(encoded_width) + "x" + std::to_string(encoded_height));
        }

        encode_params.ExtParam = encode_ext_buffers.data();
        encode_params.NumExtParam = static_cast<mfxU16>(encode_ext_buffers.size());
        encode_params.AsyncDepth = encode_params.AsyncDepth != 0 ? encode_params.AsyncDepth : kIntelVplAsyncDepth;
        return S_OK;
    }

    HRESULT allocate_surfaces()
    {
        mfxFrameAllocRequest request{};
        mfxStatus sts = MFXVideoENCODE_QueryIOSurf(session, &encode_params, &request);
        if (!vpl_status_success(sts))
            return fail_vpl("oneVPL QueryIOSurf", sts);

        request.Type |= MFX_MEMTYPE_EXTERNAL_FRAME | MFX_MEMTYPE_VIDEO_MEMORY_ENCODER_TARGET | MFX_MEMTYPE_FROM_ENCODE;
        request.NumFrameSuggested = static_cast<mfxU16>(request.NumFrameSuggested + encode_params.AsyncDepth);

        sts = frame_allocator.Alloc(frame_allocator.pthis, &request, &surface_response);
        if (!vpl_status_success(sts))
            return fail_vpl("oneVPL surface Alloc", sts);

        surfaces_allocated = true;
        surfaces.clear();
        surfaces.reserve(surface_response.NumFrameActual);
        for (mfxU16 i = 0; i < surface_response.NumFrameActual; ++i)
        {
            auto surface = std::make_unique<mfxFrameSurface1>();
            std::memset(surface.get(), 0, sizeof(mfxFrameSurface1));
            std::memcpy(&surface->Info, &encode_params.mfx.FrameInfo, sizeof(mfxFrameInfo));
            surface->Data.MemId = surface_response.mids[i];
            surfaces.push_back(std::move(surface));
        }

        return S_OK;
    }

    HRESULT initialize_tasks()
    {
        const mfxU16 task_count = encode_params.AsyncDepth != 0 ? encode_params.AsyncDepth : kIntelVplAsyncDepth;
        const mfxU32 buffer_size = std::max<mfxU32>(
            encode_params.mfx.BufferSizeInKB > 0 ? encode_params.mfx.BufferSizeInKB * 1000u : 0u,
            2u * 1024u * 1024u);

        tasks.clear();
        tasks.resize(task_count);
        first_sync_task = 0;

        for (Task& task : tasks)
        {
            auto* buffer = static_cast<uint8_t*>(av_malloc(buffer_size + AV_INPUT_BUFFER_PADDING_SIZE));
            if (buffer == nullptr)
                return fail_ffmpeg("av_malloc(oneVPL bitstream)", AVERROR(ENOMEM));

            std::memset(buffer + buffer_size, 0, AV_INPUT_BUFFER_PADDING_SIZE);
            task.buffer.reset(buffer);
            std::memset(&task.bitstream, 0, sizeof(task.bitstream));
            task.bitstream.Data = task.buffer.get();
            task.bitstream.MaxLength = buffer_size;
            task.bitstream.DataOffset = 0;
            task.bitstream.DataLength = 0;
            task.bitstream.TimeStamp = kTimestampUnknown;
            task.syncp = nullptr;
        }

        return S_OK;
    }

    HRESULT read_sequence_params(std::vector<uint8_t>& out)
    {
        out.clear();

        mfxU8 vps[1024]{};
        mfxU8 sps[1024]{};
        mfxU8 pps[1024]{};
        mfxExtCodingOptionSPSPPS spspps{};
        spspps.Header.BufferId = MFX_EXTBUFF_CODING_OPTION_SPSPPS;
        spspps.Header.BufferSz = sizeof(spspps);
        spspps.SPSBuffer = sps;
        spspps.SPSBufSize = sizeof(sps);
        spspps.PPSBuffer = pps;
        spspps.PPSBufSize = sizeof(pps);

        mfxExtCodingOptionVPS vps_ext{};
        std::vector<mfxExtBuffer*> ext_buffers;
        if (video.codec == PR_CODEC_HEVC)
        {
            vps_ext.Header.BufferId = MFX_EXTBUFF_CODING_OPTION_VPS;
            vps_ext.Header.BufferSz = sizeof(vps_ext);
            vps_ext.VPSBuffer = vps;
            vps_ext.VPSBufSize = sizeof(vps);
            ext_buffers.push_back(reinterpret_cast<mfxExtBuffer*>(&vps_ext));
        }
        ext_buffers.push_back(reinterpret_cast<mfxExtBuffer*>(&spspps));

        mfxVideoParam params{};
        params.ExtParam = ext_buffers.data();
        params.NumExtParam = static_cast<mfxU16>(ext_buffers.size());
        mfxStatus sts = MFXVideoENCODE_GetVideoParam(session, &params);
        if (!vpl_status_success(sts))
            return fail_vpl("oneVPL GetVideoParam(headers)", sts);

        if (video.codec == PR_CODEC_HEVC)
            append_annexb_nal(out, vps, vps_ext.VPSBufSize);
        append_annexb_nal(out, sps, spspps.SPSBufSize);
        append_annexb_nal(out, pps, spspps.PPSBufSize);
        return S_OK;
    }

    static void append_annexb_nal(std::vector<uint8_t>& out, const mfxU8* data, mfxU16 size)
    {
        if (data == nullptr || size == 0)
            return;

        static constexpr uint8_t start_code[] = {0, 0, 0, 1};
        out.insert(out.end(), std::begin(start_code), std::end(start_code));
        out.insert(out.end(), data, data + size);
    }

    int free_task_index() const
    {
        for (size_t i = 0; i < tasks.size(); ++i)
        {
            if (tasks[i].syncp == nullptr)
                return static_cast<int>(i);
        }
        return -1;
    }

    int free_surface_index() const
    {
        for (size_t i = 0; i < surfaces.size(); ++i)
        {
            if (surfaces[i] && surfaces[i]->Data.Locked == 0)
                return static_cast<int>(i);
        }
        return -1;
    }

    HRESULT process_queued_frame(const NativePendingVideoFrame& frame) override
    {
        ComPtr<ID3D11Texture2D> nv12_texture;
        if (!get_conversion_texture(frame.slot_index, nv12_texture))
        {
            release_conversion_slot(frame.slot_index);
            return E_INVALIDARG;
        }

        HRESULT hr = ensure_free_task_and_surface();
        if (FAILED(hr))
        {
            release_conversion_slot(frame.slot_index);
            return hr;
        }

        int task_index = free_task_index();
        int surface_index = free_surface_index();
        if (task_index < 0 || surface_index < 0)
        {
            release_conversion_slot(frame.slot_index);
            set_last_error("NativeRecorder oneVPL task/surface pool did not free a slot after sync.");
            return E_FAIL;
        }

        mfxFrameSurface1* surface = surfaces[static_cast<size_t>(surface_index)].get();
        ID3D11Texture2D* surface_texture = IntelVplD3D11Allocator::texture_from_mem_id(surface->Data.MemId);
        if (surface_texture == nullptr)
        {
            release_conversion_slot(frame.slot_index);
            return E_POINTER;
        }

        {
            std::lock_guard context_lock(d3d_context_mutex);
            D3D11_BOX src_box{};
            src_box.left = 0;
            src_box.top = 0;
            src_box.front = 0;
            src_box.right = static_cast<UINT>(converter.encoded_width());
            src_box.bottom = static_cast<UINT>(converter.encoded_height());
            src_box.back = 1;
            converter.device_context->CopySubresourceRegion(
                surface_texture,
                0,
                0,
                0,
                0,
                nv12_texture.Get(),
                0,
                &src_box);
        }
        release_conversion_slot(frame.slot_index);

        const int64_t frame_timestamp_hns = std::max<int64_t>(0, frame.timestamp_hns);
        surface->Data.TimeStamp = hns_to_vpl_timestamp(frame_timestamp_hns);

        Task& task = tasks[static_cast<size_t>(task_index)];
        task.bitstream.DataOffset = 0;
        task.bitstream.DataLength = 0;
        task.bitstream.TimeStamp = kTimestampUnknown;
        task.syncp = nullptr;
        std::memset(&task.ctrl, 0, sizeof(task.ctrl));
        if (frame.force_idr)
            task.ctrl.FrameType = MFX_FRAMETYPE_I | MFX_FRAMETYPE_IDR | MFX_FRAMETYPE_REF;

        mfxStatus sts = encode_frame_with_retry(&task, surface);
        if (sts == MFX_ERR_MORE_DATA)
        {
            pending_output_timestamps.push(frame_timestamp_hns);
            ++counters.encoder_input_frames;
            return S_OK;
        }
        if (!vpl_status_success(sts))
            return fail_vpl("oneVPL EncodeFrameAsync", sts);

        pending_output_timestamps.push(frame_timestamp_hns);
        update_max_pending_outputs();
        ++counters.encoder_input_frames;
        return S_OK;
    }

    HRESULT drain_after_queued_frame() override
    {
        return drain_ready_tasks();
    }

    HRESULT ensure_free_task_and_surface()
    {
        while (free_task_index() < 0 || free_surface_index() < 0)
        {
            HRESULT hr = sync_oldest_task(kIntelVplSyncWaitMs, false);
            if (FAILED(hr))
                return hr;
        }

        return S_OK;
    }

    mfxStatus encode_frame_with_retry(Task* task, mfxFrameSurface1* surface)
    {
        if (task == nullptr)
            return MFX_ERR_NULL_PTR;

        for (;;)
        {
            mfxStatus sts = MFXVideoENCODE_EncodeFrameAsync(
                session,
                &task->ctrl,
                surface,
                &task->bitstream,
                &task->syncp);

            if (sts > MFX_ERR_NONE && task->syncp == nullptr)
            {
                if (sts == MFX_WRN_DEVICE_BUSY)
                {
                    std::this_thread::sleep_for(std::chrono::milliseconds(kIntelVplDeviceBusySleepMs));
                    continue;
                }
            }
            else if (sts > MFX_ERR_NONE && task->syncp != nullptr)
            {
                return MFX_ERR_NONE;
            }

            return sts;
        }
    }

    HRESULT drain_ready_tasks()
    {
        while (!tasks.empty())
        {
            Task& task = tasks[first_sync_task];
            if (task.syncp == nullptr)
                return S_OK;

            bool synced = false;
            HRESULT hr = sync_task(first_sync_task, 0, true, synced);
            if (FAILED(hr) || !synced)
                return hr;
        }

        return S_OK;
    }

    HRESULT sync_oldest_task(mfxU32 wait_ms, bool tolerate_in_execution)
    {
        if (tasks.empty())
            return S_OK;

        size_t index = first_sync_task;
        if (tasks[index].syncp == nullptr)
        {
            auto found = std::find_if(tasks.begin(), tasks.end(), [](const Task& task)
            {
                return task.syncp != nullptr;
            });
            if (found == tasks.end())
                return S_OK;
            index = static_cast<size_t>(std::distance(tasks.begin(), found));
        }

        bool synced = false;
        return sync_task(index, wait_ms, tolerate_in_execution, synced);
    }

    HRESULT sync_task(size_t index, mfxU32 wait_ms, bool tolerate_in_execution, bool& synced)
    {
        synced = false;
        if (index >= tasks.size())
            return E_INVALIDARG;

        Task& task = tasks[index];
        if (task.syncp == nullptr)
            return S_OK;

        mfxStatus sts = MFXVideoCORE_SyncOperation(session, task.syncp, wait_ms);
        if (sts == MFX_WRN_IN_EXECUTION && tolerate_in_execution)
            return S_OK;
        if (!vpl_status_success(sts))
            return fail_vpl("oneVPL SyncOperation", sts);

        synced = true;
        HRESULT hr = write_task_packet(task);
        reset_task(task);
        if (index == first_sync_task)
            first_sync_task = (first_sync_task + 1) % tasks.size();
        return hr;
    }

    HRESULT write_task_packet(const Task& task)
    {
        if (task.bitstream.Data == nullptr || task.bitstream.DataLength == 0)
            return S_OK;

        const uint8_t* begin = task.bitstream.Data + task.bitstream.DataOffset;
        std::vector<uint8_t> packet(begin, begin + task.bitstream.DataLength);
        const bool key_frame =
            (task.bitstream.FrameType & MFX_FRAMETYPE_IDR) != 0 ||
            (task.bitstream.FrameType & MFX_FRAMETYPE_I) != 0;
        const int64_t timestamp = take_output_timestamp(-1);

        HRESULT hr = muxer.enqueue_video_packet(packet, key_frame, timestamp, video_sample_duration_hns);
        if (FAILED(hr))
            return hr;

        ++counters.written_packets;
        return S_OK;
    }

    static void reset_task(Task& task)
    {
        task.syncp = nullptr;
        task.bitstream.DataOffset = 0;
        task.bitstream.DataLength = 0;
        task.bitstream.TimeStamp = kTimestampUnknown;
        task.bitstream.FrameType = 0;
        task.bitstream.DataFlag = 0;
        std::memset(&task.ctrl, 0, sizeof(task.ctrl));
    }

    static mfxU64 hns_to_vpl_timestamp(int64_t timestamp_hns)
    {
        return static_cast<mfxU64>((std::max<int64_t>(0, timestamp_hns) * 9) / 1000);
    }

    void update_max_pending_outputs()
    {
        counters.max_pending_outputs = std::max<uint64_t>(
            counters.max_pending_outputs,
            static_cast<uint64_t>(pending_output_timestamps.size()));
    }

    HRESULT flush_encoder()
    {
        if (!encoder_initialized || session == nullptr)
            return S_OK;

        for (;;)
        {
            HRESULT hr = ensure_free_task_and_surface();
            if (FAILED(hr))
                return hr;

            int task_index = free_task_index();
            if (task_index < 0)
                return E_FAIL;

            Task& task = tasks[static_cast<size_t>(task_index)];
            task.bitstream.DataOffset = 0;
            task.bitstream.DataLength = 0;
            task.bitstream.TimeStamp = kTimestampUnknown;
            task.syncp = nullptr;
            std::memset(&task.ctrl, 0, sizeof(task.ctrl));

            mfxStatus sts = MFXVideoENCODE_EncodeFrameAsync(session, nullptr, nullptr, &task.bitstream, &task.syncp);
            if (sts == MFX_WRN_DEVICE_BUSY)
            {
                std::this_thread::sleep_for(std::chrono::milliseconds(kIntelVplDeviceBusySleepMs));
                continue;
            }
            if (sts == MFX_ERR_MORE_DATA)
                break;
            if (sts > MFX_ERR_NONE && task.syncp != nullptr)
                sts = MFX_ERR_NONE;
            if (!vpl_status_success(sts))
                return fail_vpl("oneVPL flush EncodeFrameAsync", sts);

            bool synced = false;
            hr = sync_task(static_cast<size_t>(task_index), kIntelVplSyncWaitMs, false, synced);
            if (FAILED(hr))
                return hr;
        }

        while (true)
        {
            bool any_pending = false;
            for (const Task& task : tasks)
            {
                if (task.syncp != nullptr)
                {
                    any_pending = true;
                    break;
                }
            }

            if (!any_pending)
                return S_OK;

            HRESULT hr = sync_oldest_task(kIntelVplSyncWaitMs, false);
            if (FAILED(hr))
                return hr;
        }
    }

    void close_vpl()
    {
        if (encoder_initialized && session != nullptr)
        {
            MFXVideoENCODE_Close(session);
            encoder_initialized = false;
        }

        if (surfaces_allocated)
        {
            frame_allocator.Free(frame_allocator.pthis, &surface_response);
            surfaces_allocated = false;
        }

        surfaces.clear();
        tasks.clear();

        if (session != nullptr)
        {
            MFXClose(session);
            session = nullptr;
        }

        if (loader != nullptr)
        {
            MFXUnload(loader);
            loader = nullptr;
        }
    }

    HRESULT stop() override
    {
        if (stopped)
            return S_OK;

        stopped = true;
        HRESULT result = stop_video_worker();

        if (SUCCEEDED(result))
            result = flush_encoder();

        close_vpl();

        HRESULT mux_hr = muxer.close();
        if (FAILED(mux_hr) && SUCCEEDED(result))
            result = mux_hr;

        clear_common_video_state();
        converter.reset();

        if (initialized && SUCCEEDED(result))
        {
            set_last_error("NativeRecorder finalized via oneVPL QSV + libavformat. " + finalize_stats());
        }

        return result;
    }
};
