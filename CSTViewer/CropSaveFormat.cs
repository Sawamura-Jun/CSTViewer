using System.Buffers.Binary;
using System.IO;

namespace CSTViewer;

internal static class CropSaveFormat
{
    public static string Extension(string sourcePath)
    {
        string extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (!ViewerSettings.SupportedImageExtensions.Contains(extension))
            throw new NotSupportedException("Cannot save using the source image's file format.");
        return extension;
    }

    public static void ValidateOutput(string sourcePath, string outputPath)
    {
        string source = CanonicalExtension(Extension(sourcePath));
        string output = CanonicalExtension(Path.GetExtension(outputPath).ToLowerInvariant());
        if (source != output) throw new ArgumentException("Save in the same file format as the source image.", nameof(outputPath));
    }

    private static string CanonicalExtension(string extension) => extension switch
    {
        ".tiff" => ".tif", ".jpeg" => ".jpg", _ => extension
    };

    public static uint ReadBmpCompression(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[34];
        stream.ReadExactly(header);
        if (!header[..2].SequenceEqual("BM"u8)) throw new InvalidDataException("Invalid BMP header.");
        uint headerSize = BinaryPrimitives.ReadUInt32LittleEndian(header[14..]);
        if (headerSize == 12) return 0;
        if (headerSize < 40) throw new NotSupportedException("Cannot preserve the BMP header format.");
        return BinaryPrimitives.ReadUInt32LittleEndian(header[30..]);
    }

    public static bool IsProgressiveJpeg(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.ReadByte() != 0xFF || stream.ReadByte() != 0xD8) throw new InvalidDataException("Invalid JPEG header.");
        Span<byte> lengthBytes = stackalloc byte[2];
        while (stream.Position < stream.Length)
        {
            if (stream.ReadByte() != 0xFF) throw new InvalidDataException("Invalid JPEG marker.");
            int marker;
            do { marker = stream.ReadByte(); } while (marker == 0xFF);
            if (marker == 0xC0) return false;
            if (marker == 0xC2) return true;
            if (marker is 0xDA or 0xD9 or -1) break;
            stream.ReadExactly(lengthBytes);
            int length = BinaryPrimitives.ReadUInt16BigEndian(lengthBytes);
            if (length < 2 || length - 2 > stream.Length - stream.Position) throw new InvalidDataException("Invalid JPEG segment length.");
            stream.Seek(length - 2, SeekOrigin.Current);
        }
        throw new NotSupportedException("Cannot save using the source image's JPEG compression format.");
    }
}
