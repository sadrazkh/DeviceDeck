using System.Globalization;
using System.Net;
using System.Text;
using DeviceDeck.Core.Abstractions;

namespace DeviceDeck.Core.Toolbox;

/// <summary>What a device report shows. Parts the phone can't give stay null and the report says "Not available".</summary>
public sealed record DeviceReportData(DeviceSummary Summary, DeviceDetails? Details, StorageInfo? Storage, BatteryInfo? Battery);

/// <summary>
/// One self-contained HTML page (inline CSS, no scripts, prints on A4) with the phone's model, OS, storage, battery
/// and details. Sensitive rows (serial, IMEI, UDID, addresses) are masked unless the user chose to reveal them.
/// </summary>
public static class DeviceReport
{
    /// <summary>Reads what the phone can give; a part that fails is left out rather than failing the report.</summary>
    public static async Task<DeviceReportData> GatherAsync(IDevice device, CancellationToken ct)
    {
        var caps = device.Summary.Capabilities;
        var details = caps.HasFlag(DeviceCapability.Info) ? OptionalAsync(device.GetDetailsAsync, ct) : Task.FromResult<DeviceDetails?>(null);
        var storage = caps.HasFlag(DeviceCapability.Storage) ? OptionalAsync(device.GetStorageAsync, ct) : Task.FromResult<StorageInfo?>(null);
        var battery = caps.HasFlag(DeviceCapability.Battery) ? OptionalAsync(device.GetBatteryAsync, ct) : Task.FromResult<BatteryInfo?>(null);
        await Task.WhenAll(details, storage, battery).ConfigureAwait(false);
        return new DeviceReportData(device.Summary, details.Result, storage.Result, battery.Result);
    }

    private static async Task<T?> OptionalAsync<T>(Func<CancellationToken, Task<T>> read, CancellationToken ct) where T : class
    {
        try
        {
            return await read(ct).ConfigureAwait(false);
        }
        catch (DeviceException ex) when (ex.Code is not (DeviceErrorCodes.Disconnected or DeviceErrorCodes.NotFound))
        {
            return null;
        }
    }

    /// <summary>Every character except spaces, colons and dashes becomes a dot, as on the Overview screen.</summary>
    public static string Mask(string value) =>
        string.Concat(value.Select(ch => char.IsWhiteSpace(ch) || ch is ':' or '-' ? ch : '•'));

    /// <summary>"DeviceDeck report – Sadra's iPhone – 2026-10-20 1644.html", safe for Windows.</summary>
    public static string FileName(DeviceSummary summary, DateTimeOffset at, string extension)
    {
        var name = string.Concat(summary.Name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch)).Trim();
        if (name.Length == 0)
        {
            name = summary.Model;
        }

