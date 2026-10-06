using System.Text;
using EKrecorder.Mp4;

namespace EKrecorder.Tests;

/// <summary>One sample to put in a fragment.</summary>
internal sealed record SampleSpec(uint Duration, int Size, bool Sync, int Cto = 0);

/// <summary>Where a sample ended up in the built file, and what it holds.</summary>
internal sealed record WrittenSample(int Track, int Index, long Offset, int Size, uint Duration, bool Sync, int Cto, int Moof);

/// <summary>
/// Writes fragmented MP4 files for the tests, in the layout Windows' fragmented MP4 sink uses (ftyp, uuid, pdin,
/// moov with mvex, then moof + mdat pairs with an absolute base offset and per-sample trun fields, then mfra) or
/// in the other layouts the standard allows. Track 1 is video (30000 ticks/s), track 2 is audio (48000 ticks/s).
/// </summary>
internal sealed class FragmentedMp4Builder
{
    public enum DataBase
    {
        /// <summary>tfhd carries the absolute base offset (what Media Foundation writes).</summary>
        ExplicitOffset,

        /// <summary>default-base-is-moof; trun offsets count from the moof.</summary>
        Moof,

        /// <summary>No base: the first traf counts from the moof, the next one starts where the previous data ended.</summary>
        Implicit,
    }

    public DataBase Base { get; init; } = DataBase.ExplicitOffset;

    public bool Tfdt { get; init; }

    public bool TfdtVersion1 { get; init; } = true;

    /// <summary>Durations and flags come from tfhd defaults (and first_sample_flags) instead of every sample.</summary>
    public bool TrunDefaults { get; init; }

    public bool MoofPerTrack { get; init; }

    public bool LargeMdatHeader { get; init; }

    public bool OpenEndedLastMdat { get; init; }

    public bool Mfra { get; init; } = true;

    public bool MediaFoundationPrefix { get; init; } = true;

    public string[] Brands { get; init; } = ["mp42", "mp41", "isom"];

    public bool InitEditList { get; init; }

    public uint MovieTimescale { get; init; } = 48000;

    /// <summary>Decode time of each track's first sample (written in tfdt).</summary>
    public long[] StartTimes { get; init; } = [0, 0];

    /// <summary>Extra decode-time jump written into each fragment's tfdt after the first (per track), to test gaps.</summary>
    public long[] GapPerFragment { get; init; } = [0, 0];

    public static readonly uint[] Timescales = [30000, 48000];

    /// <summary>Where each moof box ended up (start, end), in file order.</summary>
    public List<(long Start, long End)> Moofs { get; } = new();

    public static byte PayloadByte(int track, int index, int k) => (byte)((track * 31) + (index * 7) + k);

    /// <summary>A typical recording: video at 30 fps (keyframe every <paramref name="gop"/>) and AAC audio, in fragments of about a third of a second.</summary>
    public static List<SampleSpec[][]> TypicalFragments(int fragments, int gop = 60, bool compositionOffsets = false, int seed = 1)
    {
        var random = new Random(seed);
        var list = new List<SampleSpec[][]>();
        int videoIndex = 0;
        for (int f = 0; f < fragments; f++)
        {
            int videoCount = 7 + random.Next(5);
            var video = new SampleSpec[videoCount];
            for (int i = 0; i < videoCount; i++, videoIndex++)
            {
                bool sync = videoIndex % gop == 0;
                int cto = compositionOffsets ? (sync ? 1000 : (videoIndex % 2 == 0 ? 2000 : 0)) : 0;
                video[i] = new SampleSpec(1000, sync ? 2000 + random.Next(3000) : 20 + random.Next(400), sync, cto);
            }

            int audioCount = 13 + random.Next(4);
            var audio = new SampleSpec[audioCount];
            for (int i = 0; i < audioCount; i++)
            {
                audio[i] = new SampleSpec(1024, 330 + random.Next(20), true);
            }

            list.Add([video, audio]);
        }

        return list;
    }

