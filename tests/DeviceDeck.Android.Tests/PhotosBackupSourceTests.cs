using DeviceDeck.Core.Abstractions;

namespace DeviceDeck.Android.Tests;

/// <summary>MediaStore as a backup source: keys from path and size, nothing to complete, one query for the count and the plan.</summary>
public class PhotosBackupSourceTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task Keys_are_the_canonical_path_and_size_and_the_index_is_reused()
    {
        var (device, fake) = await DeviceTests.ReadyAsync("android-12-xiaomi-2201117tg");
        var source = Assert.IsAssignableFrom<IPhotoBackupSource>(device.Photos);

        var assets = await source.GetBackupAssetsAsync(None);
        Assert.Equal(5, assets.Count);
        var first = assets.Single(a => a.AssetId == "1001");
        Assert.Equal("file:/storage/emulated/0/DCIM/Camera/IMG_20240501_101500.jpg|3456789", first.Key);
        Assert.Equal("/storage/emulated/0/DCIM/Camera/IMG_20240501_101500.jpg", first.SourcePath);
        Assert.Equal((3456789L, PhotoKind.Photo), (first.Size, first.Kind));
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1714546500000), first.TakenAt);
        Assert.Equal(PhotoKind.Video, assets.Single(a => a.AssetId == "1002").Kind);
        Assert.Equal("عکس خانوادگی.heic", assets.Single(a => a.AssetId == "1005").FileName);
        Assert.All(assets, a => Assert.False(a.InCloudOnly || a.Hidden || a.Motion is not null));

        Assert.Same(assets, await source.CompleteAsync(assets, None));
        await source.GetBackupAssetsAsync(None);
        Assert.Single(fake.Commands, c => c.StartsWith("content query", StringComparison.Ordinal));
    }
}
