using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;

namespace Recorder.Encoding;

internal sealed class NativeRecorderTimingDiagnostics
{
    private readonly List<long> _captureDeltaHns = [];
    private readonly List<long> _captureJitterHns = [];
    private readonly List<long> _sampleAgeTicks = [];
    private readonly List<long> _submitAttemptTicks = [];
    private readonly List<long> _acceptedSubmitTicks = [];
    private readonly List<long> _rejectedSubmitTicks = [];

    private long _expectedFrameHns;
    private long _frameBudgetTicks;
    private long _lastCaptureTimestampHns;
    private long _earlyFrames;
    private long _lateFrames;
    private long _longGapFrames;
    private long _nonMonotonicCaptureFrames;
    private long _sampleAgeOverBudgetFrames;
    private long _submitOverBudgetFrames;
    private long _maxConsecutiveLongGaps;
    private long _currentConsecutiveLongGaps;

    public void Reset(int fps)
    {
        int safeFps = Math.Max(1, fps);
        _expectedFrameHns = Math.Max(1, 10_000_000L / safeFps);
        _frameBudgetTicks = Math.Max(1, Stopwatch.Frequency / safeFps);
        _lastCaptureTimestampHns = -1;
        _earlyFrames = 0;
        _lateFrames = 0;
        _longGapFrames = 0;
        _nonMonotonicCaptureFrames = 0;
        _sampleAgeOverBudgetFrames = 0;
        _submitOverBudgetFrames = 0;
        _maxConsecutiveLongGaps = 0;
        _currentConsecutiveLongGaps = 0;
        _captureDeltaHns.Clear();
        _captureJitterHns.Clear();
        _sampleAgeTicks.Clear();
        _submitAttemptTicks.Clear();
        _acceptedSubmitTicks.Clear();
        _rejectedSubmitTicks.Clear();
    }

    public void RecordSubmitAttempt(long submitTicks, bool accepted)
    {
        long safeSubmitTicks = Math.Max(0, submitTicks);
        _submitAttemptTicks.Add(safeSubmitTicks);
        if (accepted)
            _acceptedSubmitTicks.Add(safeSubmitTicks);
        else
            _rejectedSubmitTicks.Add(safeSubmitTicks);

        if (safeSubmitTicks > _frameBudgetTicks)
            _submitOverBudgetFrames++;
    }

    public void RecordSubmittedFrame(long captureTimestampHns, long sampleAgeTicks)
    {
        _sampleAgeTicks.Add(Math.Max(0, sampleAgeTicks));

        if (sampleAgeTicks > _frameBudgetTicks)
            _sampleAgeOverBudgetFrames++;

        if (_lastCaptureTimestampHns >= 0)
        {
            long deltaHns = captureTimestampHns - _lastCaptureTimestampHns;
            if (deltaHns <= 0)
            {
                _nonMonotonicCaptureFrames++;
            }
            else
            {
                _captureDeltaHns.Add(deltaHns);
                _captureJitterHns.Add(Math.Abs(deltaHns - _expectedFrameHns));

                if (deltaHns < _expectedFrameHns * 3 / 4)
                    _earlyFrames++;
                if (deltaHns > _expectedFrameHns * 5 / 4)
                    _lateFrames++;

                if (deltaHns > _expectedFrameHns * 2)
                {
                    _longGapFrames++;
                    _currentConsecutiveLongGaps++;
                    _maxConsecutiveLongGaps = Math.Max(_maxConsecutiveLongGaps, _currentConsecutiveLongGaps);
                }
                else
                {
                    _currentConsecutiveLongGaps = 0;
                }
            }
        }

        _lastCaptureTimestampHns = captureTimestampHns;
    }

    public string BuildSummary()
    {
        return "outputTiming=obsStyleSharedTextureCfr" +
               ", targetFrameMs=" + FormatMs(_expectedFrameHns / 10_000.0) +
               ", captureDeltaMs=" + FormatHnsSummary(_captureDeltaHns) +
               ", captureJitterMs=" + FormatHnsSummary(_captureJitterHns) +
               ", earlyFrames=" + _earlyFrames.ToString(CultureInfo.InvariantCulture) +
               ", lateFrames=" + _lateFrames.ToString(CultureInfo.InvariantCulture) +
               ", longGapFrames=" + _longGapFrames.ToString(CultureInfo.InvariantCulture) +
               ", maxConsecutiveLongGaps=" + _maxConsecutiveLongGaps.ToString(CultureInfo.InvariantCulture) +
               ", nonMonotonicCaptureFrames=" + _nonMonotonicCaptureFrames.ToString(CultureInfo.InvariantCulture) +
               ", sampleAgeMs=" + FormatTicksSummary(_sampleAgeTicks) +
               ", sampleAgeOverBudgetFrames=" + _sampleAgeOverBudgetFrames.ToString(CultureInfo.InvariantCulture) +
               ", submitMs=" + FormatTicksSummary(_submitAttemptTicks) +
               ", acceptedSubmitMs=" + FormatTicksSummary(_acceptedSubmitTicks) +
               ", rejectedSubmitMs=" + FormatTicksSummary(_rejectedSubmitTicks) +
               ", submitOverBudgetFrames=" + _submitOverBudgetFrames.ToString(CultureInfo.InvariantCulture) +
               ", thresholds=early<75%,late>125%,longGap>200%";
    }

    private static string FormatHnsSummary(List<long> values)
    {
        if (values.Count == 0)
            return "count=0";

        return FormatSummary(values, value => value / 10_000.0);
    }

    private static string FormatTicksSummary(List<long> values)
    {
        if (values.Count == 0)
            return "count=0";

        return FormatSummary(values, value => value * 1_000.0 / Stopwatch.Frequency);
    }

    private static string FormatSummary(List<long> values, Func<long, double> toMilliseconds)
    {
        long[] sorted = values.ToArray();
        Array.Sort(sorted);

        double p50 = toMilliseconds(Percentile(sorted, 0.50));
        double p95 = toMilliseconds(Percentile(sorted, 0.95));
        double p99 = toMilliseconds(Percentile(sorted, 0.99));
        double max = toMilliseconds(sorted[^1]);
        double avg = 0;
        for (int i = 0; i < sorted.Length; i++)
            avg += toMilliseconds(sorted[i]);
        avg /= sorted.Length;

        return "count=" + sorted.Length.ToString(CultureInfo.InvariantCulture) +
               ",avg=" + FormatMs(avg) +
               ",p50=" + FormatMs(p50) +
               ",p95=" + FormatMs(p95) +
               ",p99=" + FormatMs(p99) +
               ",max=" + FormatMs(max);
    }

    private static long Percentile(long[] sorted, double percentile)
    {
        if (sorted.Length == 0)
            return 0;

        int index = (int)Math.Ceiling(sorted.Length * percentile) - 1;
        index = Math.Clamp(index, 0, sorted.Length - 1);
        return sorted[index];
    }

    private static string FormatMs(double value)
        => value.ToString("0.###", CultureInfo.InvariantCulture);
}
