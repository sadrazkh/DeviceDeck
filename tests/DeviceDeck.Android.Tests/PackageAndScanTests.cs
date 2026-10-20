using System.IO.Compression;
using DeviceDeck.Android.Apps;
using DeviceDeck.Android.Photos;
using DeviceDeck.Core.Abstractions;

namespace DeviceDeck.Android.Tests;

public class PackageArchiveTests
{
    private static readonly string[] Arm64 = ["arm64-v8a", "armeabi-v7a", "armeabi"];

    private static string Bundle(string extension, params (string Name, int Size)[] entries)
    {
        string path = Path.Combine(Path.GetTempPath(), $"dd-bundle-{Guid.NewGuid():N}{extension}");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, size) in entries)
        {
            using var stream = zip.CreateEntry(name).Open();
            stream.Write(new byte[size]);
        }
        return path;
    }

    [Fact]
    public void Bundletool_apks_takes_splits_only_and_drops_foreign_abis()
    {
        string apks = Bundle(".apks",
            ("toc.pb", 10),
            ("splits/base-master.apk", 100),
            ("splits/base-arm64_v8a.apk", 20),
            ("splits/base-x86_64.apk", 20),
            ("splits/base-xxhdpi.apk", 5),
            ("splits/base-fa.apk", 3),
            ("standalones/standalone-x86_64_hdpi.apk", 500),
            ("universal.apk", 600));
        using var plan = PackageArchive.Prepare([apks], Arm64);
        Assert.Equal(["base-master.apk", "base-arm64_v8a.apk", "base-xxhdpi.apk", "base-fa.apk"],
            plan.Apks.Select(p => Path.GetFileName(p)[4..]));
        Assert.Empty(plan.Obbs);
        Assert.All(plan.Apks, p => Assert.True(File.Exists(p)));
        string folder = Path.GetDirectoryName(plan.Apks[0])!;
        plan.Dispose();
        Assert.False(Directory.Exists(folder)); // temp files cleaned up
    }

    [Fact]
    public void Sai_style_apks_and_apkm_without_a_splits_folder()
    {
        string apkm = Bundle(".apkm", ("base.apk", 100), ("split_config.arm64_v8a.apk", 10), ("split_config.x86.apk", 10), ("info.json", 5), ("icon.png", 5));
        using var plan = PackageArchive.Prepare([apkm], Arm64);
        Assert.Equal(["base.apk", "split_config.arm64_v8a.apk"], plan.Apks.Select(p => Path.GetFileName(p)[4..]));
    }

    [Fact]
    public void Xapk_obb_goes_to_android_obb()
    {
        string xapk = Bundle(".xapk", ("com.game.apk", 50), ("Android/obb/com.game/main.12.com.game.obb", 30), ("manifest.json", 2));
        using var plan = PackageArchive.Prepare([xapk], Arm64);
        Assert.Single(plan.Apks);
        var (local, device) = Assert.Single(plan.Obbs);
        Assert.Equal("/sdcard/Android/obb/com.game/main.12.com.game.obb", device);
        Assert.Equal(30, new FileInfo(local).Length);
    }

    [Fact]
    public void Bad_inputs_are_explained()
    {
        string encrypted = Path.Combine(Path.GetTempPath(), $"dd-{Guid.NewGuid():N}.apkm");
        File.WriteAllBytes(encrypted, [1, 2, 3, 4, 5]);
        var notZip = Assert.Throws<DeviceException>(() => PackageArchive.Prepare([encrypted], Arm64));
        Assert.Contains("encrypted", notZip.Message, StringComparison.Ordinal);

        string empty = Bundle(".apks", ("toc.pb", 1));
        Assert.Contains("no APK", Assert.Throws<DeviceException>(() => PackageArchive.Prepare([empty], Arm64)).Message, StringComparison.Ordinal);

        string apk = Path.Combine(Path.GetTempPath(), $"dd-{Guid.NewGuid():N}.apk");
        File.WriteAllBytes(apk, [1]);
        Assert.Throws<DeviceException>(() => PackageArchive.Prepare([apk, empty], Arm64));
        Assert.Throws<DeviceException>(() => PackageArchive.Prepare([Path.ChangeExtension(apk, ".exe")], Arm64));
        Assert.Equal(DeviceErrorCodes.PathNotFound, Assert.Throws<DeviceException>(() => PackageArchive.Prepare([apk + ".missing"], Arm64)).Code);
        Assert.Throws<DeviceException>(() => PackageArchive.Prepare([], Arm64));
    }

    [Fact]
    public void Split_filter_keeps_everything_when_the_abi_list_is_unknown()
    {
        var kept = PackageArchive.FilterSplits([("base.apk", "a"), ("split_config.x86.apk", "b")], []);
        Assert.Equal(2, kept.Count);
    }

    [Theory]
    [InlineData("Success: created install session [1234567]", "1234567")]
    [InlineData("Failure [INSTALL_FAILED_INTERNAL_ERROR]", null)]
    [InlineData("Success", null)]
    public void Install_session_id(string output, string? session)
    {
        Assert.Equal(session, AndroidAppManager.ParseSession(output));
    }

    [Theory]
    [InlineData("com.whatsapp", true)]
    [InlineData("ir.mservices.market", true)]
    [InlineData("com.example.app_2", true)]
    [InlineData("whatsapp", false)]
    [InlineData("com.x; reboot", false)]
    [InlineData("com.x$(id)", false)]
    [InlineData("1com.x", false)]
    public void Package_names_are_validated_before_reaching_the_shell(string name, bool valid)
    {
        if (valid)
        {
            AndroidAppManager.ValidatePackageName(name);
        }
        else
        {
            Assert.Throws<DeviceException>(() => AndroidAppManager.ValidatePackageName(name));
        }
    }
}

public class MediaScannerTests
{
    [Theory]
    [InlineData("/sdcard/DCIM/a.jpg", "/storage/emulated/0/DCIM/a.jpg")]
    [InlineData("/sdcard", "/storage/emulated/0")]
    [InlineData("/storage/self/primary/x", "/storage/emulated/0/x")]
    [InlineData("/storage/3A1F-12C9/DCIM/b.jpg", "/storage/3A1F-12C9/DCIM/b.jpg")]
    [InlineData("/sdcardX/y", "/sdcardX/y")]
    public void Canonical_paths(string input, string expected)
    {
        Assert.Equal(expected, MediaScanner.CanonicalPath(input));
    }

    [Fact]
    public void Broadcast_is_explicit_and_percent_encoded()
    {
        Assert.Equal(
            "am broadcast -a android.intent.action.MEDIA_SCANNER_SCAN_FILE -d 'file:///storage/emulated/0/DCIM/%D8%B9%DA%A9%D8%B3%20%D9%87%D8%A7/it%27s%20%231.jpg' -p com.android.providers.media >/dev/null",
            MediaScanner.BroadcastCommand("/sdcard/DCIM/عکس ها/it's #1.jpg", "com.android.providers.media"));
        Assert.DoesNotContain(" -p ", MediaScanner.BroadcastCommand("/sdcard/a.jpg", null), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("package:com.android.providers.media\npackage:com.google.android.providers.media.module\n", "com.google.android.providers.media.module")]
    [InlineData("package:com.android.providers.media.module\npackage:com.android.providers.media\n", "com.android.providers.media.module")]
    [InlineData("package:com.android.providers.media\n", "com.android.providers.media")]
    [InlineData("", null)]
    public void Picks_the_mainline_media_provider_first(string pmOutput, string? expected)
    {
        Assert.Equal(expected, MediaScanner.PickProviderPackage(pmOutput));
    }
}
