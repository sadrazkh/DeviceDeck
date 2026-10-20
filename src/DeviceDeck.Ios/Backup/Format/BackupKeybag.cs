using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace DeviceDeck.Ios.Backup.Format;

/// <summary>
/// The keybag in an encrypted backup's Manifest.plist (BackupKeyBag): tag-length-value blocks with the salts and
/// iteration counts, then one wrapped key per protection class. The password unwraps the class keys; a class key
/// unwraps each file's own key.
/// </summary>
internal sealed class BackupKeybag
{
    private const int WrapPasscode = 2;

    private readonly Dictionary<int, ClassKey> _classes;

    private BackupKeybag(Dictionary<string, byte[]> attributes, Dictionary<int, ClassKey> classes)
    {
        Attributes = attributes;
        _classes = classes;
    }

    /// <summary>The bag-level values (VERS, TYPE, SALT, ITER, DPSL, DPIC, …), raw.</summary>
    public IReadOnlyDictionary<string, byte[]> Attributes { get; }

    public IReadOnlyCollection<int> Classes => _classes.Keys;

    public static BackupKeybag Parse(ReadOnlySpan<byte> data)
    {
        var attributes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var classes = new Dictionary<int, ClassKey>();
        Dictionary<string, byte[]>? current = null;

        while (data.Length >= 8)
        {
            var tag = Encoding.ASCII.GetString(data[..4]);
            var length = BinaryPrimitives.ReadInt32BigEndian(data[4..8]);
            if (length < 0 || 8 + length > data.Length)
                throw new FormatException($"The backup's keybag is damaged (block {tag} runs past the end).");
            var value = data.Slice(8, length).ToArray();
            data = data[(8 + length)..];

            // The first UUID names the bag; every later one starts a class key.
            if (tag == "UUID" && attributes.ContainsKey("UUID"))
            {
                Add(classes, current);
                current = new Dictionary<string, byte[]>(StringComparer.Ordinal) { [tag] = value };
            }
            else if (current is not null && tag is "CLAS" or "WRAP" or "WPKY" or "KTYP" or "PBKY")
                current[tag] = value;
            else
                attributes.TryAdd(tag, value);
        }
        Add(classes, current);
        if (!attributes.ContainsKey("SALT") || !attributes.ContainsKey("ITER"))
            throw new FormatException("The backup's keybag has no salt or iteration count.");
        return new BackupKeybag(attributes, classes);

        static void Add(Dictionary<int, ClassKey> classes, Dictionary<string, byte[]>? block)
        {
            if (block is null || !block.TryGetValue("CLAS", out var clas) || !block.TryGetValue("WPKY", out var wrapped))
                return;
            var wrap = block.TryGetValue("WRAP", out var w) ? Int(w) : 0;
            classes[Int(clas)] = new ClassKey(wrap, wrapped);
        }
    }

    /// <summary>
    /// The key the password stands for: PBKDF2-SHA256 with DPSL/DPIC first (iOS 10.2 and later), then PBKDF2-SHA1
    /// with SALT/ITER. Deliberately slow: DPIC is ten million rounds on recent iOS.
    /// </summary>
    public byte[] DeriveKey(string password)
    {
        var secret = Encoding.UTF8.GetBytes(password);
        if (Attributes.TryGetValue("DPSL", out var dpSalt) && Attributes.TryGetValue("DPIC", out var dpRounds))
            secret = Rfc2898DeriveBytes.Pbkdf2(secret, dpSalt, Int(dpRounds), HashAlgorithmName.SHA256, 32);
        return Rfc2898DeriveBytes.Pbkdf2(secret, Attributes["SALT"], Int(Attributes["ITER"]), HashAlgorithmName.SHA1, 32);
    }

    /// <summary>The class keys, or null when the password is wrong (a key doesn't unwrap).</summary>
    public BackupKeys? Unlock(byte[] passwordKey)
    {
        var keys = new Dictionary<int, byte[]>();
        foreach (var (clas, key) in _classes)
        {
            if ((key.Wrap & WrapPasscode) == 0)
                continue;
            var unwrapped = AesKeyWrap.Unwrap(passwordKey, key.Wrapped);
            if (unwrapped is null)
                return null;
            keys[clas] = unwrapped;
        }
        return keys.Count == 0 ? null : new BackupKeys(keys);
    }

    private static int Int(byte[] value) => value.Length switch
    {
        4 => BinaryPrimitives.ReadInt32BigEndian(value),
        8 => checked((int)BinaryPrimitives.ReadInt64BigEndian(value)),
        _ => throw new FormatException("The backup's keybag holds a number of an unexpected size."),
    };

    private sealed record ClassKey(int Wrap, byte[] Wrapped);
}

