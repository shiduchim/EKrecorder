using EKrecorder.Mp4;

namespace EKrecorder.Tests;

/// <summary>
/// The crash-safe file format: a fragmented recording becomes a regular MP4 in place, a recording cut off at any byte
/// keeps every complete sample, and a conversion interrupted at any step finishes with the same result.
/// </summary>
public sealed class Mp4RepairTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "ekrecorder-mp4-" + Guid.NewGuid().ToString("N"));

    public Mp4RepairTests()
    {
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void MediaFoundationLayoutBecomesARegularMp4WithEverySample()
    {
        var builder = new FragmentedMp4Builder();
        var written = new List<WrittenSample>();
        byte[] original = builder.Build(FragmentedMp4Builder.TypicalFragments(20), written);
        string path = Save(original);

        RepairResult result = Mp4Repair.Run(path);

        Assert.Equal(RepairOutcome.Converted, result.Outcome);
        AssertTracks(path, Expected(written, builder, original.Length));
        AssertNoFragmentBoxes(path);
        Mp4Summary summary = Mp4File.Summarize(path);
        Assert.True(summary.Ok, summary.Problem);
        Assert.Equal(written.Count(w => w.Track == 1), summary.Video!.Samples);
        Assert.Equal(written.Count(w => w.Track == 2), summary.Audio!.Samples);
        Assert.Equal("avc1", summary.Video.Codec);
        Assert.Equal(1280, summary.Video.Width);
        Assert.Equal("mp4a", summary.Audio.Codec);
        Assert.Equal(48000, summary.Audio.SampleRate);
    }

    [Fact]
    public void ConvertingTwiceChangesNothingTheSecondTime()
    {
        var written = new List<WrittenSample>();
        string path = Save(new FragmentedMp4Builder().Build(FragmentedMp4Builder.TypicalFragments(8), written));
        Mp4Repair.Run(path);
        byte[] once = File.ReadAllBytes(path);

        RepairResult again = Mp4Repair.Run(path);

        Assert.Equal(RepairOutcome.AlreadyRegular, again.Outcome);
        Assert.Equal(once, File.ReadAllBytes(path));
    }

    [Fact]
    public void ARecordingCutOffAnywhereKeepsEveryCompleteSample()
    {
        var builder = new FragmentedMp4Builder();
        var written = new List<WrittenSample>();
        byte[] original = builder.Build(FragmentedMp4Builder.TypicalFragments(6, seed: 7), written);
        var cuts = new SortedSet<int>();
        for (int cut = 0; cut <= original.Length; cut += 97)
        {
            cuts.Add(cut);
        }

        // Around every box boundary, where the interesting cases are.
        foreach ((long start, long end) in builder.Moofs)
        {
            for (int d = -9; d <= 9; d++)
            {
                cuts.Add((int)Math.Clamp(start + d, 0, original.Length));
                cuts.Add((int)Math.Clamp(end + d, 0, original.Length));
            }
        }

        foreach (int cut in cuts)
        {
            string path = Save(original.AsSpan(0, cut).ToArray(), $"cut-{cut}.mp4");
            Dictionary<int, List<WrittenSample>> expected = Expected(written, builder, cut);
            RepairResult result = Mp4Repair.Run(path);
            if (expected.Values.All(list => list.Count == 0))
            {
                Assert.False(result.Playable, $"cut at {cut}: nothing complete, yet {result.Outcome}");
                Assert.Equal(original.AsSpan(0, cut).ToArray(), File.ReadAllBytes(path));
                continue;
            }

            Assert.True(result.Outcome == RepairOutcome.Converted, $"cut at {cut}: {result.Outcome} {result.Detail}");
            AssertTracks(path, expected, $"cut at {cut}");
            Mp4Summary summary = Mp4File.Summarize(path);
            Assert.True(summary.Ok, $"cut at {cut}: {summary.Problem}");
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void AConversionInterruptedAtAnyStepFinishesTheSame(int crashBeforeStep)
    {
        // Cut inside the last fragment's media data, so every step has work to do; fragment brands so step 5 does too.
        var builder = new FragmentedMp4Builder { Brands = ["iso6", "iso6", "mp41"] };
        var written = new List<WrittenSample>();
        byte[] original = builder.Build(FragmentedMp4Builder.TypicalFragments(5), written);
        int cut = (int)builder.Moofs[^1].End + 8 + 700;
        byte[] cutOff = original.AsSpan(0, cut).ToArray();

        string reference = Save(cutOff, "reference.mp4");
        Assert.Equal(RepairOutcome.Converted, Mp4Repair.Run(reference).Outcome);

        string path = Save(cutOff, "interrupted.mp4");
        Assert.Throws<SimulatedCrash>(() => Mp4Repair.Run(path, step =>
        {
            if (step == crashBeforeStep)
            {
                throw new SimulatedCrash();
            }
        }));
        RepairResult resumed = Mp4Repair.Run(path);

        Assert.True(resumed.Playable, resumed.Detail);
        if (crashBeforeStep == 4)
        {
            // The index was on disk but not yet marked as the index: it is not trusted, a new one is written after it.
            AssertTracks(path, Expected(written, builder, cut));
            Assert.True(Mp4File.Summarize(path).Ok);
            return;
        }

        Assert.Equal(File.ReadAllBytes(reference), File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.5)]
    [InlineData(0.99)]
    public void AHalfWrittenIndexIsNeverTrusted(double keptPart)
    {
        // A power cut while the index was being written: its start reached the disk, the rest reads as zeros.
        var builder = new FragmentedMp4Builder();
        var written = new List<WrittenSample>();
        byte[] original = builder.Build(FragmentedMp4Builder.TypicalFragments(8), written);
        string path = Save(original);
        Assert.Throws<SimulatedCrash>(() => Mp4Repair.Run(path, step =>
        {
            if (step == 4)
            {
                throw new SimulatedCrash();
            }
        }));
        byte[] crashed = File.ReadAllBytes(path);
        int indexAt = IndexOfLastBox(crashed);
        int kept = indexAt + 8 + (int)((crashed.Length - indexAt - 8) * keptPart);
        Array.Clear(crashed, kept, crashed.Length - kept);
        File.WriteAllBytes(path, crashed);

        RepairResult resumed = Mp4Repair.Run(path);

        Assert.Equal(RepairOutcome.Converted, resumed.Outcome);
        AssertTracks(path, Expected(written, builder, original.Length));
        Assert.True(Mp4File.Summarize(path).Ok);
    }

    [Fact]
    public void ADamagedIndexIsBuiltAgainFromTheFragments()
    {
        // The index was marked as the index, then the disk lost part of it before the fragment headers were touched.
        var builder = new FragmentedMp4Builder();
        var written = new List<WrittenSample>();
        byte[] original = builder.Build(FragmentedMp4Builder.TypicalFragments(8), written);
        string path = Save(original);
        Assert.Throws<SimulatedCrash>(() => Mp4Repair.Run(path, step =>
        {
            if (step == 5)
            {
                throw new SimulatedCrash();
            }
        }));
        byte[] crashed = File.ReadAllBytes(path);
        int indexAt = IndexOfLastBox(crashed);
        int half = indexAt + ((crashed.Length - indexAt) / 2);
        Array.Clear(crashed, half, crashed.Length - half);
        File.WriteAllBytes(path, crashed);

        RepairResult resumed = Mp4Repair.Run(path);

        Assert.Equal(RepairOutcome.Converted, resumed.Outcome);
        AssertTracks(path, Expected(written, builder, original.Length));
        AssertNoFragmentBoxes(path);
    }

    [Fact]
    public void AWrongMdatSizeInTheMiddleLosesNothing()
    {
        var builder = new FragmentedMp4Builder();
        var written = new List<WrittenSample>();
        byte[] original = builder.Build(FragmentedMp4Builder.TypicalFragments(20), written);
        byte[] damaged = (byte[])original.Clone();
        long mdat = builder.Moofs[9].End;
        damaged[mdat] ^= 0x40; // the size field's top byte: far past the end of the file
        string path = Save(damaged);

        RepairResult result = Mp4Repair.Run(path);

        Assert.Equal(RepairOutcome.Converted, result.Outcome);
        AssertTracks(path, Expected(written, builder, original.Length));
        AssertNoFragmentBoxes(path);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(5000)]
    public void AnMdatSizeThatSwallowsFragmentsIsCorrected(int extra)
    {
        // A size that still fits in the file, but runs into the following fragments.
        var builder = new FragmentedMp4Builder();
        var written = new List<WrittenSample>();
        byte[] original = builder.Build(FragmentedMp4Builder.TypicalFragments(20), written);
        byte[] damaged = (byte[])original.Clone();
        long mdat = builder.Moofs[9].End;
        uint size = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(damaged.AsSpan((int)mdat));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(damaged.AsSpan((int)mdat), size + (uint)extra);
        string path = Save(damaged);

        Assert.Equal(RepairOutcome.Converted, Mp4Repair.Run(path).Outcome);
        AssertTracks(path, Expected(written, builder, original.Length));
    }

    [Fact]
    public void ADamagedFragmentLosesOnlyThatFragment()
    {
        var builder = new FragmentedMp4Builder();
        var written = new List<WrittenSample>();
        byte[] original = builder.Build(FragmentedMp4Builder.TypicalFragments(20), written);
        byte[] damaged = (byte[])original.Clone();
        (long start, long end) = builder.Moofs[9];
        Array.Clear(damaged, (int)start, (int)(end - start)); // the whole fragment header is gone
        string path = Save(damaged);

        RepairResult result = Mp4Repair.Run(path);

        Assert.Equal(RepairOutcome.Converted, result.Outcome);
        var expected = new Dictionary<int, List<WrittenSample>>();
        for (int track = 1; track <= 2; track++)
        {
            expected[track] = written.Where(w => w.Track == track && w.Moof != 9).ToList();
        }

        AssertTracks(path, expected);
        Assert.True(Mp4File.Summarize(path).Ok);
        Assert.Contains(result.Notes, n => n.Contains("damaged", StringComparison.Ordinal) || n.Contains("really ends", StringComparison.Ordinal));
    }

    [Fact]
    public void ARunWithADamagedSampleCountIsReadFromItsSize()
    {
        var builder = new FragmentedMp4Builder();
        var written = new List<WrittenSample>();
        byte[] original = builder.Build(FragmentedMp4Builder.TypicalFragments(20), written);
        byte[] damaged = (byte[])original.Clone();
        int trun = damaged.AsSpan((int)builder.Moofs[9].Start).IndexOf("trun"u8) + (int)builder.Moofs[9].Start;
        damaged[trun + 4 + 4 + 1] ^= 0x10; // sample_count, second byte
        string path = Save(damaged);

        Assert.Equal(RepairOutcome.Converted, Mp4Repair.Run(path).Outcome);
        AssertTracks(path, Expected(written, builder, original.Length));
    }

    [Fact]
    public void MissingSamplesInTheMiddleKeepTheTimeline()
    {
        // The bytes of fragment 10 are not in any mdat any more (its mdat header was damaged beyond repair), but its
        // header is fine: the gap is bridged by the sample before it, so everything after keeps its time.
        var builder = new FragmentedMp4Builder { MediaFoundationPrefix = false };
        var written = new List<WrittenSample>();
        byte[] original = builder.Build(FragmentedMp4Builder.TypicalFragments(12), written);
        byte[] damaged = (byte[])original.Clone();
        long mdat = builder.Moofs[9].End;
        "free"u8.CopyTo(damaged.AsSpan((int)mdat + 4));
        string path = Save(damaged);

        Assert.Equal(RepairOutcome.Converted, Mp4Repair.Run(path).Outcome);
        using FileStream stream = File.OpenRead(path);
        (List<TrackTable> tracks, _, string? problem) = Mp4File.ReadTracks(stream);
        Assert.Null(problem);
        foreach (TrackTable table in tracks)
        {
            int trackId = (int)table.Id;
            List<WrittenSample> all = written.Where(w => w.Track == trackId).ToList();
            Assert.Equal(all.Count(w => w.Moof != 9), table.Count);
            Assert.Equal(all.Sum(w => (long)w.Duration), table.MediaDuration);
        }
    }

    [Fact]
    public void ZeroFilledSamplesAtTheEndAreLeftOut()
    {
        // The power went off: the file's length reached the disk, the last fragment's data did not.
        var builder = new FragmentedMp4Builder { Mfra = false };
        var written = new List<WrittenSample>();
        byte[] original = builder.Build(FragmentedMp4Builder.TypicalFragments(6), written);
        byte[] damaged = (byte[])original.Clone();
        int lastData = (int)builder.Moofs[^1].End + 8;
        Array.Clear(damaged, lastData, damaged.Length - lastData);
        string path = Save(damaged);

        RepairResult result = Mp4Repair.Run(path);

        Assert.Equal(RepairOutcome.Converted, result.Outcome);
        var expected = new Dictionary<int, List<WrittenSample>>();
        for (int track = 1; track <= 2; track++)
        {
            expected[track] = written.Where(w => w.Track == track && w.Moof != builder.Moofs.Count - 1).ToList();
        }

        AssertTracks(path, expected);
    }

    [Theory]
    [InlineData(0, false, false, false)]
    [InlineData(1, true, false, false)]
    [InlineData(2, true, false, false)]
    [InlineData(1, true, true, false)]
    [InlineData(0, false, false, true)]
    [InlineData(2, false, true, true)]
    public void EveryStandardFragmentLayoutIsRead(int dataBase, bool tfdt, bool moofPerTrack, bool largeMdat)
    {
        var builder = new FragmentedMp4Builder
        {
            Base = (FragmentedMp4Builder.DataBase)dataBase,
            Tfdt = tfdt,
            MoofPerTrack = moofPerTrack,
            LargeMdatHeader = largeMdat,
            MediaFoundationPrefix = false,
        };
        var written = new List<WrittenSample>();
        byte[] original = builder.Build(FragmentedMp4Builder.TypicalFragments(9, compositionOffsets: true), written);
        string path = Save(original);

        Assert.Equal(RepairOutcome.Converted, Mp4Repair.Run(path).Outcome);
        AssertTracks(path, Expected(written, builder, original.Length));
    }

    [Fact]
    public void DefaultsFromTheFragmentHeaderAreUsed()
    {
        // Durations and flags come from tfhd; only each fragment's first video sample is a keyframe.
        List<SampleSpec[][]> fragments = FragmentedMp4Builder.TypicalFragments(7)
            .Select(f => new[] { f[0].Select((s, i) => s with { Sync = i == 0 }).ToArray(), f[1] })
            .ToList();
        var builder = new FragmentedMp4Builder { TrunDefaults = true, Tfdt = true };
        var written = new List<WrittenSample>();
        byte[] original = builder.Build(fragments, written);
        string path = Save(original);

        Assert.Equal(RepairOutcome.Converted, Mp4Repair.Run(path).Outcome);
        AssertTracks(path, Expected(written, builder, original.Length));
    }

    [Fact]
    public void GapsBetweenFragmentsAreKeptInTheTimeline()
    {
        var builder = new FragmentedMp4Builder { Tfdt = true, GapPerFragment = [500, 96] };
        var written = new List<WrittenSample>();
        byte[] original = builder.Build(FragmentedMp4Builder.TypicalFragments(6), written);
        string path = Save(original);

        Assert.Equal(RepairOutcome.Converted, Mp4Repair.Run(path).Outcome);
        AssertTracks(path, Expected(written, builder, original.Length));
    }

    [Fact]
    public void ATrackThatStartsLateGetsAnEmptyEditForTheDelay()
    {
        var builder = new FragmentedMp4Builder { Tfdt = true, StartTimes = [0, 4800] };
        var written = new List<WrittenSample>();
        string path = Save(builder.Build(FragmentedMp4Builder.TypicalFragments(4), written));

        Assert.Equal(RepairOutcome.Converted, Mp4Repair.Run(path).Outcome);
        using FileStream stream = File.OpenRead(path);
        (List<TrackTable> tracks, uint movieTimescale, string? problem) = Mp4File.ReadTracks(stream);
        Assert.Null(problem);
        TrackTable audio = tracks.Single(t => t.Handler == "soun");
        Assert.Equal(0.1, audio.StartSeconds(movieTimescale), 6);
        Assert.Equal(-1, audio.Edits[0].MediaTime);
        Assert.Equal(0, audio.Edits[1].MediaTime);
        Assert.Empty(tracks.Single(t => t.Handler == "vide").Edits);
    }

    [Fact]
    public void AnEncoderReorderingDelayIsRemovedWithAnEdit()
    {
        // B-frames: the first picture (decode time 0) is shown at 1000 ticks (one frame). The regular file starts
        // the track at that picture, so it is not shown a frame later than the sound recorded with it.
        var written = new List<WrittenSample>();
        string path = Save(new FragmentedMp4Builder().Build(FragmentedMp4Builder.TypicalFragments(4, compositionOffsets: true), written));

        Assert.Equal(RepairOutcome.Converted, Mp4Repair.Run(path).Outcome);
        using FileStream stream = File.OpenRead(path);
        (List<TrackTable> tracks, uint movieTimescale, _) = Mp4File.ReadTracks(stream);
        TrackTable video = tracks.Single(t => t.Handler == "vide");
        MoovBuilder.Edit edit = Assert.Single(video.Edits);
        Assert.Equal(1000, edit.MediaTime);

        // Until the last picture has been shown, not just until the last one decoded.
        long decode = 0;
        long shownUntil = 0;
        foreach (WrittenSample sample in written.Where(w => w.Track == 1))
        {
            shownUntil = Math.Max(shownUntil, decode + sample.Cto + sample.Duration);
            decode += sample.Duration;
        }

        Assert.True(shownUntil > video.MediaDuration);
        Assert.Equal((ulong)((shownUntil - 1000) * movieTimescale / video.Timescale), edit.SegmentDuration);
        Assert.Empty(tracks.Single(t => t.Handler == "soun").Edits);
    }

    [Fact]
    public void AnEditListUntilTheEndGetsTheRealLength()
    {
        var builder = new FragmentedMp4Builder { InitEditList = true };
        var written = new List<WrittenSample>();
        string path = Save(builder.Build(FragmentedMp4Builder.TypicalFragments(4), written));

        Assert.Equal(RepairOutcome.Converted, Mp4Repair.Run(path).Outcome);
        using FileStream stream = File.OpenRead(path);
        (List<TrackTable> tracks, uint movieTimescale, _) = Mp4File.ReadTracks(stream);
        foreach (TrackTable track in tracks)
        {
            ulong expected = (ulong)Math.Round((decimal)track.MediaDuration * movieTimescale / track.Timescale, MidpointRounding.AwayFromZero);
            Assert.Equal(expected, track.Edits.Single().SegmentDuration);
        }
    }

    [Fact]
    public void AnOpenEndedLastMdatGetsItsRealSizeBeforeTheIndex()
    {
        var builder = new FragmentedMp4Builder { OpenEndedLastMdat = true };
        var written = new List<WrittenSample>();
        byte[] original = builder.Build(FragmentedMp4Builder.TypicalFragments(5), written);
        string path = Save(original);

        Assert.Equal(RepairOutcome.Converted, Mp4Repair.Run(path).Outcome);
        AssertTracks(path, Expected(written, builder, original.Length));
    }

    [Fact]
    public void FragmentBrandsAreReplacedRegularBrandsAreKept()
    {
        var fragmentedBrands = new FragmentedMp4Builder { Brands = ["iso6", "dash", "msdh"] };
        string path = Save(fragmentedBrands.Build(FragmentedMp4Builder.TypicalFragments(3), new List<WrittenSample>()), "a.mp4");
        Mp4Repair.Run(path);
        Assert.Equal("isom", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(path), 8, 4));

        string kept = Save(new FragmentedMp4Builder().Build(FragmentedMp4Builder.TypicalFragments(3), new List<WrittenSample>()), "b.mp4");
        Mp4Repair.Run(kept);
        Assert.Equal("mp42", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(kept), 8, 4));
    }

    [Fact]
    public void FilesWithNothingToSaveAreLeftAlone()
    {
        var builder = new FragmentedMp4Builder();
        byte[] full = builder.Build(FragmentedMp4Builder.TypicalFragments(3), new List<WrittenSample>());
        byte[] headerOnly = full.AsSpan(0, (int)builder.Moofs[0].Start).ToArray();
        string path = Save(headerOnly, "header-only.mp4");
        Assert.Equal(RepairOutcome.Unrecoverable, Mp4Repair.Run(path).Outcome);
        Assert.Equal(headerOnly, File.ReadAllBytes(path));

        byte[] garbage = Enumerable.Range(0, 5000).Select(i => (byte)(i * 13)).ToArray();
        string junk = Save(garbage, "junk.mp4");
        Assert.Equal(RepairOutcome.Unrecoverable, Mp4Repair.Run(junk).Outcome);
        Assert.Equal(garbage, File.ReadAllBytes(junk));

        string empty = Save([], "empty.mp4");
        Assert.Equal(RepairOutcome.Unrecoverable, Mp4Repair.Run(empty).Outcome);
    }

    [Fact]
    public void PowerCutZerosAfterTheDataAreDropped()
    {
        var builder = new FragmentedMp4Builder();
        var written = new List<WrittenSample>();
        byte[] original = builder.Build(FragmentedMp4Builder.TypicalFragments(6), written);
        int keep = (int)builder.Moofs[4].Start;
        byte[] damaged = original.AsSpan(0, keep).ToArray().Concat(new byte[4096]).ToArray();
        string path = Save(damaged);

        Assert.Equal(RepairOutcome.Converted, Mp4Repair.Run(path).Outcome);
        AssertTracks(path, Expected(written, builder, keep));
    }

    /// <summary>Where the last top-level box starts.</summary>
    private static int IndexOfLastBox(byte[] file)
    {
        int position = 0;
        int last = 0;
        while (position + 8 <= file.Length)
        {
            uint size = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(position));
            if (size < 8)
            {
                break;
            }

            last = position;
            position += (int)size;
        }

        return last;
    }

    private string Save(byte[] bytes, string name = "recording.mp4")
    {
        string path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>
    /// What the converted file must hold when the original is cut at <paramref name="length"/>: per track, every
    /// sample up to the first one whose moof or bytes are missing. Gaps in the fragments' decode times lengthen the
    /// sample before them.
    /// </summary>
    private static Dictionary<int, List<WrittenSample>> Expected(List<WrittenSample> written, FragmentedMp4Builder builder, long length)
    {
        var expected = new Dictionary<int, List<WrittenSample>>();
        for (int track = 1; track <= 2; track++)
        {
            var list = new List<WrittenSample>();
            int lastMoof = -1;
            foreach (WrittenSample sample in written.Where(w => w.Track == track))
            {
                bool present = builder.Moofs[sample.Moof].End <= length && sample.Offset + sample.Size <= length;
                if (!present)
                {
                    break;
                }

                if (builder.Tfdt && list.Count > 0 && sample.Moof != lastMoof && builder.GapPerFragment[track - 1] != 0)
                {
                    WrittenSample previous = list[^1];
                    list[^1] = previous with { Duration = (uint)(previous.Duration + builder.GapPerFragment[track - 1]) };
                }

                lastMoof = sample.Moof;
                list.Add(sample);
            }

            expected[track] = list;
        }

        return expected;
    }

    private static void AssertTracks(string path, Dictionary<int, List<WrittenSample>> expected, string context = "")
    {
        byte[] file = File.ReadAllBytes(path);
        using var stream = new MemoryStream(file);
        (List<TrackTable> tracks, _, string? problem) = Mp4File.ReadTracks(stream);
        Assert.True(problem is null, $"{context}: {problem}");
        foreach ((int trackId, List<WrittenSample> samples) in expected)
        {
            TrackTable? table = tracks.SingleOrDefault(t => t.Id == trackId);
            if (samples.Count == 0)
            {
                Assert.True(table is null, $"{context}: track {trackId} should be left out");
                continue;
            }

            Assert.True(table is not null, $"{context}: track {trackId} is missing");
            Assert.True(samples.Count == table!.Count, $"{context}: track {trackId} has {table.Count} samples, expected {samples.Count}");
            for (int i = 0; i < samples.Count; i++)
            {
                WrittenSample sample = samples[i];
                Assert.Equal(sample.Offset, table.Offsets[i]);
                Assert.Equal((uint)sample.Size, table.Sizes[i]);
                Assert.Equal(sample.Duration, table.Durations[i]);
                Assert.Equal(sample.Sync, table.Sync[i]);
                Assert.Equal(sample.Cto, table.CompositionOffsets[i]);
                for (int k = 0; k < sample.Size; k += 61)
                {
                    Assert.Equal(FragmentedMp4Builder.PayloadByte(trackId, sample.Index, k), file[sample.Offset + k]);
                }
            }
        }
    }

    private static void AssertNoFragmentBoxes(string path)
    {
        using FileStream stream = File.OpenRead(path);
        Mp4Layout layout = Mp4Scanner.Scan(stream);
        Assert.Equal(Mp4Kind.Regular, layout.Kind);
        Assert.DoesNotContain(layout.TopLevel, b => b.Type is "moof" or "mfra");
        Assert.Single(layout.TopLevel, b => b.Type == "moov");
        Assert.Null(layout.CutOff);
        Assert.Equal(stream.Length, layout.ValidEnd);
    }

    private sealed class SimulatedCrash : Exception
    {
    }
}

