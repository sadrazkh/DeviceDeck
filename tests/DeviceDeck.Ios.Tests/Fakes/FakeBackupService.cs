using System.Buffers.Binary;
using System.Text;
using DeviceDeck.Ios.Backup;
using DeviceDeck.Ios.Plist;

namespace DeviceDeck.Ios.Tests.Fakes;

/// <summary>
/// The phone's side of com.apple.mobilebackup2 and com.apple.mobile.notification_proxy: DeviceLink version exchange,
/// Hello, then the request goes to a test script that drives the PC through file requests and sends the result.
/// </summary>
internal sealed class FakeBackupService
{
    private Stream? _notifications;

    public List<string> Posted { get; } = [];
    public List<string> Observed { get; } = [];
    public List<PlistDict> Requests { get; } = [];

    /// <summary>What the phone does after a request (Backup, Restore, ChangePassword); it must end with a result or hang up.</summary>
    public Func<PhoneSide, PlistDict, Task> Script { get; set; } = (p, _) => p.ResultAsync();

    public FakeBackupService Attach(FakePhone phone)
    {
        phone.ExtraServices[MobileBackup2Client.ServiceName] = ServeBackupAsync;
        phone.ExtraServices[BackupServices.NotificationProxy] = ServeNotificationsAsync;
        return this;
    }

    /// <summary>The phone relays a notification, e.g. the user tapping Cancel Sync.</summary>
    public Task RelayAsync(string name) =>
        Wire.WritePlistAsync(_notifications ?? throw new InvalidOperationException("No notification_proxy connection."),
            new PlistDict { ["Command"] = "RelayNotification", ["Name"] = name }, CancellationToken.None);

    private async Task ServeBackupAsync(Stream s, CancellationToken ct)
    {
        var dl = new DeviceLinkChannel(s);
        try
        {
            await dl.SendAsync(["DLMessageVersionExchange", 300L, 0L], ct);
            var ok = await dl.ReceiveAsync(ct);
            if (ok.Count < 2 || ok[1] as string != "DLVersionsOk")
                return;
            await dl.SendAsync(["DLMessageDeviceReady"], ct);
            var hello = await dl.ReceiveProcessMessageAsync(ct);
            if (hello.GetString("MessageName") != "Hello")
                return;
            await dl.SendProcessMessageAsync(new PlistDict { ["MessageName"] = "Response", ["ErrorCode"] = 0L, ["ProtocolVersion"] = 2.1 }, ct);
            var request = await dl.ReceiveProcessMessageAsync(ct);
            lock (Requests)
                Requests.Add(request);
            await Script(new PhoneSide(dl, s), request);
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or ObjectDisposedException or OperationCanceledException)
        {
            // The PC hung up (cancel) or the test pulled the cable.
        }
    }

    private async Task ServeNotificationsAsync(Stream s, CancellationToken ct)
    {
        _notifications = s;
        while (await Wire.ReadPlistAsync(s, ct) is { } message)
        {
            var name = message.GetString("Name") ?? "";
            switch (message.GetString("Command"))
            {
                case "PostNotification":
                    lock (Posted)
                        Posted.Add(name);
                    break;
                case "ObserveNotification":
                    lock (Observed)
                        Observed.Add(name);
                    break;
                case "Shutdown":
                    await Wire.WritePlistAsync(s, new PlistDict { ["Command"] = "ProxyDeath" }, ct);
                    return;
            }
        }
    }
}

/// <summary>What the phone can do during a mobilebackup2 request.</summary>
internal sealed class PhoneSide(DeviceLinkChannel dl, Stream stream)
{
    public Task SendAsync(params object[] message) => dl.SendAsync([.. message], CancellationToken.None);

    /// <summary>The PC's DLMessageStatusResponse: [name, code, text, detail].</summary>
    public async Task<List<object>> StatusAsync()
    {
        var reply = await dl.ReceiveAsync(CancellationToken.None);
        Assert.Equal("DLMessageStatusResponse", reply[0]);
        return reply;
    }

    /// <summary>Sends a request and returns the PC's status response.</summary>
    public async Task<List<object>> AskAsync(params object[] message)
    {
        await SendAsync(message);
        return await StatusAsync();
    }