    public byte[] Build(IReadOnlyList<SampleSpec[][]> fragments, List<WrittenSample> written)
    {
        var file = new MemoryStream();
        Write(file, Ftyp());
        if (MediaFoundationPrefix)
        {
            Write(file, RawBox("uuid", new byte[32]));
            Write(file, RawBox("pdin", new byte[12]));
        }

        Write(file, InitMoov());
        long[] decodeTimes = [StartTimes[0], StartTimes[1]];
        int[] indexes = [0, 0];
        int sequence = 1;
        for (int f = 0; f < fragments.Count; f++)
        {
            if (f > 0)
            {
                decodeTimes[0] += GapPerFragment[0];
                decodeTimes[1] += GapPerFragment[1];
            }

            bool last = f == fragments.Count - 1;
            if (MoofPerTrack)
            {
                for (int t = 0; t < 2; t++)
                {
                    SampleSpec[][] single = t == 0 ? [fragments[f][0], []] : [[], fragments[f][1]];
                    WriteFragment(file, single, sequence++, decodeTimes, indexes, written, last && t == 1);
                }
            }
            else
            {
                WriteFragment(file, fragments[f], sequence++, decodeTimes, indexes, written, last);
            }
        }

        if (Mfra && !OpenEndedLastMdat)
        {
            Write(file, RawBox("mfra", new byte[24]));
        }

        return file.ToArray();
    }

    private void WriteFragment(MemoryStream file, SampleSpec[][] tracks, int sequence, long[] decodeTimes, int[] indexes, List<WrittenSample> written, bool lastBox)
    {
        long moofPosition = file.Length;
        byte[] moof = Moof(tracks, sequence, decodeTimes, moofPosition, 0);
        int mdatHeader = LargeMdatHeader ? 16 : 8;
        long payload = moofPosition + moof.Length + mdatHeader;
        moof = Moof(tracks, sequence, decodeTimes, moofPosition, payload);
        Write(file, moof);
        Moofs.Add((moofPosition, moofPosition + moof.Length));
        int moofIndex = Moofs.Count - 1;

        var data = new MemoryStream();
        for (int t = 0; t < 2; t++)
        {
            foreach (SampleSpec spec in tracks[t])
            {
                long offset = payload + data.Length;
                for (int k = 0; k < spec.Size; k++)
                {
                    data.WriteByte(PayloadByte(t + 1, indexes[t], k));
                }

                written.Add(new WrittenSample(t + 1, indexes[t], offset, spec.Size, spec.Duration, spec.Sync, spec.Cto, moofIndex));
                indexes[t]++;
                decodeTimes[t] += spec.Duration;
            }
        }

        var header = new byte[mdatHeader];
        long size = mdatHeader + data.Length;
        if (LargeMdatHeader)
        {
            BeU32(header, 0, 1);
            Encoding.ASCII.GetBytes("mdat").CopyTo(header, 4);
            BeU64(header, 8, (ulong)size);
        }
        else
        {
            BeU32(header, 0, OpenEndedLastMdat && lastBox ? 0 : (uint)size);
            Encoding.ASCII.GetBytes("mdat").CopyTo(header, 4);
        }

        file.Write(header);
        data.Position = 0;
        data.CopyTo(file);
    }