        return FormattableString.Invariant($"DeviceDeck report – {name} – {at:yyyy-MM-dd HHmm}{extension}");
    }

    public static string ToHtml(DeviceReportData data, string language, bool revealSensitive, DateTimeOffset generatedAt, string appVersion)
    {
        var w = new Words(language == "fa");
        var s = data.Summary;
        var html = new StringBuilder();
        html.Append("<!doctype html><html lang=\"").Append(w.Fa ? "fa" : "en").Append("\" dir=\"").Append(w.Fa ? "rtl" : "ltr").Append("\"><head><meta charset=\"utf-8\">");
        html.Append("<title>").Append(E(w["title"] + " – " + s.Name)).Append("</title><style>").Append(Css).Append("</style></head><body>");

        html.Append("<header><h1>").Append(E(s.Name)).Append("</h1><p class=\"sub\">")
            .Append(E(w["title"])).Append(" · ").Append(E(w.Format("generated", w.Date(generatedAt), w.Digits(appVersion)))).Append("</p></header>");

        // Phone
        var platformOs = s.Platform == DevicePlatform.Ios ? "iOS" : "Android";
        Section(html, w["phone"], [
            (w["model"], s.MarketingName is { Length: > 0 } marketing && marketing != s.Model ? $"{marketing} ({s.Model})" : s.Model),
            (w.Format("osVersion", platformOs), s.OsVersion.Length > 0 ? w.Digits($"{platformOs} {s.OsVersion}") : w["na"]),
            (w["connection"], s.Connection == ConnectionKind.Wifi ? "Wi-Fi" : "USB"),
            (w["mode"], s.Mode == DeviceMode.Full ? w["modeFull"] : w["modeLimited"]),
        ]);

        // Storage
        html.Append("<section><h2>").Append(E(w["storage"])).Append("</h2>");
        if (data.Storage is { Volumes.Count: > 0 } storage)
        {
            foreach (var volume in storage.Volumes)
            {
                var used = Math.Max(0, volume.TotalBytes - volume.FreeBytes);
                html.Append("<h3>").Append(E(volume.Label)).Append("</h3>");
                html.Append("<div class=\"bar\" role=\"img\" aria-label=\"").Append(E(w.Format("usedOf", w.Bytes(used), w.Bytes(volume.TotalBytes)))).Append("\">");
                foreach (var category in volume.Categories.Where(c => c.Bytes > 0))
                {
                    var pct = volume.TotalBytes > 0 ? 100.0 * category.Bytes / volume.TotalBytes : 0;
                    html.Append(CultureInfo.InvariantCulture, $"<span class=\"c-{E(category.Key)}\" style=\"width:{pct:0.##}%\"></span>");
                }

                html.Append("</div>");
                var rows = new List<(string, string)>
                {
                    (w["capacity"], w.Bytes(volume.TotalBytes)),
                    (w["used"], w.Bytes(used)),
                    (w["free"], w.Bytes(volume.FreeBytes)),
                };
                rows.AddRange(volume.Categories.Where(c => c.Bytes > 0).Select(c => (w.Category(c.Key), w.Bytes(c.Bytes))));
                Table(html, rows);
            }
        }
        else
        {
            html.Append("<p class=\"na\">").Append(E(w["na"])).Append("</p>");
        }

        html.Append("</section>");

        // Battery
        if (data.Battery is { } b)
        {
            var rows = new List<(string, string)>();
            if (b.LevelPercent is { } level) rows.Add((w["charge"], w.Percent(level)));
            rows.Add((w["charging"], b.IsCharging ? w["yes"] : w["no"]));
            if (b.HealthPercent is { } health) rows.Add((s.Platform == DevicePlatform.Ios ? w["healthEstimated"] : w["health"], w.Percent(health)));
            if (b.CycleCount is { } cycles) rows.Add((w["cycles"], w.Number(cycles)));
            if (b.TemperatureCelsius is { } temp) rows.Add((w["temperature"], w.Digits(temp.ToString("0.#", CultureInfo.InvariantCulture)) + " °C"));
            if (b.DesignCapacityMah is { } design) rows.Add((w["design"], w.Number(design) + " mAh"));
            if (b.FullChargeCapacityMah is { } full) rows.Add((w["fullCharge"], w.Number(full) + " mAh"));
            Section(html, w["battery"], rows);
        }
        else
        {
            html.Append("<section><h2>").Append(E(w["battery"])).Append("</h2><p class=\"na\">").Append(E(w["na"])).Append("</p></section>");
        }

        // Details
        if (data.Details is { Rows.Count: > 0 } details)
        {
            var masked = false;
            var rows = new List<(string, string)>();
            foreach (var row in details.Rows)
            {
                var hide = row.Sensitive && !revealSensitive;
                masked |= hide;
                rows.Add((w.DetailLabel(row, s.Platform), hide ? Mask(row.Value) : row.Value));
            }

            Section(html, w["details"], rows);
            if (masked)
            {
                html.Append("<p class=\"note\">").Append(E(w["maskedNote"])).Append("</p>");
            }
        }

        html.Append("<footer>").Append(E(w.Format("footer", w.Digits(appVersion)))).Append("</footer></body></html>");
        return html.ToString();
    }

    private static void Section(StringBuilder html, string title, IReadOnlyList<(string Label, string Value)> rows)
    {
        html.Append("<section><h2>").Append(E(title)).Append("</h2>");
        Table(html, rows);
        html.Append("</section>");
    }

    private static void Table(StringBuilder html, IReadOnlyList<(string Label, string Value)> rows)
    {
        html.Append("<table>");
        foreach (var (label, value) in rows)
        {
            // Values such as serials stay left-to-right inside a Persian page.
            html.Append("<tr><th>").Append(E(label)).Append("</th><td><bdi>").Append(E(value)).Append("</bdi></td></tr>");
        }

        html.Append("</table>");
    }

    private static string E(string text) => WebUtility.HtmlEncode(text);

    private const string Css =
        "@page{size:A4;margin:16mm}" +
        "body{font-family:'Segoe UI Variable Text','Segoe UI',Tahoma,sans-serif;color:#1b1b1b;background:#fff;margin:24px auto;max-width:760px;font-size:14px;line-height:1.5}" +
        "[dir=rtl] body,body:lang(fa){font-family:'Vazirmatn','Segoe UI',Tahoma,sans-serif}" +
        "header{border-bottom:2px solid #0f6cbd;margin-bottom:16px;padding-bottom:8px}" +
        "h1{font-size:26px;margin:0}h2{font-size:17px;margin:20px 0 8px;color:#0f6cbd}h3{font-size:14px;margin:12px 0 6px}" +
        ".sub{color:#616161;margin:4px 0 0}section{break-inside:avoid}" +
        "table{border-collapse:collapse;width:100%}th,td{text-align:start;padding:6px 8px;border-bottom:1px solid #e0e0e0;vertical-align:top}" +
        "th{font-weight:600;width:42%;color:#424242}td{font-variant-numeric:tabular-nums}" +
        ".bar{display:flex;height:12px;border-radius:6px;overflow:hidden;background:#e6e6e6;margin:6px 0 8px}" +
        ".bar span{display:block;height:100%}.c-photos{background:#f7630c}.c-videos{background:#e3008c}.c-apps{background:#0f6cbd}" +
        ".c-media{background:#8764b8}.c-system{background:#8a8886}.c-other{background:#c8c6c4}" +
        ".na{color:#8a8886}.note{color:#616161;font-size:12px;margin-top:8px}" +
        "footer{margin-top:28px;color:#8a8886;font-size:12px;border-top:1px solid #e0e0e0;padding-top:8px}";

    /// <summary>The report's words in English or Persian, with numbers, sizes and dates to match.</summary>
    private sealed class Words(bool fa)
    {
        private static readonly Dictionary<string, (string En, string Fa)> Text = new()
        {
            ["title"] = ("Device report", "گزارش دستگاه"),
            ["generated"] = ("made {0} with DeviceDeck {1}", "ساخته‌شده در {0} با DeviceDeck {1}"),
            ["phone"] = ("Phone", "گوشی"),
            ["model"] = ("Model", "مدل"),
            ["osVersion"] = ("{0} version", "نسخهٔ {0}"),
            ["connection"] = ("Connection", "اتصال"),
            ["mode"] = ("Access", "دسترسی"),
            ["modeFull"] = ("Full", "کامل"),
            ["modeLimited"] = ("Limited (photos only)", "محدود (فقط عکس‌ها)"),
            ["storage"] = ("Storage", "حافظه"),
            ["capacity"] = ("Capacity", "ظرفیت"),
            ["used"] = ("Used", "پرشده"),
            ["free"] = ("Free", "آزاد"),
            ["usedOf"] = ("{0} used of {1}", "{0} از {1} پر شده"),
            ["battery"] = ("Battery", "باتری"),
            ["charge"] = ("Charge", "شارژ"),
            ["charging"] = ("Charging", "در حال شارژ"),
            ["health"] = ("Health", "سلامت"),
            ["healthEstimated"] = ("Health (estimated)", "سلامت (تخمینی)"),
            ["cycles"] = ("Charge cycles", "چرخهٔ شارژ"),
            ["temperature"] = ("Temperature", "دما"),
            ["design"] = ("Design capacity", "ظرفیت طراحی"),
            ["fullCharge"] = ("Full-charge capacity", "ظرفیت شارژ کامل"),
            ["details"] = ("Details", "جزئیات"),
            ["yes"] = ("Yes", "بله"),
            ["no"] = ("No", "خیر"),
            ["na"] = ("Not available", "در دسترس نیست"),
            ["maskedNote"] = ("Serial numbers and similar values are hidden. Turn on “Show serial numbers” in DeviceDeck to include them.",
                "شماره‌های سریال و مقدارهای مشابه پنهان شده‌اند. برای آوردن آن‌ها در گزارش، «نمایش شماره‌های سریال» را در DeviceDeck روشن کنید."),
            ["footer"] = ("DeviceDeck {0}", "DeviceDeck {0}"),
            ["photos"] = ("Photos", "عکس‌ها"),
            ["videos"] = ("Videos", "ویدیوها"),
            ["apps"] = ("Apps", "برنامه‌ها"),
            ["media"] = ("Media", "رسانه"),
            ["system"] = ("System", "سیستم"),
            ["other"] = ("Other", "سایر"),
            ["serial"] = ("Serial number", "شمارهٔ سریال"),
            ["imei"] = ("IMEI", "IMEI"),
            ["udid"] = ("UDID", "UDID"),
            ["wifi"] = ("Wi-Fi address", "نشانی Wi-Fi"),
            ["bluetooth"] = ("Bluetooth", "بلوتوث"),
            ["carrier"] = ("Carrier", "اپراتور"),
        };

        private static readonly string[] UnitsEn = ["B", "KB", "MB", "GB", "TB"];
        private static readonly string[] UnitsFa = ["بایت", "کیلوبایت", "مگابایت", "گیگابایت", "ترابایت"];

        public bool Fa { get; } = fa;

        public string this[string key] => Text.TryGetValue(key, out var t) ? (Fa ? t.Fa : t.En) : key;

        public string Format(string key, params object[] args) => string.Format(CultureInfo.InvariantCulture, this[key], args);

        public string Category(string key) => Text.ContainsKey(key) ? this[key] : key;

        public string DetailLabel(DetailRow row, DevicePlatform platform) => row.Key switch
        {
            "os" => platform == DevicePlatform.Ios ? "iOS" : (Fa ? "اندروید" : "Android"),
            "model" or "serial" or "imei" or "udid" or "wifi" or "bluetooth" or "carrier" => this[row.Key],
            _ => row.Label,
        };

        public string Digits(string text)
        {
            if (!Fa)
            {
                return text;
            }

            var chars = text.ToCharArray();
            for (var i = 0; i < chars.Length; i++)
            {
                if (chars[i] is >= '0' and <= '9')
                {
                    chars[i] = (char)('۰' + (chars[i] - '0'));
                }
            }

            return new string(chars);
        }

        public string Number(long value) => Digits(value.ToString("#,0", CultureInfo.InvariantCulture)).Replace(",", Fa ? "٬" : ",", StringComparison.Ordinal);

        public string Percent(int value) => Fa ? Digits(value.ToString(CultureInfo.InvariantCulture)) + "٪" : value.ToString(CultureInfo.InvariantCulture) + "%";

        /// <summary>Decimal units (1 GB = 10^9 bytes), the way phones report storage and the app shows it.</summary>
        public string Bytes(long bytes)
        {
            double value = Math.Max(0, bytes);
            var unit = 0;
            while (value >= 1000 && unit < UnitsEn.Length - 1)
            {
                value /= 1000;
                unit++;
            }

            var text = value.ToString(unit == 0 || value >= 100 ? "0" : "0.#", CultureInfo.InvariantCulture);
            return Fa ? Digits(text).Replace(".", "٫", StringComparison.Ordinal) + " " + UnitsFa[unit] : text + " " + UnitsEn[unit];
        }

        public string Date(DateTimeOffset at)
        {
            if (!Fa)
            {
                return at.ToString("d MMMM yyyy, HH:mm", CultureInfo.GetCultureInfo("en-GB"));
            }

            var persian = new PersianCalendar();
            var local = at.DateTime;
            string[] months = ["فروردین", "اردیبهشت", "خرداد", "تیر", "مرداد", "شهریور", "مهر", "آبان", "آذر", "دی", "بهمن", "اسفند"];
            return Digits(FormattableString.Invariant(
                $"{persian.GetDayOfMonth(local)} {months[persian.GetMonth(local) - 1]} {persian.GetYear(local)}، {local:HH:mm}"));
        }
    }
}
