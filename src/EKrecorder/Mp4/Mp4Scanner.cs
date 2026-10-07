using System.Globalization;

namespace EKrecorder.Mp4;

/// <summary>A track as the fragmented movie header describes it, with the boxes the finished file reuses.</summary>
internal sealed class TrackHeader
{
    public uint Id { get; init; }

    public uint Timescale { get; init; }

    public string Handler { get; init; } = "";

    public byte[] Tkhd { get; init; } = [];

    public byte[]? Edts { get; init; }

    public byte[] Mdhd { get; init; } = [];

    public byte[] Hdlr { get; init; } = [];

    /// <summary>vmhd, smhd or another media header (null if there is none).</summary>
    public byte[]? MediaHeader { get; init; }

    public byte[]? Dinf { get; init; }

    public byte[] Stsd { get; init; } = [];

    // Defaults from the movie extends box (trex).
    public uint DefaultDescriptionIndex { get; set; } = 1;

    public uint DefaultDuration { get; set; }

    public uint DefaultSize { get; set; }

    public uint DefaultFlags { get; set; }
}

/// <summary>The fragmented movie's header (the moov written at the start, with mvex).</summary>
internal sealed class MovieHeader
{
    public byte[] Mvhd { get; init; } = [];

    public uint Timescale { get; init; }

    public bool Fragmented { get; init; }

    public List<TrackHeader> Tracks { get; } = new();

    public TrackHeader? Track(uint id) => Tracks.FirstOrDefault(t => t.Id == id);
}

/// <summary>A run of samples that lie one after the other in the file.</summary>
internal readonly record struct Chunk(long Offset, int FirstSample, int Count);

/// <summary>One track's samples in decode order, as found in the movie fragments.</summary>
internal sealed class TrackSamples
{
    public TrackSamples(TrackHeader header)
    {
        Header = header;
    }

    public TrackHeader Header { get; }

    public uint Id => Header.Id;

    public List<uint> Sizes { get; } = new();

    public List<uint> Durations { get; } = new();

    public List<int> CompositionOffsets { get; } = new();

    public List<bool> Sync { get; } = new();

    public List<Chunk> Chunks { get; } = new();

    /// <summary>Decode time of the first sample (from the first fragment's tfdt), in the track's time scale.</summary>
    public long FirstDecodeTime { get; set; }

    /// <summary>Decode time right after the last sample.</summary>
    public long DecodeTime { get; set; }

    public bool AnyCompositionOffset { get; set; }

    public bool AnyNonSync { get; set; }

    /// <summary>Samples skipped since the last one kept (not in the file, or in a damaged part), and their total duration.</summary>
    public int MissingSamples { get; set; }

    public long MissingDuration { get; set; }

    /// <summary>Where the first of the skipped samples was supposed to be (for the notes).</summary>
    public long MissingFrom { get; set; }

    public long Bytes { get; set; }

    public int Count => Sizes.Count;

    public long MediaDuration => DecodeTime - FirstDecodeTime;

    /// <summary>Where the last sample is in the file, and its size.</summary>
    public (long Offset, uint Size) LastSample()
    {
        Chunk chunk = Chunks[^1];
        long offset = chunk.Offset;
        for (int i = chunk.FirstSample; i < Count - 1; i++)
        {
            offset += Sizes[i];
        }

        return (offset, Sizes[^1]);
    }

    public void RemoveLast()
    {
        int last = Count - 1;
        Bytes -= Sizes[last];
        DecodeTime -= Durations[last];
        Sizes.RemoveAt(last);
        Durations.RemoveAt(last);
        CompositionOffsets.RemoveAt(last);
        Sync.RemoveAt(last);
        Chunk chunk = Chunks[^1];
        if (chunk.Count == 1)
        {
            Chunks.RemoveAt(Chunks.Count - 1);
        }
        else
        {
            Chunks[^1] = chunk with { Count = chunk.Count - 1 };
        }
    }
}

/// <summary>What kind of file a scan found.</summary>
internal enum Mp4Kind
{
    /// <summary>Not an MP4 we can use: no movie header at all.</summary>
    Unrecognized,

    /// <summary>A fragmented MP4 (finished or cut off by a crash); its samples are in <see cref="Mp4Layout.Tracks"/>.</summary>
    Fragmented,

    /// <summary>A fragmented MP4 whose conversion already wrote the regular movie header but did not finish.</summary>
    ConversionUnfinished,

