namespace DeviceDeck.Bridge.Images;

/// <summary>The last screenshots taken by tools.screenshot, served at https://device.local/shot/{deviceId}/{shotId}.</summary>
public sealed class ScreenshotStore(int keep = 20)
{
    private readonly Lock _gate = new();
    private readonly LinkedList<(string DeviceId, string ShotId, byte[] Bytes)> _shots = new();

    public string Add(string deviceId, byte[] png)
    {
        var shotId = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss-fff", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..6];
        lock (_gate)
        {
            _shots.AddFirst((deviceId, shotId, png));
            while (_shots.Count > keep)
            {
                _shots.RemoveLast();
            }
        }

        return shotId;
    }

    public byte[]? TryGet(string deviceId, string shotId)
    {
        lock (_gate)
        {
            foreach (var shot in _shots)
            {
                if (shot.DeviceId == deviceId && shot.ShotId == shotId)
                {
                    return shot.Bytes;
                }
            }
        }

        return null;
    }

    public static string UrlFor(string deviceId, string shotId) =>
        $"{DeviceImageHandler.Origin}shot/{Uri.EscapeDataString(deviceId)}/{Uri.EscapeDataString(shotId)}";
}
