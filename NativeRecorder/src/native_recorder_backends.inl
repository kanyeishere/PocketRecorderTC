struct NativeEncoderCounters
{
    uint64_t submitted_frames = 0;
    uint64_t encoder_input_frames = 0;
    uint64_t written_packets = 0;
    uint64_t encoder_input_full_drops = 0;
    uint64_t query_repeat_returns = 0;
    uint64_t audio_packets = 0;
};

struct NativePendingVideoFrame
{
    size_t slot_index = 0;
    int64_t timestamp_hns = 0;
    bool force_idr = false;
};

struct NativeD3D11TexturePool
{
    struct Slot
    {
        ComPtr<ID3D11Texture2D> texture;
        bool in_use = false;
    };

    std::vector<Slot> slots;
    size_t next_slot = 0;
    std::mutex mutex;

    template <typename CreateTexture>
    HRESULT ensure(size_t slot_count, CreateTexture&& create_texture)
    {
        std::lock_guard lock(mutex);
        if (!slots.empty())
            return S_OK;

        slots.reserve(slot_count);
        for (size_t i = 0; i < slot_count; ++i)
        {
            Slot slot{};
            HRESULT hr = create_texture(slot.texture);
            if (FAILED(hr))
            {
                slots.clear();
                next_slot = 0;
                return hr;
            }
            slots.push_back(std::move(slot));
        }

        next_slot = 0;
        return S_OK;
    }

    HRESULT acquire(size_t& slot_index, ID3D11Texture2D** texture, const std::string& full_message)
    {
        if (texture == nullptr)
            return E_POINTER;

        std::lock_guard lock(mutex);
        for (size_t offset = 0; offset < slots.size(); ++offset)
        {
            size_t index = (next_slot + offset) % slots.size();
            if (slots[index].in_use || !slots[index].texture)
                continue;

            slots[index].in_use = true;
            next_slot = (index + 1) % slots.size();
            slot_index = index;
            *texture = slots[index].texture.Get();
            return S_OK;
        }

        set_last_error(full_message);
        return DXGI_ERROR_WAS_STILL_DRAWING;
    }

    bool get_texture(size_t slot_index, ComPtr<ID3D11Texture2D>& texture)
    {
        std::lock_guard lock(mutex);
        if (slot_index >= slots.size() || !slots[slot_index].texture)
            return false;

        texture = slots[slot_index].texture;
        return true;
    }

    void release(size_t slot_index)
    {
        std::lock_guard lock(mutex);
        if (slot_index < slots.size())
            slots[slot_index].in_use = false;
    }

    void clear()
    {
        std::lock_guard lock(mutex);
        slots.clear();
        next_slot = 0;
    }
};

struct NativeVideoFrameQueue
{
    std::mutex mutex;
    std::condition_variable cv;
    std::deque<NativePendingVideoFrame> queue;
    bool accepting = false;
    bool stopping = false;

    void start()
    {
        std::lock_guard lock(mutex);
        queue.clear();
        accepting = true;
        stopping = false;
    }

    HRESULT enqueue(NativePendingVideoFrame frame, const std::string& full_message)
    {
        std::unique_lock lock(mutex);
        if (!accepting)
            return E_ABORT;
        if (queue.size() >= kMaxNativeVideoQueueItems)
        {
            set_last_error(full_message);
            return DXGI_ERROR_WAS_STILL_DRAWING;
        }

        queue.push_back(frame);
        lock.unlock();
        cv.notify_one();
        return S_OK;
    }

    bool wait_pop(NativePendingVideoFrame& frame)
    {
        for (;;)
        {
            std::unique_lock lock(mutex);
            cv.wait(lock, [this] { return stopping || !queue.empty(); });
            if (!queue.empty())
            {
                frame = queue.front();
                queue.pop_front();
                return true;
            }
            if (stopping)
                return false;
        }
    }

    void request_stop()
    {
        {
            std::lock_guard lock(mutex);
            accepting = false;
            stopping = true;
        }
        cv.notify_one();
    }

    std::vector<size_t> fail_and_take_slots()
    {
        std::vector<size_t> slots;
        {
            std::lock_guard lock(mutex);
            accepting = false;
            stopping = true;
            while (!queue.empty())
            {
                slots.push_back(queue.front().slot_index);
                queue.pop_front();
            }
        }
        cv.notify_one();
        return slots;
    }

    void clear()
    {
        std::lock_guard lock(mutex);
        queue.clear();
        accepting = false;
        stopping = false;
    }
};

struct NativeOutputTimestampQueue
{
    struct Entry
    {
        int64_t timestamp_hns = 0;
        size_t resource_slot = 0;
        bool has_resource_slot = false;
    };

    std::deque<Entry> entries;

    void push(int64_t timestamp_hns)
    {
        entries.push_back(Entry{std::max<int64_t>(0, timestamp_hns), 0, false});
    }

    void push_with_resource(int64_t timestamp_hns, size_t resource_slot)
    {
        entries.push_back(Entry{std::max<int64_t>(0, timestamp_hns), resource_slot, true});
    }

    template <typename ReleaseResource>
    int64_t take(int64_t encoder_timestamp_hns, uint64_t written_packets, int64_t duration_hns, ReleaseResource release_resource)
    {
        if (!entries.empty())
        {
            Entry entry = entries.front();
            entries.pop_front();
            if (entry.has_resource_slot)
                release_resource(entry.resource_slot);
            return entry.timestamp_hns;
        }

        if (encoder_timestamp_hns >= 0)
            return encoder_timestamp_hns;

        return static_cast<int64_t>(written_packets) * duration_hns;
    }

