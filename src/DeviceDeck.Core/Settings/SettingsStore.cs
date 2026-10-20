using System.Text.Json;
using System.Text.Json.Nodes;
using DeviceDeck.Core.Serialization;

namespace DeviceDeck.Core.Settings;

/// <summary>The app's settings, persisted in %LocalAppData%\DeviceDeck\settings.json.</summary>
public sealed class SettingsStore
{
    private readonly string _path;
    private readonly Lock _gate = new();
    private AppSettings _current;

    public SettingsStore(string path)
    {
        _path = path;
        _current = Load(path);
    }

    public event EventHandler<AppSettings>? Changed;

    public AppSettings Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>Applies a <c>Partial&lt;Settings&gt;</c>. Unknown keys and bad values throw <see cref="ArgumentException"/>; nothing is saved then.</summary>
    public AppSettings Update(JsonObject? patch)
    {
        AppSettings updated;
        lock (_gate)
        {
            var node = JsonSerializer.SerializeToNode(_current, DeviceDeckJson.Options)!.AsObject();
            foreach (var (key, value) in patch ?? [])
            {
                if (!node.ContainsKey(key))
                {
                    throw new ArgumentException($"Unknown setting '{key}'.");
                }

                node[key] = value?.DeepClone();
            }

            try
            {
                updated = node.Deserialize<AppSettings>(DeviceDeckJson.Options)
                    ?? throw new ArgumentException("Settings must be an object.");
            }
            catch (JsonException ex)
            {
                throw new ArgumentException($"A settings value has the wrong type: {ex.Message}", ex);
            }

            updated.Validate();
            if (updated == _current)
            {
                return updated;
            }

            JsonFileStore.Write(_path, updated);
            _current = updated;
        }

        Changed?.Invoke(this, updated);
        return updated;
    }

    private static AppSettings Load(string path)
    {
        var loaded = JsonFileStore.Read<AppSettings>(path);
        if (loaded is null)
        {
            return new AppSettings();
        }

        try
        {
            loaded.Validate();
            return loaded;
        }
        catch (ArgumentException)
        {
            return new AppSettings();
        }
    }
}