    /// <summary>A regular MP4 (with its index), nothing left to do.</summary>
    Regular,
}

/// <summary>The result of scanning a recording: its boxes, its movie header and every usable sample.</summary>
internal sealed class Mp4Layout
{
    public long FileLength { get; init; }

    /// <summary>Complete top-level boxes, in file order.</summary>
    public List<Box> TopLevel { get; } = new();

    /// <summary>The first top-level box that is cut off by the end of the file (a crash), if any.</summary>
    public Box? CutOff { get; set; }

    /// <summary>End of the last complete top-level box.</summary>
    public long ValidEnd { get; set; }

    public Box? Ftyp { get; set; }

    public Box? InitMoov { get; set; }

    public MovieHeader? Init { get; set; }

    public Box? RegularMoov { get; set; }

    public List<TrackSamples> Tracks { get; } = new();

    public int Fragments { get; set; }

    public Mp4Kind Kind { get; set; }

    /// <summary>Where the file must end for the conversion (after the last usable sample's box).</summary>
    public long CutAt { get; set; }

    /// <summary>An mdat whose size field must be rewritten (cut off by a crash, or "to the end of the file").</summary>
    public (Box Mdat, long NewSize)? MdatSizeFix { get; set; }

    /// <summary>
    /// Boxes whose header the scan worked out around damage in the middle of the file: an mdat that really ends
    /// where the next fragment starts, or damaged bytes up to the next fragment, read as an mdat. Their headers
    /// are written as they are here when the file is converted.
    /// </summary>
    public List<Box> HeaderFixes { get; } = new();

    /// <summary>Things worth knowing about the file (gaps, cut-off data, unusual layout), for the log.</summary>
    public List<string> Notes { get; } = new();

    public bool HasSamples => Tracks.Any(t => t.Count > 0);
}

/// <summary>
/// Reads a recording without trusting it: a fragmented MP4 as the recorder writes it, the same file cut off at any
/// byte by a crash or a power cut, a file whose conversion was interrupted, or a regular MP4. It never writes.
/// </summary>
internal static class Mp4Scanner
{
    /// <summary>Movie headers larger than this are not read (a damaged size field).</summary>
    private const long MaxMoovSize = 512L * 1024 * 1024;

    private const uint TfhdBaseDataOffset = 0x1;
    private const uint TfhdDescriptionIndex = 0x2;
    private const uint TfhdDefaultDuration = 0x8;
    private const uint TfhdDefaultSize = 0x10;
    private const uint TfhdDefaultFlags = 0x20;
    private const uint TfhdDefaultBaseIsMoof = 0x20000;

    private const uint TrunDataOffset = 0x1;
    private const uint TrunFirstSampleFlags = 0x4;
    private const uint TrunDuration = 0x100;
    private const uint TrunSize = 0x200;
    private const uint TrunFlags = 0x400;
    private const uint TrunCompositionOffset = 0x800;

    /// <summary>sample_is_non_sync_sample in the ISO sample flags.</summary>
    private const uint NonSyncSample = 0x10000;

    /// <summary>How far past damage the next fragment is looked for (damage normally spans a fragment or two).</summary>
    private const long MaxResyncDistance = 64L * 1024 * 1024;

    /// <summary>Damaged places the scan steps over at most (a guard against a file that is nothing but damage).</summary>
    private const int MaxResyncs = 10_000;

    /// <summary>Samples per run when the run has no per-sample fields to check the count against.</summary>
    private const uint MaxSamplesPerRun = 1_000_000;

    /// <summary>Samples at the end of a track checked for zeros (the length of the file reached the disk, the data did not).</summary>
    private const int MaxZeroFilledSamples = 100_000;

