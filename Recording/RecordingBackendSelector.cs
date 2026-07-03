using System.Collections.Generic;
using System.Linq;

namespace Recorder.Recording;

internal sealed class RecordingBackendSelector
{
    private readonly NativeRecorderRecordingBackend _nativeRecorder = new();
    private readonly FFmpegRecordingBackend _ffmpeg;
    private readonly IRecorderLogger _log;

    public RecordingBackendSelector(IRecorderLogger log)
    {
        _log = log;
        _ffmpeg = new FFmpegRecordingBackend(log);
    }

    public RecordingBackendPlan SelectInitial(RecordingRequest request)
    {
        foreach (IRecordingBackend backend in EnumeratePreferredBackends())
        {
            RecordingBackendProbeResult probe = backend.Probe(request);
            if (probe.IsAvailable)
                return new RecordingBackendPlan(backend, probe);

            if (backend.Id == _nativeRecorder.Id)
                _log.Info($"[Record] NativeRecorder unavailable: {probe.Reason}");
        }

        return SelectFFmpeg(request, "no preferred backend available");
    }

    public RecordingBackendPlan SelectFFmpeg(RecordingRequest request, string reason)
    {
        RecordingBackendProbeResult probe = _ffmpeg.Probe(request);
        string probeReason = string.IsNullOrWhiteSpace(reason)
            ? probe.Reason
            : $"{reason}; {probe.Reason}";
        return new RecordingBackendPlan(
            _ffmpeg,
            probe with { Reason = probeReason });
    }

    private IEnumerable<IRecordingBackend> EnumeratePreferredBackends()
        => new IRecordingBackend[] { _nativeRecorder, _ffmpeg };

    public string DescribeAvailableBackends(RecordingRequest request)
    {
        return string.Join(
            ", ",
            EnumeratePreferredBackends().Select(backend =>
            {
                RecordingBackendProbeResult probe = backend.Probe(request);
                return $"{backend.DisplayName}:{(probe.IsAvailable ? "available" : probe.Reason)}";
            }));
    }
}