/// <summary>An unlocked keybag's class keys: they unwrap the manifest's and each file's key.</summary>
internal sealed class BackupKeys(Dictionary<int, byte[]> classes)
{
    /// <summary>A key stored as {4-byte little-endian class}{40-byte wrapped key} (ManifestKey, a file's EncryptionKey).</summary>
    public byte[] UnwrapStoredKey(ReadOnlySpan<byte> stored)
    {
        if (stored.Length < 4 + 24)
            throw new FormatException("A key in the backup is too short.");
        return Unwrap(BinaryPrimitives.ReadInt32LittleEndian(stored), stored[4..]);
    }

    public byte[] Unwrap(int protectionClass, ReadOnlySpan<byte> wrapped)
    {
        if (!classes.TryGetValue(protectionClass, out var classKey))
            throw new FormatException($"The backup has no key for protection class {protectionClass}.");
        return AesKeyWrap.Unwrap(classKey, wrapped)
               ?? throw new FormatException($"A file key in the backup doesn't unwrap with class {protectionClass}.");
    }
}

/// <summary>AES key wrap (RFC 3394), as the keybag uses it.</summary>
internal static class AesKeyWrap
{
    private const ulong DefaultIv = 0xA6A6A6A6A6A6A6A6;

    /// <summary>The unwrapped key, or null when the integrity check fails (wrong key).</summary>
    public static byte[]? Unwrap(ReadOnlySpan<byte> kek, ReadOnlySpan<byte> wrapped)
    {
        if (wrapped.Length < 24 || wrapped.Length % 8 != 0)
            return null;
        var n = wrapped.Length / 8 - 1;
        var a = BinaryPrimitives.ReadUInt64BigEndian(wrapped);
        var r = new ulong[n];
        for (var i = 0; i < n; i++)
            r[i] = BinaryPrimitives.ReadUInt64BigEndian(wrapped[(8 * (i + 1))..]);

        using var aes = Aes.Create();
        aes.Key = kek.ToArray();
        Span<byte> input = stackalloc byte[16];
        Span<byte> output = stackalloc byte[16];
        for (var j = 5; j >= 0; j--)
        {
            for (var i = n; i >= 1; i--)
            {
                BinaryPrimitives.WriteUInt64BigEndian(input, a ^ (ulong)(n * j + i));
                BinaryPrimitives.WriteUInt64BigEndian(input[8..], r[i - 1]);
                aes.DecryptEcb(input, output, PaddingMode.None);
                a = BinaryPrimitives.ReadUInt64BigEndian(output);
                r[i - 1] = BinaryPrimitives.ReadUInt64BigEndian(output[8..]);
            }
        }
        if (a != DefaultIv)
            return null;
        var key = new byte[8 * n];
        for (var i = 0; i < n; i++)
            BinaryPrimitives.WriteUInt64BigEndian(key.AsSpan(8 * i), r[i]);
        return key;
    }

    /// <summary>The inverse of <see cref="Unwrap"/>; DeviceDeck reads backups, the tests write encrypted ones with it.</summary>
    public static byte[] Wrap(ReadOnlySpan<byte> kek, ReadOnlySpan<byte> key)
    {
        if (key.Length < 16 || key.Length % 8 != 0)
            throw new ArgumentException("Only keys of 16 bytes or more, in 8-byte steps, can be wrapped.", nameof(key));
        var n = key.Length / 8;
        var a = DefaultIv;
        var r = new ulong[n];
        for (var i = 0; i < n; i++)
            r[i] = BinaryPrimitives.ReadUInt64BigEndian(key[(8 * i)..]);

        using var aes = Aes.Create();
        aes.Key = kek.ToArray();
        Span<byte> input = stackalloc byte[16];
        Span<byte> output = stackalloc byte[16];
        for (var j = 0; j <= 5; j++)
        {
            for (var i = 1; i <= n; i++)
            {
                BinaryPrimitives.WriteUInt64BigEndian(input, a);
                BinaryPrimitives.WriteUInt64BigEndian(input[8..], r[i - 1]);
                aes.EncryptEcb(input, output, PaddingMode.None);
                a = BinaryPrimitives.ReadUInt64BigEndian(output) ^ (ulong)(n * j + i);
                r[i - 1] = BinaryPrimitives.ReadUInt64BigEndian(output[8..]);
            }
        }
        var wrapped = new byte[8 * (n + 1)];
        BinaryPrimitives.WriteUInt64BigEndian(wrapped, a);
        for (var i = 0; i < n; i++)
            BinaryPrimitives.WriteUInt64BigEndian(wrapped.AsSpan(8 * (i + 1)), r[i]);
        return wrapped;
    }
}
