using System.Buffers.Binary;
using DeviceDeck.Ios.Plist;

namespace DeviceDeck.Ios.Tests.Fakes;

/// <summary>Server-side framing helpers for the fakes: what the phone or usbmuxd would do.</summary>
internal static class Wire
{
    public static async Task<(uint Tag, PlistDict Message)?> ReadUsbmuxAsync(Stream s, CancellationToken ct)
    {
        var header = new byte[16];
        try
        {
            await s.ReadExactlyAsync(header, ct);
        }
        catch (EndOfStreamException)
        {
            return null;
        }
        var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        var tag = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12));
        var body = new byte[length - 16];
        await s.ReadExactlyAsync(body, ct);
        return (tag, PlistSerializer.ParseDict(body));
    }

    public static async Task WriteUsbmuxAsync(Stream s, uint tag, PlistDict message, CancellationToken ct, bool binary = false)
    {
        var body = binary ? PlistSerializer.ToBinary(message) : PlistSerializer.ToXml(message);
        var frame = new byte[16 + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)frame.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(8), 8);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(12), tag);
        body.CopyTo(frame, 16);
        await s.WriteAsync(frame, ct);
        await s.FlushAsync(ct);
    }

    public static async Task<PlistDict?> ReadPlistAsync(Stream s, CancellationToken ct)
    {
        var header = new byte[4];
        try
        {
            await s.ReadExactlyAsync(header, ct);
        }
        catch (EndOfStreamException)
        {
            return null;
        }
        var body = new byte[BinaryPrimitives.ReadUInt32BigEndian(header)];
        await s.ReadExactlyAsync(body, ct);
        return PlistSerializer.ParseDict(body);
    }

    /// <summary>Phones answer lockdown in XML and some services in binary; the fakes do both.</summary>
    public static async Task WritePlistAsync(Stream s, PlistDict message, CancellationToken ct, bool binary = false)
    {
        var body = binary ? PlistSerializer.ToBinary(message) : PlistSerializer.ToXml(message);
        var frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)body.Length);
        body.CopyTo(frame, 4);
        await s.WriteAsync(frame, ct);
        await s.FlushAsync(ct);
    }
}
