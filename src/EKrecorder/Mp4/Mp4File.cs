using System.Globalization;
using System.Text;

namespace EKrecorder.Mp4;

/// <summary>One track of a regular MP4, every sample expanded from the index.</summary>
internal sealed class TrackTable
{
    public uint Id { get; init; }

    public string Handler { get; init; } = "";

    /// <summary>The sample entry's four-letter code (avc1, mp4a, ...).</summary>
    public string Codec { get; init; } = "";

    /// <summary>The H.264 profile (Baseline, Main, High), when the track is H.264.</summary>
    public string Profile { get; init; } = "";

    public uint Timescale { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }

    public int Channels { get; init; }

    public int SampleRate { get; init; }

    public long[] Offsets { get; init; } = [];

    public uint[] Sizes { get; init; } = [];

    public uint[] Durations { get; init; } = [];

    public int[] CompositionOffsets { get; init; } = [];

    public bool[] Sync { get; init; } = [];

    public List<MoovBuilder.Edit> Edits { get; init; } = new();

    public int Count => Sizes.Length;

    public long MediaDuration => Durations.Sum(d => (long)d);

    /// <summary>When the track starts playing, in seconds (an empty first edit delays it).</summary>
    public double StartSeconds(uint movieTimescale) =>
        Edits.Count > 0 && Edits[0].MediaTime < 0 && movieTimescale > 0 ? Edits[0].SegmentDuration / (double)movieTimescale : 0;
}

/// <summary>A track in a few numbers, for checks and the report.</summary>
internal sealed record TrackSummary(
    uint Id, string Handler, string Codec, string Profile, int Samples, int SyncSamples, double Seconds, double StartSeconds, long Bytes, double PeakBitsPerSecond,
    int Width, int Height, int Channels, int SampleRate)
{
    public double AverageBitsPerSecond => Seconds > 0 ? Bytes * 8 / Seconds : 0;
}

/// <summary>A regular MP4 read back and checked.</summary>
internal sealed record Mp4Summary(bool Ok, string? Problem, IReadOnlyList<TrackSummary> Tracks, long FileLength, uint MovieTimescale)
{
    public TrackSummary? Video => Tracks.FirstOrDefault(t => t.Handler == "vide");

    public TrackSummary? Audio => Tracks.FirstOrDefault(t => t.Handler == "soun");

    public static Mp4Summary Failed(string problem, long length) => new(false, problem, [], length, 0);
}

/// <summary>
/// Reads a regular MP4's index and checks it against the file: the tables agree with each other, and every sample
/// lies inside an mdat box. Fast even for long recordings (it reads the index, not the media).
/// </summary>
internal static class Mp4File
{
    public static Mp4Summary Summarize(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Summarize(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Mp4Summary.Failed($"the file could not be read: {ex.Message}", 0);
        }
    }

    public static Mp4Summary Summarize(Stream stream)
    {
        long length = stream.Length;
        try
        {
            (List<TrackTable> tracks, uint movieTimescale, string? problem) = ReadTracks(stream);
            if (problem is not null)
            {
                return Mp4Summary.Failed(problem, length);
            }

            var summaries = tracks.Select(t => Summarize(t, movieTimescale)).ToList();
            return new Mp4Summary(true, null, summaries, length, movieTimescale);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidDataException or IndexOutOfRangeException or OverflowException)
        {
            return Mp4Summary.Failed($"the index could not be read: {ex.Message}", length);
        }
    }

