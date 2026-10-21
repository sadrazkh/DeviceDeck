using System.Globalization;
using DeviceDeck.Core.Settings;

namespace DeviceDeck.Core.Photos;

/// <summary>The photo backup's Windows notifications, in English and Persian (numbers in Persian digits there).</summary>
internal sealed class PhotoBackupText
{
    private readonly bool _persian;

    private PhotoBackupText(bool persian) => _persian = persian;

    public static PhotoBackupText For(AppLanguage language) => new(language == AppLanguage.Fa);

    public string NewPhotos(int count, string device) => _persian
        ? $"{N(count)} عکس تازه روی {device}"
        : count == 1 ? $"1 new photo on {device}" : $"{N(count)} new photos on {device}";

    public string NewDetail(PhotoBackupCount count)
    {
        var parts = new List<string>();
        if (count.NewPhotos > 0)
        {
            parts.Add(_persian ? $"{N(count.NewPhotos)} عکس" : count.NewPhotos == 1 ? "1 photo" : $"{N(count.NewPhotos)} photos");
        }

        if (count.NewVideos > 0)
        {
            parts.Add(_persian ? $"{N(count.NewVideos)} ویدیو" : count.NewVideos == 1 ? "1 video" : $"{N(count.NewVideos)} videos");
        }

        if (count.NewBytes > 0)
        {
            parts.Add(_persian ? $"حدود {Size(count.NewBytes)}" : $"about {Size(count.NewBytes)}");
        }

        return string.Join(" · ", parts);
    }

    public string BackUpNow => _persian ? "پشتیبان‌گیری" : "Back up now";

    public string NotStarted(string device) => _persian ? $"از عکس‌های {device} پشتیبان گرفته نشد" : $"{device}’s photos weren’t backed up";

    public string Open => _persian ? "باز کردن DeviceDeck" : "Open DeviceDeck";

    public string Done(int copied, int failed) => (_persian, failed) switch
    {
        (true, 0) => $"از {N(copied)} عکس پشتیبان گرفته شد",
        (true, _) => $"از {N(copied)} عکس پشتیبان گرفته شد؛ {N(failed)} کپی نشد",
        (false, 0) => copied == 1 ? "1 photo backed up" : $"{N(copied)} photos backed up",
        _ => $"{(copied == 1 ? "1 photo" : $"{N(copied)} photos")} backed up, {N(failed)} couldn’t be copied",
    };

    public string DoneDetail(string device, long bytes) => _persian
        ? $"از {device} · {Size(bytes)}"
        : $"From {device} · {Size(bytes)}";

    public string OpenFolder => _persian ? "باز کردن پوشه" : "Open folder";

    public string UpToDate(string device) => _persian ? $"عکس‌های {device} به‌روز است" : $"Photos from {device} are up to date";

    public string UpToDateDetail => _persian ? "عکس‌های تازه از قبل در پوشه بودند." : "The new ones were already in the folder.";

    public string Stopped(string device) => _persian ? $"پشتیبان‌گیری از {device} متوقف شد" : $"The backup of {device} stopped";

    public string SomePhone => _persian ? "گوشی" : "Your phone";

    private string N(long value)
    {
        var text = value.ToString("N0", CultureInfo.InvariantCulture);
        return _persian ? Persian(text.Replace(",", "٬", StringComparison.Ordinal)) : text;
    }

    private string Size(long bytes)
    {
        var text = bytes >= 1_000_000_000
            ? (bytes / 1e9).ToString("0.0", CultureInfo.InvariantCulture) + (_persian ? " گیگابایت" : " GB")
            : Math.Max(1, (int)Math.Round(bytes / 1e6)).ToString(CultureInfo.InvariantCulture) + (_persian ? " مگابایت" : " MB");
        return _persian ? Persian(text.Replace(".", "٫", StringComparison.Ordinal)) : text;
    }

    private static string Persian(string text) => string.Concat(text.Select(c => c is >= '0' and <= '9' ? (char)('۰' + (c - '0')) : c));
}
