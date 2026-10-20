using System.Security.Cryptography;
using System.Text;

namespace DeviceDeck.Bridge.Images;

/// <summary>Thumbnails on disk under %LocalAppData%\DeviceDeck\cache\thumbs, one folder per device, trimmed oldest-first.</summary>
public sealed class ThumbnailCache(string folder, long maxBytes = 2L * 1024 * 1024 * 1024)
{
    public string Folder { get; } = folder;

    public long MaxBytes { get; } = maxBytes;

    public string PathFor(string deviceId, string assetId, int size) =>
        Path.Combine(Folder, Hash(deviceId), $"{Hash(assetId)}-{size}.img");

    public byte[]? TryRead(string deviceId, string assetId, int size)
    {
        var path = PathFor(deviceId, assetId, size);
        try
        {
            var bytes = File.ReadAllBytes(path);
            File.SetLastAccessTimeUtc(path, DateTime.UtcNow);
            return bytes.Length == 0 ? null : bytes;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Write(string deviceId, string assetId, int size, byte[] bytes)
    {
        var path = PathFor(deviceId, assetId, size);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + "." + Environment.CurrentManagedThreadId + ".tmp";
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: true);
        }
        catch (IOException)
        {
            // A cache write that fails only costs a re-fetch later.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Deletes the least recently used files until the cache is under 80% of <see cref="MaxBytes"/>.</summary>
    public void Trim()
    {
        if (!Directory.Exists(Folder))
        {
            return;
        }

        var files = new DirectoryInfo(Folder).EnumerateFiles("*", SearchOption.AllDirectories).ToList();
        var total = files.Sum(f => f.Length);
        if (total <= MaxBytes)
        {
            return;
        }

        var target = MaxBytes * 8 / 10;
        foreach (var file in files.OrderBy(f => f.LastAccessTimeUtc))
        {
            if (total <= target)
            {
                break;
            }

            try
            {
                total -= file.Length;
                file.Delete();
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0, 12));
}
