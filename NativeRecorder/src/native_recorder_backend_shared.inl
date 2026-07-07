struct NativeEncoderCounters
{
    uint64_t submitted_frames = 0;
    uint64_t encoder_input_frames = 0;
    uint64_t written_packets = 0;
    uint64_t encoder_input_full_drops = 0;
    uint64_t query_repeat_returns = 0;
    uint64_t audio_packets = 0;
    uint64_t conversion_pool_waits = 0;
    uint64_t conversion_pool_timeouts = 0;
    uint64_t native_queue_waits = 0;
    uint64_t native_queue_timeouts = 0;
    uint64_t max_native_queue_depth = 0;
    uint64_t surface_pool_waits = 0;
    uint64_t surface_pool_wait_ms = 0;
    uint64_t surface_pool_timeouts = 0;
    uint64_t submit_input_full_waits = 0;
    uint64_t submit_input_full_wait_ms = 0;
    uint64_t submit_input_full_timeouts = 0;
    uint64_t output_delay_waits = 0;
    uint64_t output_delay_wait_ms = 0;
    uint64_t output_delay_timeouts = 0;
    uint64_t max_pending_outputs = 0;
    uint64_t timestamp_pts_matches = 0;
    uint64_t timestamp_fifo_fallbacks = 0;
    uint64_t timestamp_missing_entries = 0;
    NativeTimingStats native_submit_stats;
    NativeTimingStats native_convert_stats;
    NativeTimingStats native_worker_frame_stats;
    NativeTimingStats conversion_pool_wait_stats;
    NativeTimingStats native_queue_wait_stats;
    NativeTimingStats surface_pool_wait_stats;
    NativeTimingStats submit_input_full_wait_stats;
    NativeTimingStats output_delay_wait_stats;
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
    std::condition_variable cv;

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

    HRESULT acquire_with_backpressure(
        size_t& slot_index,
        ID3D11Texture2D** texture,
        int timeout_ms,
        int retry_sleep_ms,
        const std::string& timeout_message,
        bool& waited,
        uint64_t& wait_ms)
    {
        if (texture == nullptr)
            return E_POINTER;

        waited = false;
        wait_ms = 0;
        const auto start = std::chrono::steady_clock::now();
        std::unique_lock lock(mutex);
        for (;;)
        {
            for (size_t offset = 0; offset < slots.size(); ++offset)
            {
                size_t index = (next_slot + offset) % slots.size();
                if (slots[index].in_use || !slots[index].texture)
                    continue;

                slots[index].in_use = true;
                next_slot = (index + 1) % slots.size();
                slot_index = index;
                *texture = slots[index].texture.Get();
                if (waited)
                    wait_ms = NativeTimingStats::elapsed_ms_since(start);
                return S_OK;
            }

            wait_ms = NativeTimingStats::elapsed_ms_since(start);
            if (wait_ms >= static_cast<uint64_t>(timeout_ms))
            {
                set_last_error(timeout_message);
                return DXGI_ERROR_WAS_STILL_DRAWING;
            }

            waited = true;
            cv.wait_for(lock, std::chrono::milliseconds(retry_sleep_ms));
        }
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
        {
            std::lock_guard lock(mutex);
            if (slot_index < slots.size())
                slots[slot_index].in_use = false;
        }
        cv.notify_one();
    }

    void clear()
    {
        {
            std::lock_guard lock(mutex);
            slots.clear();
            next_slot = 0;
        }
        cv.notify_all();
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

    HRESULT enqueue_with_backpressure(
        NativePendingVideoFrame frame,
        int timeout_ms,
        int retry_sleep_ms,
        const std::string& timeout_message,
        bool& waited,
        uint64_t& wait_ms,
        size_t& depth_after_enqueue)
    {
        waited = false;
        wait_ms = 0;
        depth_after_enqueue = 0;
        const auto start = std::chrono::steady_clock::now();
        std::unique_lock lock(mutex);
        for (;;)
        {
            if (!accepting)
                return E_ABORT;

            if (queue.size() < kMaxNativeVideoQueueItems)
            {
                queue.push_back(frame);
                depth_after_enqueue = queue.size();
                if (waited)
                    wait_ms = NativeTimingStats::elapsed_ms_since(start);
                lock.unlock();
                cv.notify_one();
                return S_OK;
            }

            wait_ms = NativeTimingStats::elapsed_ms_since(start);
            if (wait_ms >= static_cast<uint64_t>(timeout_ms))
            {
                set_last_error(timeout_message);
                return DXGI_ERROR_WAS_STILL_DRAWING;
            }

            waited = true;
            cv.wait_for(lock, std::chrono::milliseconds(retry_sleep_ms));
        }
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
                lock.unlock();
                cv.notify_one();
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
        cv.notify_all();
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
        cv.notify_all();
        return slots;
    }

    void clear()
    {
        {
            std::lock_guard lock(mutex);
            queue.clear();
            accepting = false;
            stopping = false;
        }
        cv.notify_all();
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
    int64_t take_fifo(int64_t encoder_timestamp_hns, uint64_t written_packets, int64_t duration_hns, ReleaseResource release_resource)
    {
        if (!entries.empty())
        {
            return take_entry(entries.begin(), release_resource).timestamp_hns;
        }

        if (encoder_timestamp_hns >= 0)
            return std::max<int64_t>(0, encoder_timestamp_hns);

        return static_cast<int64_t>(written_packets) * duration_hns;
    }

    template <typename ReleaseResource>
    int64_t take_matching_or_fifo(
        int64_t encoder_timestamp_hns,
        uint64_t written_packets,
        int64_t duration_hns,
        ReleaseResource release_resource,
        bool& matched_encoder_pts,
        bool& used_fifo_fallback,
        bool& used_missing_entry_fallback)
    {
        matched_encoder_pts = false;
        used_fifo_fallback = false;
        used_missing_entry_fallback = false;

        if (encoder_timestamp_hns >= 0)
        {
            const int64_t safe_encoder_timestamp = std::max<int64_t>(0, encoder_timestamp_hns);
            auto matching_entry = std::find_if(
                entries.begin(),
                entries.end(),
                [safe_encoder_timestamp](const Entry& entry)
                {
                    return entry.timestamp_hns == safe_encoder_timestamp;
                });

            if (matching_entry != entries.end())
            {
                matched_encoder_pts = true;
                return take_entry(matching_entry, release_resource).timestamp_hns;
            }

            if (entries.empty())
            {
                used_missing_entry_fallback = true;
                return safe_encoder_timestamp;
            }

            used_fifo_fallback = true;
        }

        return take_fifo(encoder_timestamp_hns, written_packets, duration_hns, release_resource);
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

private:
    template <typename Iterator, typename ReleaseResource>
    Entry take_entry(Iterator entry_iterator, ReleaseResource release_resource)
    {
        Entry entry = *entry_iterator;
        entries.erase(entry_iterator);
        if (entry.has_resource_slot)
            release_resource(entry.resource_slot);
        return entry;
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

        const auto submit_start = std::chrono::steady_clock::now();
        auto record_submit = [this, submit_start]()
        {
            counters.native_submit_stats.record_since(submit_start);
        };

        HRESULT hr = initialize(source_device, source_format);
        if (FAILED(hr))
        {
            record_submit();
            return hr;
        }

        hr = video_worker_result.load();
        if (FAILED(hr))
        {
            record_submit();
            return hr;
        }

        size_t slot_index = 0;
        ID3D11Texture2D* nv12_texture = nullptr;
        hr = acquire_conversion_slot_with_backpressure(slot_index, &nv12_texture);
        if (FAILED(hr))
        {
            record_submit();
            return hr;
        }

        const auto convert_start = std::chrono::steady_clock::now();
        {
            std::lock_guard context_lock(d3d_context_mutex);
            hr = converter.convert_shared_texture_to(shared_handle, source_format, nv12_texture);
        }
        counters.native_convert_stats.record_since(convert_start);
        if (FAILED(hr))
        {
            conversion_nv12_pool.release(slot_index);
            record_submit();
            return hr;
        }

        const bool force_idr = counters.submitted_frames == 0;
        hr = enqueue_video_frame_with_backpressure(
            NativePendingVideoFrame{slot_index, std::max<int64_t>(0, timestamp_hns), force_idr});
        if (FAILED(hr))
        {
            conversion_nv12_pool.release(slot_index);
            record_submit();
            return hr;
        }

        ++counters.submitted_frames;
        record_submit();
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
            conversion_pool_slot_count(),
            [this](ComPtr<ID3D11Texture2D>& texture)
            {
                return create_conversion_texture(texture);
            });
    }

    virtual size_t conversion_pool_slot_count() const
    {
        return kNativeNv12ConversionPoolSize;
    }

    virtual HRESULT create_conversion_texture(ComPtr<ID3D11Texture2D>& texture)
    {
        return converter.create_nv12_texture(texture);
    }

    bool get_conversion_texture(size_t slot_index, ComPtr<ID3D11Texture2D>& texture)
    {
        return conversion_nv12_pool.get_texture(slot_index, texture);
    }

    void release_conversion_slot(size_t slot_index)
    {
        conversion_nv12_pool.release(slot_index);
    }

    HRESULT acquire_conversion_slot_with_backpressure(size_t& slot_index, ID3D11Texture2D** nv12_texture)
    {
        bool waited = false;
        uint64_t wait_ms = 0;
        HRESULT hr = conversion_nv12_pool.acquire_with_backpressure(
            slot_index,
            nv12_texture,
            kNativeBackpressureTimeoutMs,
            kNativeBackpressureRetrySleepMs,
            conversion_pool_timeout_message(),
            waited,
            wait_ms);

        if (waited)
        {
            ++counters.conversion_pool_waits;
            counters.conversion_pool_wait_stats.record_ms(wait_ms);
        }
        if (hr == DXGI_ERROR_WAS_STILL_DRAWING)
            ++counters.conversion_pool_timeouts;

        return hr;
    }

    HRESULT enqueue_video_frame_with_backpressure(NativePendingVideoFrame frame)
    {
        bool waited = false;
        uint64_t wait_ms = 0;
        size_t depth_after_enqueue = 0;
        HRESULT hr = video_queue.enqueue_with_backpressure(
            frame,
            kNativeBackpressureTimeoutMs,
            kNativeBackpressureRetrySleepMs,
            input_queue_timeout_message(),
            waited,
            wait_ms,
            depth_after_enqueue);

        if (depth_after_enqueue > 0)
            update_max_native_queue_depth(depth_after_enqueue);
        if (waited)
        {
            ++counters.native_queue_waits;
            counters.native_queue_wait_stats.record_ms(wait_ms);
        }
        if (hr == DXGI_ERROR_WAS_STILL_DRAWING)
            ++counters.native_queue_timeouts;

        return hr;
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

            const auto frame_start = std::chrono::steady_clock::now();
            HRESULT hr = process_queued_frame(frame);
            if (SUCCEEDED(hr))
                hr = drain_after_queued_frame();
            counters.native_worker_frame_stats.record_since(frame_start);

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
        return pending_output_timestamps.take_fifo(
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
            ", nativeSubmitMs=" + counters.native_submit_stats.summary_ms() +
            ", nativeConvertMs=" + counters.native_convert_stats.summary_ms() +
            ", nativeWorkerFrameMs=" + counters.native_worker_frame_stats.summary_ms() +
            ", conversionPoolWaits=" + std::to_string(counters.conversion_pool_waits) +
            ", conversionPoolWaitMs=" + counters.conversion_pool_wait_stats.summary_ms() +
            ", conversionPoolTimeouts=" + std::to_string(counters.conversion_pool_timeouts) +
            ", nativeQueueWaits=" + std::to_string(counters.native_queue_waits) +
            ", nativeQueueWaitMs=" + counters.native_queue_wait_stats.summary_ms() +
            ", nativeQueueTimeouts=" + std::to_string(counters.native_queue_timeouts) +
            ", maxNativeQueue=" + std::to_string(counters.max_native_queue_depth) +
            ", surfacePoolWaits=" + std::to_string(counters.surface_pool_waits) +
            ", surfacePoolWaitMs=" + counters.surface_pool_wait_stats.summary_ms() +
            ", surfacePoolTimeouts=" + std::to_string(counters.surface_pool_timeouts) +
            ", submitFullWaits=" + std::to_string(counters.submit_input_full_waits) +
            ", submitFullWaitMs=" + counters.submit_input_full_wait_stats.summary_ms() +
            ", submitFullTimeouts=" + std::to_string(counters.submit_input_full_timeouts) +
            ", outputDelayWaits=" + std::to_string(counters.output_delay_waits) +
            ", outputDelayWaitMs=" + counters.output_delay_wait_stats.summary_ms() +
            ", outputDelayTimeouts=" + std::to_string(counters.output_delay_timeouts) +
            ", maxPendingOutputs=" + std::to_string(counters.max_pending_outputs) +
            ", timestampPtsMatches=" + std::to_string(counters.timestamp_pts_matches) +
            ", timestampFifoFallbacks=" + std::to_string(counters.timestamp_fifo_fallbacks) +
            ", timestampMissingEntries=" + std::to_string(counters.timestamp_missing_entries) +
            ", " + muxer.stats_summary() +
            ", audioPackets=" + std::to_string(counters.audio_packets);
    }

    std::string conversion_pool_timeout_message() const
    {
        return "NativeRecorder " + backend_tag + " NV12 conversion pool stayed full for " +
            std::to_string(kNativeBackpressureTimeoutMs) + "ms; dropping one frame.";
    }

    std::string input_queue_timeout_message() const
    {
        return "NativeRecorder " + backend_tag + " input queue stayed full for " +
            std::to_string(kNativeBackpressureTimeoutMs) + "ms; dropping one frame.";
    }

    void update_max_native_queue_depth(size_t depth)
    {
        counters.max_native_queue_depth = std::max<uint64_t>(
            counters.max_native_queue_depth,
            static_cast<uint64_t>(depth));
    }

    virtual HRESULT process_queued_frame(const NativePendingVideoFrame& frame) = 0;

    virtual HRESULT drain_after_queued_frame()
    {
        return S_OK;
    }
};
