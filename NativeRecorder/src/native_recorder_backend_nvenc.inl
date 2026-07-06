#if NVENCAPI_MAJOR_VERSION > 13 || (NVENCAPI_MAJOR_VERSION == 13 && NVENCAPI_MINOR_VERSION >= 1)
#define PR_NVENC_HAS_OUTPUT_FRAME_METADATA 1
#else
#define PR_NVENC_HAS_OUTPUT_FRAME_METADATA 0
#endif

struct PrNvencOutputPacket
{
    std::vector<uint8_t> frame;
    bool key_frame = false;
    int64_t encoder_timestamp_hns = -1;
};

bool is_annex_b_start_code(const std::vector<uint8_t>& data, size_t offset, size_t& length)
{
    length = 0;
    if (offset + 3 <= data.size() &&
        data[offset] == 0 &&
        data[offset + 1] == 0 &&
        data[offset + 2] == 1)
    {
        length = 3;
        return true;
    }

    if (offset + 4 <= data.size() &&
        data[offset] == 0 &&
        data[offset + 1] == 0 &&
        data[offset + 2] == 0 &&
        data[offset + 3] == 1)
    {
        length = 4;
        return true;
    }

    return false;
}

size_t find_next_annex_b_start_code(const std::vector<uint8_t>& data, size_t offset)
{
    for (size_t i = offset; i + 3 <= data.size(); ++i)
    {
        size_t length = 0;
        if (is_annex_b_start_code(data, i, length))
            return i;
    }

    return data.size();
}

bool nvenc_packet_contains_key_frame(const std::vector<uint8_t>& data, int32_t codec)
{
    for (size_t offset = 0; offset + 3 <= data.size();)
    {
        size_t start_code_length = 0;
        if (!is_annex_b_start_code(data, offset, start_code_length))
        {
            ++offset;
            continue;
        }

        const size_t nal_start = offset + start_code_length;
        if (nal_start >= data.size())
            return false;

        if (codec == PR_CODEC_H264)
        {
            const uint8_t nal_type = data[nal_start] & 0x1F;
            if (nal_type == 5)
                return true;
        }
        else if (nal_start + 1 < data.size())
        {
            const uint8_t nal_type = (data[nal_start] >> 1) & 0x3F;
            if (nal_type >= 16 && nal_type <= 21)
                return true;
        }

        offset = find_next_annex_b_start_code(data, nal_start + 1);
    }

    return false;
}

void nvenc_encode_frame(
    NvEncoderD3D11& encoder,
    int32_t codec,
    std::vector<PrNvencOutputPacket>& packets,
    NV_ENC_PIC_PARAMS* picture_params)
{
#if PR_NVENC_HAS_OUTPUT_FRAME_METADATA
    std::vector<NvEncOutputFrame> raw_packets;
    encoder.EncodeFrame(raw_packets, picture_params);
    packets.reserve(packets.size() + raw_packets.size());
    for (NvEncOutputFrame& raw_packet : raw_packets)
    {
        PrNvencOutputPacket packet;
        packet.frame = std::move(raw_packet.frame);
        packet.key_frame = nvenc_output_is_key_frame(raw_packet.pictureType) ||
            nvenc_packet_contains_key_frame(packet.frame, codec);
        packet.encoder_timestamp_hns = static_cast<int64_t>(raw_packet.timeStamp);
        packets.push_back(std::move(packet));
    }
#else
    std::vector<std::vector<uint8_t>> raw_packets;
    encoder.EncodeFrame(raw_packets, picture_params);
    packets.reserve(packets.size() + raw_packets.size());
    for (std::vector<uint8_t>& raw_packet : raw_packets)
    {
        PrNvencOutputPacket packet;
        packet.frame = std::move(raw_packet);
        packet.key_frame = nvenc_packet_contains_key_frame(packet.frame, codec);
        packets.push_back(std::move(packet));
    }
#endif
}

void nvenc_end_encode(
    NvEncoderD3D11& encoder,
    int32_t codec,
    std::vector<PrNvencOutputPacket>& packets)
{
#if PR_NVENC_HAS_OUTPUT_FRAME_METADATA
    std::vector<NvEncOutputFrame> raw_packets;
    encoder.EndEncode(raw_packets);
    packets.reserve(packets.size() + raw_packets.size());
    for (NvEncOutputFrame& raw_packet : raw_packets)
    {
        PrNvencOutputPacket packet;
        packet.frame = std::move(raw_packet.frame);
        packet.key_frame = nvenc_output_is_key_frame(raw_packet.pictureType) ||
            nvenc_packet_contains_key_frame(packet.frame, codec);
        packet.encoder_timestamp_hns = static_cast<int64_t>(raw_packet.timeStamp);
        packets.push_back(std::move(packet));
    }
#else
    std::vector<std::vector<uint8_t>> raw_packets;
    encoder.EndEncode(raw_packets);
    packets.reserve(packets.size() + raw_packets.size());
    for (std::vector<uint8_t>& raw_packet : raw_packets)
    {
        PrNvencOutputPacket packet;
        packet.frame = std::move(raw_packet);
        packet.key_frame = nvenc_packet_contains_key_frame(packet.frame, codec);
        packets.push_back(std::move(packet));
    }
#endif
}

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
        if (video.width <= 0 || video.height <= 0 || video_output_width(video) <= 0 || video_output_height(video) <= 0 || video.fps <= 0)
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
            ", source=" + std::to_string(video.width) + "x" + std::to_string(video.height) +
            ", output=" + std::to_string(video_output_width(video)) + "x" + std::to_string(video_output_height(video)) +
            ", sourceFormat=" + dxgi_format_to_string(source_format) +
            ", encoded=" + std::to_string(encoded_width) + "x" + std::to_string(encoded_height) +
            ", pad=" + std::to_string(encoded_width - video_output_width(video)) + "x" + std::to_string(encoded_height - video_output_height(video)) +
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

            std::vector<PrNvencOutputPacket> packets;
            pending_output_timestamps.push(frame.timestamp_hns);
            ++counters.encoder_input_frames;
            nvenc_encode_frame(*encoder, video.codec, packets, &picture_params);
            for (const PrNvencOutputPacket& packet : packets)
            {
                if (packet.frame.empty())
                    continue;

                int64_t packet_timestamp = take_output_timestamp(packet.encoder_timestamp_hns);
                HRESULT hr = muxer.enqueue_video_packet(
                    packet.frame,
                    packet.key_frame,
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
                std::vector<PrNvencOutputPacket> packets;
                nvenc_end_encode(*encoder, video.codec, packets);
                for (const PrNvencOutputPacket& packet : packets)
                {
                    if (packet.frame.empty())
                        continue;

                    int64_t packet_timestamp = take_output_timestamp(packet.encoder_timestamp_hns);
                    HRESULT hr = muxer.enqueue_video_packet(
                        packet.frame,
                        packet.key_frame,
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