    void clear()
    {
        entries.clear();
    }

    template <typename ReleaseResource>
    void clear(ReleaseResource release_resource)
    {
        while (!entries.empty())
        {
            Entry entry = entries.front();
            entries.pop_front();
            if (entry.has_resource_slot)
                release_resource(entry.resource_slot);
        }
    }

    size_t size() const
    {
        return entries.size();
    }
};

struct NativeD3D11LibavRecorderBackend : NativeRecorderBackend
{
    pr_video_config video{};
    pr_audio_config audio{};
    std::wstring output_path;
    SharedTextureNv12Converter converter;
    AsyncLibavMp4Muxer muxer;
    bool initialized = false;
    bool stopped = false;
    NativeEncoderCounters counters{};
    int64_t video_sample_duration_hns = 0;
    NativeD3D11TexturePool conversion_nv12_pool;
    NativeVideoFrameQueue video_queue;
    NativeOutputTimestampQueue pending_output_timestamps;
    std::mutex d3d_context_mutex;
    std::thread video_worker;
    std::atomic<HRESULT> video_worker_result{S_OK};
    std::string backend_tag;

    NativeD3D11LibavRecorderBackend(
        const pr_video_config& video_config,
        const pr_audio_config& audio_config,
        std::wstring output,
        std::string tag)
        : video(video_config), audio(audio_config), output_path(std::move(output)), backend_tag(std::move(tag))
    {
        converter.video = video;
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

        hr = video_worker_result.load();
        if (FAILED(hr))
            return hr;

        size_t slot_index = 0;
        ID3D11Texture2D* nv12_texture = nullptr;
        hr = conversion_nv12_pool.acquire(slot_index, &nv12_texture, conversion_pool_full_message());
        if (FAILED(hr))
            return hr;

        {
            std::lock_guard context_lock(d3d_context_mutex);
            hr = converter.convert_shared_texture_to(shared_handle, source_format, nv12_texture);
        }
        if (FAILED(hr))
        {
            conversion_nv12_pool.release(slot_index);
            return hr;
        }

        const bool force_idr = counters.submitted_frames == 0;
        hr = video_queue.enqueue(
            NativePendingVideoFrame{slot_index, std::max<int64_t>(0, timestamp_hns), force_idr},
            input_queue_full_message());
        if (FAILED(hr))
        {
            conversion_nv12_pool.release(slot_index);
            return hr;
        }

        ++counters.submitted_frames;
        return S_OK;
    }

    HRESULT submit_audio(const void* data, int32_t byte_count, int64_t timestamp_hns) override
    {
        if (!audio.enabled)
            return S_OK;
        if (!initialized)
            return S_OK;

        HRESULT hr = muxer.enqueue_audio(data, byte_count, timestamp_hns);
        if (FAILED(hr))
            return hr;

        ++counters.audio_packets;
        return S_OK;
    }

    HRESULT ensure_conversion_pool()
    {
        return conversion_nv12_pool.ensure(
            kNativeNv12ConversionPoolSize,
            [this](ComPtr<ID3D11Texture2D>& texture)
            {
                return converter.create_nv12_texture(texture);
            });
    }

    bool get_conversion_texture(size_t slot_index, ComPtr<ID3D11Texture2D>& texture)
    {
        return conversion_nv12_pool.get_texture(slot_index, texture);
    }

    void release_conversion_slot(size_t slot_index)
    {
        conversion_nv12_pool.release(slot_index);
    }

    void start_video_worker()
    {
        video_worker_result.store(S_OK);
        video_queue.start();
        video_worker = std::thread([this] { video_worker_loop(); });
    }

    HRESULT stop_video_worker()
    {
        video_queue.request_stop();

        if (video_worker.joinable())
            video_worker.join();

        return video_worker_result.load();
    }

    void video_worker_loop()
    {
        for (;;)
        {
            NativePendingVideoFrame frame{};
            if (!video_queue.wait_pop(frame))
                return;

            HRESULT hr = process_queued_frame(frame);
            if (SUCCEEDED(hr))
                hr = drain_after_queued_frame();

            if (FAILED(hr))
            {
                video_worker_result.store(hr);
                std::vector<size_t> abandoned_slots = video_queue.fail_and_take_slots();
                for (size_t slot : abandoned_slots)
                    release_conversion_slot(slot);
                return;
            }
        }
    }

    int64_t take_output_timestamp(int64_t encoder_timestamp_hns)
    {
        return pending_output_timestamps.take(
            encoder_timestamp_hns,
            counters.written_packets,
            video_sample_duration_hns,
            [](size_t) {});
    }

    void clear_common_video_state()
    {
        pending_output_timestamps.clear();
        video_queue.clear();
        conversion_nv12_pool.clear();
    }

    std::string finalize_stats() const
    {
        return "submitted=" + std::to_string(counters.submitted_frames) +
            ", encoderInput=" + std::to_string(counters.encoder_input_frames) +
            ", packets=" + std::to_string(counters.written_packets) +
            ", inputFullDrops=" + std::to_string(counters.encoder_input_full_drops) +
            ", queryRepeats=" + std::to_string(counters.query_repeat_returns) +
            ", audioPackets=" + std::to_string(counters.audio_packets);
    }

