using System;

namespace Recorder.Encoding;

internal sealed class NativeRecorderCfrFrameScheduler
{
    private long _frameDurationHns = 1;
    private long _firstCaptureTimestampHns = -1;
    private long _nextOutputFrameIndex;

    public long FrameDurationHns => _frameDurationHns;
    public long NextOutputFrameIndex => _nextOutputFrameIndex;

    public void Reset(int fps)
    {
        int safeFps = Math.Max(1, fps);
        _frameDurationHns = Math.Max(1, 10_000_000L / safeFps);
        _firstCaptureTimestampHns = -1;
        _nextOutputFrameIndex = 0;
    }

    public NativeRecorderCfrFramePlan PlanFrame(long captureTimestampHns)
    {
        if (_firstCaptureTimestampHns < 0)
            _firstCaptureTimestampHns = captureTimestampHns;

        long elapsedHns = Math.Max(0, captureTimestampHns - _firstCaptureTimestampHns);
        long targetFrameIndex = (elapsedHns + (_frameDurationHns / 2)) / _frameDurationHns;
        if (targetFrameIndex < _nextOutputFrameIndex)
            targetFrameIndex = _nextOutputFrameIndex;

        long duplicateCount = Math.Max(0, targetFrameIndex - _nextOutputFrameIndex);
        return new NativeRecorderCfrFramePlan(
            _nextOutputFrameIndex * _frameDurationHns,
            duplicateCount,
            targetFrameIndex * _frameDurationHns,
            targetFrameIndex);
    }

    public NativeRecorderCfrFramePlan PlanTail(TimeSpan? finalDuration)
    {
        if (finalDuration == null || finalDuration <= TimeSpan.Zero || _nextOutputFrameIndex <= 0)
            return NativeRecorderCfrFramePlan.Empty(_nextOutputFrameIndex, _frameDurationHns);

        long finalDurationHns = Math.Max(0, finalDuration.Value.Ticks);
        long targetFrameCount = (finalDurationHns + _frameDurationHns - 1) / _frameDurationHns;
        long duplicateCount = Math.Max(0, targetFrameCount - _nextOutputFrameIndex);
        long lastFrameIndex = duplicateCount > 0
            ? _nextOutputFrameIndex + duplicateCount - 1
            : _nextOutputFrameIndex - 1;

        return new NativeRecorderCfrFramePlan(
            _nextOutputFrameIndex * _frameDurationHns,
            duplicateCount,
            Math.Max(0, lastFrameIndex * _frameDurationHns),
            Math.Max(0, lastFrameIndex));
    }

    public void Commit(NativeRecorderCfrFramePlan plan)
    {
        _nextOutputFrameIndex = Math.Max(_nextOutputFrameIndex, plan.CurrentFrameIndex + 1);
    }
}

internal readonly record struct NativeRecorderCfrFramePlan(
    long FirstDuplicateTimestampHns,
    long DuplicateCount,
    long CurrentTimestampHns,
    long CurrentFrameIndex)
{
    public static NativeRecorderCfrFramePlan Empty(long nextFrameIndex, long frameDurationHns)
        => new(nextFrameIndex * frameDurationHns, 0, nextFrameIndex * frameDurationHns, Math.Max(0, nextFrameIndex - 1));
}
