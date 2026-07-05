using System;
using System.Diagnostics;
using System.Globalization;

namespace Recorder.Capture;

internal sealed class VideoCaptureFramePacer
{
    private long _minFrameIntervalTicks;
    private long _lateResyncThresholdTicks;
    private long _nextFrameDueTicks;
    private long _lateResyncCount;
    private long _maxLateTicks;

    public void Reset(int targetFps)
    {
        int fps = Math.Max(1, targetFps);
        _minFrameIntervalTicks = Math.Max(1, Stopwatch.Frequency / fps);
        _lateResyncThresholdTicks = Math.Max(1, _minFrameIntervalTicks / 3);
        _nextFrameDueTicks = 0;
        _lateResyncCount = 0;
        _maxLateTicks = 0;
    }

    public bool ShouldCapture(long nowTicks)
    {
        if (_nextFrameDueTicks == 0)
        {
            _nextFrameDueTicks = nowTicks + _minFrameIntervalTicks;
            return true;
        }

        if (nowTicks < _nextFrameDueTicks)
            return false;

        long lateTicks = nowTicks - _nextFrameDueTicks;
        long nextDue = lateTicks >= _lateResyncThresholdTicks
            ? nowTicks + _minFrameIntervalTicks
            : _nextFrameDueTicks + _minFrameIntervalTicks;

        if (lateTicks >= _lateResyncThresholdTicks)
        {
            _lateResyncCount++;
            _maxLateTicks = Math.Max(_maxLateTicks, lateTicks);
        }

        _nextFrameDueTicks = nextDue;
        return true;
    }

    public string BuildSummary()
        => "pacerLateResyncs=" + _lateResyncCount.ToString(CultureInfo.InvariantCulture) +
           ", pacerLateResyncThresholdMs=" + FormatTicks(_lateResyncThresholdTicks) +
           ", pacerMaxLateMs=" + FormatTicks(_maxLateTicks);

    private static string FormatTicks(long ticks)
        => (ticks * 1_000.0 / Stopwatch.Frequency).ToString("0.###", CultureInfo.InvariantCulture);
}
