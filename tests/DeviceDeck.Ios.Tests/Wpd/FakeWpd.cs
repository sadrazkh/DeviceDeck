using DeviceDeck.Ios.Wpd;

namespace DeviceDeck.Ios.Tests.Wpd;

/// <summary>An in-memory Windows Portable Devices: devices, a tree of objects per device, and deletes with injectable failures.</summary>
internal sealed class FakeWpd : IWpdBackend
{
    private readonly Lock _gate = new();

    public List<FakeWpdDevice> Devices { get; } = [];

    public int ReadOnlyOpens { get; private set; }
    public int ReadWriteOpens { get; private set; }

    /// <summary>An iPhone the way Windows lists it: Apple's VID, the UDID (no dash) as the USB serial, Internal Storage.</summary>
    public FakeWpdDevice AddIphone(string udid, string? usbSerial = "", string? wpdSerial = null)
    {
        var device = new FakeWpdDevice(
            $@"\\?\usb#vid_05ac&pid_12a8&mi_00#7&{Devices.Count:x}&0&0000#{{6ac27878-a6fa-4155-ba85-f98f491d4f33}}",
            usbSerial == "" ? udid.Replace("-", "", StringComparison.Ordinal) : usbSerial,
            new WpdDeviceProperties("Apple iPhone", "Apple Inc.", "iPhone", wpdSerial, "PTP: 1.00", "27.0"));
        Devices.Add(device);
        return device;
    }

    public IReadOnlyList<WpdDeviceInfo> ListDevices()
    {
        lock (_gate)
        {
            return Devices.Select(d => new WpdDeviceInfo(d.PnpId, "Apple iPhone", "Apple Inc.", "Apple iPhone", d.UsbSerial)).ToList();
        }
    }

    public IWpdConnection Open(string pnpDeviceId, bool readOnly)
    {
        lock (_gate)
        {
            var device = Devices.FirstOrDefault(d => d.PnpId == pnpDeviceId)
                         ?? throw new WpdException("No such device.", WpdErrors.DeviceNotConnected);
            if (readOnly)
            {
                ReadOnlyOpens++;
            }
            else
            {
                ReadWriteOpens++;
            }

            return new Connection(device, readOnly);
        }
    }

    private sealed class Connection(FakeWpdDevice device, bool readOnly) : IWpdConnection
    {
        public WpdDeviceProperties Device => device.Properties;

        public IReadOnlyList<string> ChildIds(string parentId, CancellationToken ct) => device.Children(parentId);

        public WpdObject? Get(string objectId) => device.Get(objectId);

        public IReadOnlyList<int> Delete(IReadOnlyList<string> objectIds) => device.Delete(objectIds, readOnly);

        public void Dispose()
        {
        }
    }
}

