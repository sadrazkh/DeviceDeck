using System.Buffers.Binary;
using System.Security.Cryptography;
using DeviceDeck.Core.Abstractions;

namespace DeviceDeck.Core.Photos;

/// <summary>
/// A cheap fingerprint of a photo file: SHA-256 over its size and its first, middle and last 64 KB (the whole file when
/// it is small). With the same size and the same name it tells a photo already in the destination without copying it:
/// camera files differ in their first bytes (the time to the millisecond, the camera's own identifiers) and all through
/// their picture data.
/// </summary>
public static class PhotoBackupSample
{
    public const int Part = 64 * 1024;

    /// <summary>What to read for a file of <paramref name="size"/> bytes: all of it up to three parts, else the first, the middle and the last part.</summary>
    public static IReadOnlyList<(long Offset, int Count)> Ranges(long size) =>
        size <= 3L * Part ? [(0, (int)size)] : [(0, Part), (size / 2 - Part / 2, Part), (size - Part, Part)];

    public static byte[] Of(long size, IReadOnlyList<byte[]> parts)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(length, size);
        hash.AppendData(length);
        foreach (var part in parts)
        {
            hash.AppendData(part);
        }

        return hash.GetHashAndReset();
    }

    /// <summary>The sample of bytes in memory (a HEIC that became a JPEG keeps its sample with the JPEG).</summary>
    public static byte[] Of(ReadOnlySpan<byte> bytes)
    {
        var parts = new List<byte[]>();
        foreach (var (offset, count) in Ranges(bytes.Length))
        {
            parts.Add(bytes.Slice((int)offset, count).ToArray());
        }

        return Of(bytes.Length, parts);
    }

    /// <summary>The sample of a photo on the phone; null when the phone answered short (the file changed or is gone).</summary>
    public static async Task<byte[]?> OfPhoneAsync(IPhotoRangeReader reader, string assetId, long size, CancellationToken ct)
    {
        var parts = new List<byte[]>();
        foreach (var (offset, count) in Ranges(size))
        {
            var part = await reader.ReadRangeAsync(assetId, offset, count, ct).ConfigureAwait(false);
            if (part.Length != count)
            {
                return null;
            }

            parts.Add(part);
        }

        return Of(size, parts);
    }

    /// <summary>The sample of a file on the PC; null when it isn't there with that size, or can't be read.</summary>
    public static async Task<byte[]?> OfFileAsync(string path, long size, CancellationToken ct)
    {
        try
        {
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.Asynchronous | FileOptions.RandomAccess);
            if (file.Length != size)
            {
                return null;
            }

            var parts = new List<byte[]>();
            foreach (var (offset, count) in Ranges(size))
            {
                var part = new byte[count];
                file.Position = offset;
                await file.ReadExactlyAsync(part, ct).ConfigureAwait(false);
                parts.Add(part);
            }

            return Of(size, parts);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>"IMG_0001 (2).HEIC" → "IMG_0001": the name a copy was given, without the number a clash added or the extension.</summary>
    public static string Stem(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var cut = stem.LastIndexOf(" (", StringComparison.Ordinal);
        return cut > 0 && stem.EndsWith(')') && int.TryParse(stem.AsSpan(cut + 2, stem.Length - cut - 3), out _) ? stem[..cut] : stem;
    }
}
