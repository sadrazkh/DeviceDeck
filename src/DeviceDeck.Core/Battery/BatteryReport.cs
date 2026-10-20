using System.Globalization;
using System.Net;
using System.Text;
using DeviceDeck.Core.Abstractions;

namespace DeviceDeck.Core.Battery;

/// <summary>
/// The exported battery report: one self-contained HTML file (inline CSS and SVG, no scripts) with the latest figures,
/// health and capacity over time, and every reading. Prints cleanly, so "Print → Save as PDF" gives the PDF.
/// English or Persian (right to left, Persian digits and the Solar Hijri calendar).
/// </summary>
public static class BatteryReport
{
    public static string FileNameFor(string deviceName, DateTimeOffset now)
    {
        var safe = string.Concat(deviceName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim().TrimEnd('.');
        return $"Battery report - {(safe.Length == 0 ? "phone" : safe)} - {now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.html";
    }

    /// <summary>Writes the report into <paramref name="folder"/> (made if missing) without replacing a file; returns its path.</summary>
    public static async Task<string> WriteAsync(string folder, IReadOnlyList<BatterySnapshot> snapshots, string language, DateTimeOffset now, string appVersion, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder))
        {
            throw new ArgumentException("destination must be a full folder path on this PC.");
        }

        var html = Render(snapshots, language, now, appVersion);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, FileNameFor(snapshots[^1].DeviceName, now));
        for (var n = 1; File.Exists(path); n++)
        {
            path = Path.Combine(folder, Path.GetFileNameWithoutExtension(FileNameFor(snapshots[^1].DeviceName, now)) + $" ({n}).html");
        }

        await File.WriteAllTextAsync(path, html, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), ct).ConfigureAwait(false);
        return path;
    }

    public static string Render(IReadOnlyList<BatterySnapshot> snapshots, string language, DateTimeOffset now, string appVersion)
    {
        if (snapshots.Count == 0)
        {
            throw new ArgumentException("There is no battery history for this phone yet.");
        }

        var t = language == "fa" ? Text.Fa : Text.En;
        var f = new Formatter(language == "fa");
        var latest = snapshots[^1];
        var ios = latest.Platform == DevicePlatform.Ios;
        var html = new StringBuilder(16 * 1024);

        html.Append($"""
            <!doctype html>
            <html lang="{(f.Persian ? "fa" : "en")}" dir="{(f.Persian ? "rtl" : "ltr")}">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{E(t.Title)} · {E(latest.DeviceName)}</title>
            <style>{Css}</style>
            </head>
            <body>
            <header>
            <h1>{E(t.Title)}</h1>
            <p class="who">{E(latest.DeviceName)} · {E(latest.Model)} · {E(f.Digits((ios ? "iOS " : "Android ") + latest.OsVersion))}</p>
            <p class="muted">{E(string.Format(CultureInfo.InvariantCulture, t.Created, f.Date(now, withTime: true), f.Digits(appVersion)))}</p>
            </header>
            <section class="tiles">
            """);

        Tile(html, latest.HealthEstimated ? t.HealthEstimated : t.Health, latest.HealthPercent is { } h ? f.Percent(h) : t.Unknown, ChangeLine(snapshots, f, t));
        Tile(html, t.Cycles, latest.CycleCount is { } c ? f.Number(c) : t.Unknown, null);
        Tile(html, t.FullCharge, latest.FullChargeCapacityMah is { } full ? f.Mah(full) : t.Unknown,
            latest.DesignCapacityMah is { } design ? string.Format(CultureInfo.InvariantCulture, t.OfDesign, f.Mah(design)) : null);
        Tile(html, t.Readings, f.Number(snapshots.Count),
            string.Format(CultureInfo.InvariantCulture, t.Span, f.Date(snapshots[0].At, false), f.Date(latest.At, false)));
        html.Append("</section>\n");

        if (snapshots.Any(s => s.HealthEstimated))
        {
            html.Append($"<p class=\"note\">{E(t.EstimatedNote)}</p>\n");
        }

        Chart(html, t.HealthChart, snapshots, s => s.HealthPercent, v => f.Percent((int)Math.Round(v)), f, reference: null, referenceLabel: null, floorAtZero: false, maxCap: 100);
        // Capacity ticks are bare numbers (the title names the unit), so long Persian units never crowd the axis.
        Chart(html, t.CapacityChart, snapshots, s => s.FullChargeCapacityMah, v => f.Number((int)Math.Round(v)), f,
            reference: latest.DesignCapacityMah,
            referenceLabel: latest.DesignCapacityMah is { } designMah ? $"{t.DesignCapacity} {f.Number(designMah)}" : null,
            floorAtZero: false, maxCap: null);
        Chart(html, t.CyclesChart, snapshots, s => s.CycleCount, v => f.Number((int)Math.Round(v)), f, reference: null, referenceLabel: null, floorAtZero: true, maxCap: null);

        html.Append($"""
            <section>
            <h2>{E(t.AllReadings)}</h2>
            <table>
            <thead><tr><th>{E(t.ColDate)}</th><th>{E(t.ColLevel)}</th><th>{E(t.ColHealth)}</th><th>{E(t.ColCycles)}</th><th>{E(t.ColFull)}</th><th>{E(t.ColDesign)}</th><th>{E(t.ColTemp)}</th><th>{E(t.ColOs)}</th></tr></thead>
            <tbody>

            """);
        foreach (var s in snapshots.Reverse())
        {
            var level = s.LevelPercent is { } l ? f.Percent(l) + (s.IsCharging ? " " + t.Charging : "") : t.Dash;
            html.Append("<tr>")
                .Append(Cell(f.Date(s.At, withTime: true)))
                .Append(Cell(level))
                .Append(Cell(s.HealthPercent is { } hp ? f.Percent(hp) + (s.HealthEstimated ? " *" : "") : t.Dash))
                .Append(Cell(s.CycleCount is { } cc ? f.Number(cc) : t.Dash))
                .Append(Cell(s.FullChargeCapacityMah is { } fc ? f.Mah(fc) : t.Dash))
                .Append(Cell(s.DesignCapacityMah is { } dc ? f.Mah(dc) : t.Dash))
                .Append(Cell(s.TemperatureCelsius is { } tc ? f.Celsius(tc) : t.Dash))
                .Append(Cell(f.Digits(s.OsVersion)))
                .Append("</tr>\n");
        }

        html.Append("</tbody>\n</table>\n");
        if (snapshots.Any(s => s.HealthEstimated))
        {
            html.Append($"<p class=\"muted\">* {E(t.EstimatedShort)}</p>\n");
        }

        html.Append($"""
            </section>
            <footer class="muted">{E(t.Footer)}</footer>
            </body>
            </html>

            """);
        return html.ToString();
    }

    private static string? ChangeLine(IReadOnlyList<BatterySnapshot> snapshots, Formatter f, Text t)
    {
        var withHealth = snapshots.Where(s => s.HealthPercent is not null).ToList();
        if (withHealth.Count < 2)
        {
            return null;
        }

        var change = withHealth[^1].HealthPercent!.Value - withHealth[0].HealthPercent!.Value;
        var amount = change == 0 ? f.Number(0) : (change > 0 ? "+" : "−") + f.Number(Math.Abs(change));
        return string.Format(CultureInfo.InvariantCulture, t.Change, amount, f.Date(withHealth[0].At, false));
    }

    private static void Tile(StringBuilder html, string label, string value, string? sub)
    {
        html.Append("<div class=\"tile\"><div class=\"label\">").Append(E(label)).Append("</div><div class=\"value\">").Append(E(value)).Append("</div>");
        if (sub is not null)
        {
            html.Append("<div class=\"sub\">").Append(E(sub)).Append("</div>");
        }

        html.Append("</div>\n");
    }

    private static string Cell(string text) => "<td>" + E(text) + "</td>";

    /// <summary>One measure over time: a 2 px line with dots, hairline grid, the latest value labelled at its end.</summary>
    private static void Chart(
        StringBuilder html,
        string title,
        IReadOnlyList<BatterySnapshot> snapshots,
        Func<BatterySnapshot, int?> pick,
        Func<double, string> label,
        Formatter f,
        int? reference,
        string? referenceLabel,
        bool floorAtZero,
        int? maxCap)
    {
        var points = snapshots.Where(s => pick(s) is not null).Select(s => (At: s.At, Value: (double)pick(s)!.Value)).ToList();
        html.Append("<section class=\"chart\">\n<h2>").Append(E(title)).Append("</h2>\n");
        if (points.Count == 0)
        {
            html.Append("<p class=\"muted\">").Append(E(f.Persian ? Text.Fa.NoData : Text.En.NoData)).Append("</p>\n</section>\n");
            return;
        }

        const double width = 720, height = 220, left = 64, right = 96, top = 16, bottom = 32;
        var values = points.Select(p => p.Value).ToList();
        if (reference is { } r)
        {
            values.Add(r);
        }

        var (min, max, step) = NiceRange(floorAtZero ? 0 : values.Min(), values.Max(), maxCap);
        var t0 = points[0].At.ToUnixTimeMilliseconds();
        var t1 = points[^1].At.ToUnixTimeMilliseconds();
        // Time runs with the reading direction: right to left in Persian, as in the app. text-anchor is logical,
        // so mirroring the x positions is all a right-to-left page needs.
        var rtl = f.Persian;
        double M(double x) => rtl ? width - x : x;
        double X(DateTimeOffset at) => M(t1 == t0 ? left + (width - left - right) / 2 : left + (at.ToUnixTimeMilliseconds() - t0) / (double)(t1 - t0) * (width - left - right));
        double Y(double v) => top + (1 - (v - min) / (max - min)) * (height - top - bottom);
        string N(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

        html.Append($"<svg viewBox=\"0 0 {N(width)} {N(height)}\" role=\"img\" aria-label=\"{E(title)}\">\n");
        for (var v = min; v <= max + step / 2; v += step)
        {
            html.Append($"<line class=\"grid\" x1=\"{N(M(left))}\" x2=\"{N(M(width - right))}\" y1=\"{N(Y(v))}\" y2=\"{N(Y(v))}\"/>")
                .Append($"<text class=\"tick\" x=\"{N(M(left - 8))}\" y=\"{N(Y(v) + 4)}\" text-anchor=\"end\">{E(label(v))}</text>\n");
        }

        foreach (var (at, anchor) in new[] { (points[0].At, "start"), (points[^1].At, "end") }.DistinctBy(p => p.At))
        {
            html.Append($"<text class=\"tick\" x=\"{N(X(at))}\" y=\"{N(height - 10)}\" text-anchor=\"{(points.Count == 1 ? "middle" : anchor)}\">{E(f.Date(at, false))}</text>\n");
        }

        if (reference is { } design)
        {
            html.Append($"<line class=\"reference\" x1=\"{N(M(left))}\" x2=\"{N(M(width - right))}\" y1=\"{N(Y(design))}\" y2=\"{N(Y(design))}\"/>")
                .Append($"<text class=\"tick\" x=\"{N(M(width - right + 8))}\" y=\"{N(Y(design) + 4)}\">{E(referenceLabel ?? "")}</text>\n");
        }

        if (points.Count > 1)
        {
            var path = string.Join(' ', points.Select((p, i) => $"{(i == 0 ? 'M' : 'L')}{N(X(p.At))},{N(Y(p.Value))}"));
            html.Append($"<path class=\"line\" d=\"{path}\"/>\n");
        }

        foreach (var p in points)
        {
            html.Append($"<circle class=\"dot\" cx=\"{N(X(p.At))}\" cy=\"{N(Y(p.Value))}\" r=\"4\"><title>{E(f.Date(p.At, true))}: {E(label(p.Value))}</title></circle>\n");
        }

        var last = points[^1];
        var lastLabelY = reference is { } d2 && Math.Abs(Y(d2) - Y(last.Value)) < 14 ? Y(last.Value) - 10 : Y(last.Value) + 4;
        html.Append($"<text class=\"end\" x=\"{N(X(last.At) + (rtl ? -10 : 10))}\" y=\"{N(lastLabelY)}\">{E(label(last.Value))}</text>\n</svg>\n</section>\n");
    }

    /// <summary>Clean tick values around the data: steps of 1, 2, 5 × 10ⁿ (at least 1), about four lines.</summary>
    internal static (double Min, double Max, double Step) NiceRange(double min, double max, int? cap)
    {
        if (max - min < 1)
        {
            min -= 1;
            max += 1;
        }

        var raw = (max - min) / 4;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        // Every measure here is a whole number, so a tick never falls between two of them.
        var step = Math.Max(1, new[] { 1, 2, 5, 10 }.Select(m => m * magnitude).First(s => s >= raw));
        var lo = Math.Floor(min / step) * step;
        var hi = Math.Ceiling(max / step) * step;
        if (cap is { } c && hi > c)
        {
            hi = c;
            lo = Math.Min(lo, hi - step);
        }

        return (lo, hi, step);
    }

    private static string E(string text) => WebUtility.HtmlEncode(text);

    private sealed class Formatter(bool persian)
    {
        private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-GB");
        private readonly CultureInfo _culture = persian ? PersianCulture() : En;

        public bool Persian { get; } = persian;

        private static CultureInfo PersianCulture()
        {
            var culture = (CultureInfo)CultureInfo.GetCultureInfo("fa-IR").Clone();
            if (culture.OptionalCalendars.Any(c => c is PersianCalendar) && culture.DateTimeFormat.Calendar is not PersianCalendar)
            {
                culture.DateTimeFormat.Calendar = new PersianCalendar();
            }

            return culture;
        }

        public string Number(int value) => Digits(value.ToString("#,0", _culture));

        public string Percent(int value) => Persian ? Digits(value.ToString(CultureInfo.InvariantCulture)) + "٪" : value.ToString(CultureInfo.InvariantCulture) + "%";

        public string Mah(int value) => Number(value) + " mAh";

        public string Celsius(double value) => Digits(value.ToString("0.#", CultureInfo.InvariantCulture)) + " °C";

        public string Date(DateTimeOffset at, bool withTime) =>
            Digits(at.ToLocalTime().ToString(withTime ? (Persian ? "d MMMM yyyy، HH:mm" : "d MMM yyyy, HH:mm") : (Persian ? "d MMMM yyyy" : "d MMM yyyy"), _culture));

        public string Digits(string text)
        {
            if (!Persian)
            {
                return text;
            }

            // Persian digits and separators (.NET's fa-IR gives ASCII ones): 3,699 → ۳٬۶۹۹, 26.1 → ۲۶٫۱.
            var chars = text.ToCharArray();
            for (var i = 0; i < chars.Length; i++)
            {
                if (chars[i] is >= '0' and <= '9')
                {
                    chars[i] = (char)('۰' + (chars[i] - '0'));
                }
                else if (chars[i] is ',' or '.' && i > 0 && i + 1 < chars.Length && char.IsDigit(chars[i - 1]) && char.IsDigit(chars[i + 1]))
                {
                    chars[i] = chars[i] == ',' ? '٬' : '٫';
                }
            }

            return new string(chars);
        }
    }

    private sealed record Text(
        string Title,
        string Created,
        string Health,
        string HealthEstimated,
        string Cycles,
        string FullCharge,
        string OfDesign,
        string Readings,
        string Span,
        string Change,
        string EstimatedNote,
        string EstimatedShort,
        string HealthChart,
        string CapacityChart,
        string CyclesChart,
        string DesignCapacity,
        string AllReadings,
        string ColDate,
        string ColLevel,
        string ColHealth,
        string ColCycles,
        string ColFull,
        string ColDesign,
        string ColTemp,
        string ColOs,
        string Charging,
        string Unknown,
        string Dash,
        string NoData,
        string Footer)
    {
        public static readonly Text En = new(
            "Battery report",
            "Made on {0} by DeviceDeck {1}",
            "Health",
            "Health (estimated)",
            "Charge cycles",
            "Full charge",
            "of {0} design",
            "Readings",
            "{0} to {1}",
            "{0} points since {1}",
            "iPhone health here is estimated from the battery's full-charge and design capacity, so it can differ from \"Maximum Capacity\" in iOS Settings, which Apple corrects with data the phone doesn't share over USB.",
            "Estimated from full-charge and design capacity.",
            "Health over time",
            "Full-charge capacity over time (mAh)",
            "Charge cycles over time",
            "Design",
            "All readings",
            "Date",
            "Level",
            "Health",
            "Cycles",
            "Full charge",
            "Design",
            "Temperature",
            "System",
            "(charging)",
            "Unknown",
            "—",
            "The phone didn't report this.",
            "A reading is taken each time the phone connects to DeviceDeck. Print this page and choose \"Save as PDF\" for a PDF.");

        public static readonly Text Fa = new(
            "گزارش باتری",
            "ساخته‌شده در {0} با DeviceDeck {1}",
            "سلامت",
            "سلامت (تخمینی)",
            "چرخهٔ شارژ",
            "شارژ کامل",
            "از {0} ظرفیت طراحی",
            "اندازه‌گیری‌ها",
            "از {0} تا {1}",
            "{0} واحد از {1}",
            "سلامت باتری آیفون در اینجا از ظرفیت شارژ کامل و ظرفیت طراحی تخمین زده می‌شود، پس ممکن است با «حداکثر ظرفیت» در تنظیمات iOS فرق داشته باشد؛ اپل آن عدد را با داده‌هایی اصلاح می‌کند که گوشی از راه USB نمی‌دهد.",
            "تخمین از ظرفیت شارژ کامل و ظرفیت طراحی.",
            "سلامت در طول زمان",
            "ظرفیت شارژ کامل در طول زمان (mAh)",
            "چرخهٔ شارژ در طول زمان",
            "طراحی",
            "همهٔ اندازه‌گیری‌ها",
            "تاریخ",
            "شارژ",
            "سلامت",
            "چرخه",
            "شارژ کامل",
            "طراحی",
            "دما",
            "سیستم",
            "(در حال شارژ)",
            "نامعلوم",
            "—",
            "گوشی این را گزارش نکرد.",
            "هر بار که گوشی به DeviceDeck وصل می‌شود یک اندازه‌گیری ثبت می‌شود. برای PDF این صفحه را چاپ کنید و «ذخیره به‌صورت PDF» را بزنید.");
    }

    private const string Css = """
        :root { --surface: #fcfcfb; --ink: #0b0b0b; --ink-2: #52514e; --grid: #e7e6e2; --series: #2a78d6; --tile: #ffffff; color-scheme: light dark; }
        @media (prefers-color-scheme: dark) { :root { --surface: #1a1a19; --ink: #f0efec; --ink-2: #c3c2b7; --grid: #383835; --series: #3987e5; --tile: #222220; } }
        @media print { :root { --surface: #ffffff; --ink: #000000; --ink-2: #444444; --grid: #dddddd; --series: #2a78d6; --tile: #ffffff; } section { break-inside: avoid; } }
        * { box-sizing: border-box; }
        body { margin: 0 auto; max-width: 880px; padding: 32px 24px 48px; background: var(--surface); color: var(--ink);
               font: 15px/1.5 "Segoe UI Variable Text", "Segoe UI", Vazirmatn, Tahoma, sans-serif; }
        h1 { font-size: 28px; margin: 0 0 4px; font-weight: 600; }
        h2 { font-size: 17px; margin: 32px 0 8px; font-weight: 600; }
        .who { margin: 0; font-size: 17px; }
        .muted, .sub, .label { color: var(--ink-2); }
        .tiles { display: grid; grid-template-columns: repeat(auto-fit, minmax(180px, 1fr)); gap: 12px; margin-top: 24px; }
        .tile { background: var(--tile); border: 1px solid var(--grid); border-radius: 8px; padding: 12px 16px; }
        .label { font-size: 13px; }
        .value { font-size: 26px; font-weight: 600; }
        .sub { font-size: 13px; }
        .note { border-inline-start: 3px solid var(--series); padding: 4px 12px; color: var(--ink-2); }
        svg { width: 100%; height: auto; display: block; }
        .grid { stroke: var(--grid); stroke-width: 1; }
        .reference { stroke: var(--ink-2); stroke-width: 1; }
        .line { fill: none; stroke: var(--series); stroke-width: 2; stroke-linejoin: round; stroke-linecap: round; }
        .dot { fill: var(--series); stroke: var(--surface); stroke-width: 2; }
        .tick { fill: var(--ink-2); font-size: 12px; font-variant-numeric: tabular-nums; }
        .end { fill: var(--ink); font-size: 13px; font-weight: 600; }
        table { width: 100%; border-collapse: collapse; font-variant-numeric: tabular-nums; font-size: 14px; }
        th, td { text-align: start; padding: 6px 8px; border-bottom: 1px solid var(--grid); white-space: nowrap; }
        th { color: var(--ink-2); font-weight: 600; }
        footer { margin-top: 32px; font-size: 13px; }
        """;
}
