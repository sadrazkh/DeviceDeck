using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Text.Json;
using DeviceDeck.Shell.Desktop.DropIn;
using DeviceDeck.Shell.Desktop.Interop;
using ComIDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace DeviceDeck.Shell.Tests;

/// <summary>Files dropped from File Explorer: the window's OLE drop target reads them, the forwarder hands them to the page.</summary>
public sealed class DropInTests
{
    /// <summary>What the page was sent.</summary>
    private sealed class Page
    {
        public List<(JsonElement Message, IReadOnlyList<DroppedItem>? Attached)> Sent { get; } = [];

        public void Post(string json, IReadOnlyList<DroppedItem>? attached) => Sent.Add((JsonDocument.Parse(json).RootElement.Clone(), attached));

        public string[] Phases => Sent.Select(s => s.Message.GetProperty("phase").GetString()!).ToArray();
    }

    private static JsonElement Answer(bool accept) =>
        JsonDocument.Parse($$"""{"$dd":"nativeDragFeedback","accept":{{(accept ? "true" : "false")}}}""").RootElement;

    [Fact]
    public void The_page_hears_where_the_drag_is_and_gets_the_files_on_the_drop()
    {
        var page = new Page();
        var forwarder = new DropInForwarder(page.Post);
        DroppedItem[] items = [new(@"E:\Films\Persepolis.mkv", false), new(@"E:\Films\Subtitles", true), new(@"E:\Films\درباره الی.mkv", false)];

        forwarder.Enter(100.04, 200, items);
        forwarder.Over(110, 210);
        var taken = forwarder.Drop(120, 220);

        Assert.True(taken);
        Assert.Equal(["enter", "over", "drop"], page.Phases);
        var enter = page.Sent[0].Message;
        Assert.Equal("nativeDrag", enter.GetProperty("$dd").GetString());
        Assert.Equal(100, enter.GetProperty("x").GetDouble());
        var files = enter.GetProperty("files").EnumerateArray().ToArray();
        Assert.Equal(["Persepolis.mkv", "Subtitles", "درباره الی.mkv"], files.Select(f => f.GetProperty("name").GetString()));
        // Files get handles 0, 1 among the drop's additional objects; a folder goes by its path.
        Assert.Equal([0, -1, 1], files.Select(f => f.GetProperty("handle").GetInt32()));
        Assert.True(files[1].GetProperty("directory").GetBoolean());
        Assert.Null(page.Sent[0].Attached);
        Assert.False(page.Sent[1].Message.TryGetProperty("files", out _));
        Assert.Same(items, page.Sent[2].Attached);
        Assert.False(forwarder.Active);
    }

    [Fact]
    public void Each_drop_says_which_route_it_came_by()
    {
        var page = new Page();
        var log = new List<string>();
        var forwarder = new DropInForwarder(page.Post, log.Add);
        forwarder.Enter(1, 1, [new(@"E:\a.txt", false), new(@"E:\b.txt", false)], "XAML");
        forwarder.Drop(1, 1);
        forwarder.Enter(1, 1, [new(@"E:\a.txt", false)]);
        forwarder.OnFeedback(Answer(false));
        forwarder.Drop(1, 1);
        Assert.Equal(
            ["Drop from File Explorer (XAML): 2 item(s), taken by the page.", "Drop from File Explorer (OLE): 1 item(s), refused where it landed."],
            log);
    }

    [Fact]
    public void The_effect_follows_the_page_answer()
    {
        var page = new Page();
        var forwarder = new DropInForwarder(page.Post);
        forwarder.Enter(10, 10, [new(@"E:\a.txt", false)]);
        Assert.True(forwarder.Accepts);

        forwarder.OnFeedback(Answer(false));
        Assert.False(forwarder.Accepts);
        forwarder.OnFeedback(Answer(true));
        Assert.True(forwarder.Accepts);

        forwarder.OnFeedback(Answer(false));
        Assert.False(forwarder.Drop(10, 10));
        // The page still hears of the drop (it refuses it and says why).
        Assert.Equal("drop", page.Phases[^1]);
    }

