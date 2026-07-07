using System;

namespace Recorder.Recording;

internal sealed record RecordingFinishedEventArgs(string OutputPath, TimeSpan Duration, bool Saved, bool Starred);
