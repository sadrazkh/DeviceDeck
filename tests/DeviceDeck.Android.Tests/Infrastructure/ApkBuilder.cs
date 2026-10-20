using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using DeviceDeck.Android.Apk;

namespace DeviceDeck.Android.Tests.Infrastructure;

/// <summary>An attribute for <see cref="AxmlWriter"/>: android:name (with its R.attr id) or a plain one (id 0).</summary>
internal sealed record TestAttr(string Name, uint ResourceId, byte Type, uint Data, string? Text = null)
{
    public static TestAttr String(string name, uint id, string text) => new(name, id, ResValue.TypeString, 0, text);
    public static TestAttr Reference(string name, uint id, uint target) => new(name, id, ResValue.TypeReference, target);
    public static TestAttr Int(string name, uint id, int value) => new(name, id, ResValue.TypeIntDec, (uint)value);
    public static TestAttr Color(string name, uint id, uint argb) => new(name, id, ResValue.TypeColorArgb8, argb);
    public static TestAttr Float(string name, uint id, float value) => new(name, id, ResValue.TypeFloat, (uint)BitConverter.SingleToInt32Bits(value));
}

internal sealed record TestElement(string Name, IReadOnlyList<TestAttr> Attributes, IReadOnlyList<TestElement> Children)
{
    public TestElement(string name, params TestAttr[] attributes) : this(name, attributes, []) { }
}

/// <summary>Writes Android's compiled XML (ResXMLTree) the way aapt2 lays it out: UTF-16 pool, resource map, namespace, elements.</summary>
internal static class AxmlWriter
{
    private const string AndroidUri = "http://schemas.android.com/apk/res/android";

    public static byte[] Write(TestElement root, bool withResourceMap = true)
    {
        // Attribute names that carry ids come first so their pool index matches the resource map.
        var all = Flatten(root).ToList();
        var idNames = all.SelectMany(e => e.Attributes).Where(a => a.ResourceId != 0 && withResourceMap)
            .Select(a => (a.Name, a.ResourceId)).Distinct().ToList();
        var strings = new List<string>(idNames.Select(n => n.Name));
        int Index(string s)
        {
            int i = strings.IndexOf(s);
            if (i < 0)
            {
                strings.Add(s);
                i = strings.Count - 1;
            }
            return i;
        }
        foreach (var element in all)
        {
            Index(element.Name);
            foreach (var attribute in element.Attributes)
            {
                Index(attribute.Name);
                if (attribute.Text != null)
                {
                    Index(attribute.Text);
                }
            }
        }
        int uri = Index(AndroidUri);
        int prefix = Index("android");

        var body = new MemoryStream();
        body.Write(StringPool(strings, utf8: false));
        if (withResourceMap && idNames.Count > 0)
        {
            var map = new MemoryStream();
            foreach (var (_, id) in idNames)
            {
                map.Write(U32(id));
            }
            body.Write(Chunk(Res.XmlResourceMap, 8, map.ToArray()));
        }
        body.Write(Chunk(Res.XmlStartNamespace, 16, [.. U32(0), .. U32(uint.MaxValue), .. U32((uint)prefix), .. U32((uint)uri)]));
        WriteElement(body, root, Index, uri);
        body.Write(Chunk(Res.XmlEndNamespace, 16, [.. U32(0), .. U32(uint.MaxValue), .. U32((uint)prefix), .. U32((uint)uri)]));
        return Chunk(Res.XmlType, 8, body.ToArray());
    }

    private static void WriteElement(Stream output, TestElement element, Func<string, int> index, int uri)
    {
        var data = new MemoryStream();
        data.Write(U32(0));               // line
        data.Write(U32(uint.MaxValue));   // comment
        data.Write(U32(uint.MaxValue));   // ns
        data.Write(U32((uint)index(element.Name)));
        data.Write(U16(20));
        data.Write(U16(20));
        data.Write(U16((ushort)element.Attributes.Count));
        data.Write(U16(0));
        data.Write(U16(0));
        data.Write(U16(0));
        foreach (var attribute in element.Attributes)
        {
            data.Write(U32(attribute.ResourceId != 0 ? (uint)uri : uint.MaxValue));
            data.Write(U32((uint)index(attribute.Name)));
            uint text = attribute.Text != null ? (uint)index(attribute.Text) : uint.MaxValue;
            data.Write(U32(text));
            data.Write(U16(8));
            data.WriteByte(0);
            data.WriteByte(attribute.Type);
            data.Write(U32(attribute.Type == ResValue.TypeString ? text : attribute.Data));
        }
        // Header (8) + line/comment (8) are the 16-byte node header; the rest is the extension.
        output.Write(Chunk(Res.XmlStartElement, 16, data.ToArray()));
        foreach (var child in element.Children)
        {
            WriteElement(output, child, index, uri);
        }
        output.Write(Chunk(Res.XmlEndElement, 16, [.. U32(0), .. U32(uint.MaxValue), .. U32(uint.MaxValue), .. U32((uint)index(element.Name))]));
    }

