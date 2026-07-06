using System;
using System.Collections.Generic;
using System.Linq;

namespace Recorder.Recording;

internal sealed class RecordingBackendSelector
{
    private readonly NativeRecorderRecordingBackend _nvenc = new(NativeRecordingBackendKind.Nvenc);
    private readonly NativeRecorderRecordingBackend _amf = new(NativeRecordingBackendKind.Amf);
    private readonly NativeRecorderRecordingBackend _qsv = new(NativeRecordingBackendKind.Qsv);
    private readonly FFmpegRecordingBackend _ffmpeg;
    private readonly IRecorderLogger _log;

    public RecordingBackendSelector(IRecorderLogger log)
    {
        _log = log;
        _ffmpeg = new FFmpegRecordingBackend(log);
    }

    public RecordingBackendPlan SelectInitial(RecordingRequest request)
        => SelectFirstAvailable(request, "initial backend selection", failedBackendId: null, inheritedNativeProbeReason: null);

    public RecordingBackendPlan SelectFallbackAfter(
        RecordingRequest request,
        string failedBackendId,
        string reason,
        string? inheritedNativeProbeReason = null)
    {
        return SelectFirstAvailable(
            request,
            reason,
            failedBackendId,
            CombineNativeProbeReason(inheritedNativeProbeReason, $"{failedBackendId} failed at start: {reason}"));
    }

    public RecordingBackendPlan SelectFFmpeg(
        RecordingRequest request,
        string reason,
        string? nativeRecorderProbeReason = null)
    {
        RecordingBackendProbeResult probe = _ffmpeg.Probe(request);
        string probeReason = string.IsNullOrWhiteSpace(reason)
            ? probe.Reason
            : $"{reason}; {probe.Reason}";
        return new RecordingBackendPlan(
            _ffmpeg,
            probe with { Reason = probeReason },
            nativeRecorderProbeReason);
    }

    public bool IsLastBackendInVendorChain(RecordingRequest request, string backendId)
    {
        List<IRecordingBackend> backends = EnumeratePreferredBackends(request).ToList();
        int index = backends.FindIndex(backend => string.Equals(backend.Id, backendId, StringComparison.OrdinalIgnoreCase));
        return index < 0 || index >= backends.Count - 1;
    }

    public string DescribeAvailableBackends(RecordingRequest request)
    {
        return string.Join(
            ", ",
            EnumeratePreferredBackends(request).Select(backend =>
            {
                RecordingBackendProbeResult probe = backend.Probe(request);
                return $"{backend.DisplayName}:{(probe.IsAvailable ? "available" : probe.Reason)}";
            }));
    }

    private RecordingBackendPlan SelectFirstAvailable(
        RecordingRequest request,
        string selectionReason,
        string? failedBackendId,
        string? inheritedNativeProbeReason)
    {
        string? nativeProbeReason = inheritedNativeProbeReason;
        bool skipUntilFailedBackend = !string.IsNullOrWhiteSpace(failedBackendId);

        foreach (IRecordingBackend backend in EnumeratePreferredBackends(request))
        {
            if (skipUntilFailedBackend)
            {
                if (string.Equals(backend.Id, failedBackendId, StringComparison.OrdinalIgnoreCase))
                    skipUntilFailedBackend = false;
                continue;
            }

            RecordingBackendProbeResult probe = backend.Probe(request);
            if (probe.IsAvailable)
            {
                string reason = string.Equals(selectionReason, "initial backend selection", StringComparison.OrdinalIgnoreCase)
                    ? probe.Reason
                    : $"{selectionReason}; {probe.Reason}";
                string? selectedNativeReason = IsNativeBackend(backend)
                    ? CombineNativeProbeReason(nativeProbeReason, probe.DiagnosticDetails ?? probe.Reason)
                    : nativeProbeReason;
                return new RecordingBackendPlan(
                    backend,
                    probe with { Reason = reason },
                    selectedNativeReason);
            }

            if (IsNativeBackend(backend))
            {
                string detail = $"{backend.Id}: {probe.DiagnosticDetails ?? probe.Reason}";
                nativeProbeReason = CombineNativeProbeReason(nativeProbeReason, detail);
                _log.Info($"[Record] {backend.DisplayName} unavailable: {probe.Reason}");
            }
        }

        return SelectFFmpeg(request, $"{selectionReason}; no preferred backend available", nativeProbeReason);
    }

    private IEnumerable<IRecordingBackend> EnumeratePreferredBackends(RecordingRequest request)
    {
        if (request.ForceFFmpegFallbackForTesting)
        {
            yield return _ffmpeg;
            yield break;
        }

        if (request.GameGraphicsDevice.Available)
        {
            if (string.Equals(request.GameGraphicsDevice.Vendor, "nvidia", StringComparison.OrdinalIgnoreCase))
            {
                yield return _nvenc;
            }
            else if (string.Equals(request.GameGraphicsDevice.Vendor, "amd", StringComparison.OrdinalIgnoreCase))
            {
                yield return _amf;
            }
            else if (string.Equals(request.GameGraphicsDevice.Vendor, "intel", StringComparison.OrdinalIgnoreCase))
            {
                yield return _qsv;
            }
        }

        yield return _ffmpeg;
    }

    private static bool IsNativeBackend(IRecordingBackend backend)
        => backend.PrefersD3D11TextureFrames;

    private static string? CombineNativeProbeReason(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first))
            return string.IsNullOrWhiteSpace(second) ? null : second;
        if (string.IsNullOrWhiteSpace(second))
            return first;

        return $"{first} | {second}";
    }
}
