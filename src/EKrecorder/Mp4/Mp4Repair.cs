using System.Globalization;
using System.Text;

namespace EKrecorder.Mp4;

/// <summary>What <see cref="Mp4Repair.Run"/> did.</summary>
internal enum RepairOutcome
{
    /// <summary>Already a regular MP4: nothing was changed.</summary>
    AlreadyRegular,

    /// <summary>A fragmented recording was turned into a regular MP4 (possibly after a crash cut it off).</summary>
    Converted,

    /// <summary>A conversion that had been interrupted was finished.</summary>
    Finished,

    /// <summary>Nothing playable was found; the file was left exactly as it was.</summary>
    Unrecoverable,
}

internal sealed record RepairResult(RepairOutcome Outcome, string Detail, long FileLengthBefore, long FileLengthAfter, IReadOnlyList<string> Notes)
{
    public bool Playable => Outcome != RepairOutcome.Unrecoverable;
}

/// <summary>
/// Turns a recording into a regular MP4 in place, without copying or re-encoding anything. The recorder writes a
/// fragmented MP4, which stays playable up to its last complete fragment whatever happens to the PC. When it is
/// finished (or after a crash) this appends the regular index (moov) after the media, turns the fragment headers into
/// padding (free boxes) and sets the file type. The sample bytes never move.
/// <para>
/// Every step is safe to interrupt and repeat: the index is written and flushed to disk before any fragment header
/// is touched, and a file that already has it only gets the remaining steps. A crash-cut tail is first trimmed to
/// the last complete sample, so the index always comes right after usable media.
/// </para>
/// </summary>
internal static class Mp4Repair
{
    /// <summary>Box types that only mean something in a fragmented file; they become padding.</summary>
    private static readonly HashSet<string> FragmentOnly = ["moof", "mfra", "sidx", "styp", "ssix", "prft", "emsg"];

    private static readonly string[] RegularBrands = ["isom", "iso2", "avc1", "mp41"];

