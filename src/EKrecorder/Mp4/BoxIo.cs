using System.Buffers.Binary;
using System.Text;

namespace EKrecorder.Mp4;

/// <summary>
/// Where a box is: its four-letter type, first byte, total size and header size (8, or 16 with a 64-bit size).
/// <see cref="OpenEnded"/>: the size field was 0, meaning "to the end of the file".
/// </summary>
internal readonly record struct Box(string Type, long Position, long Size, int HeaderSize, bool OpenEnded = false)
{
    public long End => Position + Size;

    public long PayloadPosition => Position + HeaderSize;

    public long PayloadSize => Size - HeaderSize;
}

/// <summary>Reading MP4 boxes from a file or a buffer. Nothing here trusts the file: every size is checked.</summary>
internal static class BoxIo
{
    /// <summary>
    /// Reads the box header at <paramref name="position"/>. False when there is no valid header there: fewer than 8
    /// bytes left, a type that is not four printable characters, or a size smaller than the header.
    /// <paramref name="fits"/> is false when the box claims to end after <paramref name="limit"/> (a cut-off box).
    /// A size of 0 ("to the end") is returned as reaching exactly <paramref name="limit"/>.
    /// </summary>
    public static bool TryReadHeader(Stream stream, long position, long limit, out Box box, out bool fits)
    {
        box = default;
        fits = false;
        if (limit - position < 8)
        {
            return false;
        }

        Span<byte> head = stackalloc byte[16];
        int count = (int)Math.Min(16, limit - position);
        stream.Position = position;
        stream.ReadExactly(head[..count]);
        return TryParseHeader(head[..count], position, limit, out box, out fits);
    }

    /// <summary>The same as <see cref="TryReadHeader"/> for a header already in memory (16 bytes, or fewer at the end).</summary>
    public static bool TryParseHeader(ReadOnlySpan<byte> head, long position, long limit, out Box box, out bool fits)
    {
        box = default;
        fits = false;
        if (head.Length < 8 || !IsValidType(head.Slice(4, 4)))
        {
            return false;
        }

        string type = Encoding.Latin1.GetString(head.Slice(4, 4));
        long size = BinaryPrimitives.ReadUInt32BigEndian(head);
        int headerSize = 8;
        bool openEnded = size == 0;
        if (size == 1)
        {
            if (head.Length < 16)
            {
                return false;
            }

            ulong large = BinaryPrimitives.ReadUInt64BigEndian(head[8..]);
            if (large > long.MaxValue)
            {
                return false;
            }

            size = (long)large;
            headerSize = 16;
        }
        else if (size == 0)
        {
            size = limit - position;
        }

        if (size < headerSize)
        {
            return false;
        }

        box = new Box(type, position, size, headerSize, openEnded);
        fits = position + size <= limit;
        return true;
    }

    /// <summary>The boxes inside <paramref name="data"/> between <paramref name="start"/> and <paramref name="end"/>; stops at the first one that does not fit.</summary>
    public static List<Box> Children(byte[] data, int start, int end)
    {
        var children = new List<Box>();
        long position = start;
        while (position + 8 <= end)
        {
            int count = (int)Math.Min(16, end - position);
            if (!TryParseHeader(data.AsSpan((int)position, count), position, end, out Box box, out bool fits) || !fits)
            {
                break;
            }

            children.Add(box);
            position = box.End;
        }

        return children;
    }

    /// <summary>The first child of the given type, or null.</summary>
    public static Box? Child(byte[] data, Box parent, string type, int skip = 0)
    {
        foreach (Box child in Children(data, (int)parent.PayloadPosition + skip, (int)parent.End))
        {
            if (child.Type == type)
            {
                return child;
            }
        }

        return null;
    }

    /// <summary>A copy of the whole box (header included).</summary>
    public static byte[] Copy(byte[] data, Box box) => data.AsSpan((int)box.Position, (int)box.Size).ToArray();

    public static byte[] ReadExactly(Stream stream, long position, int count)
    {
        var buffer = new byte[count];
        stream.Position = position;
        stream.ReadExactly(buffer);
        return buffer;
    }

    public static uint U32(ReadOnlySpan<byte> data, long at) => BinaryPrimitives.ReadUInt32BigEndian(data[(int)at..]);

    public static ulong U64(ReadOnlySpan<byte> data, long at) => BinaryPrimitives.ReadUInt64BigEndian(data[(int)at..]);

    public static ushort U16(ReadOnlySpan<byte> data, long at) => BinaryPrimitives.ReadUInt16BigEndian(data[(int)at..]);

    /// <summary>Box types are four bytes of printable ASCII (© is allowed for metadata boxes).</summary>
    private static bool IsValidType(ReadOnlySpan<byte> type)
    {
        foreach (byte b in type)
        {
            if ((b < 0x20 || b > 0x7E) && b != 0xA9)
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>Writes boxes into memory, filling in each box's size when it is closed.</summary>
internal sealed class BoxWriter
{
    private readonly MemoryStream _stream = new();
    private readonly Stack<long> _open = new();

    public long Length => _stream.Length;

    public BoxScope Box(string type)
    {
        _open.Push(_stream.Position);
        UInt32(0);
        Type(type);
        return new BoxScope(this);
    }

    public BoxScope FullBox(string type, byte version, uint flags)
    {
        BoxScope scope = Box(type);
        UInt8(version);
        UInt24(flags);
        return scope;
    }

    public void Bytes(ReadOnlySpan<byte> bytes) => _stream.Write(bytes);

    public void UInt8(byte value) => _stream.WriteByte(value);

    public void UInt16(ushort value)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(b, value);
        _stream.Write(b);
    }

    public void UInt24(uint value)
    {
        _stream.WriteByte((byte)(value >> 16));
        _stream.WriteByte((byte)(value >> 8));
        _stream.WriteByte((byte)value);
    }

    public void UInt32(uint value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, value);
        _stream.Write(b);
    }

    public void Int32(int value) => UInt32(unchecked((uint)value));

    public void UInt64(ulong value)
    {
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(b, value);
        _stream.Write(b);
    }

    public void Int64(long value) => UInt64(unchecked((ulong)value));

    public void Type(string type)
    {
        if (type.Length != 4)
        {
            throw new ArgumentException($"Box type must be 4 characters: \"{type}\"", nameof(type));
        }

        Span<byte> b = stackalloc byte[4];
        Encoding.Latin1.GetBytes(type, b);
        _stream.Write(b);
    }

    public byte[] ToArray()
    {
        if (_open.Count != 0)
        {
            throw new InvalidOperationException("A box is still open.");
        }

        return _stream.ToArray();
    }

    private void Close()
    {
        long start = _open.Pop();
        long end = _stream.Position;
        long size = end - start;
        if (size > uint.MaxValue)
        {
            throw new InvalidOperationException("Box larger than 4 GB.");
        }

        _stream.Position = start;
        UInt32((uint)size);
        _stream.Position = end;
    }

    public readonly struct BoxScope : IDisposable
    {
        private readonly BoxWriter _writer;

        public BoxScope(BoxWriter writer)
        {
            _writer = writer;
        }

        public void Dispose() => _writer.Close();
    }
}
