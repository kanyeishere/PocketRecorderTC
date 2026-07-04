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
            // 确保输出目录存在，避免 avio_open 返回 ENOENT (-2)
            try
            {
                auto parent = std::filesystem::path(output_path_utf8).parent_path();
                if (!parent.empty())
                    std::filesystem::create_directories(parent);
            }
            catch (const std::exception&)
            {
            }

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

struct AsyncLibavMp4Muxer
{
    struct WorkItem
    {
        enum class Kind
        {
            Video,
            Audio,
        };

        Kind kind = Kind::Video;
        std::vector<uint8_t> data;
        bool key_frame = false;
        int64_t timestamp_hns = 0;
        int64_t duration_hns = 0;
    };

    LibavMp4Muxer muxer;
    std::thread worker;
    std::mutex queue_mutex;
    std::condition_variable queue_cv;
    std::deque<WorkItem> queue;
    std::atomic<HRESULT> worker_result{S_OK};
    bool accepting = false;
    bool stopping = false;

    ~AsyncLibavMp4Muxer()
    {
        close();
    }

    HRESULT open(
        const std::wstring& path,
        const pr_video_config& video_config,
        const pr_audio_config& audio_config,
        const std::vector<uint8_t>& video_extradata)
    {
        HRESULT hr = muxer.open(path, video_config, audio_config, video_extradata);
        if (FAILED(hr))
            return hr;

        worker_result.store(S_OK);
        accepting = true;
        stopping = false;
        worker = std::thread([this] { worker_loop(); });
        return S_OK;
    }

    HRESULT enqueue_video_packet(const std::vector<uint8_t>& data, bool key_frame, int64_t timestamp_hns, int64_t duration_hns)
    {
        if (data.empty())
            return S_OK;

        HRESULT result = worker_result.load();
        if (FAILED(result))
            return result;

        WorkItem item{};
        item.kind = WorkItem::Kind::Video;
        item.data = data;
        item.key_frame = key_frame;
        item.timestamp_hns = timestamp_hns;
        item.duration_hns = duration_hns;

        std::unique_lock lock(queue_mutex);
        if (!accepting)
            return E_ABORT;
        if (queue.size() >= kMaxMuxQueueItems)
        {
            set_last_error("NativeRecorder mux queue is full; dropping one encoded video packet.");
            return S_OK;
        }

        queue.push_back(std::move(item));
        lock.unlock();
        queue_cv.notify_one();
        return S_OK;
    }

    HRESULT enqueue_audio(const void* data, int32_t byte_count, int64_t timestamp_hns)
    {
        if (data == nullptr || byte_count <= 0)
            return S_OK;

        HRESULT result = worker_result.load();
        if (FAILED(result))
            return result;

        WorkItem item{};
        item.kind = WorkItem::Kind::Audio;
        const auto* bytes = static_cast<const uint8_t*>(data);
        item.data.assign(bytes, bytes + byte_count);
        item.timestamp_hns = timestamp_hns;

        std::unique_lock lock(queue_mutex);
        if (!accepting)
            return S_OK;
        if (queue.size() >= kMaxMuxQueueItems)
        {
            set_last_error("NativeRecorder mux queue is full; dropping one audio packet.");
            return S_OK;
        }

        queue.push_back(std::move(item));
        lock.unlock();
        queue_cv.notify_one();
        return S_OK;
    }

    HRESULT close()
    {
        {
            std::lock_guard lock(queue_mutex);
            accepting = false;
            stopping = true;
        }
        queue_cv.notify_one();

        if (worker.joinable())
            worker.join();

        HRESULT result = worker_result.load();
        HRESULT close_hr = muxer.close();
        if (FAILED(close_hr) && SUCCEEDED(result))
            result = close_hr;

        return result;
    }

    void worker_loop()
    {
        for (;;)
        {
            WorkItem item;
            {
                std::unique_lock lock(queue_mutex);
                queue_cv.wait(lock, [this] { return stopping || !queue.empty(); });
                if (queue.empty())
                {
                    if (stopping)
                        return;
                    continue;
                }

                item = std::move(queue.front());
                queue.pop_front();
            }

            HRESULT hr = S_OK;
            if (item.kind == WorkItem::Kind::Video)
            {
                hr = muxer.write_video_packet(item.data, item.key_frame, item.timestamp_hns, item.duration_hns);
            }
            else
            {
                hr = muxer.write_audio(item.data.data(), static_cast<int32_t>(item.data.size()), item.timestamp_hns);
            }

            if (FAILED(hr))
            {
                worker_result.store(hr);
                std::lock_guard lock(queue_mutex);
                accepting = false;
                stopping = true;
                queue.clear();
                queue_cv.notify_one();
                return;
            }
        }
    }
};
}
