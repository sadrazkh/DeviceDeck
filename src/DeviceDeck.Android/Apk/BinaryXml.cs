using System.Buffers.Binary;
using System.Text;

namespace DeviceDeck.Android.Apk;

/// <summary>A Res_value: the typed value of an attribute or resource entry (frameworks/base/libs/androidfw ResourceTypes.h).</summary>
internal readonly record struct ResValue(byte Type, uint Data)
{
    public const byte TypeNull = 0x00;
    public const byte TypeReference = 0x01;
    public const byte TypeAttribute = 0x02;
    public const byte TypeString = 0x03;
    public const byte TypeFloat = 0x04;
    public const byte TypeDimension = 0x05;
    public const byte TypeFraction = 0x06;
    public const byte TypeDynamicReference = 0x07;
    public const byte TypeIntDec = 0x10;
    public const byte TypeIntHex = 0x11;
    public const byte TypeIntBoolean = 0x12;
    public const byte TypeColorArgb8 = 0x1c;
    public const byte TypeColorRgb8 = 0x1d;
    public const byte TypeColorArgb4 = 0x1e;
    public const byte TypeColorRgb4 = 0x1f;

    public bool IsReference => Type is TypeReference or TypeDynamicReference;
    public bool IsColor => Type is >= TypeColorArgb8 and <= TypeColorRgb4;
    public bool IsInt => Type is >= TypeIntDec and <= TypeColorRgb4;

    public float AsFloat() => Type switch
    {
        TypeFloat => BitConverter.Int32BitsToSingle((int)Data),
        TypeDimension => ComplexToFloat(Data),
        TypeFraction => ComplexToFloat(Data),
        _ when IsInt => (int)Data,
        _ => 0f,
    };

    /// <summary>
    /// Dimensions and fractions pack a signed 24-bit mantissa, a radix and a unit (TypedValue.complexToFloat).
    /// A fraction comes out as a ratio: 16.7% is 0.167.
    /// </summary>
    public static float ComplexToFloat(uint complex)
    {
        const float mantissaMultiplier = 1f / (1 << 8);
        float[] radixMultipliers =
        [
            1f * mantissaMultiplier,
            1f / (1 << 7) * mantissaMultiplier,
            1f / (1 << 15) * mantissaMultiplier,
            1f / (1 << 23) * mantissaMultiplier,
        ];
        int mantissa = (int)(complex & 0xFFFFFF00);
        return mantissa * radixMultipliers[(complex >> 4) & 0x3];
    }

    /// <summary>The unit nibble of a dimension (0 px, 1 dp, 2 sp, ...) or fraction (0 = %, 1 = %p).</summary>
    public int ComplexUnit => (int)(Data & 0xF);
}

/// <summary>ResStringPool: UTF-8 or UTF-16 strings, decoded lazily.</summary>
internal sealed class StringPool
{
    private const uint Utf8Flag = 1 << 8;
    private readonly byte[] _data;
    private readonly int _stringsStart;
    private readonly int[] _offsets;
    private readonly bool _utf8;
    private readonly string?[] _cache;

    private StringPool(byte[] data, int stringsStart, int[] offsets, bool utf8)
    {
        _data = data;
        _stringsStart = stringsStart;
        _offsets = offsets;
        _utf8 = utf8;
        _cache = new string?[offsets.Length];
    }

    public int Count => _offsets.Length;

    /// <summary>Parses the pool chunk that starts at <paramref name="chunk"/>.</summary>
    public static StringPool Read(byte[] data, int chunk)
    {
        int headerSize = Res.U16(data, chunk + 2);
        int stringCount = (int)Res.U32(data, chunk + 8);
        uint flags = Res.U32(data, chunk + 16);
        int stringsStart = chunk + (int)Res.U32(data, chunk + 20);
        if (stringCount < 0 || chunk + headerSize + stringCount * 4L > data.Length)
        {
            throw new InvalidDataException("String pool is out of range.");
        }
        int[] offsets = new int[stringCount];
        for (int i = 0; i < stringCount; i++)
        {
            offsets[i] = (int)Res.U32(data, chunk + headerSize + i * 4);
        }
        return new StringPool(data, stringsStart, offsets, (flags & Utf8Flag) != 0);
    }

