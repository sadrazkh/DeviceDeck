using System.Security.Cryptography;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.Backups;
using DeviceDeck.Ios.Device;
using DeviceDeck.Ios.Plist;

namespace DeviceDeck.Ios.Backup.Format;

/// <summary>
/// Reads backups in Apple's layout (iOS 10 and later: Manifest.db): the header from Info.plist, Manifest.plist and
/// Status.plist, and the files through Manifest.db. Encrypted backups are opened with the user's password; the
/// manifest is decrypted into memory and each file as it is read, so nothing decrypted lands on disk.
/// </summary>
public sealed class IosBackupFormat : IIosBackupFormat
{
    public IosBackupHeader? ReadHeader(string folder)
    {
        var info = BackupFiles.TryRead(Path.Combine(folder, "Info.plist"));
        var manifest = BackupFiles.TryRead(Path.Combine(folder, "Manifest.plist"));
        if (info is null && manifest is null)
            return null;
        var status = BackupFiles.TryRead(Path.Combine(folder, "Status.plist"));
        var lockdown = manifest?.GetDict("Lockdown");

        var deviceId = info?.GetString("Target Identifier") ?? info?.GetString("Unique Identifier") ?? lockdown?.GetString("UniqueDeviceID")
                       ?? Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)).Split('-')[0];
        var productType = info?.GetString("Product Type") ?? lockdown?.GetString("ProductType");
        var complete = manifest is not null
                       && status?.GetString("SnapshotState") is null or "finished"
                       && File.Exists(Path.Combine(folder, "Manifest.db"));
        return new IosBackupHeader(
            DeviceId: deviceId,
            DeviceName: info?.GetString("Device Name") ?? info?.GetString("Display Name") ?? lockdown?.GetString("DeviceName") ?? "iPhone",
            ProductType: productType,
            MarketingName: DeviceModels.MarketingName(productType),
            ProductVersion: info?.GetString("Product Version") ?? lockdown?.GetString("ProductVersion"),
            Encrypted: manifest?.GetBool("IsEncrypted") == true,
            Date: status?.GetDate("Date") ?? info?.GetDate("Last Backup Date") ?? manifest?.GetDate("Date"),
            Complete: complete,
            SnapshotId: status?.GetString("UUID"),
            AppCount: manifest?.GetDict("Applications")?.Count ?? info?.GetArray("Installed Applications")?.Count ?? 0,
            AppNames: AppNames(info));
    }

    public Task<IBackupArchive> OpenAsync(string folder, string? password, CancellationToken ct) => Task.Run<IBackupArchive>(() =>
    {
        var manifestPlist = BackupFiles.TryRead(Path.Combine(folder, "Manifest.plist"))
                            ?? throw new DeviceException(BackupErrors.Corrupt, "The backup has no Manifest.plist; it may be unfinished.");
        var databasePath = Path.Combine(folder, "Manifest.db");
        if (!File.Exists(databasePath))
            throw new DeviceException(BackupErrors.Corrupt, "The backup has no Manifest.db; it may be unfinished, or from iOS 9 or older.");

        if (manifestPlist.GetBool("IsEncrypted") != true)
        {
            using var plain = Read(() => SqliteImage.OpenFile(databasePath));
            return new IosBackupArchive(folder, Read(() => ManifestDatabase.Read(plain.Connection)), null);
        }

        if (string.IsNullOrEmpty(password))
            throw new DeviceException(BackupErrors.PasswordRequired, "This backup is encrypted. Enter its password to open it.");
        var keys = Unlock(manifestPlist, password, ct);
        var storedKey = manifestPlist.GetData("ManifestKey")
                        ?? throw new DeviceException(BackupErrors.Corrupt, "The encrypted backup has no ManifestKey.");
        var image = BackupCrypto.DecryptAll(File.ReadAllBytes(databasePath), Read(() => keys.UnwrapStoredKey(storedKey)));
        using var db = Read(() => SqliteImage.FromBytes(image));
        return new IosBackupArchive(folder, Read(() => ManifestDatabase.Read(db.Connection)), keys);
    }, ct);

    /// <summary>Checks a password without opening the backup (before a restore, so the phone isn't asked in vain).</summary>
    public Task VerifyPasswordAsync(string folder, string password, CancellationToken ct) => Task.Run(() =>
    {
        var manifestPlist = BackupFiles.TryRead(Path.Combine(folder, "Manifest.plist"))
                            ?? throw new DeviceException(BackupErrors.Corrupt, "The backup has no Manifest.plist.");
        Unlock(manifestPlist, password, ct);
    }, ct);

    private static BackupKeys Unlock(PlistDict manifestPlist, string password, CancellationToken ct)
    {
        var bag = Read(() => BackupKeybag.Parse(manifestPlist.GetData("BackupKeyBag")
                                               ?? throw new FormatException("The encrypted backup has no keybag.")));
        ct.ThrowIfCancellationRequested();
        return bag.Unlock(bag.DeriveKey(password))
               ?? throw new DeviceException(BackupErrors.WrongPassword, "That isn't the password of this backup.");
    }

    /// <summary>Bundle id → name: DeviceDeck's own list, else the App Store's name iTunes stored per app.</summary>
    private static IReadOnlyDictionary<string, string> AppNames(PlistDict? info)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        if (info?.GetDict("Applications") is { } apps)
        {
            foreach (var (id, value) in apps)
            {
                if (value is PlistDict app && app.GetData("iTunesMetadata") is { Length: > 0 } metadata)
                {
                    try
                    {
                        if (PlistSerializer.ParseDict(metadata).GetString("itemName") is { Length: > 0 } name)
                            names[id] = name;
                    }
                    catch (FormatException)
                    {
                    }
                }
            }
        }
        if (info?.GetDict(InfoPlist.AppNamesKey) is { } ours)
        {
            foreach (var (id, value) in ours)
            {
                if (value is string { Length: > 0 } name)
                    names[id] = name;
            }
        }
        return names;
    }

    /// <summary>Damaged input becomes backup_corrupt rather than a dropped connection.</summary>
    private static T Read<T>(Func<T> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is FormatException or Microsoft.Data.Sqlite.SqliteException or CryptographicException)
        {
            throw new DeviceException(BackupErrors.Corrupt, "The backup is damaged: " + ex.Message, ex);
        }
    }
}

