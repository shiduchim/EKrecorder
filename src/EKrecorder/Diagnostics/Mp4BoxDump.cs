using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace EKrecorder.Diagnostics;

/// <summary>
/// A readable dump of an MP4 file's box structure, with the fields that matter for timing and recovery
/// (time scales, durations, edit lists, fragment headers, sample tables). Used by the encoder probe and in
/// recovery diagnostics. Never throws for a damaged file: it stops at the first box that does not fit.
/// </summary>
internal static class Mp4BoxDump
{
    private static readonly HashSet<string> Containers = ["moov", "trak", "mdia", "minf", "stbl", "edts", "dinf", "mvex", "moof", "traf", "udta", "mfra"];

    public static string Dump(string path, int detailedFragments = 4)
    {
        var text = new StringBuilder();
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long length = stream.Length;
            text.AppendLine(Invariant($"{Path.GetFileName(path)}: {length:N0} bytes"));
            int moofs = 0;
            int mdats = 0;
            long mdatBytes = 0;
            long position = 0;
            while (position + 8 <= length)
            {
                if (!TryReadHeader(stream, position, length, out string type, out long size, out int header))
                {
                    text.AppendLine(Invariant($"  @{position} {type} size {size}: does not fit (file ends at {length}); the rest is not a box"));
                    break;
                }

                if (type == "moof")
                {
                    moofs++;
                }

                if (type == "mdat")
                {
                    mdats++;
                    mdatBytes += size;
                }

                bool detail = (type != "moof" && type != "mdat") || moofs <= detailedFragments;
                if (detail)
                {
                    DumpBox(stream, position, size, header, type, 1, text);
                }

                position += size;
            }

            if (position < length && position + 8 > length)
            {
                text.AppendLine(Invariant($"  {length - position} trailing byte(s) after the last box"));
            }

            text.AppendLine(Invariant($"  total: {moofs} moof, {mdats} mdat ({mdatBytes:N0} bytes)"));
        }
        catch (Exception ex)
        {
            text.AppendLine($"  dump failed: {ex.GetType().Name}: {ex.Message}");
        }

