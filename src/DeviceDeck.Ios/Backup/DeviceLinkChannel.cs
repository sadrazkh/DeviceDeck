using System.Buffers.Binary;
using DeviceDeck.Ios.Plist;

namespace DeviceDeck.Ios.Backup;

/// <summary>
/// Apple's DeviceLink framing, used by mobilebackup2: messages are plist arrays ["DLMessage…", …] sent as a 4-byte
/// big-endian length and a binary plist. While files move, raw blocks travel between messages on the same socket:
/// a 4-byte big-endian length (code byte included), a code byte, then the data.
/// </summary>
internal sealed class DeviceLinkChannel(Stream stream) : IAsyncDisposable
{
    /// <summary>Stands in for a missing string or plist argument, as the phone expects.</summary>
    public const string EmptyParameter = "___EmptyParameterString___";

    /// <summary>Messages are small; anything bigger is a framing error.</summary>
    private const int MaxMessageBytes = 32 * 1024 * 1024;

    private readonly byte[] _word = new byte[4];

    public Stream Stream => stream;

    /// <summary>The phone opens with ["DLMessageVersionExchange", major, minor]; we accept it and wait for DeviceReady.</summary>
    public async Task VersionExchangeAsync(long major, long minor, CancellationToken ct)
    {
        var hello = await ReceiveAsync(ct).ConfigureAwait(false);
        if (NameOf(hello) != "DLMessageVersionExchange" || hello.Count < 3)
            throw new FormatException($"DeviceLink expected a version exchange, got {NameOf(hello) ?? "nothing"}.");
        var theirMajor = PlistValue.AsLong(hello[1]) ?? 0;
        var theirMinor = PlistValue.AsLong(hello[2]) ?? 0;
        if (theirMajor > major || (theirMajor == major && theirMinor > minor))
            throw new FormatException($"The phone speaks DeviceLink {theirMajor}.{theirMinor}, newer than {major}.{minor}.");
        await SendAsync(["DLMessageVersionExchange", "DLVersionsOk", major], ct).ConfigureAwait(false);
        var ready = await ReceiveAsync(ct).ConfigureAwait(false);
        if (NameOf(ready) != "DLMessageDeviceReady")
            throw new FormatException($"DeviceLink expected DeviceReady, got {NameOf(ready) ?? "nothing"}.");
    }

    public async Task SendAsync(List<object> message, CancellationToken ct)
    {
        var body = PlistSerializer.ToBinary(message);
        var frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)body.Length);
        body.CopyTo(frame, 4);
        await stream.WriteAsync(frame, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public async Task<List<object>> ReceiveAsync(CancellationToken ct)
    {
        var length = await ReadUInt32Async(ct).ConfigureAwait(false);
        if (length == 0 || length > MaxMessageBytes)
            throw new FormatException($"DeviceLink message length {length} is out of range.");
        var body = new byte[length];
        await stream.ReadExactlyAsync(body, ct).ConfigureAwait(false);
        return PlistSerializer.Parse(body) as List<object>
               ?? throw new FormatException("A DeviceLink message is not an array.");
    }

    public Task SendProcessMessageAsync(PlistDict message, CancellationToken ct) =>
        SendAsync(["DLMessageProcessMessage", message], ct);

    public async Task<PlistDict> ReceiveProcessMessageAsync(CancellationToken ct)
    {
        var message = await ReceiveAsync(ct).ConfigureAwait(false);
        if (NameOf(message) != "DLMessageProcessMessage" || message.Count < 2 || message[1] is not PlistDict dict)
            throw new FormatException($"DeviceLink expected a process message, got {NameOf(message) ?? "nothing"}.");
        return dict;
    }

    /// <summary>The answer to a file-system request: 0 for success or a negative errno-style code, a text, and a plist.</summary>
    public Task SendStatusResponseAsync(long code, string? text, object? detail, CancellationToken ct) =>
        SendAsync(["DLMessageStatusResponse", code, text ?? EmptyParameter, detail ?? EmptyParameter], ct);

    public Task DisconnectAsync(CancellationToken ct) => SendAsync(["DLMessageDisconnect", EmptyParameter], ct);

    public async Task<uint> ReadUInt32Async(CancellationToken ct)
    {
        await stream.ReadExactlyAsync(_word, ct).ConfigureAwait(false);
        return BinaryPrimitives.ReadUInt32BigEndian(_word);
    }

    public async Task<byte> ReadByteAsync(CancellationToken ct)
    {
        await stream.ReadExactlyAsync(_word.AsMemory(0, 1), ct).ConfigureAwait(false);
        return _word[0];
    }

    public ValueTask ReadExactlyAsync(Memory<byte> buffer, CancellationToken ct) => stream.ReadExactlyAsync(buffer, ct);

    public async Task<string> ReadStringAsync(int length, CancellationToken ct)
    {
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    /// <summary>Writes raw bytes; call <see cref="FlushAsync"/> when a batch is complete.</summary>
    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct) => stream.WriteAsync(data, ct);

    public Task FlushAsync(CancellationToken ct) => stream.FlushAsync(ct);

    public static string? NameOf(List<object> message) => message.Count > 0 ? message[0] as string : null;

    public ValueTask DisposeAsync() => stream.DisposeAsync();
}