    private byte[] Moof(SampleSpec[][] tracks, int sequence, long[] decodeTimes, long moofPosition, long payload)
    {
        var w = new BoxWriter();
        using (w.Box("moof"))
        {
            using (w.FullBox("mfhd", 0, 0))
            {
                w.UInt32((uint)sequence);
            }

            long dataBefore = 0;
            for (int t = 0; t < 2; t++)
            {
                SampleSpec[] samples = tracks[t];
                if (samples.Length == 0)
                {
                    continue;
                }

                bool firstTraf = dataBefore == 0;
                using (w.Box("traf"))
                {
                    uint tfhdFlags = Base switch
                    {
                        DataBase.ExplicitOffset => 0x1u,
                        DataBase.Moof => 0x20000u,
                        _ => 0u,
                    };
                    if (TrunDefaults)
                    {
                        tfhdFlags |= 0x8 | 0x20;
                    }

                    using (w.FullBox("tfhd", 0, tfhdFlags))
                    {
                        w.UInt32((uint)(t + 1));
                        if (Base == DataBase.ExplicitOffset)
                        {
                            w.UInt64((ulong)payload);
                        }

                        if (TrunDefaults)
                        {
                            w.UInt32(samples[0].Duration);
                            w.UInt32(t == 0 ? 0x10000u : 0u);
                        }
                    }

                    if (Tfdt)
                    {
                        using (w.FullBox("tfdt", TfdtVersion1 ? (byte)1 : (byte)0, 0))
                        {
                            if (TfdtVersion1)
                            {
                                w.UInt64((ulong)decodeTimes[t]);
                            }
                            else
                            {
                                w.UInt32((uint)decodeTimes[t]);
                            }
                        }
                    }

                    bool writeOffset = Base != DataBase.Implicit || firstTraf;
                    long offsetValue = Base switch
                    {
                        DataBase.ExplicitOffset => dataBefore,
                        _ => payload - moofPosition + dataBefore,
                    };
                    bool anyCto = samples.Any(s => s.Cto != 0);
                    uint trunFlags = (writeOffset ? 0x1u : 0) | 0x200;
                    if (TrunDefaults)
                    {
                        trunFlags |= t == 0 ? 0x4u : 0;
                    }
                    else
                    {
                        trunFlags |= 0x100 | 0x400;
                    }

                    if (anyCto)
                    {
                        trunFlags |= 0x800;
                    }

                    using (w.FullBox("trun", 1, trunFlags))
                    {
                        w.UInt32((uint)samples.Length);
                        if (writeOffset)
                        {
                            w.Int32((int)offsetValue);
                        }

                        if (TrunDefaults && t == 0)
                        {
                            w.UInt32(samples[0].Sync ? 0u : 0x10000u);
                        }

                        foreach (SampleSpec s in samples)
                        {
                            if (!TrunDefaults)
                            {
                                w.UInt32(s.Duration);
                            }

                            w.UInt32((uint)s.Size);
                            if (!TrunDefaults)
                            {
                                w.UInt32(s.Sync ? 0u : 0x10000u);
                            }

                            if (anyCto)
                            {
                                w.Int32(s.Cto);
                            }
                        }
                    }
                }

                dataBefore += samples.Sum(s => (long)s.Size);
            }
        }

        return w.ToArray();
    }

    private byte[] Ftyp()
    {
        var w = new BoxWriter();
        using (w.Box("ftyp"))
        {
            w.Type(Brands[0]);
            w.UInt32(0);
            foreach (string brand in Brands.Skip(1))
            {
                w.Type(brand);
            }
        }

        return w.ToArray();
    }