    /// <summary>Every track with every sample; a problem text instead when the file is not a usable regular MP4.</summary>
    public static (List<TrackTable> Tracks, uint MovieTimescale, string? Problem) ReadTracks(Stream stream)
    {
        long length = stream.Length;
        var top = new List<Box>();
        long position = 0;
        while (position + 8 <= length)
        {
            if (!BoxIo.TryReadHeader(stream, position, length, out Box box, out bool fits) || !fits)
            {
                return ([], 0, string.Create(CultureInfo.InvariantCulture, $"the file is damaged at byte {position:N0}"));
            }

            top.Add(box);
            position = box.End;
        }

        Box? moovBox = null;
        byte[] moov = [];
        foreach (Box box in top.Where(b => b.Type == "moov"))
        {
            byte[] data = BoxIo.ReadExactly(stream, box.Position, (int)box.Size);
            var root = new Box("moov", 0, data.Length, box.HeaderSize);
            if (BoxIo.Child(data, root, "mvex") is null)
            {
                moovBox = root;
                moov = data;
            }
        }

        if (moovBox is not { } moovRoot)
        {
            return ([], 0, top.Any(b => b.Type == "moof") ? "the file is still fragmented (no regular index)" : "the file has no index (moov)");
        }

        if (BoxIo.Child(moov, moovRoot, "mvhd") is not { } mvhd)
        {
            return ([], 0, "the index has no movie header");
        }

        int mvhdBody = (int)mvhd.PayloadPosition;
        uint movieTimescale = moov[mvhdBody] == 1 ? BoxIo.U32(moov, mvhdBody + 20) : BoxIo.U32(moov, mvhdBody + 12);
        List<Box> mdats = top.Where(b => b.Type == "mdat").ToList();
        var tracks = new List<TrackTable>();
        foreach (Box trak in BoxIo.Children(moov, (int)moovRoot.PayloadPosition, (int)moovRoot.End).Where(b => b.Type == "trak"))
        {
            (TrackTable? table, string? problem) = ReadTrack(moov, trak, mdats);
            if (problem is not null)
            {
                return ([], movieTimescale, problem);
            }

            tracks.Add(table!);
        }

        return tracks.Count == 0 ? ([], movieTimescale, "the index has no tracks") : (tracks, movieTimescale, null);
    }

