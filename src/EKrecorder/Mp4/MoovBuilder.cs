namespace EKrecorder.Mp4;

/// <summary>
/// Builds the index (moov) of a regular MP4 for samples that stay exactly where they are in the file. The fragmented
/// movie's own boxes are reused (track headers, sample descriptions); the sample tables are written from the
/// fragments, and the timeline is kept exactly as it was: a track that started later than 0 gets an empty edit for
/// that delay, and an edit list the recording already had is kept.
/// </summary>
internal static class MoovBuilder
{
    /// <summary>An edit: <c>MediaTime</c> -1 is an empty edit (a delay).</summary>
    internal readonly record struct Edit(ulong SegmentDuration, long MediaTime, uint Rate);

    public static byte[] Build(MovieHeader init, IReadOnlyList<TrackSamples> tracks)
    {
        List<TrackSamples> used = tracks.Where(t => t.Count > 0).ToList();
        if (used.Count == 0)
        {
            throw new InvalidOperationException("There are no samples to index.");
        }

        uint movieTimescale = init.Timescale != 0 ? init.Timescale : 1000;
        var edits = used.ToDictionary(t => t.Id, t => EditsFor(t, movieTimescale));
        var durations = used.ToDictionary(t => t.Id, t => PresentationDuration(t, edits[t.Id], movieTimescale));
        ulong movieDuration = durations.Values.Max();
        long largestOffset = used.Max(t => t.Chunks.Count == 0 ? 0 : t.Chunks.Max(c => c.Offset));
        bool largeOffsets = largestOffset > uint.MaxValue;

        var writer = new BoxWriter();
        using (writer.Box("moov"))
        {
            WriteMovieHeader(writer, init.Mvhd, movieTimescale, movieDuration);
            foreach (TrackSamples track in used)
            {
                WriteTrack(writer, track, edits[track.Id], durations[track.Id], largeOffsets);
            }
        }

        return writer.ToArray();
    }

    /// <summary>
    /// The edits that keep the track's timeline as the fragments had it: a track whose first fragment starts after 0
    /// gets an empty edit for that delay, and an edit list the recording already had is kept. Without one, an
    /// encoder's reordering delay (B-frames: the first picture is shown a frame or two after its decode time) is
    /// removed with an edit that starts the track at its first picture, the way MP4 muxers normally do it, so the
    /// picture is not shown later than the sound recorded with it.
    /// </summary>
    internal static List<Edit> EditsFor(TrackSamples track, uint movieTimescale)
    {
        var edits = new List<Edit>();
        uint mediaTimescale = track.Header.Timescale;
        long start = track.FirstDecodeTime;
        if (start > 0)
        {
            edits.Add(new Edit(Rescale((ulong)start, mediaTimescale, movieTimescale), -1, 0x10000));
        }

        List<Edit> original = ReadEdits(track.Header.Edts);
        if (original.Count == 0)
        {
            long delay = ReorderingDelay(track);
            if (start > 0 || delay > 0)
            {
                long shown = Math.Max(0, track.MediaDuration - delay);
                edits.Add(new Edit(Rescale((ulong)shown, mediaTimescale, movieTimescale), delay, 0x10000));
            }

            return edits;
        }

        foreach (Edit edit in original)
        {
            if (edit.MediaTime < 0)
            {
                edits.Add(edit);
                continue;
            }

            // The fragments' media times count from the movie's start; in the regular file they count from the
            // track's first sample.
            long mediaTime = Math.Max(0, edit.MediaTime - start);
            ulong segment = edit.SegmentDuration;
            if (segment == 0)
            {
                // "Until the end of the media" (allowed in fragmented files): the real length now.
                long remaining = Math.Max(0, track.MediaDuration - mediaTime);
                segment = Rescale((ulong)remaining, mediaTimescale, movieTimescale);
            }

            edits.Add(new Edit(segment, mediaTime, edit.Rate));
        }

        return edits;
    }