    public static Mp4Layout Scan(Stream stream)
    {
        var layout = new Mp4Layout { FileLength = stream.Length };
        long position = 0;
        int resyncs = 0;
        while (position + 8 <= layout.FileLength)
        {
            bool valid = BoxIo.TryReadHeader(stream, position, layout.FileLength, out Box box, out bool fits);
            bool mdatToTheEnd = valid && box.Type == "mdat" && (box.OpenEnded || !fits);
            if (valid && fits && !mdatToTheEnd)
            {
                layout.TopLevel.Add(box);
                position = box.End;
                continue;
            }

            // The end of a recording cut off by a crash, or damage in the middle. When a fragment follows, the
            // recording goes on there and nothing after the damage is lost: the mdat before it (or the one that
            // claims to run to the end) really ends where that fragment starts, or the damaged bytes become an mdat.
            Box? previous = layout.TopLevel.Count > 0 ? layout.TopLevel[^1] : null;
            Box? mdat = mdatToTheEnd ? box : previous is { Type: "mdat" } before ? before : null;
            long searchFrom = mdat is { } m ? m.PayloadPosition : position + 8;
            long? next = resyncs < MaxResyncs
                ? FindNextFragment(stream, searchFrom, Math.Max(searchFrom, position) + MaxResyncDistance, layout.FileLength)
                : null;
            if (next is { } at)
            {
                resyncs++;
                Box fixedBox;
                if (mdat is { } data)
                {
                    fixedBox = data with { Size = at - data.Position, OpenEnded = false };
                    if (mdatToTheEnd)
                    {
                        layout.TopLevel.Add(fixedBox);
                    }
                    else
                    {
                        layout.TopLevel[^1] = fixedBox;
                    }

                    layout.Notes.Add(Invariant($"the mdat at byte {data.Position:N0} really ends at the next fragment (byte {at:N0}); the recording goes on there"));
                }
                else
                {
                    fixedBox = new Box("mdat", position, at - position, 8);
                    layout.TopLevel.Add(fixedBox);
                    layout.Notes.Add(Invariant($"bytes {position:N0} to {at:N0} are damaged; the recording goes on with the fragment after them"));
                }

                layout.HeaderFixes.RemoveAll(b => b.Position == fixedBox.Position);
                layout.HeaderFixes.Add(fixedBox);
                position = at;
                continue;
            }

            if (mdatToTheEnd && box.OpenEnded)
            {
                // The last mdat, "to the end of the file": kept, and given its real size when the file is converted.
                layout.TopLevel.Add(box);
                position = box.End;
                continue;
            }

            if (valid)
            {
                layout.CutOff = box;
                layout.Notes.Add(Invariant($"{box.Type} at byte {box.Position:N0} is cut off: {box.Size:N0} bytes long, but only {layout.FileLength - box.Position:N0} are in the file"));
            }
            else
            {
                layout.Notes.Add(Invariant($"no valid box at byte {position:N0}; {layout.FileLength - position:N0} byte(s) after it are not used"));
            }

            break;
        }

        layout.ValidEnd = position;
        foreach (Box box in layout.TopLevel)
        {
            if (box.Type == "ftyp" && layout.Ftyp is null)
            {
                layout.Ftyp = box;
            }
            else if (box.Type == "moov" && box.Size <= MaxMoovSize)
            {
                byte[] moov = BoxIo.ReadExactly(stream, box.Position, (int)box.Size);
                MovieHeader? header = ReadMovieHeader(moov, layout.Notes);
                if (header is null)
                {
                    continue;
                }

                if (header.Fragmented && layout.Init is null)
                {
                    layout.Init = header;
                    layout.InitMoov = box;
                }
                else if (!header.Fragmented)
                {
                    layout.RegularMoov = box;
                }
            }
        }

        bool anyFragment = layout.TopLevel.Any(b => b.Type == "moof");
        layout.Kind = layout.RegularMoov is not null
            ? (anyFragment || layout.Init is not null ? Mp4Kind.ConversionUnfinished : Mp4Kind.Regular)
            : layout.Init is not null ? Mp4Kind.Fragmented
            : Mp4Kind.Unrecognized;
        if (layout.Kind == Mp4Kind.Fragmented)
        {
            ReadFragments(stream, layout);
        }

        return layout;
    }