    /// <summary>DLMessageUploadFiles: the phone sends files in small blocks; returns the PC's status response.</summary>
    public async Task<List<object>> UploadAsync(IEnumerable<(string Path, byte[] Data)> files, double percent = 42, int block = 7)
    {
        var list = files.ToList();
        await SendAsync("DLMessageUploadFiles", new PlistDict(), percent, (long)list.Sum(f => f.Data.Length));
        foreach (var (path, data) in list)
        {
            await WriteStringAsync("/var/mobile/" + path);
            await WriteStringAsync(path);
            for (var at = 0; at < data.Length; at += block)
                await WriteBlockAsync(BackupFolderHost.CodeFileData, data.AsMemory(at, Math.Min(block, data.Length - at)));
            await WriteBlockAsync(BackupFolderHost.CodeSuccess, ReadOnlyMemory<byte>.Empty);
        }
        await WriteUInt32Async(0);
        return await StatusAsync();
    }

    /// <summary>Starts a file and then drops the connection, as a pulled cable does.</summary>
    public async Task UploadAndHangUpAsync(string path, byte[] firstPart)
    {
        await SendAsync("DLMessageUploadFiles", new PlistDict(), 10.0, 1_000_000L);
        await WriteStringAsync("/var/mobile/" + path);
        await WriteStringAsync(path);
        await WriteBlockAsync(BackupFolderHost.CodeFileData, firstPart);
        await stream.DisposeAsync();
    }

    /// <summary>DLMessageDownloadFiles: the PC sends the files; null for one it doesn't have. Then its status response.</summary>
    public async Task<(Dictionary<string, byte[]?> Files, List<object> Status)> DownloadAsync(params string[] paths)
    {
        await SendAsync("DLMessageDownloadFiles", paths.Cast<object>().ToList(), new PlistDict(), 12.5);
        var files = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
        while (true)
        {
            var nameLength = await dl.ReadUInt32Async(CancellationToken.None);
            if (nameLength == 0)
                break;
            var name = await dl.ReadStringAsync((int)nameLength, CancellationToken.None);
            var content = new MemoryStream();
            byte[]? result = null;
            while (true)
            {
                var length = await dl.ReadUInt32Async(CancellationToken.None);
                var code = await dl.ReadByteAsync(CancellationToken.None);
                var payload = new byte[length - 1];
                await dl.ReadExactlyAsync(payload, CancellationToken.None);
                if (code == BackupFolderHost.CodeFileData)
                {
                    content.Write(payload);
                    continue;
                }
                if (code == BackupFolderHost.CodeSuccess)
                    result = content.ToArray();
                break;
            }
            files[name] = result;
        }
        return (files, await StatusAsync());
    }

    public Task ResultAsync(long code = 0, string? description = null)
    {
        var result = new PlistDict { ["ErrorCode"] = code };
        if (description is not null)
            result["ErrorDescription"] = description;
        return dl.SendProcessMessageAsync(result, CancellationToken.None);
    }

    /// <summary>Waits for the PC to close the request (DLMessageDisconnect or the socket closing).</summary>
    public async Task WaitForHangUpAsync()
    {
        try
        {
            while (true)
                await dl.ReceiveAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or ObjectDisposedException or FormatException)
        {
        }
    }

    private async Task WriteStringAsync(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await WriteUInt32Async((uint)bytes.Length);
        await dl.WriteAsync(bytes, CancellationToken.None);
    }

    private async Task WriteBlockAsync(byte code, ReadOnlyMemory<byte> data)
    {
        await WriteUInt32Async((uint)data.Length + 1);
        await dl.WriteAsync(new[] { code }, CancellationToken.None);
        if (!data.IsEmpty)
            await dl.WriteAsync(data, CancellationToken.None);
        await dl.FlushAsync(CancellationToken.None);
    }

    private async Task WriteUInt32Async(uint value)
    {
        var word = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(word, value);
        await dl.WriteAsync(word, CancellationToken.None);
        await dl.FlushAsync(CancellationToken.None);
    }
}