    /// <summary>
    /// When the first picture is shown, counted from the first sample's decode time: the smallest presentation time
    /// among the first samples (reordering never reaches further than a few frames). 0 without composition offsets.
    /// </summary>
    internal static long ReorderingDelay(TrackSamples track)
    {
        if (!track.AnyCompositionOffset || track.Count == 0)
        {
            return 0;
        }

        long decode = 0;
        long earliest = long.MaxValue;
        for (int i = 0; i < track.Count && i < 32; i++)
        {
            earliest = Math.Min(earliest, decode + track.CompositionOffsets[i]);
            decode += track.Durations[i];
        }

        return Math.Max(0, earliest);
    }

    /// <summary>The entries of an edts box (header included), or none.</summary>
    internal static List<Edit> ReadEdits(byte[]? edts)
    {
        var edits = new List<Edit>();
        if (edts is null)
        {
            return edits;
        }

        var root = new Box("edts", 0, edts.Length, 8);
        if (BoxIo.Child(edts, root, "elst") is not { } elst || elst.PayloadSize < 8)
        {
            return edits;
        }

        int body = (int)elst.PayloadPosition;
        byte version = edts[body];
        uint count = BoxIo.U32(edts, body + 4);
        int at = body + 8;
        int entrySize = version == 1 ? 20 : 12;
        for (uint i = 0; i < count && at + entrySize <= elst.End; i++)
        {
            if (version == 1)
            {
                edits.Add(new Edit(BoxIo.U64(edts, at), (long)BoxIo.U64(edts, at + 8), BoxIo.U32(edts, at + 16)));
            }
            else
            {
                edits.Add(new Edit(BoxIo.U32(edts, at), unchecked((int)BoxIo.U32(edts, at + 4)), BoxIo.U32(edts, at + 8)));
            }

            at += entrySize;
        }

        return edits;
    }

    private static ulong PresentationDuration(TrackSamples track, List<Edit> edits, uint movieTimescale) =>
        edits.Count > 0
            ? edits.Aggregate(0UL, (sum, e) => sum + e.SegmentDuration)
            : Rescale((ulong)track.MediaDuration, track.Header.Timescale, movieTimescale);

    private static ulong Rescale(ulong value, uint from, uint to) =>
        from == to ? value : (ulong)Math.Round((decimal)value * to / from, MidpointRounding.AwayFromZero);

    private static void WriteMovieHeader(BoxWriter writer, byte[] mvhd, uint timescale, ulong duration)
    {
        (ulong creation, ulong modification, _, _, byte[] rest) = ReadTimedHeader(mvhd, idField: false);
        bool large = creation > uint.MaxValue || modification > uint.MaxValue || duration > uint.MaxValue;
        using (writer.FullBox("mvhd", large ? (byte)1 : (byte)0, 0))
        {
            WriteTimes(writer, large, creation, modification);
            writer.UInt32(timescale);
            WriteDuration(writer, large, duration);
            writer.Bytes(rest);
        }
    }

    private static void WriteTrack(BoxWriter writer, TrackSamples track, List<Edit> edits, ulong presentationDuration, bool largeOffsets)
    {
        TrackHeader header = track.Header;
        using (writer.Box("trak"))
        {
            (ulong creation, ulong modification, uint trackId, uint flags, byte[] rest) = ReadTimedHeader(header.Tkhd, idField: true);
            bool large = creation > uint.MaxValue || modification > uint.MaxValue || presentationDuration > uint.MaxValue;
            // A track that is not marked enabled and in the movie would be skipped by some players.
            using (writer.FullBox("tkhd", large ? (byte)1 : (byte)0, flags == 0 ? 3u : flags))
            {
                WriteTimes(writer, large, creation, modification);
                writer.UInt32(trackId);
                writer.UInt32(0);
                WriteDuration(writer, large, presentationDuration);
                writer.Bytes(rest);
            }

            if (edits.Count > 0)
            {
                WriteEdits(writer, edits);
            }

            using (writer.Box("mdia"))
            {
                (ulong mdCreation, ulong mdModification, _, _, byte[] mdRest) = ReadTimedHeader(header.Mdhd, idField: false);
                ulong mediaDuration = (ulong)track.MediaDuration;
                bool mdLarge = mdCreation > uint.MaxValue || mdModification > uint.MaxValue || mediaDuration > uint.MaxValue;
                using (writer.FullBox("mdhd", mdLarge ? (byte)1 : (byte)0, 0))
                {
                    WriteTimes(writer, mdLarge, mdCreation, mdModification);
                    writer.UInt32(header.Timescale);
                    WriteDuration(writer, mdLarge, mediaDuration);
                    writer.Bytes(mdRest);
                }

                writer.Bytes(header.Hdlr);
                using (writer.Box("minf"))
                {
                    WriteMediaHeader(writer, header);
                    WriteDataInformation(writer, header);
                    using (writer.Box("stbl"))
                    {
                        writer.Bytes(header.Stsd);
                        WriteTimeToSample(writer, track);
                        if (track.AnyCompositionOffset)
                        {
                            WriteCompositionOffsets(writer, track);
                        }

                        if (track.AnyNonSync)
                        {
                            WriteSyncSamples(writer, track);
                        }

                        WriteSampleToChunk(writer, track);
                        WriteSampleSizes(writer, track);
                        WriteChunkOffsets(writer, track, largeOffsets);
                    }
                }
            }
        }
    }

