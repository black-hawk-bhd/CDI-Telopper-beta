using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Windows.Media.Imaging;

namespace EEWTelop.Wpf.Obs;

internal sealed record SubtitlePng(string Id, ReadOnlyMemory<byte> Data)
{
    private const int MaximumBytes = 2 * 1024 * 1024;
    private const int MaximumDimension = 4096;
    private static ReadOnlySpan<byte> Signature => [137, 80, 78, 71, 13, 10, 26, 10];

    public static byte[] ReadFile(string path)
    {
        using FileStream input = File.OpenRead(path);
        if (input.Length is < 33 or > MaximumBytes)
            throw new InvalidDataException("2 MB以下のPNG画像を選んでください。");

        var bytes = new byte[(int)input.Length];
        input.ReadExactly(bytes);
        if (input.ReadByte() != -1 || !IsSupported(bytes))
            throw new InvalidDataException("縦横4096ピクセル以下の有効なPNG画像を選んでください。");

        return bytes;
    }

    public static SubtitlePng? FromBase64(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > ((MaximumBytes + 2) / 3) * 4)
            return null;

        var bytes = new byte[checked((value.Length + 3) / 4 * 3)];
        if (!Convert.TryFromBase64String(value, bytes, out int count) || count > MaximumBytes ||
            !IsSupported(bytes.AsMemory(0, count)))
            return null;

        return new SubtitlePng(Convert.ToHexStringLower(SHA256.HashData(bytes.AsSpan(0, count))), bytes.AsMemory(0, count));
    }

    private static bool IsSupported(ReadOnlyMemory<byte> data)
    {
        ReadOnlySpan<byte> bytes = data.Span;
        if (bytes.Length < 33 || !bytes.StartsWith(Signature) ||
            !bytes.Slice(12, 4).SequenceEqual("IHDR"u8) ||
            BinaryPrimitives.ReadUInt32BigEndian(bytes[16..]) is < 1 or > MaximumDimension ||
            BinaryPrimitives.ReadUInt32BigEndian(bytes[20..]) is < 1 or > MaximumDimension)
            return false;

        try
        {
            using var stream = new MemoryStream(data.ToArray(), writable: false);
            var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            return decoder.Frames.Count == 1;
        }
        catch (Exception exception) when (exception is IOException or FileFormatException or NotSupportedException or ArgumentException)
        {
            return false;
        }
    }
}
