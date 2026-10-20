namespace DeviceDeck.Ios.Plist;

/// <summary>Entry point for property lists: XML ("&lt;?xml" / "&lt;plist") and binary ("bplist00").</summary>
public static class PlistSerializer
{
    private static readonly byte[] BinaryMagic = "bplist00"u8.ToArray();

    /// <summary>Parses either format. Throws <see cref="FormatException"/> on malformed input.</summary>
    public static object Parse(ReadOnlySpan<byte> data)
    {
        if (data.StartsWith(BinaryMagic))
            return BinaryPlistReader.Read(data);
        return XmlPlistReader.Read(data);
    }

    /// <summary>Parses and requires a dictionary at the top.</summary>
    public static PlistDict ParseDict(ReadOnlySpan<byte> data) =>
        Parse(data) as PlistDict ?? throw new FormatException("The property list's top object is not a dictionary.");

    public static bool IsBinary(ReadOnlySpan<byte> data) => data.StartsWith(BinaryMagic);

    public static byte[] ToXml(object root) => XmlPlistWriter.Write(root);

    public static byte[] ToBinary(object root) => BinaryPlistWriter.Write(root);
}