/// <summary>AES-256-CBC with a zero IV: how an encrypted backup stores Manifest.db and each file.</summary>
internal static class BackupCrypto
{
    public static byte[] DecryptAll(byte[] data, byte[] key)
    {
        if (data.Length % 16 != 0)
            throw new DeviceException(BackupErrors.Corrupt, "The encrypted Manifest.db has a length that isn't a whole number of blocks.");
        using var aes = Aes.Create();
        aes.Key = key;
        var plain = aes.DecryptCbc(data, new byte[16], PaddingMode.None);
        // PKCS#7 padding when it is there; SQLite ignores anything past its last page anyway.
        var pad = plain.Length > 0 ? plain[^1] : 0;
        if (pad is >= 1 and <= 16 && plain.AsSpan(plain.Length - pad).IndexOfAnyExcept((byte)pad) < 0)
            Array.Resize(ref plain, plain.Length - pad);
        return plain;
    }

    /// <summary>A stream of the plain bytes, padding included; the caller stops at the file's size.</summary>
    public static Stream Decrypting(Stream encrypted, byte[] key)
    {
        var aes = Aes.Create();
        aes.Key = key;
        aes.IV = new byte[16];
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        return new CryptoStream(encrypted, aes.CreateDecryptor(), CryptoStreamMode.Read, leaveOpen: true);
    }

    /// <summary>The inverse, for tests that write encrypted backups: PKCS#7-padded AES-256-CBC with a zero IV.</summary>
    public static byte[] Encrypt(byte[] plain, byte[] key)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        return aes.EncryptCbc(plain, new byte[16], PaddingMode.PKCS7);
    }
}
