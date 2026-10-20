namespace DeviceDeck.Core.Fake;

public sealed class FakeOptions
{
    /// <summary>How long a phone takes from "seen" to Ready when the provider starts. Zero in tests.</summary>
    public TimeSpan ArrivalDelay { get; init; } = TimeSpan.FromMilliseconds(400);

    /// <summary>Simulated copy speed; null copies as fast as the disk allows (tests).</summary>
    public long? BytesPerSecond { get; init; } = 60L * 1024 * 1024;

    /// <summary>How long a rebooting or shut-down phone stays away before it comes back.</summary>
    public TimeSpan RebootDelay { get; init; } = TimeSpan.FromSeconds(6);

    /// <summary>Simulated photo-library indexing on arrival (raises <see cref="Extensions.IPhotoIndexProgressSource.IndexProgress"/>).</summary>
    public TimeSpan IndexDuration { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>The phones to simulate; the plan's sample data when null.</summary>
    public IReadOnlyList<FakeDeviceSpec>? Devices { get; init; }

    public static FakeOptions ForTests(params FakeDeviceSpec[] devices) => new()
    {
        ArrivalDelay = TimeSpan.Zero,
        BytesPerSecond = null,
        RebootDelay = TimeSpan.FromMilliseconds(50),
        IndexDuration = TimeSpan.Zero,
        Devices = devices.Length == 0 ? null : devices,
    };
}
