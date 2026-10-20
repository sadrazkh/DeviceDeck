using System.Security.Cryptography;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Ios.Device;
using DeviceDeck.Ios.Protocol.Afc;
using DeviceDeck.Ios.Tests.Fakes;

namespace DeviceDeck.Ios.Tests;

/// <summary>Hashing camera-roll files for the duplicate search: read-only, over one AFC connection, only as far as asked.</summary>
public sealed class CleanupTests : IAsyncLifetime
{
    private const string Udid = "00008140-000A1B2C3D4E801E";
    private static CancellationToken Ct => TestContext.Timeout();

    private readonly string _dir = Directory.CreateTempSubdirectory("devicedeck-ios-clean-").FullName;
    private readonly FakeUsbmux _mux = new();
    private readonly IosDeviceProvider _provider;

    public CleanupTests()
    {
        _provider = new IosDeviceProvider(_mux, new IosProviderOptions
        {
            ApplePairRecordDirectory = Path.Combine(_dir, "apple"),
            LocalPairRecordDirectory = Path.Combine(_dir, "local"),
            CacheDirectory = Path.Combine(_dir, "cache"),
            RetryInterval = TimeSpan.FromMilliseconds(40),
            ReconnectInterval = TimeSpan.FromMilliseconds(50),
        });
    }

    private async Task<(IosDevice Device, FakePhone Phone)> ReadyAsync()
    {
        _mux.PairRecords[Udid] = TestPairing.StoredRecordXml();
        var phone = new FakePhone(Udid) { ExpectedHostId = TestPairing.HostId };
        _mux.Attach(1, phone);
        await _provider.StartAsync(Ct);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (_provider.Find(Udid)?.Summary.State != DeviceState.Ready)
        {
            Assert.True(DateTime.UtcNow < deadline, "the phone never became ready");
            await Task.Delay(20, Ct);
        }

        return (_provider.Find(Udid)!, phone);
    }

    private static byte[] Bytes(int length, byte seed, byte? tail = null)
    {
        var bytes = Enumerable.Range(0, length).Select(i => (byte)(seed + i % 7)).ToArray();
        if (tail is { } t)
        {
            bytes[^1] = t;
        }

        return bytes;
    }

    [Fact]
    public async Task Openings_and_whole_files_hash_like_SHA256_and_missing_or_foreign_ids_are_left_out()
    {
        var (device, phone) = await ReadyAsync();
        var original = Bytes(3 * AfcClient.ReadChunk + 10, 1);
        phone.Media.AddFile("/DCIM/100APPLE/IMG_0001.HEIC", original);
        phone.Media.AddFile("/DCIM/100APPLE/IMG_0002.HEIC", original.ToArray());
        phone.Media.AddFile("/DCIM/101APPLE/IMG_0103.HEIC", Bytes(original.Length, 1, tail: 0xFF)); // same opening, other ending

        string[] ids = ["DCIM/100APPLE/IMG_0001.HEIC", "DCIM/100APPLE/IMG_0002.HEIC", "DCIM/101APPLE/IMG_0103.HEIC", "DCIM/100APPLE/GONE.HEIC", "../etc/passwd"];
        var openings = await device.HashAssetsAsync(ids, 64 * 1024, null, Ct);
        Assert.Equal(3, openings.Count);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(original.AsSpan(0, 64 * 1024))), openings[ids[0]]);
        Assert.Equal(openings[ids[0]], openings[ids[2]]);
        Assert.True(phone.Media.MaxReadSeen <= 64 * 1024, $"read {phone.Media.MaxReadSeen} bytes at once for a 64 KiB opening");

        var reported = new List<long>();
        var whole = await device.HashAssetsAsync(ids[..3], null, new Inline(reported.Add), Ct);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(original)), whole[ids[0]]);
        Assert.Equal(whole[ids[0]], whole[ids[1]]);
        Assert.NotEqual(whole[ids[0]], whole[ids[2]]);
        Assert.Equal(3L * original.Length, reported[^1]);

        // Read-only: nothing was written, removed or renamed.
        Assert.DoesNotContain(phone.Media.Operations, op => op is AfcOperation.FileWrite or AfcOperation.RemovePath or AfcOperation.RemovePathAndContents
            or AfcOperation.RenamePath or AfcOperation.MakeDir);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class Inline(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }
}