    private static (TrackTable? Table, string? Problem) ReadTrack(byte[] moov, Box trak, List<Box> mdats)
    {
        Box? tkhd = BoxIo.Child(moov, trak, "tkhd");
        Box? mdia = BoxIo.Child(moov, trak, "mdia");
        Box? mdhd = mdia is { } a ? BoxIo.Child(moov, a, "mdhd") : null;
        Box? hdlr = mdia is { } b ? BoxIo.Child(moov, b, "hdlr") : null;
        Box? minf = mdia is { } c ? BoxIo.Child(moov, c, "minf") : null;
        Box? stbl = minf is { } d ? BoxIo.Child(moov, d, "stbl") : null;
        if (tkhd is null || mdhd is null || hdlr is null || stbl is null)
        {
            return (null, "a track is missing tkhd, mdhd, hdlr or stbl");
        }

        Box table = stbl.Value;
        int tkhdBody = (int)tkhd.Value.PayloadPosition;
        uint id = moov[tkhdBody] == 1 ? BoxIo.U32(moov, tkhdBody + 20) : BoxIo.U32(moov, tkhdBody + 12);
        int mdhdBody = (int)mdhd.Value.PayloadPosition;
        uint timescale = moov[mdhdBody] == 1 ? BoxIo.U32(moov, mdhdBody + 20) : BoxIo.U32(moov, mdhdBody + 12);
        string handler = Encoding.Latin1.GetString(moov, (int)hdlr.Value.PayloadPosition + 8, 4);

        Box? stsd = BoxIo.Child(moov, table, "stsd");
        Box? stts = BoxIo.Child(moov, table, "stts");
        Box? stsc = BoxIo.Child(moov, table, "stsc");
        Box? stsz = BoxIo.Child(moov, table, "stsz");
        Box? stco = BoxIo.Child(moov, table, "stco");
        Box? co64 = BoxIo.Child(moov, table, "co64");
        if (stsd is null || stts is null || stsc is null || stsz is null || (stco is null && co64 is null))
        {
            return (null, $"track {id} is missing a sample table");
        }

        // Sample sizes.
        int at = (int)stsz.Value.PayloadPosition;
        uint constantSize = BoxIo.U32(moov, at + 4);
        int count = checked((int)BoxIo.U32(moov, at + 8));
        var sizes = new uint[count];
        for (int i = 0; i < count; i++)
        {
            sizes[i] = constantSize != 0 ? constantSize : BoxIo.U32(moov, at + 12 + (4 * i));
        }

        // Durations.
        var durations = new uint[count];
        at = (int)stts.Value.PayloadPosition;
        uint runs = BoxIo.U32(moov, at + 4);
        int sample = 0;
        for (uint r = 0; r < runs; r++)
        {
            uint runCount = BoxIo.U32(moov, at + 8 + (int)(8 * r));
            uint delta = BoxIo.U32(moov, at + 12 + (int)(8 * r));
            for (uint k = 0; k < runCount; k++)
            {
                if (sample >= count)
                {
                    return (null, $"track {id}: stts lists more samples than stsz");
                }

                durations[sample++] = delta;
            }
        }

        if (sample != count)
        {
            return (null, $"track {id}: stts lists {sample} samples, stsz {count}");
        }

        // Composition offsets.
        var compositions = new int[count];
        if (BoxIo.Child(moov, table, "ctts") is { } ctts)
        {
            at = (int)ctts.PayloadPosition;
            uint entries = BoxIo.U32(moov, at + 4);
            sample = 0;
            for (uint r = 0; r < entries; r++)
            {
                uint runCount = BoxIo.U32(moov, at + 8 + (int)(8 * r));
                int offset = unchecked((int)BoxIo.U32(moov, at + 12 + (int)(8 * r)));
                for (uint k = 0; k < runCount && sample < count; k++)
                {
                    compositions[sample++] = offset;
                }
            }
        }

        // Sync samples (none listed: every sample is one).
        var sync = new bool[count];
        if (BoxIo.Child(moov, table, "stss") is { } stss)
        {
            at = (int)stss.PayloadPosition;
            uint entries = BoxIo.U32(moov, at + 4);
            for (uint r = 0; r < entries; r++)
            {
                uint number = BoxIo.U32(moov, at + 8 + (int)(4 * r));
                if (number >= 1 && number <= count)
                {
                    sync[number - 1] = true;
                }
            }
        }
        else
        {
            Array.Fill(sync, true);
        }

        // Chunk offsets.
        bool large = co64 is not null;
        Box offsetsBox = (co64 ?? stco)!.Value;
        at = (int)offsetsBox.PayloadPosition;
        int chunkCount = checked((int)BoxIo.U32(moov, at + 4));
        var chunkOffsets = new long[chunkCount];
        for (int i = 0; i < chunkCount; i++)
        {
            chunkOffsets[i] = large ? (long)BoxIo.U64(moov, at + 8 + (8 * i)) : BoxIo.U32(moov, at + 8 + (4 * i));
        }

        // Samples per chunk, then every sample's offset.
        at = (int)stsc.Value.PayloadPosition;
        uint stscEntries = BoxIo.U32(moov, at + 4);
        var offsets = new long[count];
        sample = 0;
        for (uint e = 0; e < stscEntries; e++)
        {
            int firstChunk = (int)BoxIo.U32(moov, at + 8 + (int)(12 * e)) - 1;
            uint perChunk = BoxIo.U32(moov, at + 12 + (int)(12 * e));
            int nextFirst = e + 1 < stscEntries ? (int)BoxIo.U32(moov, at + 8 + (int)(12 * (e + 1))) - 1 : chunkCount;
            if (firstChunk < 0 || nextFirst > chunkCount || nextFirst < firstChunk)
            {
                return (null, $"track {id}: stsc does not match the chunk list");
            }

            for (int chunk = firstChunk; chunk < nextFirst; chunk++)
            {
                long offset = chunkOffsets[chunk];
                long chunkStart = offset;
                for (uint k = 0; k < perChunk; k++)
                {
                    if (sample >= count)
                    {
                        return (null, $"track {id}: the chunks hold more samples than stsz lists");
                    }

                    offsets[sample] = offset;
                    offset += sizes[sample];
                    sample++;
                }

                if (perChunk > 0 && !InsideMdat(mdats, chunkStart, offset))
                {
                    return (null, string.Create(CultureInfo.InvariantCulture, $"track {id}: chunk {chunk + 1} (bytes {chunkStart:N0}-{offset:N0}) is not inside the media data"));
                }
            }
        }

        if (sample != count)
        {
            return (null, $"track {id}: the chunks hold {sample} samples, stsz lists {count}");
        }

        (string codec, string profile, int width, int height, int channels, int rate) = DescribeEntry(moov, stsd.Value);
        Box? edts = BoxIo.Child(moov, trak, "edts");
        List<MoovBuilder.Edit> edits = edts is { } e2 ? MoovBuilder.ReadEdits(BoxIo.Copy(moov, e2)) : new();
        return (new TrackTable
        {
            Id = id,
            Handler = handler,
            Codec = codec,
            Profile = profile,
            Timescale = timescale,
            Width = width,
            Height = height,
            Channels = channels,
            SampleRate = rate,
            Offsets = offsets,
            Sizes = sizes,
            Durations = durations,
            CompositionOffsets = compositions,
            Sync = sync,
            Edits = edits,
        }, null);
    }

