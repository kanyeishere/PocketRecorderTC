using System;
using System.Diagnostics;

namespace Recorder.Capture;

internal sealed class VideoCaptureFramePacer
{
    private long _minFrameIntervalTicks;
    private long _nextFrameDueTicks;

    public void Reset(int targetFps)
    {
        int fps = Math.Max(1, targetFps);
        _minFrameIntervalTicks = Math.Max(1, Stopwatch.Frequency / fps);
        _nextFrameDueTicks = 0;
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

        long nextDue = _nextFrameDueTicks + _minFrameIntervalTicks;
        if (nowTicks - _nextFrameDueTicks > _minFrameIntervalTicks)
            nextDue = nowTicks + _minFrameIntervalTicks;

        _nextFrameDueTicks = nextDue;
        return true;
    }
}