        return text.ToString();
    }

    private static bool TryReadHeader(Stream stream, long position, long end, out string type, out long size, out int header)
    {
        byte[] head = Read(stream, position, (int)Math.Min(16, end - position));
        size = BinaryPrimitives.ReadUInt32BigEndian(head);
        type = Encoding.ASCII.GetString(head, 4, 4);
        header = 8;
        if (size == 1)
        {
            if (head.Length < 16)
            {
                return false;
            }

            size = (long)BinaryPrimitives.ReadUInt64BigEndian(head.AsSpan(8));
            header = 16;
        }
        else if (size == 0)
        {
            size = end - position;
        }

        return size >= header && position + size <= end;
    }

    private static void DumpBox(Stream stream, long position, long size, int header, string type, int depth, StringBuilder text)
    {
        string indent = new(' ', depth * 2);
        string details = "";
        try
        {
            if (!Containers.Contains(type) && type != "mdat" && type != "free" && type != "skip")
            {
                byte[] body = Read(stream, position + header, (int)Math.Min(size - header, 4096));
                details = Describe(type, body);
            }
        }
        catch (Exception ex)
        {
            details = $"(could not read: {ex.Message})";
        }

        text.AppendLine(Invariant($"{indent}{type} @{position} size {size}{(details.Length > 0 ? " " + details : "")}"));
        if (!Containers.Contains(type))
        {
            return;
        }

        long child = position + header;
        long end = position + size;
        while (child + 8 <= end)
        {
            if (!TryReadHeader(stream, child, end, out string childType, out long childSize, out int childHeader))
            {
                text.AppendLine(Invariant($"{indent}  {childType} @{child} size {childSize}: does not fit"));
                return;
            }

            DumpBox(stream, child, childSize, childHeader, childType, depth + 1, text);
            child += childSize;
        }
    }

    private static string Describe(string type, byte[] b)
    {
        int version = b.Length > 0 ? b[0] : 0;
        uint flags = b.Length >= 4 ? (uint)((b[1] << 16) | (b[2] << 8) | b[3]) : 0;
        switch (type)
        {
            case "ftyp":
            {
                var brands = new List<string>();
                for (int i = 8; i + 4 <= b.Length; i += 4)
                {
                    brands.Add(Encoding.ASCII.GetString(b, i, 4));
                }

                return Invariant($"major {Encoding.ASCII.GetString(b, 0, 4)} minor {U32(b, 4)} compatible [{string.Join(",", brands)}]");
            }

            case "mvhd":
                return version == 1
                    ? Invariant($"v1 timescale {U32(b, 20)} duration {U64(b, 24)} next track {U32(b, 108)}")
                    : Invariant($"v0 timescale {U32(b, 12)} duration {U32(b, 16)} next track {U32(b, 96)}");
            case "tkhd":
                return version == 1
                    ? Invariant($"v1 flags 0x{flags:X} track {U32(b, 20)} duration {U64(b, 28)}")
                    : Invariant($"v0 flags 0x{flags:X} track {U32(b, 12)} duration {U32(b, 20)}");
            case "mdhd":
                return version == 1
                    ? Invariant($"v1 timescale {U32(b, 20)} duration {U64(b, 24)}")
                    : Invariant($"v0 timescale {U32(b, 12)} duration {U32(b, 16)}");
            case "hdlr":
                return $"handler {Encoding.ASCII.GetString(b, 8, 4)}";
            case "elst":
            {
                uint count = U32(b, 4);
                var entries = new List<string>();
                int at = 8;
                for (int i = 0; i < count && i < 4; i++)
                {
                    if (version == 1)
                    {
                        entries.Add(Invariant($"(duration {U64(b, at)}, media time {(long)U64(b, at + 8)}, rate {U32(b, at + 16) / 65536.0:0.##})"));
                        at += 20;
                    }
                    else
                    {
                        entries.Add(Invariant($"(duration {U32(b, at)}, media time {(int)U32(b, at + 4)}, rate {U32(b, at + 8) / 65536.0:0.##})"));
                        at += 12;
                    }
                }

                return Invariant($"v{version} {count} entries {string.Join(" ", entries)}");
            }

            case "stsd":
            {
                uint count = U32(b, 4);
                if (b.Length < 16)
                {
                    return Invariant($"{count} entries");
                }

                uint entrySize = U32(b, 8);
                string format = Encoding.ASCII.GetString(b, 12, 4);
                string extra = "";
                int childStart = 0;
                if (format is "avc1" or "avc3" or "hvc1" or "hev1")
                {
                    extra = Invariant($" {U16(b, 8 + 32)}x{U16(b, 8 + 34)}");
                    childStart = 8 + 86;
                }
                else if (format == "mp4a")
                {
                    extra = Invariant($" channels {U16(b, 8 + 24)} bits {U16(b, 8 + 26)} rate {U32(b, 8 + 32) >> 16}");
                    childStart = 8 + 36;
                }

                var children = new List<string>();
                int entryEnd = (int)Math.Min(8 + entrySize, (uint)b.Length);
                for (int at = childStart; childStart > 0 && at + 8 <= entryEnd;)
                {
                    uint childSize = U32(b, at);
                    string childType = Encoding.ASCII.GetString(b, at + 4, 4);
                    children.Add(Invariant($"{childType}({childSize})"));
                    if (childType == "avcC" && at + 13 <= entryEnd)
                    {
                        children[^1] += Invariant($"[profile {b[at + 9]} level {b[at + 11]}]");
                    }

                    if (childSize < 8)
                    {
                        break;
                    }

                    at += (int)childSize;
                }

                return Invariant($"{count} entries, first {format} size {entrySize}{extra} children [{string.Join(" ", children)}]");
            }

            case "stts":
            case "ctts":
            case "stsc":
            case "stss":
            case "stco":
            case "co64":
                return Invariant($"v{version} {U32(b, 4)} entries") + (type is "stts" or "ctts" && U32(b, 4) > 0 ? Invariant($", first (count {U32(b, 8)}, value {(int)U32(b, 12)})") : "");
            case "stsz":
                return Invariant($"sample size {U32(b, 4)}, {U32(b, 8)} samples");
            case "trex":
                return Invariant($"track {U32(b, 4)} desc {U32(b, 8)} duration {U32(b, 12)} size {U32(b, 16)} flags 0x{U32(b, 20):X8}");
            case "mehd":
                return version == 1 ? Invariant($"fragment duration {U64(b, 4)}") : Invariant($"fragment duration {U32(b, 4)}");
            case "mfhd":
                return Invariant($"sequence {U32(b, 4)}");
            case "tfhd":
            {
                var parts = new List<string> { Invariant($"flags 0x{flags:X}"), Invariant($"track {U32(b, 4)}") };
                int at = 8;
                if ((flags & 0x1) != 0)
                {
                    parts.Add(Invariant($"base offset {U64(b, at)}"));
                    at += 8;
                }

                if ((flags & 0x2) != 0)
                {
                    parts.Add(Invariant($"desc {U32(b, at)}"));
                    at += 4;
                }

                if ((flags & 0x8) != 0)
                {
                    parts.Add(Invariant($"default duration {U32(b, at)}"));
                    at += 4;
                }

                if ((flags & 0x10) != 0)
                {
                    parts.Add(Invariant($"default size {U32(b, at)}"));
                    at += 4;
                }

                if ((flags & 0x20) != 0)
                {
                    parts.Add(Invariant($"default flags 0x{U32(b, at):X8}"));
                }

                if ((flags & 0x10000) != 0)
                {
                    parts.Add("duration-is-empty");
                }

                if ((flags & 0x20000) != 0)
                {
                    parts.Add("default-base-is-moof");
                }

                return string.Join(", ", parts);
            }

            case "tfdt":
                return version == 1 ? Invariant($"v1 base decode time {U64(b, 4)}") : Invariant($"v0 base decode time {U32(b, 4)}");
            case "trun":
            {
                uint count = U32(b, 4);
                var parts = new List<string> { Invariant($"v{version} flags 0x{flags:X}"), Invariant($"{count} samples") };
                int at = 8;
                if ((flags & 0x1) != 0)
                {
                    parts.Add(Invariant($"data offset {(int)U32(b, at)}"));
                    at += 4;
                }

                if ((flags & 0x4) != 0)
                {
                    parts.Add(Invariant($"first flags 0x{U32(b, at):X8}"));
                    at += 4;
                }

                var samples = new List<string>();
                for (int i = 0; i < count && i < 3 && at < b.Length; i++)
                {
                    var fields = new List<string>();
                    if ((flags & 0x100) != 0)
                    {
                        fields.Add(Invariant($"d{U32(b, at)}"));
                        at += 4;
                    }

                    if ((flags & 0x200) != 0)
                    {
                        fields.Add(Invariant($"s{U32(b, at)}"));
                        at += 4;
                    }

                    if ((flags & 0x400) != 0)
                    {
                        fields.Add(Invariant($"f{U32(b, at):X8}"));
                        at += 4;
                    }

                    if ((flags & 0x800) != 0)
                    {
                        fields.Add(Invariant($"c{(int)U32(b, at)}"));
                        at += 4;
                    }

                    samples.Add(string.Join("/", fields));
                }

                return string.Join(", ", parts) + $" first [{string.Join(" ", samples)}]";
            }

            default:
                return "";
        }
    }

    private static byte[] Read(Stream stream, long position, int count)
    {
        var buffer = new byte[Math.Max(0, count)];
        stream.Position = position;
        int total = 0;
        while (total < buffer.Length)
        {
            int read = stream.Read(buffer, total, buffer.Length - total);
            if (read <= 0)
            {
                Array.Resize(ref buffer, total);
                break;
            }

            total += read;
        }

        return buffer;
    }

    private static uint U32(byte[] b, int at) => at + 4 <= b.Length ? BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(at)) : 0;

    private static ulong U64(byte[] b, int at) => at + 8 <= b.Length ? BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(at)) : 0;

    private static ushort U16(byte[] b, int at) => at + 2 <= b.Length ? BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(at)) : (ushort)0;

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