public sealed class LongRecordingIndexTests
{
    [Fact]
    public void FilesPast4GBAndHoursOfSamplesGetLargeFieldsWhereNeeded()
    {
        // 12 hours of 30 fps video at 10,000,000 ticks per second: the media duration does not fit 32 bits, and the
        // chunks lie past 4 GB.
        var header = new TrackHeader
        {
            Id = 1,
            Timescale = 10_000_000,
            Handler = "vide",
            Tkhd = Box("tkhd", version: 0, new byte[80]),
            Mdhd = Box("mdhd", version: 0, [0, 0, 0, 0, 0, 0, 0, 0, 0, 0x98, 0x96, 0x80, 0, 0, 0, 0, 0x55, 0xC4, 0, 0]),
            Hdlr = Box("hdlr", version: 0, [0, 0, 0, 0, (byte)'v', (byte)'i', (byte)'d', (byte)'e', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]),
            Stsd = Box("stsd", version: 0, [0, 0, 0, 0]),
        };
        var track = new TrackSamples(header);
        const int frames = 12 * 3600 * 30;
        long offset = 6L * 1024 * 1024 * 1024;
        for (int i = 0; i < frames; i++)
        {
            track.Sizes.Add(1000);
            track.Durations.Add(333_333 + (uint)(i % 3 == 0 ? 1 : 0));
            track.CompositionOffsets.Add(0);
            track.Sync.Add(i % 60 == 0);
            track.Chunks.Add(new Chunk(offset, i, 1));
            track.DecodeTime += track.Durations[^1];
            offset += 1000;
        }

        track.AnyNonSync = true;
        var movie = new MovieHeader { Mvhd = Box("mvhd", version: 1, new byte[108]), Timescale = 1000 };
        movie.Tracks.Add(header);

        byte[] moov = MoovBuilder.Build(movie, [track]);

        var root = new Box("moov", 0, moov.Length, 8);
        Box trak = BoxIo.Child(moov, root, "trak")!.Value;
        Box mdia = BoxIo.Child(moov, trak, "mdia")!.Value;
        Box mdhd = BoxIo.Child(moov, mdia, "mdhd")!.Value;
        Assert.Equal(1, moov[mdhd.PayloadPosition]);
        Assert.Equal((ulong)track.MediaDuration, BoxIo.U64(moov, mdhd.PayloadPosition + 24));
        Box stbl = BoxIo.Child(moov, BoxIo.Child(moov, mdia, "minf")!.Value, "stbl")!.Value;
        Assert.NotNull(BoxIo.Child(moov, stbl, "co64"));
        Assert.Null(BoxIo.Child(moov, stbl, "stco"));
        Box stts = BoxIo.Child(moov, stbl, "stts")!.Value;
        Assert.True(BoxIo.U32(moov, stts.PayloadPosition + 4) > 1000);
        Box mvhd = BoxIo.Child(moov, root, "mvhd")!.Value;
        ulong expectedMovie = (ulong)Math.Round((decimal)track.MediaDuration * 1000 / 10_000_000, MidpointRounding.AwayFromZero);
        Assert.Equal(expectedMovie, moov[mvhd.PayloadPosition] == 1 ? BoxIo.U64(moov, mvhd.PayloadPosition + 24) : BoxIo.U32(moov, mvhd.PayloadPosition + 16));
    }

    private static byte[] Box(string type, byte version, byte[] body)
    {
        var box = new byte[12 + body.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
        System.Text.Encoding.ASCII.GetBytes(type).CopyTo(box, 4);
        box[8] = version;
        body.CopyTo(box, 12);
        return box;
    }
}