    private byte[] InitMoov()
    {
        var w = new BoxWriter();
        using (w.Box("moov"))
        {
            using (w.FullBox("mvhd", 1, 0))
            {
                w.UInt64(3_000_000_000); // creation
                w.UInt64(3_000_000_000); // modification
                w.UInt32(MovieTimescale);
                w.UInt64(0);
                w.UInt32(0x00010000); // rate
                w.UInt16(0x0100); // volume
                w.Bytes(new byte[10]);
                w.Bytes(Matrix());
                w.Bytes(new byte[24]);
                w.UInt32(3); // next track
            }

            for (int t = 0; t < 2; t++)
            {
                using (w.Box("trak"))
                {
                    using (w.FullBox("tkhd", 0, 1))
                    {
                        w.UInt32(0);
                        w.UInt32(0);
                        w.UInt32((uint)(t + 1));
                        w.UInt32(0);
                        w.UInt32(0); // duration
                        w.Bytes(new byte[8]);
                        w.UInt16(0); // layer
                        w.UInt16(0); // alternate group
                        w.UInt16(t == 1 ? (ushort)0x0100 : (ushort)0);
                        w.UInt16(0);
                        w.Bytes(Matrix());
                        w.UInt32(t == 0 ? 1280u << 16 : 0);
                        w.UInt32(t == 0 ? 720u << 16 : 0);
                    }

                    if (InitEditList)
                    {
                        using (w.Box("edts"))
                        using (w.FullBox("elst", 0, 0))
                        {
                            w.UInt32(1);
                            w.UInt32(0); // "until the end"
                            w.Int32(0);
                            w.UInt32(0x10000);
                        }
                    }

                    using (w.Box("mdia"))
                    {
                        using (w.FullBox("mdhd", 0, 0))
                        {
                            w.UInt32(0);
                            w.UInt32(0);
                            w.UInt32(Timescales[t]);
                            w.UInt32(0);
                            w.UInt16(0x55C4); // "und"
                            w.UInt16(0);
                        }

                        using (w.FullBox("hdlr", 0, 0))
                        {
                            w.UInt32(0);
                            w.Type(t == 0 ? "vide" : "soun");
                            w.Bytes(new byte[12]);
                            w.Bytes(Encoding.ASCII.GetBytes(t == 0 ? "Video\0" : "Audio\0"));
                        }

                        using (w.Box("minf"))
                        {
                            if (t == 0)
                            {
                                using (w.FullBox("vmhd", 0, 1))
                                {
                                    w.UInt64(0);
                                }
                            }
                            else
                            {
                                using (w.FullBox("smhd", 0, 0))
                                {
                                    w.UInt32(0);
                                }
                            }

                            using (w.Box("dinf"))
                            using (w.FullBox("dref", 0, 0))
                            {
                                w.UInt32(1);
                                using (w.FullBox("url ", 0, 1))
                                {
                                }
                            }

                            using (w.Box("stbl"))
                            {
                                using (w.FullBox("stsd", 0, 0))
                                {
                                    w.UInt32(1);
                                    if (t == 0)
                                    {
                                        using (w.Box("avc1"))
                                        {
                                            w.Bytes(new byte[6]);
                                            w.UInt16(1);
                                            w.Bytes(new byte[16]);
                                            w.UInt16(1280);
                                            w.UInt16(720);
                                            w.UInt32(0x00480000);
                                            w.UInt32(0x00480000);
                                            w.UInt32(0);
                                            w.UInt16(1);
                                            w.Bytes(new byte[32]);
                                            w.UInt16(0x18);
                                            w.UInt16(0xFFFF);
                                            using (w.Box("avcC"))
                                            {
                                                w.Bytes([1, 100, 0, 31, 0xFF, 0xE0, 0]);
                                            }
                                        }
                                    }
                                    else
                                    {
                                        using (w.Box("mp4a"))
                                        {
                                            w.Bytes(new byte[6]);
                                            w.UInt16(1);
                                            w.Bytes(new byte[8]);
                                            w.UInt16(2);
                                            w.UInt16(16);
                                            w.UInt32(0);
                                            w.UInt32(48000u << 16);
                                            using (w.FullBox("esds", 0, 0))
                                            {
                                                w.Bytes([3, 0x19, 0, 2, 0]);
                                            }
                                        }
                                    }
                                }

                                foreach (string empty in new[] { "stts", "stsc", "stco" })
                                {
                                    using (w.FullBox(empty, 0, 0))
                                    {
                                        w.UInt32(0);
                                    }
                                }

                                using (w.FullBox("stsz", 0, 0))
                                {
                                    w.UInt32(0);
                                    w.UInt32(0);
                                }
                            }
                        }
                    }
                }
            }

            using (w.Box("mvex"))
            {
                for (int t = 0; t < 2; t++)
                {
                    using (w.FullBox("trex", 0, 0))
                    {
                        w.UInt32((uint)(t + 1));
                        w.UInt32(1);
                        w.UInt32(0);
                        w.UInt32(0);
                        w.UInt32(0x10000);
                    }
                }
            }
        }

        return w.ToArray();
    }

    private static byte[] Matrix()
    {
        var m = new byte[36];
        BeU32(m, 0, 0x00010000);
        BeU32(m, 16, 0x00010000);
        BeU32(m, 32, 0x40000000);
        return m;
    }

    private static byte[] RawBox(string type, byte[] payload)
    {
        var box = new byte[8 + payload.Length];
        BeU32(box, 0, (uint)box.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(box, 4);
        payload.CopyTo(box, 8);
        return box;
    }

    private static void Write(MemoryStream file, byte[] bytes) => file.Write(bytes);

    private static void BeU32(byte[] b, int at, uint value)
    {
        b[at] = (byte)(value >> 24);
        b[at + 1] = (byte)(value >> 16);
        b[at + 2] = (byte)(value >> 8);
        b[at + 3] = (byte)value;
    }

    private static void BeU64(byte[] b, int at, ulong value)
    {
        BeU32(b, at, (uint)(value >> 32));
        BeU32(b, at + 4, (uint)value);
    }
}
