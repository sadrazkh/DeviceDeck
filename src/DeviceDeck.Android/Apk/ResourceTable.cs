namespace DeviceDeck.Android.Apk;

/// <summary>The parts of a ResTable_config that pick an icon or a label.</summary>
internal readonly record struct ResConfig(string Language, string Country, int Density, int SdkVersion, int UiModeNight)
{
    public const int DensityAny = 0xFFFE;
    public const int DensityNone = 0xFFFF;

    public bool IsDefaultLocale => Language.Length == 0;

    /// <summary>uiMode night bits: 0 = any, 1 = not night, 2 = night.</summary>
    public bool IsNight => UiModeNight == 2;
}

internal sealed record ResEntry(ResConfig Config, ResValue Value);

/// <summary>
/// resources.arsc: the global string pool plus, per package, type chunks holding entries for one configuration.
/// Handles dense, sparse (FLAG_SPARSE) and 16-bit offset (FLAG_OFFSET16, Android 14) tables and compact entries.
/// Complex (bag) entries — styles, arrays — are skipped; icons and labels are simple values.
/// </summary>
internal sealed class ResourceTable
{
    private const byte FlagSparse = 0x01;
    private const byte FlagOffset16 = 0x02;
    private const ushort EntryFlagComplex = 0x0001;
    private const ushort EntryFlagCompact = 0x0008;

    // package id -> type id -> list of (config, chunk offset)
    private readonly Dictionary<int, Dictionary<int, List<(ResConfig Config, int Chunk)>>> _types = [];
    private readonly byte[] _data;

    private ResourceTable(byte[] data, StringPool strings)
    {
        _data = data;
        Strings = strings;
    }

    public StringPool Strings { get; }

    public static ResourceTable Parse(byte[] data)
    {
        if (data.Length < 12 || Res.U16(data, 0) != Res.TableType)
        {
            throw new InvalidDataException("Not a resources.arsc table.");
        }
        int end = (int)Math.Min(Res.U32(data, 4), data.Length);
        int position = Res.U16(data, 2);
        StringPool? strings = null;
        var packages = new List<int>();
        while (position + 8 <= end)
        {
            int type = Res.U16(data, position);
            int size = (int)Res.U32(data, position + 4);
            if (size < 8 || position + size > end)
            {
                break;
            }
            if (type == Res.StringPoolType && strings == null)
            {
                strings = StringPool.Read(data, position);
            }
            else if (type == Res.TablePackageType)
            {
                packages.Add(position);
            }
            position += size;
        }
        var table = new ResourceTable(data, strings ?? throw new InvalidDataException("resources.arsc has no string pool."));
        foreach (int package in packages)
        {
            table.ReadPackage(package);
        }
        return table;
    }

    private void ReadPackage(int chunk)
    {
        int headerSize = Res.U16(_data, chunk + 2);
        int size = (int)Res.U32(_data, chunk + 4);
        int packageId = (int)Res.U32(_data, chunk + 8);
        // typeIdOffset arrived with the 288-byte header; older tables are 284 bytes.
        int typeIdOffset = headerSize >= 288 ? (int)Res.U32(_data, chunk + 284) : 0;
        var types = new Dictionary<int, List<(ResConfig, int)>>();
        _types[packageId] = types;

        int position = chunk + headerSize;
        int end = chunk + size;
        while (position + 8 <= end)
        {
            int type = Res.U16(_data, position);
            int chunkSize = (int)Res.U32(_data, position + 4);
            if (chunkSize < 8 || position + chunkSize > end)
            {
                break;
            }
            if (type == Res.TableTypeType)
            {
                int typeId = _data[position + 8] + typeIdOffset;
                if (!types.TryGetValue(typeId, out var list))
                {
                    types[typeId] = list = [];
                }
                list.Add((ReadConfig(position + 20), position));
            }
            position += chunkSize;
        }
    }

    private ResConfig ReadConfig(int config)
    {
        int size = (int)Res.U32(_data, config);
        string Chars(int offset) =>
            size >= offset + 2 && _data[config + offset] != 0
                ? ((_data[config + offset] & 0x80) == 0
                    ? System.Text.Encoding.ASCII.GetString(_data, config + offset, _data[config + offset + 1] == 0 ? 1 : 2)
                    : "packed")
                : "";
        int density = size >= 16 ? Res.U16(_data, config + 14) : 0;
        int sdk = size >= 26 ? Res.U16(_data, config + 24) : 0;
        int night = size >= 30 ? (_data[config + 29] & 0x30) >> 4 : 0;
        return new ResConfig(Chars(8), Chars(10), density, sdk, night);
    }

