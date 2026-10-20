using DeviceDeck.Android.Apk;
using DeviceDeck.Android.Tests.Infrastructure;
using SkiaSharp;

namespace DeviceDeck.Android.Tests;

public class ApkTests
{
    private static async Task<ApkMetadata> ReadAsync(byte[] apk, bool withIcon = true) =>
        await ApkInspector.ReadAsync(new ByteArrayRangeReader(apk), withIcon, CancellationToken.None);

    [Theory]
    [InlineData(ArscLayout.Dense, false, true)]
    [InlineData(ArscLayout.Dense, false, false)]
    [InlineData(ArscLayout.Offset16, false, true)]
    [InlineData(ArscLayout.Sparse, false, true)]
    [InlineData(ArscLayout.Dense, true, true)]
    [InlineData(ArscLayout.Offset16, true, false)]
    public async Task Reads_package_label_and_version_from_any_table_layout(ArscLayout layout, bool compact, bool utf8)
    {
        var metadata = await ReadAsync(TestApks.BitmapIcon(layout, compact, utf8), withIcon: false);
        Assert.Equal("com.test.app", metadata.PackageName);
        Assert.Equal("Test App", metadata.Label); // the default locale, not "fa"
        Assert.Equal("1.2.3", metadata.VersionName);
        Assert.Equal(42, metadata.VersionCode);
        Assert.Null(metadata.IconPng);
    }

    [Fact]
    public async Task Icon_uses_the_highest_density_bitmap()
    {
        var metadata = await ReadAsync(TestApks.BitmapIcon());
        using var icon = SKBitmap.Decode(metadata.IconPng);
        Assert.Equal(ApkInspector.IconSize, icon.Width);
        var center = icon.GetPixel(icon.Width / 2, icon.Height / 2);
        Assert.Equal(new SKColor(0, 200, 0), center); // the xxxhdpi green, not the mdpi red
    }

    [Fact]
    public async Task Adaptive_icon_with_a_vector_foreground_is_rendered_and_masked()
    {
        var metadata = await ReadAsync(TestApks.AdaptiveIcon());
        Assert.Equal("Adaptive", metadata.Label);
        using var icon = SKBitmap.Decode(metadata.IconPng);
        int size = icon.Width;
        var center = icon.GetPixel(size / 2, size / 2);
        Assert.True(center.Red > 240 && center.Green > 240 && center.Blue < 20, $"centre should be the yellow foreground, was {center}");
        var edge = icon.GetPixel(size / 10, size / 2);
        Assert.True(edge.Blue > 240 && edge.Red < 20, $"edge should be the blue background, was {edge}");
        Assert.Equal(0, icon.GetPixel(1, 1).Alpha); // rounded mask
    }

    [Fact]
    public async Task Literal_label_on_the_launcher_activity_is_used_when_the_application_has_none()
    {
        var manifest = TestApks.Manifest("com.test.literal", 1, "1", TestAttr.String("label", AndroidAttr.Label, "Literal Name"), null, launcherLabel: true);
        byte[] apk = ApkZip.Build(new Dictionary<string, byte[]> { ["AndroidManifest.xml"] = AxmlWriter.Write(manifest) });
        var metadata = await ReadAsync(apk);
        Assert.Equal("Literal Name", metadata.Label);
        Assert.Null(metadata.IconPng);
    }

    [Fact]
    public async Task Attributes_are_found_by_name_when_the_resource_map_was_stripped()
    {
        var manifest = TestApks.Manifest("com.test.stripped", 3, "3.0", TestAttr.String("label", AndroidAttr.Label, "Stripped"), null);
        byte[] apk = ApkZip.Build(new Dictionary<string, byte[]> { ["AndroidManifest.xml"] = AxmlWriter.Write(manifest, withResourceMap: false) });
        var metadata = await ReadAsync(apk);
        Assert.Equal("Stripped", metadata.Label);
        Assert.Equal("3.0", metadata.VersionName);
    }

    [Fact]
    public async Task Zip_reader_reads_stored_and_deflated_entries()
    {
        byte[] text = System.Text.Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("hello deflate ", 500)));
        byte[] zip = ApkZip.Build(new Dictionary<string, byte[]> { ["a.txt"] = text, ["resources.arsc"] = [1, 2, 3] });
        var reader = await ZipReader.OpenAsync(new ByteArrayRangeReader(zip), CancellationToken.None);
        Assert.Equal(8, reader.Entries.Single(e => e.Name == "a.txt").Method);
        Assert.Equal(0, reader.Entries.Single(e => e.Name == "resources.arsc").Method);
        Assert.Equal(text, await reader.ReadAsync("a.txt", CancellationToken.None));
        Assert.Equal([1, 2, 3], await reader.ReadAsync("resources.arsc", CancellationToken.None));
        Assert.Null(await reader.ReadAsync("missing", CancellationToken.None));
    }

    [Fact]
    public async Task Not_a_zip_is_reported()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => ZipReader.OpenAsync(new ByteArrayRangeReader(new byte[100]), CancellationToken.None));
    }

    [Fact]
    public async Task Remote_reader_fetches_ranges_with_dd()
    {
        byte[] apk = TestApks.BitmapIcon();
        var fake = new FakeAdbChannel();
        fake.Files["/data/app/x/base.apk"] = apk;
        var reader = await RemoteFileRangeReader.OpenAsync(fake, "/data/app/x/base.apk", CancellationToken.None);
        Assert.Equal(apk.Length, reader.Length);
        int third = apk.Length / 3;
        Assert.Equal(apk.AsSpan(third, third).ToArray(), await reader.ReadAsync(third, third, CancellationToken.None));
        Assert.Equal(apk.AsSpan(apk.Length - 10).ToArray(), await reader.ReadAsync(apk.Length - 10, 100, CancellationToken.None));

        var metadata = await ApkInspector.ReadAsync(reader, withIcon: true, CancellationToken.None);
        Assert.Equal("Test App", metadata.Label);
        Assert.NotNull(metadata.IconPng);
        Assert.All(fake.Commands.Where(c => c.StartsWith("dd ", StringComparison.Ordinal)), c => Assert.Contains("bs=4096", c, StringComparison.Ordinal));
        Assert.Empty(fake.Unmatched);
    }

    [Theory]
    [InlineData(10, 100, "skip=0 count=1", 0)]
    [InlineData(4095, 2, "skip=0 count=2", 0)]
    [InlineData(8192, 4096, "skip=2 count=1", 8192)]
    [InlineData(9000, 10000, "skip=2 count=3", 8192)]
    public void Dd_command_is_block_aligned(long offset, int count, string expected, long blockStart)
    {
        string command = RemoteFileRangeReader.DdCommand("/a b/base.apk", offset, count, out long start);
        Assert.Equal($"dd if='/a b/base.apk' bs=4096 {expected} 2>/dev/null", command);
        Assert.Equal(blockStart, start);
    }

    [Fact]
    public void Complex_dimension_and_fraction_values()
    {
        // 108dp: mantissa 108 << 8, radix 0 (23p0), unit dp (1).
        Assert.Equal(108f, ResValue.ComplexToFloat((108u << 8) | 0x1));
        // 16.7% as a fraction: 0.167 with radix 3 (0p23).
        uint mantissa = (uint)Math.Round(0.167 * (1 << 23));
        Assert.Equal(0.167f, ResValue.ComplexToFloat((mantissa << 8) | (3 << 4)), 3);
    }
}
