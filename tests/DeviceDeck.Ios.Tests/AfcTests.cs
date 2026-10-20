using System.Text;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Ios.Protocol.Afc;
using DeviceDeck.Ios.Tests.Fakes;

namespace DeviceDeck.Ios.Tests;

public class AfcTests
{
    private static CancellationToken Ct => TestContext.Timeout();

    private static (AfcClient Client, FakeAfcServer Server) Connect(FakeAfcServer? server = null)
    {
        server ??= new FakeAfcServer();
        var (client, serverStream) = DuplexStream.CreatePair();
        _ = Task.Run(() => server.ServeAsync(serverStream, CancellationToken.None));
        return (new AfcClient(client), server);
    }

    [Fact]
    public void Packet_header_layout()
    {
        var packet = new AfcPacket(AfcOperation.FileOpen, 3, [.. AfcPacket.U64(1), .. AfcPacket.Path("/a")], [9, 9]);
        var bytes = packet.Encode();

        Assert.Equal("CFA6LPAA"u8.ToArray(), bytes[..8]);
        Assert.Equal(40ul + 8 + 3 + 2, BitConverter.ToUInt64(bytes, 8));   // entire length
        Assert.Equal(40ul + 8 + 3, BitConverter.ToUInt64(bytes, 16));      // this length (header + header data)
        Assert.Equal(3ul, BitConverter.ToUInt64(bytes, 24));               // packet number
        Assert.Equal(0x0Dul, BitConverter.ToUInt64(bytes, 32));            // FileOpen
        Assert.Equal(1ul, BitConverter.ToUInt64(bytes, 40));               // mode r
        Assert.Equal("/a\0"u8.ToArray(), bytes[48..51]);
        Assert.Equal(new byte[] { 9, 9 }, bytes[51..]);

        var (entire, thisLength, number, op) = AfcPacket.DecodeHeader(bytes);
        Assert.Equal((53ul, 51ul, 3ul, AfcOperation.FileOpen), (entire, thisLength, number, op));
    }

    [Fact]
    public void Bad_magic_and_inconsistent_lengths_are_rejected()
    {
        var bytes = new AfcPacket(AfcOperation.Status, 0, AfcPacket.U64(0), []).Encode();
        var badMagic = (byte[])bytes.Clone();
        badMagic[0] = (byte)'X';
        Assert.Throws<FormatException>(() => AfcPacket.DecodeHeader(badMagic));

        var badLength = (byte[])bytes.Clone();
        BitConverter.GetBytes(10ul).CopyTo(badLength, 8);
        Assert.Throws<FormatException>(() => AfcPacket.DecodeHeader(badLength));
    }

    [Fact]
    public void Nul_separated_lists_and_pairs()
    {
        Assert.Equal([".", "..", "DCIM", "Downloads"], AfcPacket.SplitStrings("."u8.ToArray().Concat("\0..\0DCIM\0Downloads\0"u8.ToArray()).ToArray()));
        var pairs = AfcPacket.ParsePairs("st_size\0123\0st_ifmt\0S_IFDIR\0st_mtime\01760947200000000000\0"u8);
        var info = AfcFileInfo.FromPairs(pairs);
        Assert.Equal(123, info.Size);
        Assert.True(info.IsDirectory);
        Assert.Equal(new DateTimeOffset(2025, 10, 20, 8, 0, 0, TimeSpan.Zero), info.Modified);
    }

    [Fact]
    public async Task ReadDir_skips_dot_entries_and_stat_reports_type_and_size()
    {
        var (afc, server) = Connect();
        server.AddFile("/Downloads/a.txt", Encoding.UTF8.GetBytes("hello")).AddDir("/Books");
        await using var _ = afc;

        Assert.Equal(["Books", "Downloads"], await afc.ReadDirectoryAsync("/", Ct));
        var file = await afc.GetFileInfoAsync("/Downloads/a.txt", Ct);
        Assert.Equal(5, file!.Size);
        Assert.False(file.IsDirectory);
        Assert.True((await afc.GetFileInfoAsync("/Books", Ct))!.IsDirectory);
        Assert.Null(await afc.GetFileInfoAsync("/nope", Ct));
    }