    /// <summary>Reads a moov box (header included): time scales, tracks and, for a fragmented movie, the trex defaults.</summary>
    public static MovieHeader? ReadMovieHeader(byte[] moov, List<string> notes)
    {
        var root = new Box("moov", 0, moov.Length, 8);
        if (moov.Length >= 16 && BoxIo.U32(moov, 0) == 1)
        {
            root = new Box("moov", 0, moov.Length, 16);
        }

        Box? mvhd = BoxIo.Child(moov, root, "mvhd");
        if (mvhd is null)
        {
            notes.Add("a moov box without mvhd");
            return null;
        }

        int mvhdBody = (int)mvhd.Value.PayloadPosition;
        uint timescale = moov[mvhdBody] == 1 ? BoxIo.U32(moov, mvhdBody + 20) : BoxIo.U32(moov, mvhdBody + 12);
        Box? mvex = BoxIo.Child(moov, root, "mvex");
        var header = new MovieHeader { Mvhd = BoxIo.Copy(moov, mvhd.Value), Timescale = timescale, Fragmented = mvex is not null };
        foreach (Box trak in BoxIo.Children(moov, (int)root.PayloadPosition, (int)root.End).Where(b => b.Type == "trak"))
        {
            TrackHeader? track = ReadTrack(moov, trak, notes);
            if (track is not null)
            {
                header.Tracks.Add(track);
            }
        }

        if (mvex is { } extends)
        {
            foreach (Box trex in BoxIo.Children(moov, (int)extends.PayloadPosition, (int)extends.End).Where(b => b.Type == "trex"))
            {
                int body = (int)trex.PayloadPosition;
                if (trex.PayloadSize < 24)
                {
                    continue;
                }

                TrackHeader? track = header.Track(BoxIo.U32(moov, body + 4));
                if (track is not null)
                {
                    track.DefaultDescriptionIndex = BoxIo.U32(moov, body + 8);
                    track.DefaultDuration = BoxIo.U32(moov, body + 12);
                    track.DefaultSize = BoxIo.U32(moov, body + 16);
                    track.DefaultFlags = BoxIo.U32(moov, body + 20);
                }
            }
        }

        return header;
    }

    private static TrackHeader? ReadTrack(byte[] moov, Box trak, List<string> notes)
    {
        Box? tkhd = BoxIo.Child(moov, trak, "tkhd");
        Box? mdia = BoxIo.Child(moov, trak, "mdia");
        Box? mdhd = mdia is { } m1 ? BoxIo.Child(moov, m1, "mdhd") : null;
        Box? hdlr = mdia is { } m2 ? BoxIo.Child(moov, m2, "hdlr") : null;
        Box? minf = mdia is { } m3 ? BoxIo.Child(moov, m3, "minf") : null;
        Box? stbl = minf is { } i1 ? BoxIo.Child(moov, i1, "stbl") : null;
        Box? stsd = stbl is { } s1 ? BoxIo.Child(moov, s1, "stsd") : null;
        if (tkhd is null || mdhd is null || hdlr is null || minf is null || stsd is null)
        {
            notes.Add("a track without tkhd, mdhd, hdlr, minf or stsd was left out");
            return null;
        }

        int tkhdBody = (int)tkhd.Value.PayloadPosition;
        uint id = moov[tkhdBody] == 1 ? BoxIo.U32(moov, tkhdBody + 20) : BoxIo.U32(moov, tkhdBody + 12);
        int mdhdBody = (int)mdhd.Value.PayloadPosition;
        uint timescale = moov[mdhdBody] == 1 ? BoxIo.U32(moov, mdhdBody + 20) : BoxIo.U32(moov, mdhdBody + 12);
        string handler = System.Text.Encoding.Latin1.GetString(moov, (int)hdlr.Value.PayloadPosition + 8, 4);
        Box? mediaHeader = BoxIo.Children(moov, (int)minf.Value.PayloadPosition, (int)minf.Value.End)
            .Cast<Box?>()
            .FirstOrDefault(b => b!.Value.Type is "vmhd" or "smhd" or "sthd" or "nmhd" or "hmhd");
        Box? dinf = BoxIo.Child(moov, minf.Value, "dinf");
        Box? edts = BoxIo.Child(moov, trak, "edts");
        if (timescale == 0)
        {
            notes.Add(Invariant($"track {id} has time scale 0 and was left out"));
            return null;
        }

        return new TrackHeader
        {
            Id = id,
            Timescale = timescale,
            Handler = handler,
            Tkhd = BoxIo.Copy(moov, tkhd.Value),
            Edts = edts is { } e ? BoxIo.Copy(moov, e) : null,
            Mdhd = BoxIo.Copy(moov, mdhd.Value),
            Hdlr = BoxIo.Copy(moov, hdlr.Value),
            MediaHeader = mediaHeader is { } h ? BoxIo.Copy(moov, h) : null,
            Dinf = dinf is { } d ? BoxIo.Copy(moov, d) : null,
            Stsd = BoxIo.Copy(moov, stsd.Value),
        };
    }

