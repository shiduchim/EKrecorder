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
/// Every step is safe to interrupt and repeat, and nothing is deleted. What follows the last usable sample (a
/// crash-cut fragment, zeros after a power cut) becomes padding. The index is written as padding and flushed, and
/// only then marked as the index (four bytes), so a half-written index is never taken for a real one; it is checked
/// before any fragment header is touched, and a file that already has it only gets the remaining steps.
/// </para>
/// </summary>
internal static class Mp4Repair
{
    /// <summary>Box types that only mean something in a fragmented file; they become padding.</summary>
    private static readonly HashSet<string> FragmentOnly = ["moof", "mfra", "sidx", "styp", "ssix", "prft", "emsg"];

    private static readonly string[] RegularBrands = ["isom", "iso2", "avc1", "mp41"];

    /// <summary>Boxes of the index that hold only other boxes.</summary>
    private static readonly HashSet<string> Containers = ["trak", "edts", "mdia", "minf", "dinf", "stbl"];

    /// <summary>
    /// Repairs or converts <paramref name="path"/>. <paramref name="beforeStep"/> (tests) is called with the step
    /// number before each write: 1 fix box headers (a cut-off or damaged mdat), 2 turn the unusable tail into padding,
    /// 3 append the index (as padding), 4 mark it as the index, 5 turn fragment boxes into padding, 6 set the file type.
    /// </summary>
    public static RepairResult Run(string path, Action<int>? beforeStep = null)
    {
        // No read buffer: the scan jumps from header to header, and a buffer would read far more than it uses.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, 1, FileOptions.None);
        return Run(stream, beforeStep, retried: false);
    }

