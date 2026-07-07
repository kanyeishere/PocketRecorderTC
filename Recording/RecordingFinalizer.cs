using Recorder.Capture;
using Recorder.Diagnostics;
using Recorder.Telemetry;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Recorder.Recording;

internal sealed class RecordingFinalizer
{
    private readonly IRecorderLogger _log;

    public RecordingFinalizer(IRecorderLogger log)
    {
        _log = log;
    }

    public void Finalize(RecordingFinalizationJob job, bool waitForFinalize)
    {
        string finalizeMode = waitForFinalize ? "synchronously" : "in background";
        _log.Info($"[Record] Capture stop requested in {job.Stopwatch.ElapsedMilliseconds}ms; finalizing {finalizeMode}.");

        if (waitForFinalize)
        {
            FinalizeRecording(job);
            return;
        }

        var thread = new Thread(() => FinalizeRecording(job))
        {
            IsBackground = true,
            Name = "Recorder-Finalize",
        };
        thread.Start();
    }

    private void FinalizeRecording(RecordingFinalizationJob job)
    {
        Stopwatch finalizeSw = Stopwatch.StartNew();
        _log.Info($"[Record] Capture input paused after {job.Stopwatch.ElapsedMilliseconds}ms; finalizing writer.");

        StopAndDisposeAudioCapture(job.AudioCapture);

        try
        {
            job.Writer?.Stop(job.FinalDuration);
        }
        catch (Exception ex)
        {
            _log.Warning($"[Record] Writer stop failed: {ex.Message}");
        }

        try
        {
            job.VideoCapture?.Stop();
        }
        catch (Exception ex)
        {
            _log.Warning($"[Record] Video capture stop failed: {ex.Message}");
        }
        finally
        {
            if (job.DisposeVideoCapture)
                DisposeVideoCapture(job.VideoCapture);
        }

        _log.Info($"[Record] Capture stopped after {job.Stopwatch.ElapsedMilliseconds}ms.");

        try { job.Writer?.Dispose(); } catch { }

        job.MarkFinalized();

        string finalFrameDiagnostics = job.Writer?.FinalVideoDiagnostics ?? "writer=null";
        bool saved = job.Writer != null;
        string? finishedOutputPath = job.OutputPath;
        if (saved && job.Starred && !string.IsNullOrWhiteSpace(job.OutputPath) && File.Exists(job.OutputPath))
        {
            try
            {
                finishedOutputPath = RecordingFileNames.RenameStarred(job.OutputPath, starred: true);
            }
            catch (Exception ex)
            {
                _log.Warning($"[Record] Star rename failed: {ex.Message}");
            }
        }

        _log.Info($"[Record] Saved: {finishedOutputPath}, starred={job.Starred}, finalize={finalizeSw.ElapsedMilliseconds}ms");
        AmdRecordingDiagnosticLog.FinishSession($"saved={saved}, duration={job.FinalDuration}, finalizeMs={finalizeSw.ElapsedMilliseconds}, writerCreated={job.Writer != null}");
        RecordingDiagnosticLog.FinishSession(
            $"saved={saved}, duration={job.FinalDuration}, finalizeMs={finalizeSw.ElapsedMilliseconds}, writerCreated={job.Writer != null}",
            finalFrameDiagnostics);
        if (job.TelemetryContext != null)
            PocketBackendClient.QueueRecordingFinished(job.TelemetryContext, job.FinalDuration, saved, finalFrameDiagnostics);
        if (finishedOutputPath != null)
        {
            try
            {
                job.FinishedCallback?.Invoke(new RecordingFinishedEventArgs(finishedOutputPath, job.FinalDuration, job.Writer != null, job.Starred));
            }
            catch (Exception ex)
            {
                _log.Warning($"[Record] Finished callback failed: {ex.Message}");
            }
        }
    }

    private static void DisposeVideoCapture(VideoCaptureService? videoCapture)
    {
        try { videoCapture?.Dispose(); } catch { }
    }

    private static void StopAndDisposeAudioCapture(AudioCaptureService? audioCapture)
    {
        try { audioCapture?.Stop(); } catch { }
        try { audioCapture?.Dispose(); } catch { }
    }
}

internal sealed record RecordingFinalizationJob(
    VideoCaptureService? VideoCapture,
    bool DisposeVideoCapture,
    AudioCaptureService? AudioCapture,
    IOutputSink? Writer,
    string? OutputPath,
    Action<RecordingFinishedEventArgs>? FinishedCallback,
    TimeSpan FinalDuration,
    bool Starred,
    RecordingTelemetryContext? TelemetryContext,
    Stopwatch Stopwatch,
    Action MarkFinalized);