    public string? Get(uint index)
    {
        if (index >= _offsets.Length)
        {
            return null;
        }
        return _cache[index] ??= Decode(_stringsStart + _offsets[index]);
    }

    private string Decode(int position)
    {
        if (_utf8)
        {
            // utf16 length (1-2 bytes), utf8 length (1-2 bytes), bytes, NUL
            position += (_data[position] & 0x80) != 0 ? 2 : 1;
            int length = _data[position];
            if ((length & 0x80) != 0)
            {
                length = ((length & 0x7F) << 8) | _data[position + 1];
                position += 2;
            }
            else
            {
                position += 1;
            }
            return Encoding.UTF8.GetString(_data, position, Math.Min(length, _data.Length - position));
        }
        int chars = Res.U16(_data, position);
        position += 2;
        if ((chars & 0x8000) != 0)
        {
            chars = ((chars & 0x7FFF) << 16) | Res.U16(_data, position);
            position += 2;
        }
        return Encoding.Unicode.GetString(_data, position, Math.Min(chars * 2, _data.Length - position));
    }
}

internal static class Res
{
    public const int StringPoolType = 0x0001;
    public const int TableType = 0x0002;
    public const int XmlType = 0x0003;
    public const int XmlStartNamespace = 0x0100;
    public const int XmlEndNamespace = 0x0101;
    public const int XmlStartElement = 0x0102;
    public const int XmlEndElement = 0x0103;
    public const int XmlResourceMap = 0x0180;
    public const int TablePackageType = 0x0200;
    public const int TableTypeType = 0x0201;
    public const int TableTypeSpecType = 0x0202;

    public static ushort U16(byte[] b, int i) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(i, 2));

    public static uint U32(byte[] b, int i) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(i, 4));
}

internal sealed record XmlAttribute(string? Namespace, string Name, uint ResourceId, string? RawValue, ResValue Value, string? StringValue);

internal sealed class XmlElement(string name)
{
    public string Name { get; } = name;
    public List<XmlAttribute> Attributes { get; } = [];
    public List<XmlElement> Children { get; } = [];

    /// <summary>By android: resource id when the file has one, else by local name (some tools strip the resource map).</summary>
    public XmlAttribute? Attribute(uint resourceId, string name) =>
        Attributes.FirstOrDefault(a => a.ResourceId == resourceId && resourceId != 0)
        ?? Attributes.FirstOrDefault(a => a.Name == name);

    public IEnumerable<XmlElement> Descendants()
    {
        foreach (var child in Children)
        {
            yield return child;
            foreach (var nested in child.Descendants())
            {
                yield return nested;
            }
        }
    }
}

