using DeviceDeck.Ios.Wpd;

namespace DeviceDeck.Ios.Tests.Wpd;

/// <summary>An in-memory Windows Portable Devices: devices, a tree of objects per device, and deletes with injectable failures.</summary>
internal sealed class FakeWpd : IWpdBackend
{
    private readonly Lock _gate = new();

    public List<FakeWpdDevice> Devices { get; } = [];

    public int ReadOnlyOpens { get; private set; }
    public int ReadWriteOpens { get; private set; }

    /// <summary>What each call costs, on a simulated clock (nothing sleeps); all zero unless a test sets them.</summary>
    public WpdLatency Latency { get; } = new();

    /// <summary>The driver reads many objects in one request (IPortableDevicePropertiesBulk); false: GetMany answers null.</summary>
    public bool BulkReads { get; set; } = true;

    /// <summary>Objects a bulk answer leaves out (by id), as a driver might; the reader then asks for them one by one.</summary>
    public Func<string, bool>? BulkLeavesOut { get; set; }

    /// <summary>A bulk read fails with this HRESULT (a driver that breaks or stalls on it).</summary>
    public int? BulkFails { get; set; }

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
        Latency.Charge(Latency.ListDevices);
        lock (_gate)
        {
            return Devices.Select(d => new WpdDeviceInfo(d.PnpId, "Apple iPhone", "Apple Inc.", "Apple iPhone", d.UsbSerial)).ToList();
        }
    }

    public IWpdConnection Open(string pnpDeviceId, bool readOnly)
    {
        Latency.Charge(Latency.Open);
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

            return new Connection(this, device, readOnly, Latency);
        }
    }

    private sealed class Connection(FakeWpd owner, FakeWpdDevice device, bool readOnly, WpdLatency latency) : IWpdConnection
    {
        public IReadOnlyList<WpdObject>? GetMany(IReadOnlyList<string> objectIds, Action<int>? progress, CancellationToken ct)
        {
            if (!owner.BulkReads)
            {
                return null;
            }

            if (owner.BulkFails is { } hr)
            {
                latency.Bulk(objectIds.Count);
                throw new WpdException("Windows stopped answering a bulk read of the iPhone's objects.", hr);
            }

            ct.ThrowIfCancellationRequested();
            latency.Bulk(objectIds.Count);
            var found = objectIds.Where(id => owner.BulkLeavesOut?.Invoke(id) != true).Select(device.Peek).Where(o => o is not null).Select(o => o!).ToList();
            progress?.Invoke(found.Count);
            return found;
        }

        public WpdDeviceProperties Device => device.Properties;

        public IReadOnlyList<string> ChildIds(string parentId, CancellationToken ct)
        {
            var ids = device.Children(parentId);
            latency.ChildIds(ids.Count);
            return ids;
        }

        public WpdObject? Get(string objectId)
        {
            latency.Get();
            return device.Get(objectId);
        }

        public IReadOnlyList<int> Delete(IReadOnlyList<string> objectIds) => device.Delete(objectIds, readOnly);

        public IReadOnlyList<(Guid Category, uint Id)> SupportedCommands() => device.Commands;

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

    /// <summary>Reading a deleted object back still answers with its old properties, as Windows' driver did on iOS 27.</summary>
    public bool StaleAfterDelete { get; set; }

    private readonly Dictionary<string, WpdObject> _removed = new(StringComparer.Ordinal);

    /// <summary>What reading an object Windows no longer has throws (some drivers answer with an error, not "no such object").</summary>
    public int? GoneObjectError { get; set; }

    /// <summary>A folder, storage or anything outside DCIM was asked to be deleted.</summary>
    public List<string> Forbidden { get; } = [];

    /// <summary>The DCIM path of each object Windows would show (folder/name), for the Deleted callback.</summary>
    private readonly Dictionary<string, string> _paths = new(StringComparer.Ordinal);

    public string AddStorage(string name = "Internal Storage") => Add(WpdIds.DeviceObjectId, name, WpdObjectKind.Functional, null, null);

    public string AddFolder(string parentId, string name) => Add(parentId, name, WpdObjectKind.Folder, null, null);

    /// <summary>What the device says it supports (DELETE_OBJECTS on a phone that doesn't mark its files).</summary>
    public List<(Guid Category, uint Id)> Commands { get; } = [];

    public string AddFile(string folderId, string name, long size, bool? canDelete = true, string? dcimPath = null, DateTimeOffset? created = null)
    {
        var id = Add(folderId, name, WpdObjectKind.File, size, canDelete, created);
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

    /// <summary>An object as it is now, without the stale or error behaviours of <see cref="Get"/> (bulk reads).</summary>
    public WpdObject? Peek(string id)
    {
        lock (_gate)
        {
            return _objects.GetValueOrDefault(id);
        }
    }

    public WpdObject? Get(string id)
    {
        lock (_gate)
        {
            if (!_objects.TryGetValue(id, out var o) && StaleAfterDelete && _removed.TryGetValue(id, out var stale))
            {
                return stale;
            }

            if (o is null && GoneObjectError is { } hr)
            {
                throw new WpdException("No such object.", hr);
            }

            return o;
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
        _removed[o.Id] = o;
        _children[o.ParentId!].Remove(o.Id);
        gone.Add((o, _paths.GetValueOrDefault(o.Id) ?? ""));
    }

    private string Add(string parentId, string name, WpdObjectKind kind, long? size, bool? canDelete, DateTimeOffset? created = null)
    {
        lock (_gate)
        {
            var id = $"o{++_next:X}";
            _objects[id] = new WpdObject(id, parentId, name, kind, size, canDelete, created, "pid-" + id);
            _children[id] = [];
            _children[parentId].Add(id);
            return id;
        }
    }
}

/// <summary>
/// How slow Windows Portable Devices is with a real iPhone, on a simulated clock: each call adds its cost to
/// <see cref="Elapsed"/> and nothing sleeps. The defaults are zero; <see cref="LikeTheOwnersIphone"/> sets what his
/// v0.4.0 log showed (about 160 s to read 3,258 objects one by one: ~50 ms per object).
/// </summary>
internal sealed class WpdLatency
{
    private long _ticks;
    private int _gets;
    private int _childCalls;
    private int _bulkCalls;
    private int _bulkObjects;

    public TimeSpan ListDevices { get; set; }
    public TimeSpan Open { get; set; }

    /// <summary>One IPortableDeviceProperties::GetValues (one object).</summary>
    public TimeSpan PerGet { get; set; }

    /// <summary>One EnumObjects of a folder, plus a little per id it returns.</summary>
    public TimeSpan PerChildIdsCall { get; set; }
    public TimeSpan PerChildId { get; set; }

    /// <summary>
    /// One bulk request (IPortableDevicePropertiesBulk): a fixed cost per request plus a cost per object in it.
    /// Unknown on a real iPhone: the probe's `wpd-timing` measures it.
    /// </summary>
    public TimeSpan PerBulkCall { get; set; }
    public TimeSpan PerBulkObject { get; set; }

    public TimeSpan Elapsed => TimeSpan.FromTicks(Interlocked.Read(ref _ticks));
    public int Gets => Volatile.Read(ref _gets);
    public int ChildIdsCalls => Volatile.Read(ref _childCalls);
    public int BulkCalls => Volatile.Read(ref _bulkCalls);
    public int BulkObjects => Volatile.Read(ref _bulkObjects);

    /// <summary>The owner's iPhone 16 on iOS 27 as his log showed it; bulk at <paramref name="perBulkObject"/> per object (an assumption).</summary>
    public WpdLatency LikeTheOwnersIphone(TimeSpan? perBulkObject = null)
    {
        ListDevices = TimeSpan.FromMilliseconds(30);
        Open = TimeSpan.FromMilliseconds(150);
        PerGet = TimeSpan.FromMilliseconds(50);
        PerChildIdsCall = TimeSpan.FromMilliseconds(50);
        PerChildId = TimeSpan.FromMilliseconds(0.05);
        PerBulkCall = TimeSpan.FromMilliseconds(50);
        PerBulkObject = perBulkObject ?? TimeSpan.FromMilliseconds(2);
        return this;
    }

    public void Charge(TimeSpan cost) => Interlocked.Add(ref _ticks, cost.Ticks);

    public void Get()
    {
        Interlocked.Increment(ref _gets);
        Charge(PerGet);
    }

    public void ChildIds(int count)
    {
        Interlocked.Increment(ref _childCalls);
        Charge(PerChildIdsCall + PerChildId * count);
    }

    public void Bulk(int objects)
    {
        Interlocked.Increment(ref _bulkCalls);
        Interlocked.Add(ref _bulkObjects, objects);
        Charge(PerBulkCall + PerBulkObject * objects);
    }

    public void Reset()
    {
        Interlocked.Exchange(ref _ticks, 0);
        Interlocked.Exchange(ref _gets, 0);
        Interlocked.Exchange(ref _childCalls, 0);
        Interlocked.Exchange(ref _bulkCalls, 0);
        Interlocked.Exchange(ref _bulkObjects, 0);
    }
}
