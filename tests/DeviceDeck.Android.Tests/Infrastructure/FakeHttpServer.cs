using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DeviceDeck.Android.Tests.Infrastructure;

/// <summary>
/// A tiny HTTP/1.1 server on a loopback port standing in for dl.google.com: one file, optionally a wrong status, a
/// Content-Length that lies, a stall part-way (until released or the client gives up) or a connection cut part-way.
/// Tests never reach the internet.
/// </summary>
internal sealed class FakeHttpServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _accept;
    private int _requests;

    public FakeHttpServer(byte[] body)
    {
        Body = body;
        _listener.Start();
        _accept = AcceptLoopAsync();
    }

    public Uri Url => new($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/android/repository/platform-tools-latest-windows.zip");

    public byte[] Body { get; set; }

    public int Status { get; set; } = 200;

    /// <summary>What Content-Length says; null = the body's real length.</summary>
    public long? ContentLength { get; set; }

    /// <summary>Send this many bytes, then wait for <see cref="Release"/>.</summary>
    public int? StallAfter { get; set; }

    /// <summary>Send this many bytes, then close the connection.</summary>
    public int? CutAfter { get; set; }

    /// <summary>Completes once a response stalled at <see cref="StallAfter"/>.</summary>
    public TaskCompletionSource Stalled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int Requests => Volatile.Read(ref _requests);

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception)
            {
                return;
            }
            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using var _ = client;
        var ct = _stop.Token;
        try
        {
            var stream = client.GetStream();
            await ReadRequestAsync(stream, ct);
            Interlocked.Increment(ref _requests);
            var body = Body;
            string reason = Status == 200 ? "OK" : Status == 404 ? "Not Found" : "Error";
            var head = new StringBuilder()
                .Append($"HTTP/1.1 {Status} {reason}\r\n")
                .Append("Content-Type: application/zip\r\n")
                .Append($"Content-Length: {(Status == 200 ? ContentLength ?? body.Length : 0)}\r\n")
                .Append("Connection: close\r\n\r\n");
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), ct);
            if (Status != 200)
            {
                return;
            }

            int end = Math.Min(body.Length, CutAfter ?? body.Length);
            int stall = StallAfter ?? int.MaxValue;
            for (int offset = 0; offset < end;)
            {
                if (offset >= stall && !Release.Task.IsCompleted)
                {
                    Stalled.TrySetResult();
                    await Release.Task.WaitAsync(ct);
                }

                int chunk = Math.Min(16 * 1024, Math.Min(end, offset < stall ? stall : end) - offset);
                await stream.WriteAsync(body.AsMemory(offset, chunk), ct);
                offset += chunk;
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            // The client went away (a cancelled download): nothing to do.
        }
    }

    private static async Task ReadRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        var seen = new List<byte>();
        var one = new byte[1];
        while (await stream.ReadAsync(one, ct) == 1)
        {
            seen.Add(one[0]);
            if (seen.Count >= 4 && seen[^4] == '\r' && seen[^3] == '\n' && seen[^2] == '\r' && seen[^1] == '\n')
            {
                return;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Release.TrySetResult();
        await _stop.CancelAsync();
        _listener.Stop();
        try
        {
            await _accept;
        }
        catch (Exception)
        {
        }
        _stop.Dispose();
    }
}

/// <summary>Zips shaped like Google's platform-tools (fake contents: tests stand in for the signature and `adb version`).</summary>
internal static class PlatformToolsZips
{
    public static byte[] Good(int adbBytes = 400_000) => Build(new Dictionary<string, byte[]>
    {
        ["platform-tools/adb.exe"] = Noise(adbBytes, 1),
        ["platform-tools/AdbWinApi.dll"] = Noise(20_000, 2),
        ["platform-tools/AdbWinUsbApi.dll"] = Noise(20_000, 3),
        ["platform-tools/source.properties"] = Encoding.ASCII.GetBytes("Pkg.UserSrc=false\nPkg.Revision=37.0.1\n"),
        ["platform-tools/NOTICE.txt"] = Encoding.ASCII.GetBytes("Android SDK Platform-Tools"),
    });

    public static byte[] Build(IReadOnlyDictionary<string, byte[]> files)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, bytes) in files)
            {
                var entry = zip.CreateEntry(name, CompressionLevel.Fastest);
                using var stream = entry.Open();
                stream.Write(bytes);
            }
        }
        return memory.ToArray();
    }

    public static byte[] Without(string name)
    {
        using var source = new ZipArchive(new MemoryStream(Good()), ZipArchiveMode.Read);
        return Build(source.Entries.Where(e => e.FullName != name).ToDictionary(e => e.FullName, e =>
        {
            using var stream = e.Open();
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return copy.ToArray();
        }));
    }

    /// <summary>Random bytes compress badly, so the zip stays about as big as asked: enough for several progress steps.</summary>
    public static byte[] Noise(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }
}