    private static void WriteEdits(BoxWriter writer, List<Edit> edits)
    {
        bool large = edits.Any(e => e.SegmentDuration > uint.MaxValue || e.MediaTime > int.MaxValue);
        using (writer.Box("edts"))
        using (writer.FullBox("elst", large ? (byte)1 : (byte)0, 0))
        {
            writer.UInt32((uint)edits.Count);
            foreach (Edit edit in edits)
            {
                if (large)
                {
                    writer.UInt64(edit.SegmentDuration);
                    writer.Int64(edit.MediaTime);
                }
                else
                {
                    writer.UInt32((uint)edit.SegmentDuration);
                    writer.Int32((int)edit.MediaTime);
                }

                writer.UInt32(edit.Rate);
            }
        }
    }

    private static void WriteMediaHeader(BoxWriter writer, TrackHeader header)
    {
        if (header.MediaHeader is { } existing)
        {
            writer.Bytes(existing);
        }
        else if (header.Handler == "soun")
        {
            using (writer.FullBox("smhd", 0, 0))
            {
                writer.UInt32(0); // balance + reserved
            }
        }
        else if (header.Handler == "vide")
        {
            using (writer.FullBox("vmhd", 0, 1))
            {
                writer.UInt64(0); // graphics mode + opcolor
            }
        }
        else
        {
            using (writer.FullBox("nmhd", 0, 0))
            {
            }
        }
    }

    private static void WriteDataInformation(BoxWriter writer, TrackHeader header)
    {
        if (header.Dinf is { } existing)
        {
            writer.Bytes(existing);
            return;
        }

        using (writer.Box("dinf"))
        using (writer.FullBox("dref", 0, 0))
        {
            writer.UInt32(1);
            using (writer.FullBox("url ", 0, 1))
            {
                // Flag 1: the media data is in this file.
            }
        }
    }

    private static void WriteTimeToSample(BoxWriter writer, TrackSamples track)
    {
        var runs = new List<(uint Count, uint Delta)>();
        foreach (uint duration in track.Durations)
        {
            if (runs.Count > 0 && runs[^1].Delta == duration)
            {
                runs[^1] = (runs[^1].Count + 1, duration);
            }
            else
            {
                runs.Add((1, duration));
            }
        }

        using (writer.FullBox("stts", 0, 0))
        {
            writer.UInt32((uint)runs.Count);
            foreach ((uint count, uint delta) in runs)
            {
                writer.UInt32(count);
                writer.UInt32(delta);
            }
        }
    }

    private static void WriteCompositionOffsets(BoxWriter writer, TrackSamples track)
    {
        var runs = new List<(uint Count, int Offset)>();
        bool negative = false;
        foreach (int offset in track.CompositionOffsets)
        {
            negative |= offset < 0;
            if (runs.Count > 0 && runs[^1].Offset == offset)
            {
                runs[^1] = (runs[^1].Count + 1, offset);
            }
            else
            {
                runs.Add((1, offset));
            }
        }

        using (writer.FullBox("ctts", negative ? (byte)1 : (byte)0, 0))
        {
            writer.UInt32((uint)runs.Count);
            foreach ((uint count, int offset) in runs)
            {
                writer.UInt32(count);
                writer.Int32(offset);
            }
        }
    }