    /// <summary>
    /// Reads every movie fragment and keeps each sample whose bytes are really in the file, inside an mdat. A sample
    /// that is missing (cut off, or in a damaged part) is left out; when its track goes on after it, the sample before
    /// the gap is shown for the missing time, so everything after it keeps its place in the timeline. Samples at the
    /// end that are only zeros are left out too. Then works out where the file has to end.
    /// </summary>
    private static void ReadFragments(Stream stream, Mp4Layout layout)
    {
        MovieHeader init = layout.Init!;
        var tracks = new Dictionary<uint, TrackSamples>();
        foreach (TrackHeader header in init.Tracks)
        {
            var samples = new TrackSamples(header);
            tracks[header.Id] = samples;
            layout.Tracks.Add(samples);
        }

        // Where sample bytes may be: every complete mdat, and the cut-off one up to the end of the file.
        var mdats = layout.TopLevel.Where(b => b.Type == "mdat").ToList();
        if (layout.CutOff is { Type: "mdat" } cutOffMdat)
        {
            mdats.Add(cutOffMdat);
        }

        foreach (Box moof in layout.TopLevel.Where(b => b.Type == "moof"))
        {
            if (moof.Size > MaxMoovSize)
            {
                layout.Notes.Add(Invariant($"moof at byte {moof.Position:N0} is too large to be real; it is skipped"));
                continue;
            }

            byte[] data = BoxIo.ReadExactly(stream, moof.Position, (int)moof.Size);
            var root = new Box("moof", 0, moof.Size, moof.HeaderSize);
            layout.Fragments++;
            long nextTrafBase = moof.Position;
            foreach (Box traf in BoxIo.Children(data, (int)root.PayloadPosition, (int)root.End).Where(b => b.Type == "traf"))
            {
                nextTrafBase = ReadTrackFragment(data, traf, moof.Position, nextTrafBase, tracks, mdats, layout);
            }
        }

        long lastUsedEnd = 0;
        Box? lastUsedMdat = null;
        foreach (TrackSamples track in layout.Tracks)
        {
            if (track.MissingSamples > 0)
            {
                layout.Notes.Add(Invariant($"track {track.Id} ends after {track.Count} samples: the {track.MissingSamples} after it (from byte {track.MissingFrom:N0}) are not in the file"));
            }

            DropZeroFilledEnd(stream, track, layout);
            if (track.Count > 0)
            {
                (long offset, uint size) = track.LastSample();
                if (offset + size > lastUsedEnd)
                {
                    lastUsedEnd = offset + size;
                    lastUsedMdat = Containing(mdats, offset, size, layout.FileLength);
                }
            }
        }

        if (lastUsedMdat is not { } used)
        {
            layout.CutAt = 0;
            return;
        }

        if (used.Position + used.Size > layout.FileLength || used.OpenEnded)
        {
            // The last mdat that holds samples is cut off, or says "to the end of the file": it ends after its last
            // usable sample, and its size field says so.
            layout.CutAt = lastUsedEnd;
            layout.MdatSizeFix = (used, lastUsedEnd - used.Position);
        }
        else
        {
            layout.CutAt = used.End;
        }

        if (layout.CutAt < layout.FileLength)
        {
            layout.Notes.Add(Invariant($"the file ends after its last usable sample at byte {layout.CutAt:N0}; {layout.FileLength - layout.CutAt:N0} byte(s) after it are not used"));
        }
    }

    /// <summary>
    /// Samples at the end whose bytes are zeros are left out: after a power cut the file's length can reach the disk
    /// while its last data does not. A video sample starts with the length of its first NAL unit, which is never 0;
    /// a sound frame is never all zeros.
    /// </summary>
    private static void DropZeroFilledEnd(Stream stream, TrackSamples track, Mp4Layout layout)
    {
        int checkedBytes = track.Header.Handler switch
        {
            "vide" => 4,
            "soun" => 1 << 16,
            _ => 0,
        };
        if (checkedBytes == 0)
        {
            return;
        }

        int dropped = 0;
        while (track.Count > 0 && dropped < MaxZeroFilledSamples)
        {
            (long offset, uint size) = track.LastSample();
            int length = (int)Math.Min(size, (uint)checkedBytes);
            if (length == 0 || BoxIo.ReadExactly(stream, offset, length).AsSpan().ContainsAnyExcept((byte)0))
            {
                break;
            }

            track.RemoveLast();
            dropped++;
        }

        if (dropped > 0)
        {
            layout.Notes.Add(Invariant($"track {track.Id}: the last {dropped} sample(s) are only zeros (never written to the disk) and are left out"));
        }
    }

