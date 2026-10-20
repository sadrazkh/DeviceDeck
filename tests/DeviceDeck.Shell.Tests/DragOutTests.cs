using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using DeviceDeck.Bridge;
using DeviceDeck.Core.Transfers;
using DeviceDeck.Shell.Desktop.DragOut;
using DeviceDeck.Shell.Desktop.Interop;
using ComIDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace DeviceDeck.Shell.Tests;

/// <summary>Dragging photos and files into File Explorer: the virtual-file data object, its streams and the DragOut job.</summary>
public sealed class DragOutTests
{
    private static DragOutParams PhotosRequest(DragHost host) => new("photos", DragHost.PhoneId, Items: host.Photos
        .Select(p => new DragOutEntry(p.Id, p.FileName, p.Size, false, p.TakenAt)).ToArray());

    private static DragOutParams FilesRequest() => new("files", DragHost.PhoneId, DragHost.DocsRoot, Items:
    [
        new DragOutEntry("/Movie.mkv", "Movie.mkv", 300_000),
        new DragOutEntry("/Subtitles", "Subtitles", 0, IsDirectory: true),
    ]);

    private static FORMATETC Format(short format, TYMED tymed, int index = -1) =>
        new() { cfFormat = format, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = index, tymed = tymed };

    private static (string Name, uint Flags, uint Attributes, long Size)[] Descriptor(ComIDataObject data)
    {
        var format = Format(Ole.FileGroupDescriptorW, TYMED.TYMED_HGLOBAL);
        data.GetData(ref format, out var medium);
        try
        {
            var bytes = Ole.FromHGlobal(medium.unionmember);
            var count = BitConverter.ToInt32(bytes, 0);
            return Enumerable.Range(0, count).Select(i =>
            {
                var d = bytes.AsSpan(4 + 592 * i, 592);
                var name = Encoding.Unicode.GetString(d[72..]).TrimEnd('\0');
                var size = ((long)BitConverter.ToUInt32(d[64..]) << 32) | BitConverter.ToUInt32(d[68..]);
                return (name, BitConverter.ToUInt32(d), BitConverter.ToUInt32(d[36..]), size);
            }).ToArray();
        }
        finally
        {
            Ole.ReleaseStgMedium(ref medium);
        }
    }

