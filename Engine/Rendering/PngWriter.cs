using System.IO.Compression;
using System.Text;

namespace Engine.Rendering;

// Writes RGBA pixels to a .png file with no extra libraries. A PNG is a
// signature followed by "chunks" (header, compressed pixels, end), each
// with a CRC checksum; the pixel data is just rows prefixed with a filter
// byte and zlib-compressed — which .NET can do out of the box.
public static class PngWriter
{
    // `bottomUp` = rows arrive bottom row first (as glReadPixels returns them).
    public static void Save(string path, int width, int height, byte[] rgba, bool bottomUp = true)
    {
        using var raw = new MemoryStream();
        for (int y = 0; y < height; y++)
        {
            int row = bottomUp ? height - 1 - y : y;
            raw.WriteByte(0);  // filter type 0 = none
            raw.Write(rgba, row * width * 4, width * 4);
        }

        using var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            raw.WriteTo(z);

        using var file = File.Create(path);
        file.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

        var header = new byte[13];
        WriteBigEndian(header, 0, (uint)width);
        WriteBigEndian(header, 4, (uint)height);
        header[8] = 8;   // bits per channel
        header[9] = 6;   // color type 6 = RGBA
        WriteChunk(file, "IHDR", header);
        WriteChunk(file, "IDAT", compressed.ToArray());
        WriteChunk(file, "IEND", Array.Empty<byte>());
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        var lengthBytes = new byte[4];
        WriteBigEndian(lengthBytes, 0, (uint)data.Length);
        s.Write(lengthBytes);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(data);
        uint crc = Crc32(typeBytes, 0xFFFFFFFF);
        crc = Crc32(data, crc) ^ 0xFFFFFFFF;
        var crcBytes = new byte[4];
        WriteBigEndian(crcBytes, 0, crc);
        s.Write(crcBytes);
    }

    private static void WriteBigEndian(byte[] b, int offset, uint v)
    {
        b[offset] = (byte)(v >> 24);
        b[offset + 1] = (byte)(v >> 16);
        b[offset + 2] = (byte)(v >> 8);
        b[offset + 3] = (byte)v;
    }

    private static uint[]? _crcTable;

    private static uint Crc32(byte[] data, uint crc)
    {
        _crcTable ??= Enumerable.Range(0, 256).Select(n =>
        {
            uint c = (uint)n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            return c;
        }).ToArray();
        foreach (byte b in data) crc = _crcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }
}
