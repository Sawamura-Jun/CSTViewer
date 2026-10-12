using System.Buffers.Binary;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NetVips;
using VipsImage = NetVips.Image;

namespace CSTViewer;

/// <summary>画素を展開せず、選択ページのTIFF圧縮・ビット数・白黒の極性を読む。</summary>
internal sealed record TiffEncoding(ushort Compression, ushort BitsPerSample, ushort Photometric,
    ushort Predictor, bool IsBigTiff, bool IsLittleEndian, long CompressionValueOffset)
{
    public static TiffEncoding Read(string path, int pageIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        using var stream = File.OpenRead(path);
        Span<byte> order = stackalloc byte[2];
        stream.ReadExactly(order);
        bool little = order.SequenceEqual("II"u8);
        if (!little && !order.SequenceEqual("MM"u8)) throw new InvalidDataException("Invalid TIFF byte order.");
        ulong version = ReadNumber(stream, 2, little);
        bool big = version == 43;
        if (version is not (42 or 43)) throw new InvalidDataException("Invalid TIFF header.");
        if (big && (ReadNumber(stream, 2, little) != 8 || ReadNumber(stream, 2, little) != 0))
            throw new InvalidDataException("Unsupported BigTIFF offset format.");
        int valueSize = big ? 8 : 4;
        ulong offset = ReadNumber(stream, valueSize, little);
        int entrySize = big ? 20 : 12;
        var visited = new HashSet<ulong>();
        for (int page = 0; offset != 0; page++)
        {
            if (!visited.Add(offset) || offset > (ulong)stream.Length)
                throw new InvalidDataException("Invalid TIFF page reference.");
            stream.Position = (long)offset;
            ulong count = ReadNumber(stream, big ? 8 : 2, little);
            long entriesStart = stream.Position;
            // ファイル内に収まる件数だけを認め、破損したIFDで長時間ループしない。
            long remaining = stream.Length - entriesStart - valueSize;
            if (remaining < 0 || count > (ulong)(remaining / entrySize))
                throw new InvalidDataException("Invalid TIFF tag list.");
            if (page == pageIndex)
            {
                ushort compression = 1, bits = 1, photometric = 0, predictor = 1;
                long compressionOffset = -1;
                for (ulong index = 0; index < count; index++)
                {
                    long entryStart = entriesStart + (long)index * entrySize;
                    stream.Position = entryStart;
                    ushort tag = (ushort)ReadNumber(stream, 2, little);
                    if (tag is not (258 or 259 or 262 or 317)) continue;
                    if (ReadNumber(stream, 2, little) != 3)
                        throw new InvalidDataException("The TIFF encoding tag is not of type SHORT.");
                    ulong values = ReadNumber(stream, valueSize, little);
                    if (values == 0) throw new InvalidDataException("The TIFF encoding tag has no value.");
                    long valueOffset = stream.Position;
                    if (values > (ulong)(valueSize / 2))
                    {
                        ulong dataOffset = ReadNumber(stream, valueSize, little);
                        if (dataOffset > (ulong)Math.Max(0, stream.Length - 2))
                            throw new InvalidDataException("Invalid TIFF tag reference.");
                        valueOffset = (long)dataOffset;
                        stream.Position = valueOffset;
                    }
                    ushort value = (ushort)ReadNumber(stream, 2, little);
                    switch (tag)
                    {
                        case 258: bits = value; break;
                        case 259: compression = value; compressionOffset = valueOffset; break;
                        case 262: photometric = value; break;
                        case 317: predictor = value; break;
                    }
                }
                return new TiffEncoding(compression, bits, photometric, predictor, big, little, compressionOffset);
            }
            stream.Position = entriesStart + (long)count * entrySize;
            offset = ReadNumber(stream, valueSize, little);
        }
        throw new ArgumentOutOfRangeException(nameof(pageIndex), "The requested page does not exist in the TIFF file.");
    }

    public void Save(VipsImage cropped, string outputPath)
    {
        if (Compression == 3)
        {
            // libvipsが書けないGroup 3だけ、切り抜いた1bit領域をWPFのエンコーダーへ渡す。
            if (BitsPerSample != 1 || cropped.Bands != 1 || IsBigTiff)
                throw new NotSupportedException("Cannot preserve the encoding settings of this Group 3 TIFF.");
            byte[] pixels = cropped.WriteToMemory<byte>();
            var gray = BitmapSource.Create(cropped.Width, cropped.Height, 96, 96, PixelFormats.Gray8,
                null, pixels, cropped.Width);
            var monochrome = new FormatConvertedBitmap(gray, PixelFormats.BlackWhite, null, 0);
            var encoder = new TiffBitmapEncoder { Compression = TiffCompressOption.Ccitt3 };
            encoder.Frames.Add(BitmapFrame.Create(monochrome));
            using var stream = File.Create(outputPath);
            encoder.Save(stream);
            return;
        }
        Enums.ForeignTiffCompression compression = Compression switch
        {
            1 => Enums.ForeignTiffCompression.None,
            4 => Enums.ForeignTiffCompression.Ccittfax4,
            5 => Enums.ForeignTiffCompression.Lzw,
            7 => Enums.ForeignTiffCompression.Jpeg,
            8 or 32946 => Enums.ForeignTiffCompression.Deflate,
            32773 => Enums.ForeignTiffCompression.Packbits,
            34712 => Enums.ForeignTiffCompression.Jp2k,
            50000 => Enums.ForeignTiffCompression.Zstd,
            50001 => Enums.ForeignTiffCompression.Webp,
            _ => throw new NotSupportedException($"Cannot preserve the source image's TIFF compression format (tag value {Compression}).")
        };
        Enums.ForeignTiffPredictor predictor = Predictor switch
        {
            1 => Enums.ForeignTiffPredictor.None,
            2 => Enums.ForeignTiffPredictor.Horizontal,
            3 => Enums.ForeignTiffPredictor.Float,
            _ => throw new NotSupportedException($"Unsupported TIFF predictor ({Predictor}).")
        };
        int? bitdepth = cropped.Bands == 1 && BitsPerSample is 1 or 2 or 4 ? BitsPerSample : null;
        cropped.Tiffsave(outputPath, compression: compression, predictor: predictor, bitdepth: bitdepth,
            miniswhite: Photometric == 0, bigtiff: IsBigTiff);
        if (Compression == 32946)
        {
            // 旧Deflateタグも同じzlib方式なので、書き込み後のタグ値だけを元の値に戻す。
            TiffEncoding saved = Read(outputPath, 0);
            using var stream = File.Open(outputPath, FileMode.Open, FileAccess.Write);
            stream.Position = saved.CompressionValueOffset;
            Span<byte> value = stackalloc byte[2];
            if (saved.IsLittleEndian) BinaryPrimitives.WriteUInt16LittleEndian(value, Compression);
            else BinaryPrimitives.WriteUInt16BigEndian(value, Compression);
            stream.Write(value);
        }
    }

    private static ulong ReadNumber(Stream stream, int size, bool little)
    {
        Span<byte> buffer = stackalloc byte[8];
        stream.ReadExactly(buffer[..size]);
        return size switch
        {
            2 => little ? BinaryPrimitives.ReadUInt16LittleEndian(buffer) : BinaryPrimitives.ReadUInt16BigEndian(buffer),
            4 => little ? BinaryPrimitives.ReadUInt32LittleEndian(buffer) : BinaryPrimitives.ReadUInt32BigEndian(buffer),
            _ => little ? BinaryPrimitives.ReadUInt64LittleEndian(buffer) : BinaryPrimitives.ReadUInt64BigEndian(buffer)
        };
    }
}