    /// <summary>
    /// Repairs or converts <paramref name="path"/>. <paramref name="beforeStep"/> (tests) is called with the step
    /// number before each write: 1 fix the cut-off mdat's size, 2 trim the tail, 3 append the index, 4 turn fragment
    /// boxes into padding, 5 set the file type.
    /// </summary>
    public static RepairResult Run(string path, Action<int>? beforeStep = null)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, 1 << 16, FileOptions.None);
        long before = stream.Length;
        Mp4Layout layout = Mp4Scanner.Scan(stream);
        switch (layout.Kind)
        {
            case Mp4Kind.Regular:
                FixFileType(stream, layout, beforeStep);
                return new RepairResult(RepairOutcome.AlreadyRegular, "a regular MP4", before, stream.Length, layout.Notes);

            case Mp4Kind.ConversionUnfinished:
                RenameFragmentBoxes(stream, layout, layout.RegularMoov!.Value.Position, beforeStep);
                FixFileType(stream, layout, beforeStep);
                return new RepairResult(RepairOutcome.Finished, "an interrupted conversion was finished", before, stream.Length, layout.Notes);

            case Mp4Kind.Fragmented when layout.HasSamples:
                return Convert(stream, layout, before, beforeStep);

            default:
                string why = layout.Kind == Mp4Kind.Fragmented
                    ? "the recording has no complete video or audio sample"
                    : "no MP4 movie header was found";
                return new RepairResult(RepairOutcome.Unrecoverable, why, before, before, layout.Notes);
        }
    }

    private static RepairResult Convert(FileStream stream, Mp4Layout layout, long before, Action<int>? beforeStep)
    {
        byte[] moov = MoovBuilder.Build(layout.Init!, layout.Tracks);

        // 1. A cut-off (or "to the end of the file") mdat gets its real size, so the index can follow it.
        if (layout.MdatSizeFix is ({ } mdat, long newSize))
        {
            beforeStep?.Invoke(1);
            WriteBoxSize(stream, mdat, newSize);
            stream.Flush(flushToDisk: true);
        }

        // 2. Whatever follows the last usable sample (a half-written fragment, garbage after a power cut) goes.
        if (layout.CutAt < stream.Length)
        {
            beforeStep?.Invoke(2);
            stream.SetLength(layout.CutAt);
            stream.Flush(flushToDisk: true);
        }

        // 3. The regular index, after the media. Once this is on disk the file no longer needs its fragment headers.
        beforeStep?.Invoke(3);
        stream.Position = layout.CutAt;
        stream.Write(moov);
        stream.Flush(flushToDisk: true);

        // 4 and 5. The fragment headers become padding; the file type says "regular MP4". Only boxes before the new
        // index: anything the scan saw after the cut is gone (the index now sits where it was).
        RenameFragmentBoxes(stream, layout, layout.CutAt, beforeStep);
        FixFileType(stream, layout, beforeStep);

        string tracks = string.Join(", ", layout.Tracks.Where(t => t.Count > 0).Select(t =>
            string.Create(CultureInfo.InvariantCulture, $"{t.Header.Handler} {t.Count} samples, {t.MediaDuration / (double)t.Header.Timescale:0.000} s")));
        string detail = $"{layout.Fragments} fragment(s): {tracks}";
        if (layout.CutOff is not null || layout.CutAt < before)
        {
            detail += string.Create(CultureInfo.InvariantCulture, $"; {before - layout.CutAt:N0} byte(s) after the last complete sample were not usable");
        }

        return new RepairResult(RepairOutcome.Converted, detail, before, stream.Length, layout.Notes);
    }

    /// <summary>
    /// Step 4: the fragmented movie header and every fragment-only box that ends by <paramref name="limit"/> (where the
    /// regular index starts) become free boxes of the same size.
    /// </summary>
    private static void RenameFragmentBoxes(FileStream stream, Mp4Layout layout, long limit, Action<int>? beforeStep)
    {
        var targets = layout.TopLevel
            .Where(b => b.End <= limit && (FragmentOnly.Contains(b.Type) || (layout.InitMoov is { } init && b.Position == init.Position)))
            .ToList();
        if (targets.Count == 0)
        {
            return;
        }

        beforeStep?.Invoke(4);
        byte[] free = Encoding.ASCII.GetBytes("free");
        foreach (Box box in targets)
        {
            stream.Position = box.Position + 4;
            stream.Write(free);
        }

        stream.Flush(flushToDisk: true);
    }

    /// <summary>Step 5: brands of a regular MP4, in the ftyp box's existing space.</summary>
    private static void FixFileType(FileStream stream, Mp4Layout layout, Action<int>? beforeStep)
    {
        if (layout.Ftyp is not { } ftyp || ftyp.PayloadSize < 8)
        {
            return;
        }

        byte[] current = BoxIo.ReadExactly(stream, ftyp.PayloadPosition, (int)ftyp.PayloadSize);
        int brandCount = (int)((ftyp.PayloadSize - 8) / 4);
        var wanted = new byte[ftyp.PayloadSize];
        Encoding.ASCII.GetBytes(RegularBrands[0]).CopyTo(wanted, 0);
        wanted[6] = 0x02; // minor version 512
        for (int i = 0; i < brandCount; i++)
        {
            Encoding.ASCII.GetBytes(RegularBrands[i % RegularBrands.Length]).CopyTo(wanted, 8 + (4 * i));
        }

        if (current.AsSpan().SequenceEqual(wanted) || !LooksFragmented(current, brandCount))
        {
            return;
        }

        beforeStep?.Invoke(5);
        stream.Position = ftyp.PayloadPosition;
        stream.Write(wanted);
        stream.Flush(flushToDisk: true);
    }

    /// <summary>True when the brands are not the plain regular-MP4 ones (anything else is replaced, a regular muxer's own brands are kept).</summary>
    private static bool LooksFragmented(byte[] payload, int brandCount)
    {
        var brands = new List<string> { Encoding.ASCII.GetString(payload, 0, 4) };
        for (int i = 0; i < brandCount; i++)
        {
            brands.Add(Encoding.ASCII.GetString(payload, 8 + (4 * i), 4));
        }

        return brands.Any(b => b is "iso5" or "iso6" or "iso8" or "dash" or "msdh" or "msix" or "cmfc" or "cmff" or "cmf2");
    }

    private static void WriteBoxSize(FileStream stream, Box box, long newSize)
    {
        stream.Position = box.Position;
        if (box.HeaderSize == 16)
        {
            stream.Write([0, 0, 0, 1]);
            stream.Position = box.Position + 8;
            Span<byte> large = stackalloc byte[8];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(large, (ulong)newSize);
            stream.Write(large);
            return;
        }

        if (newSize > uint.MaxValue)
        {
            throw new InvalidOperationException("An mdat with a 32-bit size field cannot grow past 4 GB.");
        }

        Span<byte> size = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(size, (uint)newSize);
        stream.Write(size);
    }
}
