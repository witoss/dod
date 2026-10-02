using System.Buffers.Binary;

namespace Dod.Api.Accounts;

internal static class AvatarFormat
{
    // Check raster format and dimensions without allocating a decoded image from untrusted input.
    public static string? Detect(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 33 && data[..8].SequenceEqual(new byte[] { 137,80,78,71,13,10,26,10 }) && data.Slice(12,4).SequenceEqual("IHDR"u8))
        {
            var width = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(16,4));
            var height = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(20,4));
            return width is > 0 and <= 2048 && height is > 0 and <= 2048 ? "image/png" : null;
        }
        if (data.Length < 4 || data[0] != 255 || data[1] != 216 || data[^2] != 255 || data[^1] != 217) return null;
        var offset = 2;
        while (offset + 4 <= data.Length)
        {
            if (data[offset++] != 255) return null;
            while (offset < data.Length && data[offset] == 255) offset++;
            if (offset >= data.Length) return null;
            var marker = data[offset++];
            if (marker is 0xDA or 0xD9) return null;
            if (offset + 2 > data.Length) return null;
            var length = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset,2));
            if (length < 2 || offset + length > data.Length) return null;
            if (marker is 0xC0 or 0xC1 or 0xC2)
            {
                if (length < 8) return null;
                var height = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset+3,2));
                var width = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset+5,2));
                return width is > 0 and <= 2048 && height is > 0 and <= 2048 ? "image/jpeg" : null;
            }
            offset += length;
        }
        return null;
    }
}