    private static byte[] ReadContents(ComIDataObject data, int index)
    {
        var format = Format(Ole.FileContents, TYMED.TYMED_ISTREAM, index);
        data.GetData(ref format, out var medium);
        var stream = (IStream)Marshal.GetObjectForIUnknown(medium.unionmember);
        Marshal.Release(medium.unionmember);
        var result = new MemoryStream();
        var buffer = new byte[64 * 1024];
        var read = Marshal.AllocHGlobal(4);
        try
        {
            while (true)
            {
                stream.Read(buffer, buffer.Length, read);
                var n = Marshal.ReadInt32(read);
                if (n == 0)
                {
                    return result.ToArray();
                }

                result.Write(buffer, 0, n);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(read);
        }
    }

    [Fact]
    public async Task Photos_go_through_the_queue_while_Explorer_reads_them()
    {
        await using var host = await DragHost.StartAsync();
        var (data, session) = await Task.Run(() => host.Service.Create(PhotosRequest(host)));

        var files = Descriptor(data);
        Assert.Equal(["IMG_0100.HEIC", "IMG_0101.HEIC", "IMG_0100 (2).HEIC"], files.Select(f => f.Name));
        Assert.All(files, f => Assert.NotEqual(0u, f.Flags & 0x40)); // FD_FILESIZE
        Assert.Equal(host.Photos.Select(p => p.Size), files.Select(f => f.Size));
        Assert.Null(session!.JobId); // nothing copies before the drop

        for (var i = 0; i < files.Length; i++)
        {
            Assert.Equal(await host.OriginalAsync(host.Photos[i].Id), await Task.Run(() => ReadContents(data, i)));
        }

        var job = host.Queue.Get(session.JobId!)!;
        Assert.Equal(TransferKind.DragOut, job.Kind);
        Assert.Equal("Copying 3 photos to File Explorer", job.Title);
        await Wait.UntilAsync(() => host.Queue.Get(session.JobId!)!.State == TransferState.Done, "the DragOut job to finish");

        data.StartOperation(null);
        data.EndOperation(Ole.S_OK, null, Ole.DROPEFFECT_COPY);
        session.DragEnded(dropped: true);
        await Wait.UntilAsync(() => host.Registry.Find(session.Id) is null && !Directory.Exists(session.StagingFolder), "the staging folder to go");
    }

    [Fact]
    public async Task Dragged_phone_folders_are_walked_and_listed_before_their_contents()
    {
        await using var host = await DragHost.StartAsync();
        var (data, session) = await Task.Run(() => host.Service.Create(FilesRequest()));

        var files = Descriptor(data);
        Assert.Equal(["Movie.mkv", "Subtitles", @"Subtitles\Movie.srt", @"Subtitles\Old", @"Subtitles\Old\Movie.old.srt"], files.Select(f => f.Name));
        Assert.Equal(0x10u, files[1].Attributes); // FILE_ATTRIBUTE_DIRECTORY
        Assert.Equal(0x80u, files[0].Attributes);

        Assert.Equal(await host.PhoneFileAsync("/Subtitles/Old/Movie.old.srt"), await Task.Run(() => ReadContents(data, 4)));
        Assert.Equal(await host.PhoneFileAsync("/Movie.mkv"), await Task.Run(() => ReadContents(data, 0)));
        Assert.Equal("Copying 3 files to File Explorer", host.Queue.Get(session!.JobId!)!.Title);

        var folder = Format(Ole.FileContents, TYMED.TYMED_ISTREAM, 1);
        var error = Assert.Throws<COMException>(() => data.GetData(ref folder, out _));
        Assert.Equal(Ole.DV_E_LINDEX, error.HResult);
    }

    [Fact]
    public async Task A_read_waits_for_the_phone_and_fails_once_the_copy_is_cancelled()
    {
        await using var host = await DragHost.StartAsync(bytesPerSecond: 200_000);
        var (data, session) = await Task.Run(() => host.Service.Create(PhotosRequest(host)));
        _ = Descriptor(data);

        var format = Format(Ole.FileContents, TYMED.TYMED_ISTREAM, 0);
        data.GetData(ref format, out var medium);
        var stream = (IStream)Marshal.GetObjectForIUnknown(medium.unionmember);
        Marshal.Release(medium.unionmember);

        var buffer = new byte[16 * 1024];
        var read = Marshal.AllocHGlobal(4);
        try
        {
            await Task.Run(() => stream.Read(buffer, buffer.Length, read));
            Assert.Equal(buffer.Length, Marshal.ReadInt32(read)); // a full read, waiting for bytes as they come

            host.Queue.Cancel(session!.JobId!);
            await Wait.UntilAsync(() => host.Queue.Get(session.JobId!)!.State == TransferState.Cancelled, "the job to cancel");
            var error = await Assert.ThrowsAsync<COMException>(() => Task.Run(() =>
            {
                for (var i = 0; i < 100; i++)
                {
                    stream.Read(buffer, buffer.Length, read);
                }
            }));
            Assert.Equal(Ole.STG_E_READFAULT, error.HResult);
        }
        finally
        {
            Marshal.FreeHGlobal(read);
        }

        data.EndOperation(Ole.E_ABORT, null, Ole.DROPEFFECT_NONE);
        session.DragEnded(dropped: true);
        await Wait.UntilAsync(() => host.Registry.Find(session.Id) is null, "the session to close");
    }

    [Fact]
    public async Task Explorer_giving_up_cancels_the_job()
    {
        await using var host = await DragHost.StartAsync(bytesPerSecond: 100_000);
        var (data, session) = await Task.Run(() => host.Service.Create(PhotosRequest(host)));
        _ = Descriptor(data);
        var format = Format(Ole.FileContents, TYMED.TYMED_ISTREAM, 0);
        data.GetData(ref format, out var medium);
        Marshal.Release(medium.unionmember);

        data.StartOperation(null);
        data.EndOperation(Ole.E_ABORT, null, Ole.DROPEFFECT_NONE);
        await Wait.UntilAsync(() => host.Queue.Get(session!.JobId!)!.State == TransferState.Cancelled, "the job to be cancelled");
    }

    [Fact]
    public async Task A_cancelled_drag_starts_nothing_and_leaves_nothing()
    {
        await using var host = await DragHost.StartAsync();
        var (data, session) = await Task.Run(() => host.Service.Create(PhotosRequest(host)));
        _ = Descriptor(data);
        session!.DragEnded(dropped: false);

        Assert.Null(host.Registry.Find(session.Id));
        Assert.Empty(host.Queue.List());
    }

    [Fact]
    public async Task The_data_object_is_not_agile_so_File_Explorer_never_calls_the_UI_thread()
    {
        await using var host = await DragHost.StartAsync();
        var (data, _) = await Task.Run(() => host.Service.Create(PhotosRequest(host)));
        var unknown = Marshal.GetIUnknownForObject(data);
        try
        {
            var marshal = Ole.IID_IMarshal;
            Assert.NotEqual(0, Marshal.QueryInterface(unknown, in marshal, out _));
            var agile = Ole.IID_IAgileObject;
            Assert.NotEqual(0, Marshal.QueryInterface(unknown, in agile, out _));
            var dataObject = Ole.IID_IDataObject;
            Assert.Equal(0, Marshal.QueryInterface(unknown, in dataObject, out var pointer));
            Marshal.Release(pointer);
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    [Fact]
    public void Pc_files_travel_as_a_plain_file_drop()
    {
        var bytes = VirtualFileDataObject.HDrop([@"C:\a\one.txt", @"D:\two.jpg"]);
        Assert.Equal(20, BitConverter.ToInt32(bytes, 0));
        Assert.Equal(1, BitConverter.ToInt32(bytes, 16));
        Assert.Equal("C:\\a\\one.txt\0D:\\two.jpg\0\0", Encoding.Unicode.GetString(bytes, 20, bytes.Length - 20));

        var data = new VirtualFileDataObject([@"C:\a\one.txt"]);
        var hdrop = Format(Ole.CF_HDROP, TYMED.TYMED_HGLOBAL);
        Assert.Equal(Ole.S_OK, data.QueryGetData(ref hdrop));
        var contents = Format(Ole.FileContents, TYMED.TYMED_ISTREAM);
        Assert.Equal(Ole.DV_E_FORMATETC, data.QueryGetData(ref contents));
        data.GetAsyncMode(out var async);
        Assert.False(async);
    }

    [Fact]
    public void Formats_set_by_the_drag_image_helper_come_back()
    {
        var data = new VirtualFileDataObject([@"C:\a\one.txt"]);
        var custom = Format(Ole.PerformedDropEffect, TYMED.TYMED_HGLOBAL);
        var medium = new STGMEDIUM { tymed = TYMED.TYMED_HGLOBAL, unionmember = Ole.ToHGlobal(BitConverter.GetBytes(5u)) };
        data.SetData(ref custom, ref medium, true);

        data.GetData(ref custom, out var back);
        Assert.Equal(5u, BitConverter.ToUInt32(Ole.FromHGlobal(back.unionmember)));
        Ole.ReleaseStgMedium(ref back);
    }

    /// <summary>
    /// The real thing without a mouse: File Explorer's own drop target for a folder (shell32, in this process) takes
    /// the drop of a marshaled proxy, as it would from DoDragDrop, and writes the photos into the folder.
    /// </summary>
    [LocalOnlyFact]
    public async Task File_Explorer_drop_target_writes_the_dragged_photos()
    {
        await using var host = await DragHost.StartAsync();
        var folder = Path.Combine(host.Root, "Dropped here");
        Directory.CreateDirectory(folder);
        var (data, session) = await Task.Run(() => host.Service.Create(PhotosRequest(host)));
        var marshaled = await Task.Run(() =>
        {
            var unknown = Marshal.GetComInterfaceForObject(data, typeof(ComIDataObject));
            try
            {
                Marshal.ThrowExceptionForHR(Ole.CoMarshalInterThreadInterfaceInStream(Ole.IID_IDataObject, unknown, out var stream));
                return stream;
            }
            finally
            {
                Marshal.Release(unknown);
            }
        });

        var expected = new List<(string Path, byte[] Bytes)>();
        foreach (var (asset, name) in host.Photos.Zip(new[] { "IMG_0100.HEIC", "IMG_0101.HEIC", "IMG_0100 (2).HEIC" }))
        {
            expected.Add((Path.Combine(folder, name), await host.OriginalAsync(asset.Id)));
        }

        var effect = Sta.Run(() =>
        {
            Marshal.ThrowExceptionForHR(Ole.CoGetInterfaceAndReleaseStream(marshaled, Ole.IID_IDataObject, out var proxy));
            Marshal.ThrowExceptionForHR(Ole.SHParseDisplayName(folder, 0, out var pidl, 0, out _));
            try
            {
                Marshal.ThrowExceptionForHR(Ole.SHBindToParent(pidl, Ole.IID_IShellFolder, out var parent, out var last));
                ((IShellFolder)parent).GetUIObjectOf(0, 1, [last], Ole.IID_IDropTarget, 0, out var target);
                var drop = (IDropTarget)target;
                var effect = Ole.DROPEFFECT_COPY;
                drop.DragEnter(proxy, Ole.MK_LBUTTON, default, ref effect);
                drop.DragOver(Ole.MK_LBUTTON, default, ref effect);
                effect = Ole.DROPEFFECT_COPY;
                drop.Drop(proxy, 0, default, ref effect);
                Sta.PumpUntil(() => expected.All(e => File.Exists(e.Path) && new FileInfo(e.Path).Length == e.Bytes.Length) && host.Registry.Find(session!.Id) is null, 30_000);
                return effect;
            }
            finally
            {
                Ole.CoTaskMemFree(pidl);
                Marshal.Release(proxy);
            }
        });

        Assert.Equal(Ole.DROPEFFECT_COPY, effect);
        foreach (var (path, bytes) in expected)
        {
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        }

        Assert.Equal(TransferState.Done, host.Queue.Get(session!.JobId!)!.State);
        Assert.Equal(expected.Count, Directory.GetFiles(folder).Length);
    }
}

/// <summary>Needs a desktop session (shell32's drop target); skipped on CI runners.</summary>
public sealed class LocalOnlyFactAttribute : FactAttribute
{
    public LocalOnlyFactAttribute()
    {
        if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase))
        {
            Skip = "Needs an interactive desktop (File Explorer's drop target); runs locally.";
        }
    }
}