    /// <summary>All values of a resource id across configurations (a label per locale, an icon per density, ...).</summary>
    public IReadOnlyList<ResEntry> Get(uint resourceId)
    {
        int packageId = (int)(resourceId >> 24);
        int typeId = (int)((resourceId >> 16) & 0xFF);
        int entryIndex = (int)(resourceId & 0xFFFF);
        if (!_types.TryGetValue(packageId, out var types))
        {
            // A table built as a shared library uses package id 0 at build time and is assigned one at runtime.
            if (_types.Count != 1)
            {
                return [];
            }
            types = _types.Values.First();
        }
        if (!types.TryGetValue(typeId, out var chunks))
        {
            return [];
        }
        var result = new List<ResEntry>();
        foreach (var (config, chunk) in chunks)
        {
            if (ReadEntry(chunk, entryIndex) is { } value)
            {
                result.Add(new ResEntry(config, value));
            }
        }
        return result;
    }

    private ResValue? ReadEntry(int chunk, int index)
    {
        int headerSize = Res.U16(_data, chunk + 2);
        int chunkSize = (int)Res.U32(_data, chunk + 4);
        byte flags = _data[chunk + 9];
        int entryCount = (int)Res.U32(_data, chunk + 12);
        int entriesStart = (int)Res.U32(_data, chunk + 16);
        int offsets = chunk + headerSize;
        long offset;

        if ((flags & FlagSparse) != 0)
        {
            // Sorted (index u16, offset/4 u16) pairs.
            int lo = 0, hi = entryCount - 1;
            offset = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                int midIndex = Res.U16(_data, offsets + mid * 4);
                if (midIndex == index) { offset = Res.U16(_data, offsets + mid * 4 + 2) * 4L; break; }
                if (midIndex < index) lo = mid + 1; else hi = mid - 1;
            }
            if (offset < 0)
            {
                return null;
            }
        }
        else if (index >= entryCount)
        {
            return null;
        }
        else if ((flags & FlagOffset16) != 0)
        {
            int raw = Res.U16(_data, offsets + index * 2);
            if (raw == 0xFFFF)
            {
                return null;
            }
            offset = raw * 4L;
        }
        else
        {
            uint raw = Res.U32(_data, offsets + index * 4);
            if (raw == uint.MaxValue)
            {
                return null;
            }
            offset = raw;
        }

        long entry = chunk + entriesStart + offset;
        if (entry + 8 > chunk + chunkSize)
        {
            return null;
        }
        int e = (int)entry;
        ushort entryFlags = Res.U16(_data, e + 2);
        if ((entryFlags & EntryFlagCompact) != 0)
        {
            // Compact: u16 key, u16 flags (data type in the high byte), u32 data.
            return new ResValue((byte)(entryFlags >> 8), Res.U32(_data, e + 4));
        }
        if ((entryFlags & EntryFlagComplex) != 0)
        {
            return null;
        }
        int entrySize = Res.U16(_data, e);
        int value = e + entrySize;
        if (value + 8 > chunk + chunkSize)
        {
            return null;
        }
        return new ResValue(_data[value + 3], Res.U32(_data, value + 4));
    }

    /// <summary>The default-locale value when there is one, else English, else any — what a launcher in an unknown locale shows.</summary>
    public string? ResolveString(uint resourceId, int depth = 0)
    {
        if (depth > 8)
        {
            return null;
        }
        var entries = Get(resourceId).Where(e => !e.Config.IsNight).ToList();
        var pick = entries.FirstOrDefault(e => e.Config.IsDefaultLocale)
            ?? entries.FirstOrDefault(e => e.Config.Language == "en")
            ?? entries.FirstOrDefault();
        if (pick == null)
        {
            return null;
        }
        return pick.Value.Type switch
        {
            ResValue.TypeString => Strings.Get(pick.Value.Data),
            ResValue.TypeReference or ResValue.TypeDynamicReference => ResolveString(pick.Value.Data, depth + 1),
            _ => null,
        };
    }
}
