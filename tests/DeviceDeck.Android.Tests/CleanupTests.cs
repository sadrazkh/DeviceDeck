using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DeviceDeck.Android.Cleanup;
using DeviceDeck.Android.Tests.Infrastructure;
using DeviceDeck.Core.Abstractions;

namespace DeviceDeck.Android.Tests;

/// <summary>Clean-up on Android: hashing on the phone, the app-folder scan, diskstats caches and pm trim-caches.</summary>
public partial class CleanupTests
{
    private static readonly CancellationToken None = CancellationToken.None;
    private const string Set = "android-14-google-pixel-7";

    [GeneratedRegex(@"'((?:[^']|'\\'')*)'")]
    private static partial Regex Quoted();

    /// <summary>
    /// Answers MediaStore lookups by id from the fixture, and the hash loop with "index md5" for the files it knows:
    /// the phone's view of which files are equal.
    /// </summary>
    private static void PhoneWithFiles(FakeAdbChannel fake, Dictionary<string, string> contentByPath)
    {
        var rows = FixtureSet.Synthetic(Set).TryRead("content-query-media.txt")!.Split('\n').Where(l => l.StartsWith("Row:", StringComparison.Ordinal)).ToList();
        fake.On(c => c.StartsWith("content query", StringComparison.Ordinal) && c.Contains("_id IN (", StringComparison.Ordinal), c =>
        {
            var ids = Regex.Match(c, @"_id IN \(([0-9,]+)\)").Groups[1].Value.Split(',');
            return FakeReply.Text(string.Join('\n', rows.Where(r => ids.Any(id => r.Contains($"_id={id},", StringComparison.Ordinal)))) + "\n");
        });
        fake.On(c => c.StartsWith("i=0; for f in", StringComparison.Ordinal), c =>
        {
            var loop = c[..c.IndexOf("; do ", StringComparison.Ordinal)];
            var paths = Quoted().Matches(loop).Select(m => m.Groups[1].Value.Replace("'\\''", "'", StringComparison.Ordinal)).ToList();
            var prefix = Regex.Match(c, @"head -c (\d+)") is { Success: true } m ? m.Groups[1].Value : null;
            var output = new StringBuilder();
            for (var i = 0; i < paths.Count; i++)
            {
                if (contentByPath.TryGetValue(paths[i], out var content))
                {
                    var bytes = Encoding.UTF8.GetBytes(prefix is null ? content : content[..Math.Min(content.Length, 3)]);
                    output.Append(i).Append(' ').Append(Convert.ToHexStringLower(MD5.HashData(bytes))).Append('\n');
                }
            }

            return FakeReply.Text(output.ToString());
        });
    }

    [Fact]
    public async Task Hashes_come_from_the_phone_and_are_mapped_back_by_position()
    {
        var (device, fake) = await DeviceTests.ReadyAsync(Set, f => PhoneWithFiles(f, new()
        {
            ["/storage/emulated/0/DCIM/Camera/PXL_20240205_060012345.jpg"] = "abcdef",
            ["/storage/emulated/0/DCIM/Camera/PXL_20240203_120000999.mp4"] = "abcdef",
            ["/storage/emulated/0/Pictures/Screenshots/Screenshot_20240130-221500.png"] = "abcxyz",
        }));
        var reported = new List<long>();

        var whole = await device.HashAssetsAsync(["31001", "31003", "31004", "31002", "99999", "not-an-id"], null, new Inline(reported.Add), None);

        Assert.Equal(["31001", "31003", "31004"], whole.Keys.Order());
        Assert.Equal(whole["31001"], whole["31003"]);
        Assert.NotEqual(whole["31001"], whole["31004"]);
        Assert.Equal(2_876_543L + 187_654_321 + 987_654 + 4_012_345, reported[^1]); // the sizes of the files asked for and found
        var script = Assert.Single(fake.Commands, c => c.StartsWith("i=0;", StringComparison.Ordinal));
        Assert.Contains("md5sum < \"$f\"", script);
        Assert.Contains("'/storage/emulated/0/DCIM/Camera/PXL_20240203_120000999.mp4'", script);
        Assert.DoesNotContain(fake.Commands, c => c.Contains("pull", StringComparison.Ordinal)); // nothing copied to the PC

        var openings = await device.HashAssetsAsync(["31001", "31004"], 65_536, null, None);
        Assert.Equal(openings["31001"], openings["31004"]); // same opening bytes, different files
        Assert.Contains(fake.Commands, c => c.Contains("head -c 65536 \"$f\" | md5sum", StringComparison.Ordinal));
    }

