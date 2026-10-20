using System.Buffers.Binary;
using System.Text;
using DeviceDeck.Core.Abstractions;

namespace DeviceDeck.Ios.Protocol.Afc;

/// <summary>AFC operation codes (The iPhone Wiki, "AFC").</summary>
public enum AfcOperation : ulong
{
    Status = 0x01,
    Data = 0x02,
    ReadDir = 0x03,
    RemovePath = 0x08,
    MakeDir = 0x09,
    GetFileInfo = 0x0A,
    GetDeviceInfo = 0x0B,
    FileOpen = 0x0D,
    FileOpenResult = 0x0E,
    FileRead = 0x0F,
    FileWrite = 0x10,
    FileSeek = 0x11,
    FileTell = 0x12,
    FileTellResult = 0x13,
    FileClose = 0x14,
    FileSetSize = 0x15,
    RenamePath = 0x18,
    SetFileModTime = 0x1E,
    RemovePathAndContents = 0x22,
}

public enum AfcStatus : ulong
{
    Success = 0,
    UnknownError = 1,
    OpHeaderInvalid = 2,
    NoResources = 3,
    ReadError = 4,
    WriteError = 5,
    UnknownPacketType = 6,
    InvalidArgument = 7,
    ObjectNotFound = 8,
    ObjectIsDir = 9,
    PermissionDenied = 10,
    ServiceNotConnected = 11,
    OpTimeout = 12,
    TooMuchData = 13,
    EndOfData = 14,
    OpNotSupported = 15,
    ObjectExists = 16,
    ObjectBusy = 17,
    NoSpaceLeft = 18,
    OpWouldBlock = 19,
    IoError = 20,
    OpInterrupted = 21,
    OpInProgress = 22,
    InternalError = 23,
    MuxError = 30,
    NoMemory = 31,
    NotEnoughData = 32,
    DirNotEmpty = 33,
}

/// <summary>fopen-style modes for FileOpen.</summary>
public enum AfcFileMode : ulong
{
    ReadOnly = 1,          // r
    ReadWrite = 2,         // r+
    WriteOnlyCreateTruncate = 3, // w
    ReadWriteCreateTruncate = 4, // w+
    Append = 5,            // a
    ReadAppend = 6,        // a+
}

/// <summary>
/// One AFC packet: a 40-byte little-endian header {magic "CFA6LPAA", entire length, this length,
/// packet number, operation}, then header data (this length − 40), then payload (entire − this).
/// </summary>
public sealed record AfcPacket(AfcOperation Operation, ulong PacketNumber, byte[] HeaderData, byte[] Payload)
{
    public const int HeaderSize = 40;
    public static ReadOnlySpan<byte> Magic => "CFA6LPAA"u8;

    public byte[] Encode() => [.. EncodeHeader(Operation, PacketNumber, HeaderData, Payload.Length), .. Payload];

    /// <summary>The 40-byte header plus header data; the payload follows separately.</summary>
    public static byte[] EncodeHeader(AfcOperation operation, ulong packetNumber, byte[] headerData, int payloadLength)
    {
        var thisLength = (ulong)(HeaderSize + headerData.Length);
        var entireLength = thisLength + (ulong)payloadLength;
        var buffer = new byte[thisLength];
        Magic.CopyTo(buffer);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(8), entireLength);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(16), thisLength);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(24), packetNumber);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(32), (ulong)operation);
        headerData.CopyTo(buffer, HeaderSize);
        return buffer;
    }

    /// <summary>Parses a 40-byte header; returns (entire length, this length, packet number, operation).</summary>
    public static (ulong Entire, ulong This, ulong PacketNumber, AfcOperation Operation) DecodeHeader(ReadOnlySpan<byte> header)
    {
        if (header.Length < HeaderSize || !header[..8].SequenceEqual(Magic))
            throw new FormatException("AFC packet does not start with CFA6LPAA.");
        var entire = BinaryPrimitives.ReadUInt64LittleEndian(header[8..]);
        var thisLength = BinaryPrimitives.ReadUInt64LittleEndian(header[16..]);
        if (thisLength < HeaderSize || entire < thisLength)
            throw new FormatException($"AFC packet lengths are inconsistent (entire {entire}, this {thisLength}).");
        return (entire, thisLength, BinaryPrimitives.ReadUInt64LittleEndian(header[24..]),
            (AfcOperation)BinaryPrimitives.ReadUInt64LittleEndian(header[32..]));
    }

    /// <summary>Header data then payload, which is how replies carry their content.</summary>
    public byte[] Content => HeaderData.Length == 0 ? Payload : Payload.Length == 0 ? HeaderData : [.. HeaderData, .. Payload];

    public static byte[] Path(string path) => [.. Encoding.UTF8.GetBytes(path), 0];

    public static byte[] U64(ulong value)
    {
        var b = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(b, value);
        return b;
    }

    /// <summary>Splits NUL-terminated UTF-8 strings (ReadDir, GetFileInfo, GetDeviceInfo replies).</summary>
    public static List<string> SplitStrings(ReadOnlySpan<byte> data)
    {
        var result = new List<string>();
        while (!data.IsEmpty)
        {
            var nul = data.IndexOf((byte)0);
            var part = nul < 0 ? data : data[..nul];
            result.Add(Encoding.UTF8.GetString(part));
            if (nul < 0)
                break;
            data = data[(nul + 1)..];
        }
        return result;
    }

    /// <summary>Key/value pairs from a NUL-separated list.</summary>
    public static Dictionary<string, string> ParsePairs(ReadOnlySpan<byte> data)
    {
        var parts = SplitStrings(data);
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i + 1 < parts.Count; i += 2)
            dict[parts[i]] = parts[i + 1];
        return dict;
    }

    public static DeviceException ErrorFor(AfcStatus status, AfcOperation op, string? path)
    {
        var where = path is null ? op.ToString() : $"{op} '{path}'";
        var (code, text) = status switch
        {
            AfcStatus.ObjectNotFound => (DeviceErrorCodes.PathNotFound, "No such file or folder on the phone"),
            AfcStatus.PermissionDenied => (DeviceErrorCodes.PermissionDenied, "The phone does not allow access to this path"),
            AfcStatus.ObjectExists => (DeviceErrorCodes.AlreadyExists, "Something with that name already exists"),
            AfcStatus.ObjectIsDir => (DeviceErrorCodes.Protocol, "That is a folder, not a file"),
            AfcStatus.DirNotEmpty => (DeviceErrorCodes.Protocol, "The folder is not empty"),
            AfcStatus.NoSpaceLeft => (DeviceErrorCodes.Protocol, "The phone is out of space"),
            AfcStatus.OpNotSupported => (DeviceErrorCodes.NotSupported, "The phone does not support this file operation"),
            AfcStatus.ServiceNotConnected or AfcStatus.MuxError => (DeviceErrorCodes.Disconnected, "The file service connection dropped"),
            _ => (DeviceErrorCodes.Protocol, "The phone's file service failed"),
        };
        return new DeviceException(code, $"{text} ({where}: AFC status {(ulong)status} {status}).");
    }
}
