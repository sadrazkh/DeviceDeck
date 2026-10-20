using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DeviceDeck.Android.Apps;

/// <summary>
/// Labels and icons read from APKs, keyed by package + APK path + version code (the path under /data/app changes
/// on every update, so a key never outlives the APK it describes). Kept in memory and, when a directory is
/// configured, on disk: labels.json plus icons/&lt;hash&gt;.png.
/// </summary>
internal sealed class AppMetadataCache
{
    private readonly string? _directory;
    private readonly ConcurrentDictionary<string, string> _labels = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte[]> _icons = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private int _loaded;
    private int _dirty;

    public AppMetadataCache(string? directory)
    {
        _directory = directory;
    }

    public static string Key(string packageName, string? apkPath, long? versionCode) => $"{packageName}|{apkPath}|{versionCode}";

    public bool TryGetLabel(string key, out string label)
    {
        EnsureLoaded();
        return _labels.TryGetValue(key, out label!);
    }

    public void SetLabel(string key, string label)
    {
        if (_labels.TryGetValue(key, out string? existing) && existing == label)
        {
            return;
        }
        _labels[key] = label;
        Interlocked.Exchange(ref _dirty, 1);
    }

    public byte[]? TryGetIcon(string key)
    {
        if (_icons.TryGetValue(key, out byte[]? icon))
        {
            return icon;
        }
        string? file = IconFile(key);
        if (file != null && File.Exists(file))
        {
            try
            {
                icon = File.ReadAllBytes(file);
                _icons[key] = icon;
                return icon;
            }
            catch (IOException)
            {
            }
        }
        return null;
    }

    public void SetIcon(string key, byte[] png)
    {
        _icons[key] = png;
        string? file = IconFile(key);
        if (file == null)
        {
            return;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file, png);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A cache that can't be written is only slower.
        }
    }

    public async Task SaveAsync(CancellationToken ct)
    {
        if (_directory == null || Interlocked.Exchange(ref _dirty, 0) == 0)
        {
            return;
        }
        await _saveGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_directory);
            string target = Path.Combine(_directory, "labels.json");
            string temp = target + ".tmp";
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(new SortedDictionary<string, string>(_labels)), ct).ConfigureAwait(false);
            File.Move(temp, target, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private void EnsureLoaded()
    {
        if (_directory == null || Interlocked.Exchange(ref _loaded, 1) == 1)
        {
            return;
        }
        try
        {
            string file = Path.Combine(_directory, "labels.json");
            if (File.Exists(file) && JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file)) is { } stored)
            {
                foreach (var (key, label) in stored)
                {
                    _labels.TryAdd(key, label);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }
    }

    private string? IconFile(string key)
    {
        if (_directory == null)
        {
            return null;
        }
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32].ToLowerInvariant();
        return Path.Combine(_directory, "icons", hash + ".png");
    }
}
