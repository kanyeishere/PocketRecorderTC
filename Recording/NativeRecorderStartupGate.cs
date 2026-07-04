using Recorder.Capture;
using System;
using System.Diagnostics;

namespace Recorder.Recording;

internal sealed class NativeRecorderStartupGate
{
    private const string NativeRecorderBackendId = "native-recorder";
    private const int MinWidth = 320;
    private const int MinHeight = 180;
    private const int StableFrameCount = 2;
    private const int LogEverySkippedFrames = 60;
    private const int FallbackTimeoutMs = 2_000;

    private int _candidateWidth;
    private int _candidateHeight;
    private int _stableFrames;
    private int _skippedFrames;
    private long _gateStartTicks;
    private bool _acceptedLogged;

    public NativeRecorderStartupGateResult Evaluate(RecordingBackendPlan? backendPlan, VideoFrame frame)
    {
        if (!IsNativeRecorderBackend(backendPlan) || !frame.IsD3D11Texture)
        {
            Reset();
            return NativeRecorderStartupGateResult.Ready();
        }

        long nowTicks = Stopwatch.GetTimestamp();
        if (_gateStartTicks == 0)
            _gateStartTicks = nowTicks;

        if (frame.Width < MinWidth || frame.Height < MinHeight)
            return RejectSmallFrame(frame, nowTicks);

        if (frame.Width != _candidateWidth || frame.Height != _candidateHeight)
            return TrackNewCandidate(frame, nowTicks);

        return TrackStableCandidate(frame, nowTicks);
    }

    public void Reset()
    {
        _candidateWidth = 0;
        _candidateHeight = 0;
        _stableFrames = 0;
        _skippedFrames = 0;
        _gateStartTicks = 0;
        _acceptedLogged = false;
    }

    private NativeRecorderStartupGateResult RejectSmallFrame(VideoFrame frame, long nowTicks)
    {
        _candidateWidth = 0;
        _candidateHeight = 0;
        _stableFrames = 0;
        _skippedFrames++;

        if (HasTimedOut(nowTicks, out long elapsedMs))
        {
            string reason = $"native startup frame stayed below real-game threshold for {elapsedMs}ms; " +
                            $"fallback=FFmpeg rawvideo, lastFrame={DescribeFrame(frame)}, " +
                            $"minimum={MinWidth}x{MinHeight}, skippedStartupFrames={_skippedFrames}";
            Reset();
            return NativeRecorderStartupGateResult.FallbackToFFmpeg(reason);
        }

        if (!ShouldLog())
            return NativeRecorderStartupGateResult.Wait();

        return NativeRecorderStartupGateResult.Wait(
            $"native startup frame rejected: {DescribeFrame(frame)}, " +
            $"minimum={MinWidth}x{MinHeight}, skippedStartupFrames={_skippedFrames}");
    }

    private NativeRecorderStartupGateResult TrackNewCandidate(VideoFrame frame, long nowTicks)
    {
        _candidateWidth = frame.Width;
        _candidateHeight = frame.Height;
        _stableFrames = 1;
        _skippedFrames++;

        if (HasTimedOut(nowTicks, out long elapsedMs))
        {
            string reason = $"native startup frame did not stabilize within {elapsedMs}ms; " +
                            $"fallback=FFmpeg rawvideo, lastFrame={DescribeFrame(frame)}, " +
                            $"stableFrames=1/{StableFrameCount}, skippedStartupFrames={_skippedFrames}";
            Reset();
            return NativeRecorderStartupGateResult.FallbackToFFmpeg(reason);
        }

        if (!ShouldLog())
            return NativeRecorderStartupGateResult.Wait();

        return NativeRecorderStartupGateResult.Wait(
            $"native startup frame candidate: {DescribeFrame(frame)}, " +
            $"stableFrames=1/{StableFrameCount}, skippedStartupFrames={_skippedFrames}");
    }

    private NativeRecorderStartupGateResult TrackStableCandidate(VideoFrame frame, long nowTicks)
    {
        _stableFrames++;
        if (_stableFrames < StableFrameCount)
        {
            _skippedFrames++;
            if (HasTimedOut(nowTicks, out long elapsedMs))
            {
                string reason = $"native startup frame did not stabilize within {elapsedMs}ms; " +
                                $"fallback=FFmpeg rawvideo, lastFrame={DescribeFrame(frame)}, " +
                                $"stableFrames={_stableFrames}/{StableFrameCount}, skippedStartupFrames={_skippedFrames}";
                Reset();
                return NativeRecorderStartupGateResult.FallbackToFFmpeg(reason);
            }

            if (!ShouldLog())
                return NativeRecorderStartupGateResult.Wait();

            return NativeRecorderStartupGateResult.Wait(
                $"native startup frame candidate: {DescribeFrame(frame)}, " +
                $"stableFrames={_stableFrames}/{StableFrameCount}, skippedStartupFrames={_skippedFrames}");
        }

        if (_skippedFrames > 0 && !_acceptedLogged)
        {
            _acceptedLogged = true;
            return NativeRecorderStartupGateResult.Ready(
                $"native startup frame accepted: {DescribeFrame(frame)}, " +
                $"stableFrames={_stableFrames}, skippedStartupFrames={_skippedFrames}");
        }

        return NativeRecorderStartupGateResult.Ready();
    }

    private static bool IsNativeRecorderBackend(RecordingBackendPlan? backendPlan)
        => string.Equals(backendPlan?.Backend.Id, NativeRecorderBackendId, StringComparison.OrdinalIgnoreCase);

    private bool ShouldLog()
        => _skippedFrames <= 5 || _skippedFrames % LogEverySkippedFrames == 0;

    private bool HasTimedOut(long nowTicks, out long elapsedMs)
    {
        elapsedMs = 0;
        if (_gateStartTicks == 0)
            return false;

        elapsedMs = Math.Max(0, (nowTicks - _gateStartTicks) * 1_000L / Stopwatch.Frequency);
        return elapsedMs >= FallbackTimeoutMs;
    }

    private static string DescribeFrame(VideoFrame frame)
    {
        string description = $"{frame.Width}x{frame.Height}, pixelFormat={frame.PixelFormat}, timestampHns={frame.TimestampHns}";
        if (!frame.IsD3D11Texture)
            return description;

        return $"{description}, dxgiFormat={frame.DxgiFormat}, sharedHandleSet={frame.D3D11SharedHandle != IntPtr.Zero}";
    }
}

internal readonly record struct NativeRecorderStartupGateResult(
    NativeRecorderStartupGateAction Action,
    string? Message = null)
{
    public static NativeRecorderStartupGateResult Ready(string? message = null)
        => new(NativeRecorderStartupGateAction.Ready, message);

    public static NativeRecorderStartupGateResult Wait(string? message = null)
        => new(NativeRecorderStartupGateAction.Wait, message);

    public static NativeRecorderStartupGateResult FallbackToFFmpeg(string message)
        => new(NativeRecorderStartupGateAction.FallbackToFFmpeg, message);
}

internal enum NativeRecorderStartupGateAction
{
    Ready,
    Wait,
    FallbackToFFmpeg,
}
