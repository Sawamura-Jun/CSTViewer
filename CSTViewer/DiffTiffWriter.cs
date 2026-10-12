using System.Buffers.Binary;
using System.IO;

namespace CSTViewer;

/// <summary>libvipsで作った単ページBigTIFFを、圧縮を解かずに連結する。</summary>
internal sealed class DiffTiffWriter : IDisposable
{
    private readonly FileStream _output;
    private long _previousLink = 8;

    public DiffTiffWriter(string path)
    {
        _output = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        // BigTIFFのヘッダー。以降の参照位置はすべて64bitで保持する。
        _output.Write(new byte[] { 73, 73, 43, 0, 8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
    }

    public void Append(Stream page, CancellationToken token)
    {
        // 書き込みに使ったハンドルをそのまま読み、他プロセスが開く隙間を作らない。
        ArgumentNullException.ThrowIfNull(page);
        if (!page.CanRead || !page.CanSeek) throw new ArgumentException("A temporary page requires a readable, seekable stream.", nameof(page));
        page.Position = 0;
        Span<byte> header = stackalloc byte[16];
        page.ReadExactly(header);
        if (!header[..8].SequenceEqual(new byte[] { 73, 73, 43, 0, 8, 0, 0, 0 }))
            throw new InvalidDataException("The temporary diff page is not a little-endian BigTIFF.");
        long directory = checked((long)BinaryPrimitives.ReadUInt64LittleEndian(header[8..]));
        page.Position = directory;
        ulong count = ReadNumber(page, 8);
        if (count > (ulong)Math.Max(0, (page.Length - directory - 16) / 20))
            throw new InvalidDataException("Invalid tag list in the temporary diff page.");

        _output.Position = _output.Length;
        AlignOutput();
        long origin = _output.Position;
        page.Position = 0;
        byte[] buffer = new byte[ViewerSettings.DiffCopyBufferSize];
        int read;
        while ((read = page.Read(buffer)) > 0)
        {
            token.ThrowIfCancellationRequested();
            _output.Write(buffer, 0, read);
        }

        // 各ページの寸法を保ち、タグの参照先だけを結合後の位置へ移す。
        for (ulong index = 0; index < count; index++)
        {
            token.ThrowIfCancellationRequested();
            long entry = directory + 8 + checked((long)index * 20);
            page.Position = entry;
            ushort tag = (ushort)ReadNumber(page, 2);
            ushort type = (ushort)ReadNumber(page, 2);
            ulong values = ReadNumber(page, 8);
            ulong value = ReadNumber(page, 8);
            int size = TypeSize(type);
            bool external = checked(values * (ulong)size) > 8;
            if (tag is 273 or 324)
            {
                // StripOffsets / TileOffsetsをLONG8へ統一し、4GBを超える参照先にも対応する。
                if (type is not (3 or 4 or 16)) throw new InvalidDataException("Invalid pixel data reference format.");
                page.Position = external ? checked((long)value) : entry + 12;
                _output.Position = _output.Length;
                AlignOutput();
                long offsets = _output.Position;
                ulong single = 0;
                for (ulong item = 0; item < values; item++)
                {
                    token.ThrowIfCancellationRequested();
                    ulong relocated = checked(ReadNumber(page, size) + (ulong)origin);
                    if (values == 1) single = relocated;
                    else WriteNumber(_output, relocated, 8);
                }
                _output.Position = origin + entry + 2;
                WriteNumber(_output, 16, 2);
                _output.Position = origin + entry + 12;
                WriteNumber(_output, values == 1 ? single : (ulong)offsets, 8);
            }
            else if (tag is 330 or 34665 or 34853 || type is 13 or 18)
                throw new InvalidDataException("The temporary diff page contains subimages.");
            else if (external)
            {
                _output.Position = origin + entry + 12;
                WriteNumber(_output, checked(value + (ulong)origin), 8);
            }
        }
        long nextLink = directory + 8 + checked((long)count * 20);
        page.Position = nextLink;
        if (ReadNumber(page, 8) != 0) throw new InvalidDataException("The temporary diff file contains more than one page.");
        _output.Position = _previousLink;
        WriteNumber(_output, checked((ulong)(origin + directory)), 8);
        _previousLink = origin + nextLink;
        _output.Position = _output.Length;
    }

    private void AlignOutput()
    {
        while (_output.Position % 8 != 0) _output.WriteByte(0);
    }

    private static int TypeSize(ushort type) => type switch
    {
        1 or 2 or 6 or 7 => 1,
        3 or 8 => 2,
        4 or 9 or 11 or 13 => 4,
        5 or 10 or 12 or 16 or 17 or 18 => 8,
        _ => throw new InvalidDataException("Invalid tag type in the temporary diff page.")
    };

    private static ulong ReadNumber(Stream stream, int size)
    {
        Span<byte> bytes = stackalloc byte[8];
        stream.ReadExactly(bytes[..size]);
        return size switch
        {
            2 => BinaryPrimitives.ReadUInt16LittleEndian(bytes),
            4 => BinaryPrimitives.ReadUInt32LittleEndian(bytes),
            _ => BinaryPrimitives.ReadUInt64LittleEndian(bytes)
        };
    }

    private static void WriteNumber(Stream stream, ulong value, int size)
    {
        Span<byte> bytes = stackalloc byte[8];
        if (size == 2) BinaryPrimitives.WriteUInt16LittleEndian(bytes, checked((ushort)value));
        else BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        stream.Write(bytes[..size]);
    }

    public void Dispose() => _output.Dispose();
}