    std::string conversion_pool_full_message() const
    {
        return "NativeRecorder " + backend_tag + " NV12 conversion pool is full; dropping one frame.";
    }

    std::string input_queue_full_message() const
    {
        return "NativeRecorder " + backend_tag + " input queue is full; dropping one frame.";
    }

    virtual HRESULT process_queued_frame(const NativePendingVideoFrame& frame) = 0;

    virtual HRESULT drain_after_queued_frame()
    {
        return S_OK;
    }
};

struct NvencLibavRecorderBackend final : NativeD3D11LibavRecorderBackend
{
    std::unique_ptr<NvEncoderD3D11> encoder;

    NvencLibavRecorderBackend(const pr_video_config& video_config, const pr_audio_config& audio_config, std::wstring output)
        : NativeD3D11LibavRecorderBackend(video_config, audio_config, std::move(output), "NVENC")
    {
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
                3);

            NV_ENC_INITIALIZE_PARAMS initialize_params = { NV_ENC_INITIALIZE_PARAMS_VER };
            NV_ENC_CONFIG encode_config = { NV_ENC_CONFIG_VER };
            initialize_params.encodeConfig = &encode_config;
            encoder->CreateDefaultEncoderParams(
                &initialize_params,
                nvenc_codec_guid(video.codec),
                nvenc_preset_guid(),
                NV_ENC_TUNING_INFO_ULTRA_LOW_LATENCY);

            initialize_params.frameRateNum = static_cast<uint32_t>(std::max(1, video.fps));
            initialize_params.frameRateDen = 1;
            initialize_params.encodeWidth = static_cast<uint32_t>(encoded_width);
            initialize_params.encodeHeight = static_cast<uint32_t>(encoded_height);
            initialize_params.darWidth = static_cast<uint32_t>(encoded_width);
            initialize_params.darHeight = static_cast<uint32_t>(encoded_height);
            initialize_params.enablePTD = 1;
#if defined(_WIN32)
            initialize_params.enableEncodeAsync =
                encoder->GetCapabilityValue(nvenc_codec_guid(video.codec), NV_ENC_CAPS_ASYNC_ENCODE_SUPPORT) ? 1 : 0;
#endif

            auto* cfg = initialize_params.encodeConfig;
            cfg->gopLength = static_cast<uint32_t>(std::max(1, video.fps) * 2);
            cfg->frameIntervalP = 1;
            cfg->rcParams.rateControlMode = NV_ENC_PARAMS_RC_CBR;
            cfg->rcParams.averageBitRate = static_cast<uint32_t>(video.bitrate_bps > 0 ? video.bitrate_bps : 12'000'000);
            cfg->rcParams.maxBitRate = cfg->rcParams.averageBitRate;
            cfg->rcParams.vbvBufferSize = std::max<uint32_t>(
                1,
                (cfg->rcParams.averageBitRate / std::max(1, video.fps)) * 3);
            cfg->rcParams.vbvInitialDelay = cfg->rcParams.vbvBufferSize;
            cfg->rcParams.zeroReorderDelay = 1;
            cfg->rcParams.lowDelayKeyFrameScale = 1;
            cfg->rcParams.enableLookahead = 0;
            cfg->rcParams.lookaheadDepth = 0;

            if (video.codec == PR_CODEC_H264)
            {
                cfg->encodeCodecConfig.h264Config.idrPeriod = cfg->gopLength;
                cfg->encodeCodecConfig.h264Config.repeatSPSPPS = 1;
                cfg->encodeCodecConfig.h264Config.outputAUD = 0;
                cfg->encodeCodecConfig.h264Config.maxNumRefFrames = 1;
                cfg->encodeCodecConfig.h264Config.numRefL0 = NV_ENC_NUM_REF_FRAMES_1;
            }
            else
            {
                cfg->encodeCodecConfig.hevcConfig.idrPeriod = cfg->gopLength;
                cfg->encodeCodecConfig.hevcConfig.repeatSPSPPS = 1;
                cfg->encodeCodecConfig.hevcConfig.outputAUD = 0;
                cfg->encodeCodecConfig.hevcConfig.maxNumRefFramesInDPB = 1;
                cfg->encodeCodecConfig.hevcConfig.numRefL0 = NV_ENC_NUM_REF_FRAMES_1;
            }

            encoder->CreateEncoder(&initialize_params);

            std::vector<uint8_t> sequence_params;
            encoder->GetSequenceParams(sequence_params);
            hr = muxer.open(output_path, video, audio, sequence_params);
            if (FAILED(hr))
                return hr;

