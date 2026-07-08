using Recorder.Localization;

namespace Recorder.Recording;

internal static class RecordingPhaseExtensions
{
    public static string ToDisplayText(this RecordingPhase phase)
    {
        return phase switch
        {
            RecordingPhase.Idle => Loc.T("Phase.Idle"),
            RecordingPhase.Preparing => Loc.T("Phase.Preparing"),
            RecordingPhase.Recording => Loc.T("Phase.Recording"),
            RecordingPhase.Finalizing => Loc.T("Phase.Finalizing"),
            _ => phase.ToString(),
        };
    }
}
