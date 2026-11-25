using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace ScalingWrites;

// A 64-bit hash that is the same in every process and on every machine. string.GetHashCode is
// randomized per process, so two app instances would disagree about where a key lives.
public static class StableHash
{
    public static ulong Of(string value)
    {
        Span<byte> digest = stackalloc byte[MD5.HashSizeInBytes];
        MD5.HashData(Encoding.UTF8.GetBytes(value), digest);
        return BinaryPrimitives.ReadUInt64LittleEndian(digest);
    }

    public static ulong Of(long value) => Of(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
}