    private static IEnumerable<TestElement> Flatten(TestElement element) =>
        new[] { element }.Concat(element.Children.SelectMany(Flatten));

    /// <summary>A ResStringPool chunk.</summary>
    public static byte[] StringPool(IReadOnlyList<string> strings, bool utf8)
    {
        var offsets = new MemoryStream();
        var data = new MemoryStream();
        foreach (string s in strings)
        {
            offsets.Write(U32((uint)data.Length));
            if (utf8)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(s);
                WriteUtf8Length(data, s.Length);
                WriteUtf8Length(data, bytes.Length);
                data.Write(bytes);
                data.WriteByte(0);
            }
            else
            {
                data.Write(U16((ushort)s.Length));
                data.Write(Encoding.Unicode.GetBytes(s));
                data.Write(U16(0));
            }
        }
        while (data.Length % 4 != 0)
        {
            data.WriteByte(0);
        }
        const int headerSize = 28;
        var body = new MemoryStream();
        body.Write(U32((uint)strings.Count));
        body.Write(U32(0));
        body.Write(U32(utf8 ? 1u << 8 : 0));
        body.Write(U32((uint)(headerSize + offsets.Length)));
        body.Write(U32(0));
        body.Write(offsets.ToArray());
        body.Write(data.ToArray());
        return Chunk(Res.StringPoolType, headerSize, body.ToArray());
    }

    private static void WriteUtf8Length(Stream stream, int length)
    {
        if (length > 0x7F)
        {
            stream.WriteByte((byte)(((length >> 8) & 0x7F) | 0x80));
        }
        stream.WriteByte((byte)(length & 0xFF));
    }

    /// <summary>type, headerSize, size, then <paramref name="rest"/> (everything after the 8-byte chunk header).</summary>
    public static byte[] Chunk(int type, int headerSize, byte[] rest)
    {
        byte[] chunk = new byte[8 + rest.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(chunk, (ushort)type);
        BinaryPrimitives.WriteUInt16LittleEndian(chunk.AsSpan(2), (ushort)headerSize);
        BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(4), (uint)chunk.Length);
        rest.CopyTo(chunk, 8);
        return chunk;
    }

    public static byte[] U16(ushort value)
    {
        byte[] b = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(b, value);
        return b;
    }

    public static byte[] U32(uint value)
    {
        byte[] b = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, value);
        return b;
    }
}

/// <summary>How the type chunks store their entry offsets.</summary>
public enum ArscLayout { Dense, Offset16, Sparse }

/// <summary>Writes a one-package resources.arsc (package 0x7f).</summary>
internal sealed class ArscWriter
{
    private readonly List<string> _values = [];
    private readonly List<string> _typeNames = [];
    private readonly List<string> _keys = [];
    private readonly List<(int Type, int Entry, string Language, int Density, int Sdk, byte DataType, uint Data)> _entries = [];

    public ArscLayout Layout { get; init; } = ArscLayout.Dense;
    public bool Compact { get; init; }
    public bool Utf8Pool { get; init; } = true;

    public uint Id(string type, int entry)
    {
        int typeId = _typeNames.IndexOf(type) + 1;
        if (typeId == 0)
        {
            _typeNames.Add(type);
            typeId = _typeNames.Count;
        }
        return 0x7f000000u | ((uint)typeId << 16) | (uint)entry;
    }

    public ArscWriter String(uint id, string value, string language = "", int density = 0) =>
        Value(id, ResValue.TypeString, ValueIndex(value), language, density);

    public ArscWriter File(uint id, string path, int density = 0, int sdk = 0) =>
        Value(id, ResValue.TypeString, ValueIndex(path), "", density, sdk);

    public ArscWriter Color(uint id, uint argb) => Value(id, ResValue.TypeColorArgb8, argb);

    public ArscWriter Value(uint id, byte type, uint data, string language = "", int density = 0, int sdk = 0)
    {
        _entries.Add(((int)((id >> 16) & 0xFF), (int)(id & 0xFFFF), language, density, sdk, type, data));
        return this;
    }

    private uint ValueIndex(string value)
    {
        int i = _values.IndexOf(value);
        if (i < 0)
        {
            _values.Add(value);
            i = _values.Count - 1;
        }
        return (uint)i;
    }