internal sealed class FakeWpdDevice(string pnpId, string? usbSerial, WpdDeviceProperties properties)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, WpdObject> _objects = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _children = new(StringComparer.Ordinal) { [WpdIds.DeviceObjectId] = [] };
    private int _next;

    public string PnpId { get; } = pnpId;
    public string? UsbSerial { get; } = usbSerial;
    public WpdDeviceProperties Properties { get; } = properties;

    /// <summary>Every Delete call, with the ids it got.</summary>
    public List<IReadOnlyList<string>> DeleteCalls { get; } = [];

    /// <summary>The HRESULT to answer for an object, by its file name; 0 (the default) deletes it.</summary>
    public Func<WpdObject, int> Answer { get; set; } = _ => 0;

    /// <summary>Called for each object deleted (the test removes the file from the fake phone's DCIM).</summary>
    public Action<WpdObject, string>? Deleted { get; set; }

    /// <summary>Deleting a Live Photo's still also takes its video, as Photos does for the whole asset.</summary>
    public bool StillTakesItsVideo { get; set; }

    /// <summary>A folder, storage or anything outside DCIM was asked to be deleted.</summary>
    public List<string> Forbidden { get; } = [];

    /// <summary>The DCIM path of each object Windows would show (folder/name), for the Deleted callback.</summary>
    private readonly Dictionary<string, string> _paths = new(StringComparer.Ordinal);

    public string AddStorage(string name = "Internal Storage") => Add(WpdIds.DeviceObjectId, name, WpdObjectKind.Functional, null, null);

    public string AddFolder(string parentId, string name) => Add(parentId, name, WpdObjectKind.Folder, null, null);

    public string AddFile(string folderId, string name, long size, bool? canDelete = true, string? dcimPath = null)
    {
        var id = Add(folderId, name, WpdObjectKind.File, size, canDelete);
        if (dcimPath is not null)
        {
            _paths[id] = dcimPath;
        }

        return id;
    }

    /// <summary>Internal Storage › DCIM › each folder with its files ("100APPLE/IMG_0001.HEIC" → size), as File Explorer shows them.</summary>
    public (string Storage, string Dcim) AddCameraRoll(IEnumerable<(string Folder, string Name, long Size)> files, bool? canDelete = true,
        Func<string, string>? wpdFolder = null, bool noDcimLevel = false)
    {
        var storage = AddStorage();
        var dcim = noDcimLevel ? storage : AddFolder(storage, "DCIM");
        var folders = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (folder, name, size) in files)
        {
            var shown = wpdFolder?.Invoke(folder) ?? folder;
            if (!folders.TryGetValue(shown, out var folderId))
            {
                folderId = folders[shown] = AddFolder(dcim, shown);
            }

            AddFile(folderId, name, size, canDelete, $"DCIM/{folder}/{name}");
        }

        return (storage, dcim);
    }

    public string IdOf(string name)
    {
        lock (_gate)
        {
            return _objects.Values.Single(o => o.FileName == name).Id;
        }
    }

    public bool Has(string name)
    {
        lock (_gate)
        {
            return _objects.Values.Any(o => o.FileName == name);
        }
    }

    public void Remove(string id)
    {
        lock (_gate)
        {
            if (_objects.Remove(id, out var o) && o.ParentId is { } parent)
            {
                _children[parent].Remove(id);
            }
        }
    }

    public IReadOnlyList<string> Children(string parentId)
    {
        lock (_gate)
        {
            return _children.TryGetValue(parentId, out var list) ? list.ToList() : [];
        }
    }

    public WpdObject? Get(string id)
    {
        lock (_gate)
        {
            return _objects.GetValueOrDefault(id);
        }
    }

    public IReadOnlyList<int> Delete(IReadOnlyList<string> ids, bool readOnly)
    {
        var answers = new List<int>();
        var gone = new List<(WpdObject Object, string Path)>();
        lock (_gate)
        {
            DeleteCalls.Add(ids.ToList());
            foreach (var id in ids)
            {
                if (!_objects.TryGetValue(id, out var o))
                {
                    answers.Add(WpdErrors.NotFound);
                    continue;
                }

                if (o.Kind != WpdObjectKind.File || !_paths.ContainsKey(id))
                {
                    Forbidden.Add(o.FileName ?? id);
                }

                if (readOnly)
                {
                    answers.Add(WpdErrors.AccessDenied);
                    continue;
                }

                var hr = Answer(o);
                answers.Add(hr);
                if (hr != 0)
                {
                    continue;
                }

                RemoveLocked(o, gone);
                if (StillTakesItsVideo && !o.FileName!.EndsWith(".MOV", StringComparison.OrdinalIgnoreCase))
                {
                    var video = Path.ChangeExtension(o.FileName, ".MOV");
                    if (_objects.Values.FirstOrDefault(x => x.ParentId == o.ParentId && x.FileName == video) is { } v)
                    {
                        RemoveLocked(v, gone);
                    }
                }
            }
        }

        foreach (var (o, path) in gone)
        {
            Deleted?.Invoke(o, path);
        }

        return answers;
    }

    private void RemoveLocked(WpdObject o, List<(WpdObject, string)> gone)
    {
        _objects.Remove(o.Id);
        _children[o.ParentId!].Remove(o.Id);
        gone.Add((o, _paths.GetValueOrDefault(o.Id) ?? ""));
    }

    private string Add(string parentId, string name, WpdObjectKind kind, long? size, bool? canDelete)
    {
        lock (_gate)
        {
            var id = $"o{++_next:X}";
            _objects[id] = new WpdObject(id, parentId, name, kind, size, canDelete, null, "pid-" + id);
            _children[id] = [];
            _children[parentId].Add(id);
            return id;
        }
    }
}