    private static TrackSummary Summarize(TrackTable track, uint movieTimescale)
    {
        long bytes = track.Sizes.Sum(s => (long)s);
        double seconds = track.Timescale > 0 ? track.MediaDuration / (double)track.Timescale : 0;
        var perSecond = new Dictionary<long, long>();
        long time = 0;
        for (int i = 0; i < track.Count; i++)
        {
            long second = track.Timescale > 0 ? time / track.Timescale : 0;
            perSecond[second] = perSecond.GetValueOrDefault(second) + track.Sizes[i];
            time += track.Durations[i];
        }

        // The busiest whole second (the last, partial one only counts when it is the only one).
        long last = perSecond.Count > 0 ? perSecond.Keys.Max() : 0;
        double peak = perSecond.Where(p => p.Key < last || perSecond.Count == 1).Select(p => p.Value * 8.0).DefaultIfEmpty(0).Max();
        return new TrackSummary(
            track.Id, track.Handler, track.Codec, track.Profile, track.Count, track.Sync.Count(s => s), seconds, track.StartSeconds(movieTimescale), bytes, peak,
            track.Width, track.Height, track.Channels, track.SampleRate);
    }

    private static (string Codec, string Profile, int Width, int Height, int Channels, int Rate) DescribeEntry(byte[] moov, Box stsd)
    {
        int at = (int)stsd.PayloadPosition;
        if (stsd.PayloadSize < 16)
        {
            return ("", "", 0, 0, 0, 0);
        }

        int entry = at + 8;
        string codec = Encoding.Latin1.GetString(moov, entry + 4, 4);
        if (codec is "avc1" or "avc3" or "hvc1" or "hev1" && entry + 36 <= stsd.End)
        {
            string profile = "";
            var sampleEntry = new Box(codec, entry, BoxIo.U32(moov, entry), 8);
            if (sampleEntry.End <= stsd.End && BoxIo.Child(moov, sampleEntry, "avcC", skip: 78) is { } avcC && avcC.PayloadSize >= 4)
            {
                profile = moov[avcC.PayloadPosition + 1] switch
                {
                    66 => "Baseline",
                    77 => "Main",
                    100 => "High",
                    byte other => string.Create(CultureInfo.InvariantCulture, $"profile {other}"),
                };
            }

            return (codec, profile, BoxIo.U16(moov, entry + 32), BoxIo.U16(moov, entry + 34), 0, 0);
        }

        if (codec == "mp4a" && entry + 36 <= stsd.End)
        {
            return (codec, "", 0, 0, BoxIo.U16(moov, entry + 24), (int)(BoxIo.U32(moov, entry + 32) >> 16));
        }

        return (codec, "", 0, 0, 0, 0);
    }

    /// <summary>True when bytes <paramref name="start"/> to <paramref name="end"/> lie in one mdat's payload (<paramref name="mdats"/> in file order).</summary>
    private static bool InsideMdat(List<Box> mdats, long start, long end)
    {
        int low = 0;
        int high = mdats.Count - 1;
        int found = -1;
        while (low <= high)
        {
            int middle = low + ((high - low) / 2);
            if (mdats[middle].PayloadPosition <= start)
            {
                found = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return found >= 0 && end <= mdats[found].End;
    }
}