            hr = ensure_conversion_pool();
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
        start_video_worker();
        std::string message = "NativeRecorder initialized: source NVIDIA adapter=" + converter.adapter_name +
            ", luid=" + std::to_string(static_cast<uint32_t>(converter.adapter_luid.HighPart)) + ":" +
            std::to_string(converter.adapter_luid.LowPart) +
            ", sourceFormat=" + dxgi_format_to_string(source_format) +
            ", encoded=" + std::to_string(encoded_width) + "x" + std::to_string(encoded_height) +
            ", pad=" + std::to_string(encoded_width - video.width) + "x" + std::to_string(encoded_height - video.height) +
            ", nvencBuffers=" + std::to_string(encoder ? encoder->GetEncoderBufferCount() : 0) +
            ", vpSourceSupport=" + hex_uint32(converter.source_format_support) +
            ", vpNv12Support=" + hex_uint32(converter.nv12_format_support) +
            ", output=" + std::string(codec_name(video.codec)) + "/MP4 via NvEncoderD3D11 + libavformat.";
        set_last_error(message);
        return S_OK;
    }

    HRESULT process_queued_frame(const NativePendingVideoFrame& frame) override
    {
        try
        {
            const NvEncInputFrame* input_frame = encoder->GetNextInputFrame();
            auto* input_texture = reinterpret_cast<ID3D11Texture2D*>(input_frame->inputPtr);
            if (input_texture == nullptr)
            {
                release_conversion_slot(frame.slot_index);
                return E_POINTER;
            }

            ComPtr<ID3D11Texture2D> nv12_texture;
            if (!get_conversion_texture(frame.slot_index, nv12_texture))
            {
                release_conversion_slot(frame.slot_index);
                return E_INVALIDARG;
            }

            {
                std::lock_guard context_lock(d3d_context_mutex);
                converter.device_context->CopyResource(
                    reinterpret_cast<ID3D11Resource*>(input_texture),
                    reinterpret_cast<ID3D11Resource*>(nv12_texture.Get()));
            }
            release_conversion_slot(frame.slot_index);

            NV_ENC_PIC_PARAMS picture_params = { NV_ENC_PIC_PARAMS_VER };
            picture_params.inputTimeStamp = static_cast<uint64_t>(std::max<int64_t>(0, frame.timestamp_hns));
            if (frame.force_idr)
                picture_params.encodePicFlags = NV_ENC_PIC_FLAG_FORCEIDR | NV_ENC_PIC_FLAG_OUTPUT_SPSPPS;

            std::vector<NvEncOutputFrame> packets;
            pending_output_timestamps.push(frame.timestamp_hns);
            ++counters.encoder_input_frames;
            encoder->EncodeFrame(packets, &picture_params);
            for (const NvEncOutputFrame& packet : packets)
            {
                if (packet.frame.empty())
                    continue;

                int64_t packet_timestamp = take_output_timestamp(static_cast<int64_t>(packet.timeStamp));
                HRESULT hr = muxer.enqueue_video_packet(
                    packet.frame,
                    nvenc_output_is_key_frame(packet.pictureType),
                    packet_timestamp,
                    video_sample_duration_hns);
                if (FAILED(hr))
                    return hr;
                ++counters.written_packets;
            }
        }
        catch (const std::exception& ex)
        {
            return fail_exception("NvEncoderD3D11 worker encode", ex,
                "submitted=" + std::to_string(counters.submitted_frames) +
                ", written=" + std::to_string(counters.written_packets));
        }

        return S_OK;
    }

    HRESULT stop() override
    {
        if (stopped)
            return S_OK;

        stopped = true;
        HRESULT result = stop_video_worker();
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

                    int64_t packet_timestamp = take_output_timestamp(static_cast<int64_t>(packet.timeStamp));
                    HRESULT hr = muxer.enqueue_video_packet(
                        packet.frame,
                        nvenc_output_is_key_frame(packet.pictureType),
                        packet_timestamp,
                        video_sample_duration_hns);
                    if (FAILED(hr) && SUCCEEDED(result))
                        result = hr;
                    ++counters.written_packets;
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
        clear_common_video_state();
        converter.reset();

        if (initialized && SUCCEEDED(result))
        {
            set_last_error("NativeRecorder finalized via NvEncoderD3D11 + libavformat. " + finalize_stats());
        }

        return result;
    }
};

struct AmfLibavRecorderBackend final : NativeD3D11LibavRecorderBackend
{
    amf::AMFContextPtr context;
    amf::AMFComponentPtr encoder;
    bool factory_initialized = false;
    struct AmfEncoderInputSlot
    {
        amf::AMFSurfacePtr surface;
        ID3D11Texture2D* texture = nullptr;
        bool in_use = false;
    };
    std::vector<AmfEncoderInputSlot> encoder_input_pool;
    std::vector<std::string> amf_optional_property_warnings;
    size_t next_encoder_input_slot = 0;
    std::mutex encoder_input_mutex;

    AmfLibavRecorderBackend(const pr_video_config& video_config, const pr_audio_config& audio_config, std::wstring output)
        : NativeD3D11LibavRecorderBackend(video_config, audio_config, std::move(output), "AMF")
    {
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

        hr = ensure_conversion_pool();
        if (FAILED(hr))
            return hr;

        hr = ensure_encoder_input_pool(encoded_width, encoded_height);
        if (FAILED(hr))
            return hr;

        initialized = true;
        start_video_worker();

        std::string message = "NativeRecorder initialized: source AMD adapter=" + converter.adapter_name +
            ", luid=" + std::to_string(static_cast<uint32_t>(converter.adapter_luid.HighPart)) + ":" +
            std::to_string(converter.adapter_luid.LowPart) +
            ", sourceFormat=" + dxgi_format_to_string(source_format) +
            ", encoded=" + std::to_string(encoded_width) + "x" + std::to_string(encoded_height) +
            ", pad=" + std::to_string(encoded_width - video.width) + "x" + std::to_string(encoded_height - video.height) +
            ", vpSourceSupport=" + hex_uint32(converter.source_format_support) +
            ", vpNv12Support=" + hex_uint32(converter.nv12_format_support) +
            ", amfInput=shared conversion pool -> AMF-owned DX11 NV12 surfaces";
        std::string optional_summary = amf_optional_property_summary();
        if (!optional_summary.empty())
            message += ", amfOptionalSkipped=" + optional_summary;
        message += ", output=" + std::string(codec_name(video.codec)) + "/MP4 via AMF + libavformat.";
        set_last_error(message);
        return S_OK;
    }