    private static void WriteSyncSamples(BoxWriter writer, TrackSamples track)
    {
        var numbers = new List<uint>();
        for (int i = 0; i < track.Sync.Count; i++)
        {
            if (track.Sync[i])
            {
                numbers.Add((uint)(i + 1));
            }
        }

        using (writer.FullBox("stss", 0, 0))
        {
            writer.UInt32((uint)numbers.Count);
            foreach (uint number in numbers)
            {
                writer.UInt32(number);
            }
        }
    }

    private static void WriteSampleToChunk(BoxWriter writer, TrackSamples track)
    {
        var entries = new List<(uint FirstChunk, uint SamplesPerChunk)>();
        for (int i = 0; i < track.Chunks.Count; i++)
        {
            uint count = (uint)track.Chunks[i].Count;
            if (entries.Count == 0 || entries[^1].SamplesPerChunk != count)
            {
                entries.Add(((uint)(i + 1), count));
            }
        }

        uint description = track.Header.DefaultDescriptionIndex == 0 ? 1 : track.Header.DefaultDescriptionIndex;
        using (writer.FullBox("stsc", 0, 0))
        {
            writer.UInt32((uint)entries.Count);
            foreach ((uint firstChunk, uint samplesPerChunk) in entries)
            {
                writer.UInt32(firstChunk);
                writer.UInt32(samplesPerChunk);
                writer.UInt32(description);
            }
        }
    }

    private static void WriteSampleSizes(BoxWriter writer, TrackSamples track)
    {
        uint first = track.Sizes[0];
        bool constant = track.Sizes.TrueForAll(s => s == first);
        using (writer.FullBox("stsz", 0, 0))
        {
            writer.UInt32(constant ? first : 0);
            writer.UInt32((uint)track.Sizes.Count);
            if (!constant)
            {
                foreach (uint size in track.Sizes)
                {
                    writer.UInt32(size);
                }
            }
        }
    }

    private static void WriteChunkOffsets(BoxWriter writer, TrackSamples track, bool large)
    {
        using (writer.FullBox(large ? "co64" : "stco", 0, 0))
        {
            writer.UInt32((uint)track.Chunks.Count);
            foreach (Chunk chunk in track.Chunks)
            {
                if (large)
                {
                    writer.UInt64((ulong)chunk.Offset);
                }
                else
                {
                    writer.UInt32((uint)chunk.Offset);
                }
            }
        }
    }

    /// <summary>
    /// Reads mvhd, tkhd or mdhd (header included): creation and modification time, the track id (tkhd) or time scale
    /// (mvhd, mdhd), the flags, and everything after the duration field (the same in both versions).
    /// </summary>
    private static (ulong Creation, ulong Modification, uint IdOrTimescale, uint Flags, byte[] Tail) ReadTimedHeader(byte[] box, bool idField)
    {
        int body = BoxIo.U32(box, 0) == 1 ? 16 : 8;
        byte version = box[body];
        uint flags = BoxIo.U32(box, body) & 0xFFFFFF;
        ulong creation;
        ulong modification;
        uint value;
        int restStart;
        if (version == 1)
        {
            creation = BoxIo.U64(box, body + 4);
            modification = BoxIo.U64(box, body + 12);
            value = BoxIo.U32(box, body + 20);
            // tkhd has 4 reserved bytes between the track id and the 8-byte duration.
            restStart = body + (idField ? 36 : 32);
        }
        else
        {
            creation = BoxIo.U32(box, body + 4);
            modification = BoxIo.U32(box, body + 8);
            value = BoxIo.U32(box, body + 12);
            restStart = body + (idField ? 24 : 20);
        }

        return (creation, modification, value, flags, box.AsSpan(restStart).ToArray());
    }

    private static void WriteTimes(BoxWriter writer, bool large, ulong creation, ulong modification)
    {
        if (large)
        {
            writer.UInt64(creation);
            writer.UInt64(modification);
        }
        else
        {
            writer.UInt32((uint)creation);
            writer.UInt32((uint)modification);
        }
    }

    private static void WriteDuration(BoxWriter writer, bool large, ulong duration)
    {
        if (large)
        {
            writer.UInt64(duration);
        }
        else
        {
            writer.UInt32((uint)duration);
        }
    }
}