    /// <summary>Reads one traf; returns where the next traf's data starts by default.</summary>
    private static long ReadTrackFragment(
        byte[] data, Box traf, long moofPosition, long defaultBase, Dictionary<uint, TrackSamples> tracks, List<Box> mdats, Mp4Layout layout)
    {
        Box? tfhd = BoxIo.Child(data, traf, "tfhd");
        if (tfhd is not { } th || th.PayloadSize < 8)
        {
            layout.Notes.Add("a traf without tfhd was skipped");
            return defaultBase;
        }

        int at = (int)th.PayloadPosition;
        uint tfhdFlags = BoxIo.U32(data, at) & 0xFFFFFF;
        uint trackId = BoxIo.U32(data, at + 4);
        if (!tracks.TryGetValue(trackId, out TrackSamples? track))
        {
            layout.Notes.Add(Invariant($"a fragment of unknown track {trackId} was skipped"));
            return defaultBase;
        }

        TrackHeader header = track.Header;
        int needed = 8 + ((tfhdFlags & TfhdBaseDataOffset) != 0 ? 8 : 0) + ((tfhdFlags & TfhdDescriptionIndex) != 0 ? 4 : 0)
            + ((tfhdFlags & TfhdDefaultDuration) != 0 ? 4 : 0) + ((tfhdFlags & TfhdDefaultSize) != 0 ? 4 : 0) + ((tfhdFlags & TfhdDefaultFlags) != 0 ? 4 : 0);
        if (th.PayloadSize < needed)
        {
            layout.Notes.Add(Invariant($"a tfhd of track {trackId} is too short for its fields; that fragment was skipped"));
            return defaultBase;
        }

        int field = at + 8;
        long baseOffset = (tfhdFlags & TfhdDefaultBaseIsMoof) != 0 ? moofPosition : defaultBase;
        if ((tfhdFlags & TfhdBaseDataOffset) != 0)
        {
            baseOffset = (long)Math.Min(BoxIo.U64(data, field), long.MaxValue / 2);
            field += 8;
        }

        if ((tfhdFlags & TfhdDescriptionIndex) != 0)
        {
            field += 4;
        }

        uint defaultDuration = header.DefaultDuration;
        uint defaultSize = header.DefaultSize;
        uint defaultFlags = header.DefaultFlags;
        if ((tfhdFlags & TfhdDefaultDuration) != 0)
        {
            defaultDuration = BoxIo.U32(data, field);
            field += 4;
        }

        if ((tfhdFlags & TfhdDefaultSize) != 0)
        {
            defaultSize = BoxIo.U32(data, field);
            field += 4;
        }

        if ((tfhdFlags & TfhdDefaultFlags) != 0)
        {
            defaultFlags = BoxIo.U32(data, field);
        }

        // The fragment's decode time is applied with its first sample that is really in the file.
        long? decodeTime = null;
        if (BoxIo.Child(data, traf, "tfdt") is { } tfdt && tfdt.PayloadSize >= 8)
        {
            int body = (int)tfdt.PayloadPosition;
            decodeTime = data[body] == 1 && tfdt.PayloadSize >= 12
                ? (long)Math.Min(BoxIo.U64(data, body + 4), long.MaxValue / 2)
                : BoxIo.U32(data, body + 4);
        }

        long dataEnd = baseOffset;
        foreach (Box trun in BoxIo.Children(data, (int)traf.PayloadPosition, (int)traf.End).Where(b => b.Type == "trun"))
        {
            dataEnd = ReadTrackRun(data, trun, baseOffset, dataEnd, defaultDuration, defaultSize, defaultFlags, track, mdats, layout, ref decodeTime);
        }

        return dataEnd;
    }

