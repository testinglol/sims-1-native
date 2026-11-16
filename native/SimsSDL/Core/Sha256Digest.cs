using System.Security.Cryptography;

namespace SimsSDL.Core;

public static class SHA256Digest
{
    public static byte[] Hash(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return SHA256.HashData(data);
    }

    public static byte[] Hash(ReadOnlySpan<byte> data) => SHA256.HashData(data);

    public static string Hex(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    }

    public static string Hex(ReadOnlySpan<byte> data) =>
        Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
}