    [Fact]
    public async Task Large_files_stream_in_chunks_with_progress()
    {
        var content = new byte[AfcClient.ReadChunk * 2 + 12345];
        new Random(1).NextBytes(content);
        var (afc, server) = Connect();
        server.AddFile("/DCIM/100APPLE/IMG_0001.MOV", content).AddDir("/Downloads");
        await using var _ = afc;

        var progress = new List<long>();
        using var dest = new MemoryStream();
        await afc.ReadFileAsync("/DCIM/100APPLE/IMG_0001.MOV", dest, new SyncProgress(progress), Ct);

        Assert.Equal(content, dest.ToArray());
        Assert.Equal([AfcClient.ReadChunk, AfcClient.ReadChunk * 2L, content.Length], progress);
        Assert.Equal(AfcClient.ReadChunk, server.MaxReadSeen);

        var upload = new byte[AfcClient.WriteChunk * 3 + 7];
        new Random(2).NextBytes(upload);
        progress.Clear();
        await afc.WriteFileAsync("/Downloads/up.bin", new MemoryStream(upload), upload.Length, new SyncProgress(progress), Ct);
        Assert.Equal(upload, server.FileContent("/Downloads/up.bin"));
        Assert.Equal(4, progress.Count);
        Assert.Equal(upload.Length, progress[^1]);
        Assert.Equal(server.Operations.Count(o => o == AfcOperation.FileOpen), server.Operations.Count(o => o == AfcOperation.FileClose));
    }

    [Fact]
    public async Task Mkdir_rename_and_delete()
    {
        var (afc, server) = Connect();
        server.AddFile("/Downloads/x/y.txt", [1]);
        await using var _ = afc;

        await afc.MakeDirectoryAsync("/Downloads/new", Ct);
        Assert.True(server.Exists("/Downloads/new"));

        await afc.RenamePathAsync("/Downloads/x", "/Downloads/z", Ct);
        Assert.True(server.Exists("/Downloads/z/y.txt"));

        var notEmpty = await Assert.ThrowsAsync<DeviceException>(() => afc.RemovePathAsync("/Downloads/z", Ct));
        Assert.Equal(DeviceErrorCodes.Protocol, notEmpty.Code);
        Assert.Contains("DirNotEmpty", notEmpty.Message);

        await afc.RemovePathAndContentsAsync("/Downloads/z", Ct);
        Assert.False(server.Exists("/Downloads/z/y.txt"));
    }

    [Theory]
    [InlineData(AfcStatus.ObjectNotFound, DeviceErrorCodes.PathNotFound)]
    [InlineData(AfcStatus.PermissionDenied, DeviceErrorCodes.PermissionDenied)]
    [InlineData(AfcStatus.ObjectExists, DeviceErrorCodes.AlreadyExists)]
    [InlineData(AfcStatus.OpNotSupported, DeviceErrorCodes.NotSupported)]
    [InlineData(AfcStatus.MuxError, DeviceErrorCodes.Disconnected)]
    [InlineData(AfcStatus.IoError, DeviceErrorCodes.Protocol)]
    public void Status_codes_map_to_device_error_codes(AfcStatus status, string code) =>
        Assert.Equal(code, AfcPacket.ErrorFor(status, AfcOperation.ReadDir, "/x").Code);

    [Fact]
    public async Task Forbidden_and_missing_paths_throw_mapped_errors()
    {
        var (afc, server) = Connect();
        server.AddDir("/PhotoData/Private");
        server.Forbidden.Add("/PhotoData/Private");
        await using var _ = afc;

        Assert.Equal(DeviceErrorCodes.PermissionDenied,
            (await Assert.ThrowsAsync<DeviceException>(() => afc.ReadDirectoryAsync("/PhotoData/Private", Ct))).Code);
        Assert.Equal(DeviceErrorCodes.PathNotFound,
            (await Assert.ThrowsAsync<DeviceException>(() => afc.ReadFileAsync("/missing", Stream.Null, null, Ct))).Code);
    }

    [Fact]
    public async Task A_cancelled_transfer_leaves_the_client_unusable_rather_than_out_of_step()
    {
        var (client, _) = DuplexStream.CreatePair(); // nobody answers
        await using var afc = new AfcClient(client);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => afc.ReadDirectoryAsync("/", cts.Token));
        var ex = await Assert.ThrowsAsync<DeviceException>(() => afc.ReadDirectoryAsync("/", Ct));
        Assert.Equal(DeviceErrorCodes.Disconnected, ex.Code);
    }

    private sealed class SyncProgress(List<long> into) : IProgress<long>
    {
        public void Report(long value) => into.Add(value);
    }
}
