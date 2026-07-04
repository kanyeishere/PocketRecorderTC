struct NativeTimingStats
{
    mutable std::mutex mutex;
    std::vector<uint64_t> samples_us;

    void reset()
    {
        std::lock_guard lock(mutex);
        samples_us.clear();
    }

    void record_us(uint64_t value_us) noexcept
    {
        try
        {
            std::lock_guard lock(mutex);
            samples_us.push_back(value_us);
        }
        catch (...)
        {
        }
    }

    void record_ms(uint64_t value_ms) noexcept
    {
        record_us(value_ms * 1000);
    }

    void record_since(std::chrono::steady_clock::time_point start) noexcept
    {
        record_us(static_cast<uint64_t>(std::chrono::duration_cast<std::chrono::microseconds>(
            std::chrono::steady_clock::now() - start).count()));
    }

    std::string summary_ms() const
    {
        std::vector<uint64_t> values;
        {
            std::lock_guard lock(mutex);
            values = samples_us;
        }

        if (values.empty())
            return "count=0";

        std::sort(values.begin(), values.end());

        uint64_t total = 0;
        for (uint64_t value : values)
            total += value;

        const uint64_t avg = total / static_cast<uint64_t>(values.size());
        return "count=" + std::to_string(values.size()) +
            ",avg=" + format_ms(avg) +
            ",p50=" + format_ms(percentile(values, 50)) +
            ",p95=" + format_ms(percentile(values, 95)) +
            ",p99=" + format_ms(percentile(values, 99)) +
            ",max=" + format_ms(values.back());
    }

    static uint64_t elapsed_ms_since(std::chrono::steady_clock::time_point start)
    {
        return static_cast<uint64_t>(std::chrono::duration_cast<std::chrono::milliseconds>(
            std::chrono::steady_clock::now() - start).count());
    }

private:
    static uint64_t percentile(const std::vector<uint64_t>& sorted, uint64_t percentile_value)
    {
        if (sorted.empty())
            return 0;

        uint64_t index = ((static_cast<uint64_t>(sorted.size()) * percentile_value) + 99) / 100;
        if (index == 0)
            index = 1;
        index = std::min<uint64_t>(index - 1, static_cast<uint64_t>(sorted.size() - 1));
        return sorted[static_cast<size_t>(index)];
    }

    static std::string format_ms(uint64_t value_us)
    {
        uint64_t whole = value_us / 1000;
        uint64_t fraction = value_us % 1000;
        std::string text = std::to_string(whole) + ".";
        if (fraction < 100)
            text += "0";
        if (fraction < 10)
            text += "0";
        text += std::to_string(fraction);
        return text;
    }
};