    [Fact]
    public void Hash_script_quotes_names_and_parses_only_well_formed_lines()
    {
        var script = AndroidCleanup.HashScript(["/sdcard/DCIM/it's here.jpg", "/sdcard/$HOME.mp4"], null);
        Assert.StartsWith("i=0; for f in '/sdcard/DCIM/it'\\''s here.jpg' '/sdcard/$HOME.mp4'; do ", script);
        Assert.EndsWith("done 2>/dev/null; true", script);

        var parsed = AndroidCleanup.ParseHashes("0 D41D8CD98F00B204E9800998ECF8427E\r\n2 0cc175b9c0f1b6a831c399e269772661\nnoise\n1 tooshort\n").ToList();
        Assert.Equal([(0, "d41d8cd98f00b204e9800998ecf8427e"), (2, "0cc175b9c0f1b6a831c399e269772661")], parsed);
    }

    [Fact]
    public async Task One_find_lists_big_files_in_app_folders_and_names_the_folders_it_could_not_read()
    {
        const string output = """
            412000000 1727000000 /sdcard/Android/media/com.whatsapp/WhatsApp/Media/WhatsApp Video/VID-2024 copy.mp4
            105000000 1727000100 /sdcard/Download/Termux_v0.119.apk
            10485760 1727000200 /sdcard/Documents/scan.pdf
            10000 1727000300 /sdcard/Download/tiny.txt
            garbage line
            90000000 1727000400 /sdcard/Android/data/org.telegram.messenger/files/Telegram/Telegram Video/clip.mp4

            """;
        const string errors = "find: '/sdcard/Android/data/com.google.android.gms': Permission denied\nfind: /sdcard/Android/data/com.x/cache: Permission denied\nfind: /sdcard/Documents: No such file or directory\n";
        var (device, fake) = await DeviceTests.ReadyAsync(Set, f => f.On(AndroidCleanup.FindCommand(10_000_000), output, exitCode: 1, stderr: errors));

        var scan = await device.FindLargeAppFilesAsync(10_000_000, 10, None);

        Assert.Equal(["/Android/media/com.whatsapp/WhatsApp/Media/WhatsApp Video/VID-2024 copy.mp4", "/Download/Termux_v0.119.apk",
            "/Android/data/org.telegram.messenger/files/Telegram/Telegram Video/clip.mp4", "/Documents/scan.pdf"], scan.Files.Select(f => f.Path));
        Assert.All(scan.Files, f => Assert.Equal("internal", f.Root));
        Assert.Equal("com.whatsapp", scan.Files[0].AppId);
        Assert.Equal("VID-2024 copy.mp4", scan.Files[0].Name);
        Assert.Null(scan.Files[1].AppId);
        Assert.Equal("Download", scan.Files[1].Folder);
        Assert.Equal("org.telegram.messenger", scan.Files[2].AppId);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1727000000), scan.Files[0].Modified);
        Assert.Equal(["Android/data"], scan.Unreadable);
        Assert.Empty(fake.Unmatched);

        Assert.Equal("find '/sdcard/Android/data' '/sdcard/Android/media' '/sdcard/Download' '/sdcard/Documents' -type f -size +9764k -exec stat -c '%s %Y %n' {} +",
            AndroidCleanup.FindCommand(10_000_000));
    }

    [Fact]
    public async Task Caches_are_what_diskstats_last_counted()
    {
        var (device, _) = await DeviceTests.ReadyAsync(Set);
        var caches = await device.GetCachesAsync(None);
        Assert.Equal(2_147_483_648, caches.TotalBytes);
        Assert.Equal(["com.spotify.music", "com.google.android.apps.photos", "org.telegram.messenger"], caches.Apps.Select(a => a.AppId));
        Assert.Equal(1_073_741_824, caches.Apps[0].Bytes);
    }

    [Fact]
    public async Task Trim_reports_the_free_space_it_made()
    {
        var calls = 0;
        var (device, fake) = await DeviceTests.ReadyAsync(Set, f =>
        {
            f.On(c => c == "df -k /data", _ => FakeReply.Text(
                $"Filesystem 1K-blocks Used Available Use% Mounted on\n/dev/block/dm-49 115217320 60512644 {(Interlocked.Increment(ref calls) == 1 ? 54704676 : 56801828)} 53% /data\n"));
            f.On($"pm trim-caches {AndroidCleanup.TrimAll}", "");
        });

        var freed = await device.TrimCachesAsync(None);

        Assert.Equal((56_801_828L - 54_704_676) * 1024, freed);
        Assert.Equal(["df -k /data", $"pm trim-caches {AndroidCleanup.TrimAll}", "df -k /data"], fake.Commands.TakeLast(3));
    }

    [Fact]
    public async Task A_refused_trim_is_an_error_not_zero_freed()
    {
        var (device, _) = await DeviceTests.ReadyAsync(Set, f =>
        {
            f.On("df -k /data", "Filesystem 1K-blocks Used Available Use% Mounted on\n/dev/block/dm-49 115217320 60512644 54704676 53% /data\n");
            f.On($"pm trim-caches {AndroidCleanup.TrimAll}", "", exitCode: 255, stderr: "Exception occurred while executing 'trim-caches': java.lang.SecurityException");
        });

        await Assert.ThrowsAsync<DeviceException>(() => device.TrimCachesAsync(None));
    }

    private sealed class Inline(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }
}
