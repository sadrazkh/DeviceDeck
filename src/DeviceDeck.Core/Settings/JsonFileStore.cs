using System.Text.Json;
using DeviceDeck.Core.Serialization;

namespace DeviceDeck.Core.Settings;

/// <summary>Reads and atomically writes one JSON file. A missing or unreadable file yields null; a corrupt one is kept aside as .bad.</summary>
public static class JsonFileStore
{
    public static T? Read<T>(string path) where T : class
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<T>(stream, DeviceDeckJson.Options);
        }
        catch (JsonException)
        {
            TryMoveAside(path);
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public static void Write<T>(string path, T value)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(folder);
        var temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, value, DeviceDeckJson.Options);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, path, overwrite: true);
    }

    private static void TryMoveAside(string path)
    {
        try
        {
            File.Move(path, path + ".bad", overwrite: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