    public byte[] Build()
    {
        var package = new MemoryStream();
        // id, name[128], typeStrings, lastPublicType, keyStrings, lastPublicKey, typeIdOffset
        byte[] typePool = AxmlWriter.StringPool(_typeNames, utf8: false);
        int maxEntry = _entries.Count == 0 ? 0 : _entries.Max(e => e.Entry) + 1;
        for (int i = 0; i < maxEntry; i++)
        {
            _keys.Add($"key{i}");
        }
        byte[] keyPool = AxmlWriter.StringPool(_keys, utf8: true);
        const int headerSize = 288;

        var chunks = new MemoryStream();
        chunks.Write(typePool);
        chunks.Write(keyPool);
        foreach (var group in _entries.GroupBy(e => (e.Type, e.Language, e.Density, e.Sdk)).OrderBy(g => g.Key.Type))
        {
            chunks.Write(TypeChunk(group.Key.Type, group.Key.Language, group.Key.Density, group.Key.Sdk, group.ToList()));
        }

        var header = new MemoryStream();
        header.Write(AxmlWriter.U32(0x7f));
        byte[] name = new byte[256];
        Encoding.Unicode.GetBytes("com.test").CopyTo(name, 0);
        header.Write(name);
        header.Write(AxmlWriter.U32(headerSize));                       // typeStrings
        header.Write(AxmlWriter.U32((uint)_typeNames.Count));
        header.Write(AxmlWriter.U32((uint)(headerSize + typePool.Length))); // keyStrings
        header.Write(AxmlWriter.U32((uint)_keys.Count));
        header.Write(AxmlWriter.U32(0));                                // typeIdOffset
        byte[] packageChunk = AxmlWriter.Chunk(Res.TablePackageType, headerSize, [.. header.ToArray(), .. chunks.ToArray()]);

        var table = new MemoryStream();
        table.Write(AxmlWriter.U32(1));
        table.Write(AxmlWriter.StringPool(_values, Utf8Pool));
        table.Write(packageChunk);
        return AxmlWriter.Chunk(Res.TableType, 12, table.ToArray());
    }

    private byte[] TypeChunk(int typeId, string language, int density, int sdk, List<(int Type, int Entry, string Language, int Density, int Sdk, byte DataType, uint Data)> entries)
    {
        int count = entries.Max(e => e.Entry) + 1;
        const int configSize = 64;
        int headerSize = 20 + configSize;

        var entryData = new MemoryStream();
        var offsets = new Dictionary<int, int>();
        foreach (var e in entries.OrderBy(e => e.Entry))
        {
            offsets[e.Entry] = (int)entryData.Length;
            if (Compact)
            {
                entryData.Write(AxmlWriter.U16((ushort)e.Entry));
                entryData.Write(AxmlWriter.U16((ushort)(0x0008 | (e.DataType << 8))));
                entryData.Write(AxmlWriter.U32(e.Data));
            }
            else
            {
                entryData.Write(AxmlWriter.U16(8));
                entryData.Write(AxmlWriter.U16(0));
                entryData.Write(AxmlWriter.U32((uint)e.Entry));
                entryData.Write(AxmlWriter.U16(8));
                entryData.WriteByte(0);
                entryData.WriteByte(e.DataType);
                entryData.Write(AxmlWriter.U32(e.Data));
            }
        }

        var table = new MemoryStream();
        byte flags = 0;
        int tableCount = count;
        switch (Layout)
        {
            case ArscLayout.Dense:
                for (int i = 0; i < count; i++)
                {
                    table.Write(AxmlWriter.U32(offsets.TryGetValue(i, out int o) ? (uint)o : uint.MaxValue));
                }
                break;
            case ArscLayout.Offset16:
                flags = 0x02;
                for (int i = 0; i < count; i++)
                {
                    table.Write(AxmlWriter.U16(offsets.TryGetValue(i, out int o) ? (ushort)(o / 4) : (ushort)0xFFFF));
                }
                while (table.Length % 4 != 0)
                {
                    table.WriteByte(0);
                }
                break;
            case ArscLayout.Sparse:
                flags = 0x01;
                tableCount = offsets.Count;
                foreach (var (entry, offset) in offsets.OrderBy(p => p.Key))
                {
                    table.Write(AxmlWriter.U16((ushort)entry));
                    table.Write(AxmlWriter.U16((ushort)(offset / 4)));
                }
                break;
        }

        byte[] config = new byte[configSize];
        BinaryPrimitives.WriteUInt32LittleEndian(config, configSize);
        if (language.Length == 2)
        {
            config[8] = (byte)language[0];
            config[9] = (byte)language[1];
        }
        BinaryPrimitives.WriteUInt16LittleEndian(config.AsSpan(14), (ushort)density);
        BinaryPrimitives.WriteUInt16LittleEndian(config.AsSpan(24), (ushort)sdk);

        var rest = new MemoryStream();
        rest.WriteByte((byte)typeId);
        rest.WriteByte(flags);
        rest.Write(AxmlWriter.U16(0));
        rest.Write(AxmlWriter.U32((uint)tableCount));
        rest.Write(AxmlWriter.U32((uint)(headerSize + table.Length)));
        rest.Write(config);
        rest.Write(table.ToArray());
        rest.Write(entryData.ToArray());
        return AxmlWriter.Chunk(Res.TableTypeType, headerSize, rest.ToArray());
    }
}

internal static class ApkZip
{
    /// <summary>A ZIP with the given entries; resources.arsc stored (as aapt2 does for targetSdk 30+), the rest deflated.</summary>
    public static byte[] Build(IReadOnlyDictionary<string, byte[]> entries)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, data) in entries)
            {
                var level = name == "resources.arsc" || name.EndsWith(".png", StringComparison.Ordinal) ? CompressionLevel.NoCompression : CompressionLevel.Optimal;
                var entry = zip.CreateEntry(name, level);
                using var stream = entry.Open();
                stream.Write(data);
            }
        }
        return buffer.ToArray();
    }
}
