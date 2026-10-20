using System.Text;
using DeviceDeck.Ios.Plist;

namespace DeviceDeck.Ios.Tests;

public class PlistTests
{
    [Theory]
    [InlineData("mixed.xml")]
    [InlineData("mixed.bplist")]
    public void Parses_every_value_type(string file)
    {
        var dict = PlistSerializer.ParseDict(Fixture.Bytes("Plist", file));

        Assert.Equal("hello", dict.GetString("String"));
        Assert.Equal("سلام \u2014 iPhone", dict.GetString("Unicode"));
        Assert.Equal("", dict.GetString("Empty"));
        Assert.Equal("   ", dict.GetString("Spaces"));
        Assert.Equal(7, dict.GetLong("Small"));
        Assert.Equal(255, dict.GetLong("Byte"));
        Assert.Equal(65535, dict.GetLong("Short"));
        Assert.Equal(4294967295, dict.GetLong("Int32"));
        Assert.Equal(9007199254740993, dict.GetLong("Int64"));
        Assert.Equal(-42, dict.GetLong("Negative"));
        Assert.Equal(ulong.MaxValue, dict["Unsigned64"]);
        Assert.Equal(3.25, dict.GetDouble("Real"));
        Assert.True(dict.GetBool("True"));
        Assert.False(dict.GetBool("False"));
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 8, 30, 15, TimeSpan.Zero), dict.GetDate("Date"));
        Assert.Equal(Enumerable.Range(0, 40).Select(i => (byte)i).ToArray(), dict.GetData("Data"));

        var array = dict.GetArray("Array")!;
        Assert.Equal(4, array.Count);
        Assert.Equal(1L, array[0]);
        Assert.Equal("two", array[1]);
        Assert.Equal(3.5, Assert.IsType<List<object>>(array[2])[0]);
        Assert.Equal(4L, Assert.IsType<PlistDict>(array[3]).GetLong("four"));

        var deep = dict.GetDict("Nested")!.GetDict("Inner")!.GetArray("Deep")!;
        Assert.Equal(20, deep.Count);
        Assert.All(deep, x => Assert.Equal("x", x));
        Assert.Equal(Enumerable.Range(0, 20).Select(i => (object)(long)i), dict.GetArray("LongArray")!);
    }

    [Fact]
    public void Xml_and_binary_fixtures_agree()
    {
        var xml = PlistSerializer.ParseDict(Fixture.Bytes("Plist", "lockdown_getvalue_all.xml"));
        var bin = PlistSerializer.ParseDict(Fixture.Bytes("Plist", "lockdown_getvalue_all.bplist"));
        AssertDeepEqual(xml, bin);
        Assert.Equal("iPhone17,3", bin.GetString("ProductType"));
        Assert.Equal("432", ((PlistDict)bin.GetArray("CarrierBundleInfoArray")![0]).GetString("MCC"));
    }

    [Fact]
    public void Reads_uids_from_binary()
    {
        var dict = PlistSerializer.ParseDict(Fixture.Bytes("Plist", "uid.bplist"));
        Assert.Equal(new PlistUid(1), dict.GetDict("$top")!["root"]);
    }

    [Theory]
    [InlineData("mixed.xml")]
    [InlineData("lockdown_getvalue_all.xml")]
    public void Round_trips_through_both_writers(string file)
    {
        var original = PlistSerializer.ParseDict(Fixture.Bytes("Plist", file));

        var viaXml = PlistSerializer.ParseDict(PlistSerializer.ToXml(original));
        var viaBinary = PlistSerializer.ParseDict(PlistSerializer.ToBinary(original));

        AssertDeepEqual(original, viaXml);
        AssertDeepEqual(original, viaBinary);
    }

    [Fact]
    public void Hand_built_binary_plist_parses()
    {
        // { "a": 1 } laid out by hand from the bplist00 format description.
        byte[] bytes =
        [
            .. "bplist00"u8,
            0xD1, 0x01, 0x02,       // obj0 @8: dict, 1 entry, key ref 1, value ref 2
            0x51, (byte)'a',        // obj1 @11: ASCII string "a"
            0x10, 0x01,             // obj2 @13: int 1
            0x08, 0x0B, 0x0D,       // offset table @15
            0, 0, 0, 0, 0, 0, 1, 1, // trailer: offset size 1, ref size 1
            0, 0, 0, 0, 0, 0, 0, 3, // 3 objects
            0, 0, 0, 0, 0, 0, 0, 0, // top object 0
            0, 0, 0, 0, 0, 0, 0, 15 // offset table at 15
        ];
        var dict = PlistSerializer.ParseDict(bytes);
        Assert.Equal(1, dict.GetLong("a"));
    }

    [Fact]
    public void Xml_writer_output_looks_like_apples()
    {
        var xml = Encoding.UTF8.GetString(PlistSerializer.ToXml(new PlistDict { ["MessageType"] = "ListDevices", ["kLibUSBMuxVersion"] = 3L }));
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>", xml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\"", xml);
        Assert.Contains("<key>MessageType</key>", xml);
        Assert.Contains("<integer>3</integer>", xml);
    }

    [Fact]
    public void Xml_with_trailing_nuls_parses()
    {
        var bytes = PlistSerializer.ToXml(new PlistDict { ["k"] = "v" }).Concat(new byte[] { 0, 0 }).ToArray();
        Assert.Equal("v", PlistSerializer.ParseDict(bytes).GetString("k"));
    }

    [Fact]
    public void Xml_external_entities_are_not_resolved()
    {
        var xml = """
            <?xml version="1.0"?>
            <!DOCTYPE plist [ <!ENTITY xxe SYSTEM "file:///c:/windows/win.ini"> ]>
            <plist version="1.0"><dict><key>k</key><string>&xxe;</string></dict></plist>
            """;
        Assert.ThrowsAny<FormatException>(() => PlistSerializer.Parse(Encoding.UTF8.GetBytes(xml)));
    }

    [Theory]
    [InlineData(new byte[] { (byte)'b', (byte)'p', (byte)'l', (byte)'i', (byte)'s', (byte)'t', (byte)'0', (byte)'0' })]
    [InlineData(new byte[] { 0x3C, 0x70, 0x6C })]
    public void Malformed_input_throws_format_exception(byte[] bytes)
    {
        Assert.ThrowsAny<FormatException>(() => PlistSerializer.Parse(bytes));
    }

    [Fact]
    public void Binary_cycle_is_rejected()
    {
        // obj0 = array containing itself.
        byte[] bytes =
        [
            .. "bplist00"u8,
            0xA1, 0x00,
            0x08,
            0, 0, 0, 0, 0, 0, 1, 1,
            0, 0, 0, 0, 0, 0, 0, 1,
            0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 10
        ];
        Assert.ThrowsAny<FormatException>(() => PlistSerializer.Parse(bytes));
    }

    internal static void AssertDeepEqual(object expected, object actual)
    {
        switch (expected)
        {
            case PlistDict e:
                var a = Assert.IsType<PlistDict>(actual);
                Assert.Equal(e.Keys.OrderBy(k => k, StringComparer.Ordinal), a.Keys.OrderBy(k => k, StringComparer.Ordinal));
                foreach (var (k, v) in e)
                    AssertDeepEqual(v, a[k]);
                break;
            case List<object> el:
                var al = Assert.IsType<List<object>>(actual);
                Assert.Equal(el.Count, al.Count);
                for (var i = 0; i < el.Count; i++)
                    AssertDeepEqual(el[i], al[i]);
                break;
            case byte[] eb:
                Assert.Equal(eb, Assert.IsType<byte[]>(actual));
                break;
            default:
                Assert.Equal(expected, actual);
                break;
        }
    }
}
