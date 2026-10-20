using System.Globalization;

namespace DeviceDeck.Android.Apk;

internal sealed record ApkMetadata(string? PackageName, string? Label, string? VersionName, long? VersionCode, string? SplitName, byte[]? IconPng);

/// <summary>Reads the manifest (and resources.arsc when needed) of an APK: package, label, version and icon.</summary>
internal static class ApkInspector
{
    public const int IconSize = 192;

    public static async Task<ApkMetadata> ReadAsync(IRangeReader reader, bool withIcon, CancellationToken ct)
    {
        var zip = await ZipReader.OpenAsync(reader, ct).ConfigureAwait(false);
        byte[] manifestBytes = await zip.ReadAsync("AndroidManifest.xml", ct).ConfigureAwait(false)
            ?? throw new InvalidDataException("The APK has no AndroidManifest.xml.");
        var manifest = BinaryXml.Parse(manifestBytes);
        var application = manifest.Children.FirstOrDefault(e => e.Name == "application");

        string? package = manifest.Attribute(0, "package")?.StringValue ?? manifest.Attribute(0, "package")?.RawValue;
        string? split = manifest.Attribute(0, "split")?.StringValue;
        var versionCodeAttribute = manifest.Attribute(AndroidAttr.VersionCode, "versionCode");
        long? versionCode = versionCodeAttribute?.Value.IsInt == true ? versionCodeAttribute.Value.Data : null;
        if (versionCode == null && long.TryParse(versionCodeAttribute?.RawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out long rawCode))
        {
            versionCode = rawCode;
        }

        var labelAttribute = application?.Attribute(AndroidAttr.Label, "label") ?? LauncherActivity(application)?.Attribute(AndroidAttr.Label, "label");
        var versionAttribute = manifest.Attribute(AndroidAttr.VersionName, "versionName");
        var iconAttribute = application?.Attribute(AndroidAttr.Icon, "icon")
            ?? application?.Attribute(AndroidAttr.RoundIcon, "roundIcon")
            ?? LauncherActivity(application)?.Attribute(AndroidAttr.Icon, "icon");

        bool needsTable = labelAttribute?.Value.IsReference == true || versionAttribute?.Value.IsReference == true
            || (withIcon && iconAttribute?.Value.IsReference == true);
        ResourceTable? table = null;
        if (needsTable && await zip.ReadAsync("resources.arsc", ct).ConfigureAwait(false) is { } arsc)
        {
            table = ResourceTable.Parse(arsc);
        }

        string? label = Text(labelAttribute, table);
        string? versionName = Text(versionAttribute, table);
        byte[]? icon = null;
        if (withIcon && table != null && iconAttribute?.Value.IsReference == true)
        {
            icon = await new IconRenderer(zip, table).RenderPngAsync(iconAttribute.Value.Data, IconSize, ct).ConfigureAwait(false);
        }
        return new ApkMetadata(package, string.IsNullOrWhiteSpace(label) ? null : label.Trim(), versionName, versionCode, split, icon);
    }

    private static string? Text(XmlAttribute? attribute, ResourceTable? table)
    {
        if (attribute == null)
        {
            return null;
        }
        if (attribute.StringValue != null)
        {
            return attribute.StringValue;
        }
        if (attribute.Value.IsReference && table != null)
        {
            return table.ResolveString(attribute.Value.Data);
        }
        return attribute.RawValue;
    }

    /// <summary>The activity with a MAIN/LAUNCHER intent filter (some apps put their label and icon only there).</summary>
    private static XmlElement? LauncherActivity(XmlElement? application) =>
        application?.Children
            .Where(c => c.Name is "activity" or "activity-alias")
            .FirstOrDefault(a => a.Descendants().Any(d =>
                d.Name == "category" && d.Attribute(AndroidAttr.Name, "name")?.StringValue == "android.intent.category.LAUNCHER"));
}