    HRESULT set_amf_required(const wchar_t* property, const amf::AMFVariant& value, const char* label)
    {
        AMF_RESULT result = encoder->SetProperty(property, value);
        if (amf_result_success(result))
            return S_OK;

        std::string operation = "AMF SetProperty(";
        operation += label != nullptr ? label : "required";
        operation += ")";
        return fail_amf(operation.c_str(), result);
    }

    void set_amf_optional(const wchar_t* property, const amf::AMFVariant& value, const char* label)
    {
        AMF_RESULT result = encoder->SetProperty(property, value);
        if (amf_result_success(result))
            return;

        std::string item = label != nullptr ? label : "optional";
        item += "=";
        item += amf_result_to_string(result);
        amf_optional_property_warnings.push_back(std::move(item));
    }

    std::string amf_optional_property_summary() const
    {
        if (amf_optional_property_warnings.empty())
            return {};

        std::string summary;
        const size_t count = std::min<size_t>(amf_optional_property_warnings.size(), 6);
        for (size_t i = 0; i < count; ++i)
        {
            if (!summary.empty())
                summary += "|";
            summary += amf_optional_property_warnings[i];
        }
        if (amf_optional_property_warnings.size() > count)
            summary += "|+" + std::to_string(amf_optional_property_warnings.size() - count) + " more";
        return summary;
    }