    [Fact]
    public void Nothing_reaches_the_page_without_files()
    {
        var page = new Page();
        var forwarder = new DropInForwarder(page.Post);
        forwarder.Over(1, 1);
        forwarder.Leave();
        forwarder.Enter(1, 1, []);
        Assert.False(forwarder.Drop(1, 1));
        Assert.Empty(page.Sent);
        Assert.False(forwarder.Accepts);
    }

    [Fact]
    public void A_leave_ends_the_drag()
    {
        var page = new Page();
        var forwarder = new DropInForwarder(page.Post);
        forwarder.Enter(1, 1, [new(@"E:\a.txt", false)]);
        forwarder.Leave();
        forwarder.Over(2, 2);
        Assert.Equal(["enter", "leave"], page.Phases);
    }

    [Fact]
    public void The_OLE_target_reads_CF_HDROP_and_answers_with_the_page_effect()
    {
        var folder = Directory.CreateTempSubdirectory("devicedeck-dropin-");
        try
        {
            var file = Path.Combine(folder.FullName, "فایل آزمایشی.txt");
            File.WriteAllText(file, "x");
            var sub = Directory.CreateDirectory(Path.Combine(folder.FullName, "Sub folder")).FullName;

            var page = new Page();
            var forwarder = new DropInForwarder(page.Post);
            var target = new OleDropTarget(forwarder, 0, () => 1.25);
            var data = Marshal.GetComInterfaceForObject(new HDropData([file, sub]), typeof(ComIDataObject));
            try
            {
                var effect = Ole.DROPEFFECT_COPY | 2 | 4;
                target.DragEnter(data, Ole.MK_LBUTTON, new Point { X = 125, Y = 250 }, ref effect);
                Assert.Equal(Ole.DROPEFFECT_COPY, effect);
                var enter = page.Sent[0].Message;
                Assert.Equal(100, enter.GetProperty("x").GetDouble());
                Assert.Equal(200, enter.GetProperty("y").GetDouble());
                Assert.Equal([file, sub], enter.GetProperty("files").EnumerateArray().Select(f => f.GetProperty("path").GetString()));
                Assert.Equal([false, true], enter.GetProperty("files").EnumerateArray().Select(f => f.GetProperty("directory").GetBoolean()));

                // The page says the element under the pointer takes no files: no-drop.
                forwarder.OnFeedback(Answer(false));
                effect = Ole.DROPEFFECT_COPY;
                target.DragOver(Ole.MK_LBUTTON, new Point { X = 130, Y = 250 }, ref effect);
                Assert.Equal(Ole.DROPEFFECT_NONE, effect);

                forwarder.OnFeedback(Answer(true));
                effect = Ole.DROPEFFECT_COPY | 2;
                target.Drop(data, 0, new Point { X = 130, Y = 250 }, ref effect);
                Assert.Equal(Ole.DROPEFFECT_COPY, effect);
                Assert.Equal(["enter", "over", "drop"], page.Phases);
                Assert.Equal(2, page.Sent[2].Attached!.Count);
            }
            finally
            {
                Marshal.Release(data);
            }
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public void The_OLE_target_refuses_data_without_files()
    {
        var page = new Page();
        var forwarder = new DropInForwarder(page.Post);
        var target = new OleDropTarget(forwarder, 0, () => 1);
        var data = Marshal.GetComInterfaceForObject(new HDropData(null), typeof(ComIDataObject));
        try
        {
            var effect = Ole.DROPEFFECT_COPY;
            target.DragEnter(data, Ole.MK_LBUTTON, default, ref effect);
            Assert.Equal(Ole.DROPEFFECT_NONE, effect);
            effect = Ole.DROPEFFECT_COPY;
            target.DragOver(Ole.MK_LBUTTON, default, ref effect);
            Assert.Equal(Ole.DROPEFFECT_NONE, effect);
            target.Drop(data, 0, default, ref effect);
            Assert.Equal(Ole.DROPEFFECT_NONE, effect);
            Assert.Empty(page.Sent);
        }
        finally
        {
            Marshal.Release(data);
        }
    }

    /// <summary>File Explorer's own data object for a selection (its folder's IShellFolder.GetUIObjectOf), as a real drag carries.</summary>
    [LocalOnlyFact]
    public void The_OLE_target_reads_File_Explorers_own_data_object()
    {
        var folder = Directory.CreateTempSubdirectory("devicedeck-dropin-");
        try
        {
            string[] paths = [Path.Combine(folder.FullName, "one.txt"), Path.Combine(folder.FullName, "دو.txt")];
            foreach (var path in paths)
            {
                File.WriteAllText(path, "x");
            }

            var read = Sta.Run(() =>
            {
                var pidls = new List<nint>();
                try
                {
                    object? parent = null;
                    var children = new nint[paths.Length];
                    for (var i = 0; i < paths.Length; i++)
                    {
                        Marshal.ThrowExceptionForHR(Ole.SHParseDisplayName(paths[i], 0, out var pidl, 0, out _));
                        pidls.Add(pidl);
                        Marshal.ThrowExceptionForHR(Ole.SHBindToParent(pidl, Ole.IID_IShellFolder, out var folderObject, out children[i]));
                        parent ??= folderObject;
                    }

                    ((IShellFolder)parent!).GetUIObjectOf(0, (uint)children.Length, children, Ole.IID_IDataObject, 0, out var data);
                    var pointer = Marshal.GetComInterfaceForObject(data, typeof(ComIDataObject));
                    try
                    {
                        return OleDropTarget.ReadFiles(pointer);
                    }
                    finally
                    {
                        Marshal.Release(pointer);
                    }
                }
                finally
                {
                    pidls.ForEach(Ole.CoTaskMemFree);
                }
            });

            Assert.Equal(paths.Order(), read.Select(i => i.Path).Order());
            Assert.All(read, i => Assert.False(i.IsDirectory));
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    /// <summary>A data object with CF_HDROP for <c>paths</c>, or with nothing at all (null).</summary>
    [ComVisible(true)]
    private sealed class HDropData(string[]? paths) : ComIDataObject
    {
        public int QueryGetData(ref FORMATETC format) =>
            paths is not null && format.cfFormat == Ole.CF_HDROP && (format.tymed & TYMED.TYMED_HGLOBAL) != 0 ? Ole.S_OK : Ole.DV_E_FORMATETC;

        public void GetData(ref FORMATETC format, out STGMEDIUM medium)
        {
            if (QueryGetData(ref format) != Ole.S_OK)
            {
                throw new COMException("No such format.", Ole.DV_E_FORMATETC);
            }

            // DROPFILES { pFiles = 20, pt, fNC, fWide = 1 } then the wide paths, each NUL-terminated, then a NUL.
            var header = new byte[20];
            BitConverter.GetBytes(20).CopyTo(header, 0);
            BitConverter.GetBytes(1).CopyTo(header, 16);
            var names = Encoding.Unicode.GetBytes(string.Concat(paths!.Select(p => p + "\0")) + "\0");
            medium = new STGMEDIUM { tymed = TYMED.TYMED_HGLOBAL, unionmember = Ole.ToHGlobal([.. header, .. names]) };
        }

        public void GetDataHere(ref FORMATETC format, ref STGMEDIUM medium) => throw new NotImplementedException();

        public int GetCanonicalFormatEtc(ref FORMATETC formatIn, out FORMATETC formatOut)
        {
            formatOut = default;
            return Ole.E_NOTIMPL;
        }

        public void SetData(ref FORMATETC formatIn, ref STGMEDIUM medium, bool release) => throw new NotImplementedException();

        public IEnumFORMATETC EnumFormatEtc(DATADIR direction) => throw new NotImplementedException();

        public int DAdvise(ref FORMATETC format, ADVF advf, IAdviseSink sink, out int connection)
        {
            connection = 0;
            return Ole.OLE_E_ADVISENOTSUPPORTED;
        }

        public void DUnadvise(int connection) => throw new NotImplementedException();

        public int EnumDAdvise(out IEnumSTATDATA? advise)
        {
            advise = null;
            return Ole.OLE_E_ADVISENOTSUPPORTED;
        }
    }
}
