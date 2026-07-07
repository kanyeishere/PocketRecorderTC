using System;
using System.Buffers.Binary;
using System.IO;

namespace Recorder.Recording;

internal static class MediaFileMetadata
{
    public static TimeSpan? TryReadMp4Duration(string path)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            return FindMvhdDuration(stream, stream.Length);
        }
        catch
        {
            return null;
        }
    }

    private static TimeSpan? FindMvhdDuration(Stream stream, long endOffset)
    {
        while (stream.Position + 8 <= endOffset)
        {
            if (!TryReadBoxHeader(stream, endOffset, out string type, out long payloadOffset, out long boxEnd))
                return null;

            if (type == "moov")
                return FindMvhdInMoov(stream, payloadOffset, boxEnd);

            stream.Position = boxEnd;
        }

        return null;
    }

    private static TimeSpan? FindMvhdInMoov(Stream stream, long payloadOffset, long boxEnd)
    {
        stream.Position = payloadOffset;
        while (stream.Position + 8 <= boxEnd)
        {
            if (!TryReadBoxHeader(stream, boxEnd, out string type, out long payloadStart, out long childEnd))
                return null;

            if (type == "mvhd")
                return ReadMvhdDuration(stream, payloadStart, childEnd);

            stream.Position = childEnd;
        }

        return null;
    }

    private static TimeSpan? ReadMvhdDuration(Stream stream, long payloadStart, long boxEnd)
    {
        stream.Position = payloadStart;
        if (boxEnd - payloadStart < 20)
            return null;

        int version = stream.ReadByte();
        if (version < 0)
            return null;

        stream.Position += 3;

        ulong duration;
        uint timescale;
        if (version == 1)
        {
            if (boxEnd - stream.Position < 28)
                return null;

            stream.Position += 16;
            timescale = ReadUInt32BigEndian(stream);
            duration = ReadUInt64BigEndian(stream);
        }
        else
        {
            if (boxEnd - stream.Position < 16)
                return null;

            stream.Position += 8;
            timescale = ReadUInt32BigEndian(stream);
            duration = ReadUInt32BigEndian(stream);
        }

        if (timescale == 0)
            return null;

        double seconds = duration / (double)timescale;
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0)
            return null;

        return TimeSpan.FromSeconds(seconds);
    }

    private static bool TryReadBoxHeader(
        Stream stream,
        long parentEnd,
        out string type,
        out long payloadOffset,
        out long boxEnd)
    {
        type = string.Empty;
        payloadOffset = stream.Position;
        boxEnd = stream.Position;

        long boxStart = stream.Position;
        uint size32 = ReadUInt32BigEndian(stream);
        type = ReadFourCc(stream);
        long headerSize = 8;
        long size = size32;

        if (size32 == 1)
        {
            if (stream.Position + 8 > parentEnd)
                return false;

            size = checked((long)ReadUInt64BigEndian(stream));
            headerSize = 16;
        }
        else if (size32 == 0)
        {
            size = parentEnd - boxStart;
        }

        if (size < headerSize)
            return false;

        payloadOffset = boxStart + headerSize;
        boxEnd = boxStart + size;
        if (boxEnd > parentEnd || boxEnd < payloadOffset)
            return false;

        return true;
    }

    private static uint ReadUInt32BigEndian(Stream stream)
    {
        Span<byte> buffer = stackalloc byte[4];
        stream.ReadExactly(buffer);
        return BinaryPrimitives.ReadUInt32BigEndian(buffer);
    }

    private static ulong ReadUInt64BigEndian(Stream stream)
    {
        Span<byte> buffer = stackalloc byte[8];
        stream.ReadExactly(buffer);
        return BinaryPrimitives.ReadUInt64BigEndian(buffer);
    }

    private static string ReadFourCc(Stream stream)
    {
        Span<byte> buffer = stackalloc byte[4];
        stream.ReadExactly(buffer);
        return System.Text.Encoding.ASCII.GetString(buffer);
    }
}
