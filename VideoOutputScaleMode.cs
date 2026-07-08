using Recorder.Localization;

namespace Recorder;

public enum VideoOutputScaleMode
{
    Original = 0,
    QuarterPixels = 1,
}

internal readonly record struct VideoOutputDimensions(int Width, int Height)
{
    public bool DiffersFrom(int sourceWidth, int sourceHeight)
        => Width != sourceWidth || Height != sourceHeight;
}

internal static class VideoOutputScale
{
    public static VideoOutputDimensions Resolve(int sourceWidth, int sourceHeight, VideoOutputScaleMode mode)
    {
        int safeWidth = Math.Max(1, sourceWidth);
        int safeHeight = Math.Max(1, sourceHeight);

        return mode switch
        {
            VideoOutputScaleMode.QuarterPixels => new(
                MakeEvenAtLeastTwo(safeWidth / 2),
                MakeEvenAtLeastTwo(safeHeight / 2)),
            _ => new(safeWidth, safeHeight),
        };
    }

    public static string ToDisplayText(VideoOutputScaleMode mode)
        => mode switch
        {
            VideoOutputScaleMode.QuarterPixels => Loc.T("VideoScale.Quarter"),
            _ => Loc.T("VideoScale.Original"),
        };

    private static int MakeEvenAtLeastTwo(int value)
    {
        int even = value & ~1;
        return Math.Max(2, even);
    }
}