    HRESULT configure_encoder(int encoded_width, int encoded_height)
    {
        amf_optional_property_warnings.clear();
        const int fps = std::max(1, video.fps);
        const int64_t bitrate = video.bitrate_bps > 0 ? video.bitrate_bps : 12'000'000;
        const int64_t vbv_buffer_bits = std::max<int64_t>(1, (bitrate / fps) * 3);

        if (video.codec == PR_CODEC_H264)
        {
            set_amf_optional(AMF_VIDEO_ENCODER_USAGE, amf::AMFVariant(static_cast<amf_int64>(AMF_VIDEO_ENCODER_USAGE_ULTRA_LOW_LATENCY)), "H264 Usage");
            set_amf_optional(AMF_VIDEO_ENCODER_PROFILE, amf::AMFVariant(static_cast<amf_int64>(AMF_VIDEO_ENCODER_PROFILE_HIGH)), "H264 Profile");
            HRESULT hr = set_amf_required(AMF_VIDEO_ENCODER_FRAMESIZE, amf::AMFVariant(AMFConstructSize(encoded_width, encoded_height)), "H264 FrameSize");
            if (FAILED(hr)) return hr;
            hr = set_amf_required(AMF_VIDEO_ENCODER_FRAMERATE, amf::AMFVariant(AMFConstructRate(fps, 1)), "H264 FrameRate");
            if (FAILED(hr)) return hr;
            hr = set_amf_required(AMF_VIDEO_ENCODER_TARGET_BITRATE, amf::AMFVariant(static_cast<amf_int64>(bitrate)), "H264 TargetBitrate");
            if (FAILED(hr)) return hr;
            hr = set_amf_required(AMF_VIDEO_ENCODER_PEAK_BITRATE, amf::AMFVariant(static_cast<amf_int64>(bitrate)), "H264 PeakBitrate");
            if (FAILED(hr)) return hr;
            hr = set_amf_required(AMF_VIDEO_ENCODER_RATE_CONTROL_METHOD, amf::AMFVariant(static_cast<amf_int64>(AMF_VIDEO_ENCODER_RATE_CONTROL_METHOD_CBR)), "H264 RateControl");
            if (FAILED(hr)) return hr;
            hr = set_amf_required(AMF_VIDEO_ENCODER_QUALITY_PRESET, amf::AMFVariant(static_cast<amf_int64>(AMF_VIDEO_ENCODER_QUALITY_PRESET_SPEED)), "H264 QualityPreset");
            if (FAILED(hr)) return hr;

            set_amf_optional(AMF_VIDEO_ENCODER_MEMORY_TYPE, amf::AMFVariant(static_cast<amf_int64>(amf::AMF_MEMORY_DX11)), "H264 MemoryType");
            set_amf_optional(AMF_VIDEO_ENCODER_OUTPUT_MODE, amf::AMFVariant(static_cast<amf_int64>(AMF_VIDEO_ENCODER_OUTPUT_MODE_FRAME)), "H264 OutputMode");
            set_amf_optional(AMF_VIDEO_ENCODER_MAX_CONSECUTIVE_BPICTURES, amf::AMFVariant(static_cast<amf_int64>(0)), "H264 BFrames");
            set_amf_optional(AMF_VIDEO_ENCODER_B_PIC_PATTERN, amf::AMFVariant(static_cast<amf_int64>(0)), "H264 BPicturePattern");
            set_amf_optional(AMF_VIDEO_ENCODER_B_REFERENCE_ENABLE, amf::AMFVariant(false), "H264 BReference");
            set_amf_optional(AMF_VIDEO_ENCODER_MAX_NUM_REFRAMES, amf::AMFVariant(static_cast<amf_int64>(1)), "H264 MaxRefFrames");
            set_amf_optional(AMF_VIDEO_ENCODER_MAX_NUM_TEMPORAL_LAYERS, amf::AMFVariant(static_cast<amf_int64>(1)), "H264 MaxTemporalLayers");
            set_amf_optional(AMF_VIDEO_ENCODER_PRE_ANALYSIS_ENABLE, amf::AMFVariant(false), "H264 PreAnalysis");
            set_amf_optional(AMF_VIDEO_ENCODER_PREENCODE_ENABLE, amf::AMFVariant(static_cast<amf_int64>(AMF_VIDEO_ENCODER_PREENCODE_DISABLED)), "H264 PreEncode");
            set_amf_optional(AMF_VIDEO_ENCODER_RATE_CONTROL_SKIP_FRAME_ENABLE, amf::AMFVariant(false), "H264 SkipFrame");
            set_amf_optional(AMF_VIDEO_ENCODER_VBV_BUFFER_SIZE, amf::AMFVariant(static_cast<amf_int64>(vbv_buffer_bits)), "H264 VBV");
            set_amf_optional(AMF_VIDEO_ENCODER_INITIAL_VBV_BUFFER_FULLNESS, amf::AMFVariant(static_cast<amf_int64>(64)), "H264 InitialVBV");
            set_amf_optional(AMF_VIDEO_ENCODER_ENFORCE_HRD, amf::AMFVariant(true), "H264 EnforceHRD");
            set_amf_optional(AMF_VIDEO_ENCODER_ENABLE_VBAQ, amf::AMFVariant(false), "H264 VBAQ");
            set_amf_optional(AMF_VIDEO_ENCODER_QUERY_TIMEOUT, amf::AMFVariant(static_cast<amf_int64>(0)), "H264 QueryTimeout");
            set_amf_optional(AMF_VIDEO_ENCODER_IDR_PERIOD, amf::AMFVariant(static_cast<amf_int64>(fps * 2)), "H264 IDRPeriod");
            set_amf_optional(AMF_VIDEO_ENCODER_HEADER_INSERTION_SPACING, amf::AMFVariant(static_cast<amf_int64>(fps * 2)), "H264 HeaderInsertion");
        }
        else
        {
            set_amf_optional(AMF_VIDEO_ENCODER_HEVC_USAGE, amf::AMFVariant(static_cast<amf_int64>(AMF_VIDEO_ENCODER_HEVC_USAGE_ULTRA_LOW_LATENCY)), "HEVC Usage");
            set_amf_optional(AMF_VIDEO_ENCODER_HEVC_PROFILE, amf::AMFVariant(static_cast<amf_int64>(AMF_VIDEO_ENCODER_HEVC_PROFILE_MAIN)), "HEVC Profile");
            HRESULT hr = set_amf_required(AMF_VIDEO_ENCODER_HEVC_FRAMESIZE, amf::AMFVariant(AMFConstructSize(encoded_width, encoded_height)), "HEVC FrameSize");
            if (FAILED(hr)) return hr;
            hr = set_amf_required(AMF_VIDEO_ENCODER_HEVC_FRAMERATE, amf::AMFVariant(AMFConstructRate(fps, 1)), "HEVC FrameRate");
            if (FAILED(hr)) return hr;
            hr = set_amf_required(AMF_VIDEO_ENCODER_HEVC_TARGET_BITRATE, amf::AMFVariant(static_cast<amf_int64>(bitrate)), "HEVC TargetBitrate");
            if (FAILED(hr)) return hr;
            hr = set_amf_required(AMF_VIDEO_ENCODER_HEVC_PEAK_BITRATE, amf::AMFVariant(static_cast<amf_int64>(bitrate)), "HEVC PeakBitrate");
            if (FAILED(hr)) return hr;
            hr = set_amf_required(AMF_VIDEO_ENCODER_HEVC_RATE_CONTROL_METHOD, amf::AMFVariant(static_cast<amf_int64>(AMF_VIDEO_ENCODER_HEVC_RATE_CONTROL_METHOD_CBR)), "HEVC RateControl");
            if (FAILED(hr)) return hr;
            hr = set_amf_required(AMF_VIDEO_ENCODER_HEVC_QUALITY_PRESET, amf::AMFVariant(static_cast<amf_int64>(AMF_VIDEO_ENCODER_HEVC_QUALITY_PRESET_SPEED)), "HEVC QualityPreset");
            if (FAILED(hr)) return hr;

            set_amf_optional(AMF_VIDEO_ENCODER_HEVC_MEMORY_TYPE, amf::AMFVariant(static_cast<amf_int64>(amf::AMF_MEMORY_DX11)), "HEVC MemoryType");
            set_amf_optional(AMF_VIDEO_ENCODER_HEVC_OUTPUT_MODE, amf::AMFVariant(static_cast<amf_int64>(AMF_VIDEO_ENCODER_HEVC_OUTPUT_MODE_FRAME)), "HEVC OutputMode");
            set_amf_optional(AMF_VIDEO_ENCODER_HEVC_MAX_NUM_REFRAMES, amf::AMFVariant(static_cast<amf_int64>(1)), "HEVC MaxRefFrames");
            set_amf_optional(AMF_VIDEO_ENCODER_HEVC_MAX_NUM_TEMPORAL_LAYERS, amf::AMFVariant(static_cast<amf_int64>(1)), "HEVC MaxTemporalLayers");
            set_amf_optional(AMF_VIDEO_ENCODER_HEVC_NUM_TEMPORAL_LAYERS, amf::AMFVariant(static_cast<amf_int64>(1)), "HEVC TemporalLayers");
            set_amf_optional(AMF_VIDEO_ENCODER_HEVC_PRE_ANALYSIS_ENABLE, amf::AMFVariant(false), "HEVC PreAnalysis");
            set_amf_optional(AMF_VIDEO_ENCODER_HEVC_PREENCODE_ENABLE, amf::AMFVariant(false), "HEVC PreEncode");
            set_amf_optional(AMF_VIDEO_ENCODER_HEVC_RATE_CONTROL_SKIP_FRAME_ENABLE, amf::AMFVariant(false), "HEVC SkipFrame");
            set_amf_optional(AMF_VIDEO_ENCODER_HEVC_VBV_BUFFER_SIZE, amf::AMFVariant(static_cast<amf_int64>(vbv_buffer_bits)), "HEVC VBV");
            set_amf_optional(AMF_VIDEO_ENCODER_HEVC_INITIAL_VBV_BUFFER_FULLNESS, amf::AMFVariant(static_cast<amf_int64>(64)), "HEVC InitialVBV");
            set_amf_optional(AMF_VIDEO_ENCODER_HEVC_ENFORCE_HRD, amf::AMFVariant(true), "HEVC EnforceHRD");
            set_amf_optional(AMF_VIDEO_ENCODER_HEVC_ENABLE_VBAQ, amf::AMFVariant(false), "HEVC VBAQ");
            set_amf_optional(AMF_VIDEO_ENCODER_HEVC_HIGH_MOTION_QUALITY_BOOST_ENABLE, amf::AMFVariant(false), "HEVC HighMotionQualityBoost");
            set_amf_optional(AMF_VIDEO_ENCODER_HEVC_SLICES_PER_FRAME, amf::AMFVariant(static_cast<amf_int64>(1)), "HEVC SlicesPerFrame");
            set_amf_optional(AMF_VIDEO_ENCODER_HEVC_QUERY_TIMEOUT, amf::AMFVariant(static_cast<amf_int64>(0)), "HEVC QueryTimeout");
            set_amf_optional(AMF_VIDEO_ENCODER_HEVC_GOP_SIZE, amf::AMFVariant(static_cast<amf_int64>(fps * 2)), "HEVC GOP");
            set_amf_optional(AMF_VIDEO_ENCODER_HEVC_NUM_GOPS_PER_IDR, amf::AMFVariant(static_cast<amf_int64>(1)), "HEVC IDR");
            set_amf_optional(AMF_VIDEO_ENCODER_HEVC_HEADER_INSERTION_MODE, amf::AMFVariant(static_cast<amf_int64>(AMF_VIDEO_ENCODER_HEVC_HEADER_INSERTION_MODE_IDR_ALIGNED)), "HEVC HeaderInsertion");
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

    HRESULT ensure_encoder_input_pool(int encoded_width, int encoded_height)
    {
        std::lock_guard lock(encoder_input_mutex);
        if (!encoder_input_pool.empty())
            return S_OK;

        encoder_input_pool.reserve(kAmfEncoderInputPoolSize);
        for (size_t i = 0; i < kAmfEncoderInputPoolSize; ++i)
        {
            AmfEncoderInputSlot slot{};
            AMF_RESULT result = context->AllocSurface(
                amf::AMF_MEMORY_DX11,
                amf::AMF_SURFACE_NV12,
                encoded_width,
                encoded_height,
                &slot.surface);
            if (!amf_result_success(result))
            {
                encoder_input_pool.clear();
                next_encoder_input_slot = 0;
                return fail_amf("AMF AllocSurface(input)", result,
                    "encoded=" + std::to_string(encoded_width) + "x" + std::to_string(encoded_height));
            }

            if (slot.surface == nullptr || slot.surface->GetPlanesCount() == 0)
            {
                encoder_input_pool.clear();
                next_encoder_input_slot = 0;
                set_last_error("NativeRecorder AMF AllocSurface returned an invalid NV12 surface.");
                return E_FAIL;
            }

            amf::AMFPlane* plane = slot.surface->GetPlaneAt(0);
            slot.texture = plane != nullptr ? static_cast<ID3D11Texture2D*>(plane->GetNative()) : nullptr;
            if (slot.texture == nullptr)
            {
                encoder_input_pool.clear();
                next_encoder_input_slot = 0;
                set_last_error("NativeRecorder AMF input surface did not expose a DX11 texture.");
                return E_FAIL;
            }

            encoder_input_pool.push_back(std::move(slot));
        }

        next_encoder_input_slot = 0;
        return S_OK;
    }

    HRESULT acquire_encoder_input_slot(size_t& slot_index, amf::AMFSurfacePtr& surface, ID3D11Texture2D** texture)
    {
        if (texture == nullptr)
            return E_POINTER;

        std::lock_guard lock(encoder_input_mutex);
        for (size_t offset = 0; offset < encoder_input_pool.size(); ++offset)
        {
            size_t index = (next_encoder_input_slot + offset) % encoder_input_pool.size();
            if (encoder_input_pool[index].in_use || encoder_input_pool[index].surface == nullptr || encoder_input_pool[index].texture == nullptr)
                continue;

            encoder_input_pool[index].in_use = true;
            next_encoder_input_slot = (index + 1) % encoder_input_pool.size();
            slot_index = index;
            surface = encoder_input_pool[index].surface;
            *texture = encoder_input_pool[index].texture;
            return S_OK;
        }

        set_last_error("NativeRecorder AMF encoder input surface pool is full; dropping one frame.");
        return DXGI_ERROR_WAS_STILL_DRAWING;
    }

    void release_encoder_input_slot(size_t slot_index)
    {
        std::lock_guard lock(encoder_input_mutex);
        if (slot_index < encoder_input_pool.size())
            encoder_input_pool[slot_index].in_use = false;
    }

    void release_pending_encoder_inputs()
    {
        pending_output_timestamps.clear([this](size_t slot_index)
        {
            release_encoder_input_slot(slot_index);
        });
    }

    void clear_encoder_input_pool()
    {
        std::lock_guard lock(encoder_input_mutex);
        encoder_input_pool.clear();
        next_encoder_input_slot = 0;
    }

    HRESULT process_queued_frame(const NativePendingVideoFrame& frame) override
    {
        ComPtr<ID3D11Texture2D> nv12_texture;
        if (!get_conversion_texture(frame.slot_index, nv12_texture))
        {
            release_conversion_slot(frame.slot_index);
            return E_INVALIDARG;
        }

        size_t encoder_slot = 0;
        amf::AMFSurfacePtr surface;
        ID3D11Texture2D* encoder_texture = nullptr;
        HRESULT hr = acquire_encoder_input_slot(encoder_slot, surface, &encoder_texture);
        if (hr == DXGI_ERROR_WAS_STILL_DRAWING)
        {
            HRESULT drain_hr = drain_output(false);
            if (FAILED(drain_hr))
            {
                release_conversion_slot(frame.slot_index);
                return drain_hr;
            }
            hr = acquire_encoder_input_slot(encoder_slot, surface, &encoder_texture);
        }
        if (FAILED(hr))
        {
            release_conversion_slot(frame.slot_index);
            if (hr == DXGI_ERROR_WAS_STILL_DRAWING)
            {
                ++counters.encoder_input_full_drops;
                return S_OK;
            }
            return hr;
        }

        {
            std::lock_guard context_lock(d3d_context_mutex);
            converter.device_context->CopyResource(
                reinterpret_cast<ID3D11Resource*>(encoder_texture),
                reinterpret_cast<ID3D11Resource*>(nv12_texture.Get()));
        }
        release_conversion_slot(frame.slot_index);

        AMF_RESULT result = surface->Clear();
        if (!amf_result_success(result))
        {
            release_encoder_input_slot(encoder_slot);
            return fail_amf("AMF input surface Clear", result);
        }

        surface->SetPts(static_cast<amf_pts>(std::max<int64_t>(0, frame.timestamp_hns)));
        surface->SetDuration(static_cast<amf_pts>(video_sample_duration_hns));

        if (frame.force_idr)
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
            {
                release_encoder_input_slot(encoder_slot);
                return hr;
            }
            result = encoder->SubmitInput(surface);
        }
        if (result == AMF_INPUT_FULL)
        {
            release_encoder_input_slot(encoder_slot);
            ++counters.encoder_input_full_drops;
            set_last_error("NativeRecorder AMF encoder input queue is full; dropping one frame.");
            return S_OK;
        }
        if (result != AMF_OK && result != AMF_NEED_MORE_INPUT)
        {
            release_encoder_input_slot(encoder_slot);
            return fail_amf("AMF SubmitInput", result);
        }

        pending_output_timestamps.push_with_resource(frame.timestamp_hns, encoder_slot);
        ++counters.encoder_input_frames;
        return S_OK;
    }

    HRESULT drain_after_queued_frame() override
    {
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
                ++counters.query_repeat_returns;
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
                ++counters.query_repeat_returns;
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

            int64_t timestamp = take_output_timestamp(static_cast<int64_t>(data->GetPts()));

            HRESULT hr = muxer.enqueue_video_packet(packet, key_frame, timestamp, video_sample_duration_hns);
            if (FAILED(hr))
                return hr;
            ++counters.written_packets;
        }
    }

