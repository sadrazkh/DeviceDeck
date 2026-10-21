using DeviceDeck.Bridge.Tests.Support;
using DeviceDeck.Core.Transfers;
using DeviceDeck.Core.Transfers.Handlers;

namespace DeviceDeck.Bridge.Tests;

/// <summary>Round 3: photos into an iPhone app go into a dated folder, in parts of 200, so Select All in Files saves them in one go.</summary>
public sealed class PhotosToAppTests
{
    [Theory]
    [InlineData(1, new[] { "F" }, new[] { 1 })]
    [InlineData(200, new[] { "F" }, new[] { 200 })]
    [InlineData(201, new[] { "F/1-200", "F/201-201" }, new[] { 200, 1 })]
    [InlineData(450, new[] { "F/1-200", "F/201-400", "F/401-450" }, new[] { 200, 200, 50 })]
    public void A_send_is_split_into_folders_of_200(int count, string[] paths, int[] counts)
    {
        var parts = PhotosToAppHandler.Parts("F", count);
        Assert.Equal(paths, parts.Select(p => p.Path));
        Assert.Equal(counts, parts.Select(p => p.Count));
    }

    [Fact]
    public async Task A_big_send_lands_in_parts_of_200_inside_the_dated_folder()
    {
        var phone = new PhotosTestPhone();
        await using var host = await PhotosBackupHost.StartAsync(phone);
        var pc = Path.Combine(host.Root, "pc", "Holiday");
        Directory.CreateDirectory(pc);
        for (var i = 1; i <= 205; i++)
        {
            await File.WriteAllBytesAsync(Path.Combine(pc, $"IMG_{i:0000}.JPG"), [(byte)i]);
        }

        var response = await new BridgeCalls(host.Transport).CallAsync("photos.importToApp",
            new { deviceId = phone.Summary.Id, appId = PhotosTestPhone.AppId, files = new[] { pc } });
        var result = response["result"]!;
        var folder = (string)result["folder"]!;
        Assert.Equal([$"{folder}/1-200", $"{folder}/201-205"], result["parts"]!.AsArray().Select(p => (string?)p!["path"]));
        await Wait.ForStateAsync(host.Queue, (string)result["jobId"]!, TransferState.Done);

        Assert.Equal(200, phone.Documents.Keys.Count(k => k.StartsWith($"/{folder}/1-200/", StringComparison.Ordinal)));
        Assert.Equal(5, phone.Documents.Keys.Count(k => k.StartsWith($"/{folder}/201-205/", StringComparison.Ordinal)));
        Assert.Equal([1], phone.Documents[$"/{folder}/1-200/IMG_0001.JPG"]);
        Assert.Equal([205], phone.Documents[$"/{folder}/201-205/IMG_0205.JPG"]);
    }
}