    /// <summary>Reads one trun; returns where its data ends (where the next run starts by default).</summary>
    private static long ReadTrackRun(
        byte[] data, Box trun, long baseOffset, long previousEnd, uint defaultDuration, uint defaultSize, uint defaultFlags,
        TrackSamples track, List<Box> mdats, Mp4Layout layout, ref long? decodeTime)
    {
        int body = (int)trun.PayloadPosition;
        if (trun.PayloadSize < 8)
        {
            return previousEnd;
        }

        byte version = data[body];
        uint flags = BoxIo.U32(data, body) & 0xFFFFFF;
        uint count = BoxIo.U32(data, body + 4);
        int field = body + 8;
        long offset = previousEnd;
        int fixedFields = ((flags & TrunDataOffset) != 0 ? 4 : 0) + ((flags & TrunFirstSampleFlags) != 0 ? 4 : 0);
        if (trun.End - field < fixedFields)
        {
            layout.Notes.Add(Invariant($"a trun of track {track.Id} is too short for its fields; it is skipped"));
            return previousEnd;
        }

        if ((flags & TrunDataOffset) != 0)
        {
            offset = baseOffset + (int)BoxIo.U32(data, field);
            field += 4;
        }

        uint? firstFlags = null;
        if ((flags & TrunFirstSampleFlags) != 0)
        {
            firstFlags = BoxIo.U32(data, field);
            field += 4;
        }

        int perSample = (((flags & TrunDuration) != 0) ? 4 : 0) + (((flags & TrunSize) != 0) ? 4 : 0)
            + (((flags & TrunFlags) != 0) ? 4 : 0) + (((flags & TrunCompositionOffset) != 0) ? 4 : 0);
        long room = trun.End - field;
        if (perSample > 0)
        {
            // A run is exactly as long as its entries: a count that disagrees with the box size is damaged, and
            // the size (which the walk through the file already checked) decides.
            long capacity = room / perSample;
            if (count != capacity && (count > capacity || room % perSample == 0))
            {
                layout.Notes.Add(Invariant($"a trun of track {track.Id} says {count} samples but has room for {capacity}; {capacity} are read"));
                count = (uint)capacity;
            }
        }
        else if (count > MaxSamplesPerRun)
        {
            layout.Notes.Add(Invariant($"a trun of track {track.Id} says {count} samples; it is skipped"));
            return previousEnd;
        }

        int chunkFirst = 0;
        long chunkOffset = 0;
        int chunkCount = 0;
        for (uint i = 0; i < count; i++)
        {
            uint duration = defaultDuration;
            uint size = defaultSize;
            uint sampleFlags = i == 0 && firstFlags is { } first ? first : defaultFlags;
            int composition = 0;
            if ((flags & TrunDuration) != 0)
            {
                duration = BoxIo.U32(data, field);
                field += 4;
            }

            if ((flags & TrunSize) != 0)
            {
                size = BoxIo.U32(data, field);
                field += 4;
            }

            if ((flags & TrunFlags) != 0)
            {
                sampleFlags = BoxIo.U32(data, field);
                field += 4;
            }

            if ((flags & TrunCompositionOffset) != 0)
            {
                uint raw = BoxIo.U32(data, field);
                composition = version == 0 ? (int)Math.Min(raw, int.MaxValue) : unchecked((int)raw);
                field += 4;
            }

            Box? mdat = Containing(mdats, offset, size, layout.FileLength);
            if (mdat is null)
            {
                // Not in the file (cut off by a crash) or in a damaged part: left out, and the samples kept so far
                // end here as one run of bytes.
                AddChunk(track, chunkFirst, chunkOffset, chunkCount);
                chunkCount = 0;
                if (track.MissingSamples++ == 0)
                {
                    track.MissingFrom = offset;
                }

                track.MissingDuration += duration;
                if (decodeTime is { } expected)
                {
                    decodeTime = expected + duration;
                }
            }
            else
            {
                if (decodeTime is { } time)
                {
                    // The fragment's own decode time places this sample exactly (whatever was missing before it).
                    track.MissingSamples = 0;
                    track.MissingDuration = 0;
                    AlignDecodeTime(track, time, layout);
                    decodeTime = null;
                }
                else if (track.MissingSamples > 0)
                {
                    BridgeGap(track, layout);
                }

                if (chunkCount == 0)
                {
                    chunkFirst = track.Count;
                    chunkOffset = offset;
                }

                track.Sizes.Add(size);
                track.Durations.Add(duration);
                track.CompositionOffsets.Add(composition);
                bool sync = (sampleFlags & NonSyncSample) == 0;
                track.Sync.Add(sync);
                track.AnyNonSync |= !sync;
                track.AnyCompositionOffset |= composition != 0;
                track.DecodeTime += duration;
                track.Bytes += size;
                chunkCount++;
            }

            offset += size;
        }

        AddChunk(track, chunkFirst, chunkOffset, chunkCount);
        return offset;
    }

    private static void AddChunk(TrackSamples track, int first, long offset, int count)
    {
        if (count > 0)
        {
            track.Chunks.Add(new Chunk(offset, first, count));
        }
    }