    int64_t take_output_timestamp(int64_t encoder_timestamp_hns)
    {
        return pending_output_timestamps.take(
            encoder_timestamp_hns,
            counters.written_packets,
            video_sample_duration_hns,
            [this](size_t slot_index)
            {
                release_encoder_input_slot(slot_index);
            });
    }

    HRESULT stop() override
    {
        if (stopped)
            return S_OK;

        stopped = true;
        HRESULT result = stop_video_worker();
        if (encoder)
        {
            AMF_RESULT amf_result = encoder->Drain();
            if (!amf_result_success(amf_result) && amf_result != AMF_INPUT_FULL && SUCCEEDED(result))
                result = fail_amf("AMF encoder Drain", amf_result);

            if (SUCCEEDED(result))
                result = drain_output(true);

            encoder->Terminate();
            encoder.Release();
        }

        release_pending_encoder_inputs();
        clear_encoder_input_pool();

        HRESULT mux_hr = muxer.close();
        if (FAILED(mux_hr) && SUCCEEDED(result))
            result = mux_hr;

        context.Release();
        clear_common_video_state();
        converter.reset();

        if (initialized && SUCCEEDED(result))
        {
            set_last_error("NativeRecorder finalized via AMF + libavformat. " + finalize_stats());
        }

        return result;
    }
};
