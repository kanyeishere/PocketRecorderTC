using Recorder.Capture;
using Recorder.Diagnostics;
using System;
using System.Diagnostics;
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
            DisposeVideoCapture(job.VideoCapture);
        }

        _log.Info($"[Record] Capture stopped after {job.Stopwatch.ElapsedMilliseconds}ms.");

        try { job.Writer?.Dispose(); } catch { }

        job.MarkFinalized();

        _log.Info($"[Record] Saved: {job.OutputPath}, finalize={finalizeSw.ElapsedMilliseconds}ms");
        AmdRecordingDiagnosticLog.FinishSession($"saved=true, duration={job.FinalDuration}, finalizeMs={finalizeSw.ElapsedMilliseconds}, writerCreated={job.Writer != null}");
        RecordingDiagnosticLog.FinishSession($"saved=true, duration={job.FinalDuration}, finalizeMs={finalizeSw.ElapsedMilliseconds}, writerCreated={job.Writer != null}");
        if (job.OutputPath != null)
        {
            try
            {
                job.FinishedCallback?.Invoke(new RecordingFinishedEventArgs(job.OutputPath, job.FinalDuration, job.Writer != null));
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
    AudioCaptureService? AudioCapture,
    IOutputSink? Writer,
    string? OutputPath,
    Action<RecordingFinishedEventArgs>? FinishedCallback,
    TimeSpan FinalDuration,
    Stopwatch Stopwatch,
    Action MarkFinalized);