    private static RepairResult Run(FileStream stream, Action<int>? beforeStep, bool retried)
    {
        long before = stream.Length;
        Mp4Layout layout = Mp4Scanner.Scan(stream);
        switch (layout.Kind)
        {
            case Mp4Kind.Regular:
                FixFileType(stream, layout, beforeStep);
                return new RepairResult(RepairOutcome.AlreadyRegular, "a regular MP4", before, stream.Length, layout.Notes);

            case Mp4Kind.ConversionUnfinished when CheckIndex(stream, layout.RegularMoov!.Value) is { } problem:
                if (retried || layout.Init is null)
                {
                    return new RepairResult(RepairOutcome.Unrecoverable, $"the index is not usable ({problem})", before, before, layout.Notes);
                }

                // The disk lost part of the index: it becomes padding, and the index is built again from the fragments.
                layout.Notes.Add($"the index written before was not usable ({problem}); it is built again");
                RenameBox(stream, layout.RegularMoov!.Value, "free");
                stream.Flush(flushToDisk: true);
                RepairResult again = Run(stream, beforeStep, retried: true);
                return again with { FileLengthBefore = before, Notes = [.. layout.Notes, .. again.Notes] };

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

        // 1. Headers the scan worked out: a cut-off (or "to the end of the file") mdat ends after its last usable
        //    sample, and around damage an mdat ends where the next fragment starts. Only boxes before the cut matter.
        var fixes = layout.HeaderFixes.Where(b => b.End <= layout.CutAt).ToList();
        if (layout.MdatSizeFix is ({ } mdat, long newSize))
        {
            fixes.Add(mdat with { Size = newSize, OpenEnded = false });
        }

        if (fixes.Count > 0)
        {
            beforeStep?.Invoke(1);
            foreach (Box box in fixes)
            {
                WriteBoxHeader(stream, box);
            }

            stream.Flush(flushToDisk: true);
        }

        // 2. Whatever follows the last usable sample (a half-written fragment, zeros or garbage after a power cut)
        //    becomes padding, so nothing is deleted. Fewer than 8 bytes cannot hold a box header; those are cut off.
        long tail = stream.Length - layout.CutAt;
        if (tail > 0)
        {
            beforeStep?.Invoke(2);
            if (tail >= 8)
            {
                WriteBoxHeader(stream, new Box("free", layout.CutAt, tail, tail > uint.MaxValue ? 16 : 8));
            }
            else
            {
                stream.SetLength(layout.CutAt);
            }

            stream.Flush(flushToDisk: true);
        }

        // 3. The regular index after everything, written as padding and flushed to disk...
        long indexAt = stream.Length;
        beforeStep?.Invoke(3);
        "free"u8.CopyTo(moov.AsSpan(4));
        stream.Position = indexAt;
        stream.Write(moov);
        stream.Flush(flushToDisk: true);

        // 4. ...and only then marked as the index: a power cut can never leave a half-written one.
        beforeStep?.Invoke(4);
        var index = new Box("moov", indexAt, moov.Length, 8);
        RenameBox(stream, index, "moov");
        stream.Flush(flushToDisk: true);

        // The fragment headers are only touched once the index checks out.
        if (CheckIndex(stream, index) is { } problem)
        {
            RenameBox(stream, index, "free");
            stream.Flush(flushToDisk: true);
            return new RepairResult(RepairOutcome.Unrecoverable, $"the new index did not check out ({problem}); the file was left as a fragmented MP4", before, stream.Length, layout.Notes);
        }

        // 5 and 6. The fragment headers become padding; the file type says "regular MP4". Only boxes before the cut:
        // the rest is inside the padding of step 2.
        RenameFragmentBoxes(stream, layout, layout.CutAt, beforeStep);
        FixFileType(stream, layout, beforeStep);

        string tracks = string.Join(", ", layout.Tracks.Where(t => t.Count > 0).Select(t =>
            string.Create(CultureInfo.InvariantCulture, $"{t.Header.Handler} {t.Count} samples, {t.MediaDuration / (double)t.Header.Timescale:0.000} s")));
        string detail = $"{layout.Fragments} fragment(s): {tracks}";
        if (layout.CutOff is not null || layout.CutAt < before)
        {
            detail += string.Create(CultureInfo.InvariantCulture, $"; {before - layout.CutAt:N0} byte(s) after the last complete sample were not usable");
        }

        if (layout.HeaderFixes.Count > 0)
        {
            detail += string.Create(CultureInfo.InvariantCulture, $"; stepped over damage in {layout.HeaderFixes.Count} place(s)");
        }

        return new RepairResult(RepairOutcome.Converted, detail, before, stream.Length, layout.Notes);
    }

    /// <summary>
    /// Null when the regular index <paramref name="index"/> is whole (every box in it lines up exactly, so nothing
    /// was lost to zeros) and every sample it lists is inside an mdat; else why not.
    /// </summary>
    private static string? CheckIndex(FileStream stream, Box index)
    {
        try
        {
            if (index.Size > int.MaxValue)
            {
                return "the index is too large";
            }

            byte[] data = BoxIo.ReadExactly(stream, index.Position, (int)index.Size);
            if (!LinesUp(data, new Box("moov", 0, data.Length, index.HeaderSize)))
            {
                return "part of the index is missing";
            }

            return Mp4File.ReadTracks(stream).Problem;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidDataException or IndexOutOfRangeException or OverflowException)
        {
            return ex.Message;
        }
    }

    /// <summary>True when the boxes inside <paramref name="box"/> (and inside its container boxes) fill it exactly.</summary>
    private static bool LinesUp(byte[] data, Box box)
    {
        long end = box.PayloadPosition;
        foreach (Box child in BoxIo.Children(data, (int)box.PayloadPosition, (int)box.End))
        {
            if (Containers.Contains(child.Type) && !LinesUp(data, child))
            {
                return false;
            }

            end = child.End;
        }

        return end == box.End;
    }

    /// <summary>
    /// Step 5: the fragmented movie header and every fragment-only box that ends by <paramref name="limit"/> (where the
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

        beforeStep?.Invoke(5);
        foreach (Box box in targets)
        {
            RenameBox(stream, box, "free");
        }

        stream.Flush(flushToDisk: true);
    }

    /// <summary>Step 6: brands of a regular MP4, in the ftyp box's existing space.</summary>
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

        beforeStep?.Invoke(6);
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

    private static void RenameBox(FileStream stream, Box box, string type)
    {
        stream.Position = box.Position + 4;
        stream.Write(Encoding.ASCII.GetBytes(type));
    }

    /// <summary>Writes the header of <paramref name="box"/> (size and type) in its place; 16 bytes with a 64-bit size when its header has room for that.</summary>
    private static void WriteBoxHeader(FileStream stream, Box box)
    {
        Span<byte> header = stackalloc byte[16];
        Encoding.ASCII.GetBytes(box.Type, header.Slice(4, 4));
        if (box.HeaderSize == 16)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header, 1);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(header[8..], (ulong)box.Size);
        }
        else if (box.Size > uint.MaxValue)
        {
            throw new InvalidOperationException("A box with a 32-bit size field cannot be larger than 4 GB.");
        }
        else
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header, (uint)box.Size);
        }

        stream.Position = box.Position;
        stream.Write(header[..box.HeaderSize]);
    }
}
