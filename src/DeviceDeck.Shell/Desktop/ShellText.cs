using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.Settings;

namespace DeviceDeck.Shell.Desktop;

/// <summary>The few sentences the window itself shows outside the page: tray menu and notifications, in English and Persian.</summary>
internal sealed record ShellText(
    bool RightToLeft,
    string TrayOpen,
    string TrayQuit,
    string Connected,
    string Waiting,
    string Open,
    string StillRunning,
    string StillRunningBody,
    string UpdateReady,
    string UpdateReadyBody,
    string Restart,
    string UpdateOnGithub,
    string UpdateOnGithubBody,
    string Download)
{
    public static readonly ShellText English = new(
        RightToLeft: false,
        TrayOpen: "Open DeviceDeck",
        TrayQuit: "Quit",
        Connected: "{0} connected",
        Waiting: "{0} is waiting for you",
        Open: "Open",
        StillRunning: "DeviceDeck is still running",
        StillRunningBody: "It keeps watching for phones in the notification area. You can change this in Settings.",
        UpdateReady: "DeviceDeck {0} is ready",
        UpdateReadyBody: "Restart DeviceDeck to finish updating.",
        Restart: "Restart",
        UpdateOnGithub: "DeviceDeck {0} is on GitHub",
        UpdateOnGithubBody: "This copy runs from the zip and can't update itself. Download the new version; the Setup installs one that updates itself.",
        Download: "Download");

    public static readonly ShellText Persian = new(
        RightToLeft: true,
        TrayOpen: "باز کردن DeviceDeck",
        TrayQuit: "خروج",
        Connected: "{0} وصل شد",
        Waiting: "{0} منتظر شماست",
        Open: "باز کردن",
        StillRunning: "DeviceDeck هنوز باز است",
        StillRunningBody: "در ناحیهٔ اعلان‌ها منتظر گوشی‌ها می‌ماند. این را می‌توانید در تنظیمات عوض کنید.",
        UpdateReady: "DeviceDeck {0} آماده است",
        UpdateReadyBody: "برای تمام شدن به‌روزرسانی، DeviceDeck را دوباره باز کنید.",
        Restart: "راه‌اندازی دوباره",
        UpdateOnGithub: "DeviceDeck {0} در GitHub منتشر شد",
        UpdateOnGithubBody: "این نسخه از فایل zip اجرا می‌شود و خودش به‌روز نمی‌شود. نسخهٔ تازه را دانلود کنید؛ Setup نسخه‌ای نصب می‌کند که خودش به‌روز می‌شود.",
        Download: "دانلود");

    public static ShellText For(AppLanguage language) => language == AppLanguage.Fa ? Persian : English;

    /// <summary>"iPhone 16 Pro · USB".</summary>
    public string DeviceLine(DeviceSummary device)
    {
        var model = device.MarketingName ?? device.Model;
        var link = device.Connection == ConnectionKind.Wifi ? (RightToLeft ? "وای‌فای" : "Wi-Fi") : "USB";
        return $"{model} · {link}";
    }
}