/// <summary>Android's compiled XML (AndroidManifest.xml, res/**/*.xml inside an APK).</summary>
internal static class BinaryXml
{
    public static XmlElement Parse(byte[] data)
    {
        if (data.Length < 8 || Res.U16(data, 0) != Res.XmlType)
        {
            throw new InvalidDataException("Not a compiled Android XML file.");
        }
        int end = (int)Math.Min(Res.U32(data, 4), data.Length);
        int position = Res.U16(data, 2);
        StringPool? strings = null;
        uint[] resourceMap = [];
        var stack = new Stack<XmlElement>();
        XmlElement? root = null;

        while (position + 8 <= end)
        {
            int type = Res.U16(data, position);
            int headerSize = Res.U16(data, position + 2);
            int size = (int)Res.U32(data, position + 4);
            if (size < 8 || position + size > end)
            {
                break;
            }
            switch (type)
            {
                case Res.StringPoolType:
                    strings = StringPool.Read(data, position);
                    break;
                case Res.XmlResourceMap:
                    resourceMap = new uint[(size - headerSize) / 4];
                    for (int i = 0; i < resourceMap.Length; i++)
                    {
                        resourceMap[i] = Res.U32(data, position + headerSize + i * 4);
                    }
                    break;
                case Res.XmlStartElement when strings != null:
                {
                    int ext = position + headerSize;
                    var element = new XmlElement(strings.Get(Res.U32(data, ext + 4)) ?? "");
                    int attributeStart = Res.U16(data, ext + 8);
                    int attributeSize = Res.U16(data, ext + 10);
                    int attributeCount = Res.U16(data, ext + 12);
                    for (int i = 0; i < attributeCount; i++)
                    {
                        int a = ext + attributeStart + i * attributeSize;
                        if (a + 20 > position + size)
                        {
                            break;
                        }
                        uint nsIndex = Res.U32(data, a);
                        uint nameIndex = Res.U32(data, a + 4);
                        uint rawIndex = Res.U32(data, a + 8);
                        var value = new ResValue(data[a + 15], Res.U32(data, a + 16));
                        element.Attributes.Add(new XmlAttribute(
                            nsIndex == uint.MaxValue ? null : strings.Get(nsIndex),
                            strings.Get(nameIndex) ?? "",
                            nameIndex < resourceMap.Length ? resourceMap[nameIndex] : 0,
                            rawIndex == uint.MaxValue ? null : strings.Get(rawIndex),
                            value,
                            value.Type == ResValue.TypeString ? strings.Get(value.Data) : null));
                    }
                    if (stack.Count > 0)
                    {
                        stack.Peek().Children.Add(element);
                    }
                    else
                    {
                        root ??= element;
                    }
                    stack.Push(element);
                    break;
                }
                case Res.XmlEndElement:
                    if (stack.Count > 0)
                    {
                        stack.Pop();
                    }
                    break;
            }
            position += size;
        }
        return root ?? throw new InvalidDataException("The compiled XML has no root element.");
    }
}

/// <summary>android.R.attr ids used here (frameworks/base/core/res/res/values/public.xml).</summary>
internal static class AndroidAttr
{
    public const uint Label = 0x01010001;
    public const uint Icon = 0x01010002;
    public const uint Name = 0x01010003;
    public const uint Src = 0x01010119;
    public const uint Tint = 0x01010121;
    public const uint Height = 0x01010155;
    public const uint Width = 0x01010159;
    public const uint Drawable = 0x01010199;
    public const uint StartColor = 0x0101019d;
    public const uint EndColor = 0x0101019e;
    public const uint Color = 0x010101a5;
    public const uint PivotX = 0x010101b5;
    public const uint PivotY = 0x010101b6;
    public const uint InsetLeft = 0x010101b7;
    public const uint InsetRight = 0x010101b8;
    public const uint InsetTop = 0x010101b9;
    public const uint InsetBottom = 0x010101ba;
    public const uint CenterColor = 0x0101020b;
    public const uint VersionCode = 0x0101021b;
    public const uint VersionName = 0x0101021c;
    public const uint Alpha = 0x0101031f;
    public const uint ScaleX = 0x01010324;
    public const uint ScaleY = 0x01010325;
    public const uint Rotation = 0x01010326;
    public const uint ViewportWidth = 0x01010402;
    public const uint ViewportHeight = 0x01010403;
    public const uint FillColor = 0x01010404;
    public const uint PathData = 0x01010405;
    public const uint StrokeColor = 0x01010406;
    public const uint StrokeWidth = 0x01010407;
    public const uint TranslateX = 0x0101045a;
    public const uint TranslateY = 0x0101045b;
    public const uint Inset = 0x010104b5;
    public const uint StrokeAlpha = 0x010104cb;
    public const uint FillAlpha = 0x010104cc;
    public const uint StartX = 0x01010510;
    public const uint StartY = 0x01010511;
    public const uint EndX = 0x01010512;
    public const uint EndY = 0x01010513;
    public const uint Offset = 0x01010514;
    public const uint FillType = 0x0101051e;
    public const uint RoundIcon = 0x0101052c;
}
