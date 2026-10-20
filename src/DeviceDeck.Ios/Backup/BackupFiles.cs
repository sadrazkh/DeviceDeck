using DeviceDeck.Ios.Device;
using DeviceDeck.Ios.Plist;

namespace DeviceDeck.Ios.Backup;

/// <summary>The files the PC writes into an iPhone backup folder before the phone takes over.</summary>
internal static class BackupFiles
{
    /// <summary>Writes under a temporary name and moves it into place, so a hard-linked copy keeps the old file.</summary>
    public static void WriteAtomically(string path, byte[] content)
    {
        var temp = path + BackupFolderHost.TempSuffix;
        File.WriteAllBytes(temp, content);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Half-received files from a run that stopped part-way.</summary>
    public static void DeleteLeftovers(string deviceFolder)
    {
        foreach (var file in Directory.EnumerateFiles(deviceFolder, "*" + BackupFolderHost.TempSuffix, SearchOption.AllDirectories))
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>
    /// A first (or full) backup starts from a fresh Status.plist and an empty Manifest.plist, which tells the phone there
    /// is nothing on the PC to build on; later runs leave both for the phone to read.
    /// </summary>
    public static void PrepareStatus(string deviceFolder, bool full)
    {
        var status = Path.Combine(deviceFolder, "Status.plist");
        if (full || !File.Exists(status))
        {
            WriteAtomically(status, PlistSerializer.ToBinary(new PlistDict
            {
                ["BackupState"] = "new",
                ["Date"] = DateTimeOffset.UtcNow,
                ["IsFullBackup"] = full,
                ["Version"] = "3.3",
                ["SnapshotState"] = "finished",
                ["UUID"] = Guid.NewGuid().ToString().ToUpperInvariant(),
            }));
        }

        var manifest = Path.Combine(deviceFolder, "Manifest.plist");
        if (full && File.Exists(manifest))
            File.Delete(manifest);
        if (!File.Exists(manifest))
            File.WriteAllBytes(manifest, []);
    }

    /// <summary>The UDID a backup folder belongs to, which the phone uses as the first segment of its paths.</summary>
    public static string? SourceIdentifier(string backupFolder)
    {
        if (!File.Exists(Path.Combine(backupFolder, "Manifest.plist")))
            return null;
        if (TryRead(Path.Combine(backupFolder, "Info.plist")) is { } info
            && (info.GetString("Target Identifier") ?? info.GetString("Unique Identifier")) is { Length: > 0 } fromInfo)
            return fromInfo;
        if (TryRead(Path.Combine(backupFolder, "Manifest.plist"))?.GetDict("Lockdown")?.GetString("UniqueDeviceID") is { Length: > 0 } fromManifest)
            return fromManifest;
        return null;
    }

    public static PlistDict? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            var bytes = File.ReadAllBytes(path);
            return bytes.Length == 0 ? null : PlistSerializer.ParseDict(bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            return null;
        }
    }
}

/// <summary>Info.plist: what iTunes writes about the phone next to a backup. The phone never reads it; tools list backups by it.</summary>
internal static class InfoPlist
{
    /// <summary>Not Apple's: app names for the backup browser, which the backup itself doesn't carry.</summary>
    public const string AppNamesKey = "DeviceDeck App Names";

    public static PlistDict Build(string udid, PlistDict values, IReadOnlyList<PlistDict> apps)
    {
        var info = new PlistDict();
        void Copy(string key, string from)
        {
            if (values.TryGetValue(from, out var value))
                info[key] = value;
        }

        Copy("Build Version", "BuildVersion");
        Copy("Device Name", "DeviceName");
        Copy("Display Name", "DeviceName");
        info["GUID"] = Guid.NewGuid().ToString("N").ToUpperInvariant();
        Copy("ICCID", "IntegratedCircuitCardIdentity");
        Copy("IMEI", "InternationalMobileEquipmentIdentity");
        Copy("MEID", "MobileEquipmentIdentifier");
        Copy("Phone Number", "PhoneNumber");
        info["Installed Applications"] = apps.Select(a => a.GetString("CFBundleIdentifier")).OfType<string>().Cast<object>().ToList();
        info["Last Backup Date"] = DateTimeOffset.UtcNow;
        var productType = values.GetString("ProductType");
        info["Product Name"] = DeviceModels.MarketingName(productType) ?? values.GetString("ProductName") ?? "iPhone";
        Copy("Product Type", "ProductType");
        Copy("Product Version", "ProductVersion");
        Copy("Serial Number", "SerialNumber");
        info["Target Identifier"] = udid;
        info["Target Type"] = "Device";
        info["Unique Identifier"] = udid.ToUpperInvariant();
        info["iTunes Version"] = "12.13.2";

        var names = new PlistDict();
        foreach (var app in apps)
        {
            if (app.GetString("CFBundleIdentifier") is { } id)
                names[id] = IosAppManager.DisplayName(app);
        }
        info[AppNamesKey] = names;
        return info;
    }
}