    /// <summary>
    /// Samples were missing in the middle of a track: the sample before them is shown for their time (the picture
    /// holds, the sound is silent), so the samples after them keep their place and stay in sync with the other track.
    /// </summary>
    private static void BridgeGap(TrackSamples track, Mp4Layout layout)
    {
        long gap = track.MissingDuration;
        if (track.Count == 0)
        {
            // The track's first samples are missing: it starts that much later.
            track.FirstDecodeTime += gap;
            track.DecodeTime += gap;
        }
        else
        {
            uint last = track.Durations[^1];
            long lengthened = Math.Min(uint.MaxValue, last + gap);
            track.Durations[^1] = (uint)lengthened;
            track.DecodeTime += lengthened - last;
        }

        string what = track.Header.Handler == "soun" ? "the sound is silent" : "the picture holds";
        layout.Notes.Add(Invariant($"track {track.Id}: {track.MissingSamples} sample(s) from byte {track.MissingFrom:N0} are damaged or missing; {what} for {gap / (double)track.Header.Timescale:0.###} s there"));
        track.MissingSamples = 0;
        track.MissingDuration = 0;
    }

    /// <summary>The track's first fragment sets its start; later ones that disagree with the running total move the previous sample's end.</summary>
    private static void AlignDecodeTime(TrackSamples track, long time, Mp4Layout layout)
    {
        if (track.Count == 0)
        {
            track.FirstDecodeTime = time;
            track.DecodeTime = time;
            return;
        }

        long gap = time - track.DecodeTime;
        if (gap == 0)
        {
            return;
        }

        uint last = track.Durations[^1];
        long adjusted = Math.Max(1, Math.Min(uint.MaxValue, last + gap));
        track.Durations[^1] = (uint)adjusted;
        track.DecodeTime += adjusted - last;
        if (Math.Abs(gap) * 1000 > track.Header.Timescale)
        {
            layout.Notes.Add(Invariant($"track {track.Id}: the fragment at decode time {time} is {gap} ticks from where the previous one ended; the previous sample was {(gap > 0 ? "lengthened" : "shortened")}"));
        }
    }

    /// <summary>
    /// Where the next complete movie fragment starts (a moof box whose first child is its mfhd, which fits in the
    /// file), looking from <paramref name="from"/> up to <paramref name="to"/>; null if there is none.
    /// </summary>
    private static long? FindNextFragment(Stream stream, long from, long to, long fileLength)
    {
        const int Signature = 16; // size, "moof", mfhd size 16, "mfhd"
        const int Block = 1 << 20;
        var buffer = new byte[Block];
        long start = from;
        while (start <= to && fileLength - start >= Signature)
        {
            int count = (int)Math.Min(Block, fileLength - start);
            stream.Position = start;
            stream.ReadExactly(buffer.AsSpan(0, count));
            ReadOnlySpan<byte> data = buffer.AsSpan(0, count);
            int lastCandidate = (int)Math.Min(count - Signature, to - start);
            int searchAt = 4;
            while (searchAt <= lastCandidate + 4)
            {
                int found = data[searchAt..].IndexOf("moof"u8);
                if (found < 0)
                {
                    break;
                }

                int candidate = searchAt + found - 4;
                if (candidate > lastCandidate)
                {
                    break;
                }

                uint size = BoxIo.U32(data, candidate);
                if (BoxIo.U32(data, candidate + 8) == 16 && data.Slice(candidate + 12, 4).SequenceEqual("mfhd"u8)
                    && size >= 24 && size <= MaxMoovSize && start + candidate + size <= fileLength)
                {
                    return start + candidate;
                }

                searchAt += found + 1;
            }

            // The next block starts right after the last position checked in this one.
            start += lastCandidate + 1;
        }

        return null;
    }

    /// <summary>The mdat whose payload holds the whole sample, or null. <paramref name="mdats"/> is in file order.</summary>
    private static Box? Containing(List<Box> mdats, long offset, uint size, long fileLength)
    {
        // The last mdat whose payload starts at or before the sample.
        int low = 0;
        int high = mdats.Count - 1;
        int found = -1;
        while (low <= high)
        {
            int middle = low + ((high - low) / 2);
            if (mdats[middle].PayloadPosition <= offset)
            {
                found = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        if (found < 0)
        {
            return null;
        }

        Box mdat = mdats[found];
        return offset + size <= Math.Min(mdat.End, fileLength) ? mdat : null;
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
