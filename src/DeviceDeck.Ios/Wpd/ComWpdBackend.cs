using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DeviceDeck.Ios.Wpd;

/// <summary>The real Windows Portable Devices API (PortableDeviceApi.dll), through COM.</summary>
[SupportedOSPlatform("windows")]
public sealed class ComWpdBackend : IWpdBackend
{
    public IReadOnlyList<WpdDeviceInfo> ListDevices() => Guard(() =>
    {
        var manager = Create<IPortableDeviceManager>(WpdClsids.PortableDeviceManager);
        try
        {
            manager.RefreshDeviceList();
            uint count = 0;
            Check(manager.GetDevices(null, ref count), "list portable devices");
            if (count == 0)
            {
                return [];
            }

            var ids = new IntPtr[count];
            Check(manager.GetDevices(ids, ref count), "list portable devices");
            var devices = new List<WpdDeviceInfo>();
            foreach (var pointer in ids.Take((int)count))
            {
                var id = Marshal.PtrToStringUni(pointer);
                Marshal.FreeCoTaskMem(pointer);
                if (string.IsNullOrEmpty(id))
                {
                    continue;
                }

                devices.Add(new WpdDeviceInfo(
                    id,
                    ManagerString(manager.GetDeviceFriendlyName, id),
                    ManagerString(manager.GetDeviceManufacturer, id),
                    ManagerString(manager.GetDeviceDescription, id),
                    UsbSerialOf(id)));
            }

            return devices;
        }
        finally
        {
            Marshal.ReleaseComObject(manager);
        }
    }, "list portable devices");

    public IWpdConnection Open(string pnpDeviceId, bool readOnly) => Guard<IWpdConnection>(() =>
    {
        var device = Create<IPortableDevice>(WpdClsids.PortableDeviceFtm);
        var client = Create<IPortableDeviceValues>(WpdClsids.PortableDeviceValues);
        try
        {
            var key = WpdKeys.ClientName;
            client.SetStringValue(ref key, "DeviceDeck");
            key = WpdKeys.ClientMajorVersion;
            client.SetUnsignedIntegerValue(ref key, 0);
            key = WpdKeys.ClientMinorVersion;
            client.SetUnsignedIntegerValue(ref key, 4);
            key = WpdKeys.ClientRevision;
            client.SetUnsignedIntegerValue(ref key, 0);
            key = WpdKeys.ClientSecurityQualityOfService;
            client.SetUnsignedIntegerValue(ref key, WpdKeys.SecurityImpersonation);
            if (readOnly)
            {
                key = WpdKeys.ClientDesiredAccess;
                client.SetUnsignedIntegerValue(ref key, WpdKeys.GenericRead);
            }

            device.Open(pnpDeviceId, client);
            return new Connection(device);
        }
        catch
        {
            Marshal.ReleaseComObject(device);
            throw;
        }
        finally
        {
            Marshal.ReleaseComObject(client);
        }
    }, "open the iPhone's camera connection");

    private sealed class Connection : IWpdConnection
    {
        private readonly IPortableDevice _device;
        private readonly IPortableDeviceContent _content;
        private readonly IPortableDeviceProperties _properties;
        private readonly IPortableDeviceKeyCollection _objectKeys;
        private IPortableDeviceKeyCollection? _bulkKeys;
        private bool _bulkUnsupported;
        private bool _disposed;

        public Connection(IPortableDevice device)
        {
            _device = device;
            _device.Content(out _content);
            _content.Properties(out _properties);
            _objectKeys = Keys(WpdKeys.ObjectParentId, WpdKeys.ObjectName, WpdKeys.ObjectOriginalFileName, WpdKeys.ObjectContentType,
                WpdKeys.ObjectSize, WpdKeys.ObjectCanDelete, WpdKeys.ObjectDateCreated, WpdKeys.ObjectPersistentUniqueId);
            Device = ReadDevice();
        }

        public WpdDeviceProperties Device { get; }

        private WpdDeviceProperties ReadDevice()
        {
            var keys = Keys(WpdKeys.DeviceFriendlyName, WpdKeys.DeviceManufacturer, WpdKeys.DeviceModel, WpdKeys.DeviceSerialNumber,
                WpdKeys.DeviceProtocol, WpdKeys.DeviceFirmwareVersion);
            try
            {
                if (_properties.GetValues(WpdKeys.DeviceObjectId, keys, out var values) < 0 || values is null)
                {
                    return new WpdDeviceProperties(null, null, null, null, null, null);
                }

                try
                {
                    return new WpdDeviceProperties(
                        Read(values, WpdKeys.DeviceFriendlyName) as string,
                        Read(values, WpdKeys.DeviceManufacturer) as string,
                        Read(values, WpdKeys.DeviceModel) as string,
                        Read(values, WpdKeys.DeviceSerialNumber) as string,
                        Read(values, WpdKeys.DeviceProtocol) as string,
                        Read(values, WpdKeys.DeviceFirmwareVersion) as string);
                }
                finally
                {
                    Marshal.ReleaseComObject(values);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(keys);
            }
        }

        public IReadOnlyList<string> ChildIds(string parentId, CancellationToken ct) => Guard(() =>
        {
            _content.EnumObjects(0, parentId, null, out var enumerator);
            try
            {
                var ids = new List<string>();
                var batch = new IntPtr[64];
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    var hr = enumerator.Next((uint)batch.Length, batch, out var fetched);
                    for (var i = 0; i < fetched; i++)
                    {
                        if (Marshal.PtrToStringUni(batch[i]) is { Length: > 0 } id)
                        {
                            ids.Add(id);
                        }

                        Marshal.FreeCoTaskMem(batch[i]);
                    }

                    Check(hr, "list the iPhone's folders");
                    if (hr != 0 || fetched == 0)
                    {
                        return (IReadOnlyList<string>)ids;
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(enumerator);
            }
        }, "list the iPhone's folders");

        public WpdObject? Get(string objectId) => Guard<WpdObject?>(() =>
        {
            var hr = _properties.GetValues(objectId, _objectKeys, out var values);
            if (hr < 0 || values is null)
            {
                return hr is WpdErrors.NotFound or WpdErrors.FileNotFound or WpdErrors.InvalidArg ? null
                    : throw new WpdException($"Windows couldn't read an object on the iPhone: {WpdErrors.Describe(hr)}.", hr);
            }

            try
            {
                return ObjectFrom(objectId, values);
            }
            finally
            {
                Marshal.ReleaseComObject(values);
            }
        }, "read an object on the iPhone");

        public IReadOnlyList<WpdObject>? GetMany(IReadOnlyList<string> objectIds, Action<int>? progress, CancellationToken ct)
        {
            if (_bulkUnsupported)
            {
                return null;
            }

            if (objectIds.Count == 0)
            {
                return [];
            }

            IPortableDevicePropertiesBulk bulk;
            try
            {
                bulk = (IPortableDevicePropertiesBulk)_properties;
            }
            catch (InvalidCastException)
            {
                // The driver's properties object doesn't do bulk (E_NOINTERFACE).
                _bulkUnsupported = true;
                return null;
            }

            return Guard(() =>
            {
                _bulkKeys ??= Keys(WpdKeys.ObjectId, WpdKeys.ObjectParentId, WpdKeys.ObjectName, WpdKeys.ObjectOriginalFileName,
                    WpdKeys.ObjectContentType, WpdKeys.ObjectSize, WpdKeys.ObjectCanDelete, WpdKeys.ObjectDateCreated, WpdKeys.ObjectPersistentUniqueId);
                var ids = Create<IPortableDevicePropVariantCollection>(WpdClsids.PortableDevicePropVariantCollection);
                try
                {
                    foreach (var id in objectIds)
                    {
                        var value = new PropVariant { Vt = PropVariant.VtLpwstr, Pointer = Marshal.StringToCoTaskMemUni(id) };
                        try
                        {
                            ids.Add(ref value);
                        }
                        finally
                        {
                            WpdNative.PropVariantClear(ref value);
                        }
                    }

                    using var callback = new BulkCallback(progress);
                    Guid context;
                    try
                    {
                        bulk.QueueGetValuesByObjectList(ids, _bulkKeys, callback, out context);
                    }
                    catch (COMException ex) when (ex.HResult is WpdErrors.NotImplemented or WpdErrors.NotSupported)
                    {
                        _bulkUnsupported = true;
                        return null;
                    }

                    bulk.Start(ref context);
                    var quiet = System.Diagnostics.Stopwatch.StartNew();
                    var seen = 0;
                    while (!callback.Ended.Wait(TimeSpan.FromMilliseconds(100)))
                    {
                        if (callback.Count != seen)
                        {
                            seen = callback.Count;
                            quiet.Restart();
                        }

                        // A driver that stops answering: given up, and the objects are read one by one.
                        if (quiet.Elapsed > BulkStall)
                        {
                            try
                            {
                                bulk.Cancel(ref context);
                            }
                            catch (COMException)
                            {
                            }

                            callback.Ended.Wait(TimeSpan.FromSeconds(5));
                            throw new WpdException("Windows stopped answering a bulk read of the iPhone's objects.", WpdErrors.Timeout);
                        }

                        if (ct.IsCancellationRequested)
                        {
                            try
                            {
                                bulk.Cancel(ref context);
                            }
                            catch (COMException)
                            {
                                // It ended meanwhile.
                            }

                            callback.Ended.Wait(TimeSpan.FromSeconds(10));
                            ct.ThrowIfCancellationRequested();
                        }
                    }

                    var objects = callback.Objects;
                    if (callback.Status is WpdErrors.NotImplemented or WpdErrors.NotSupported && objects.Count == 0)
                    {
                        _bulkUnsupported = true;
                        return null;
                    }

                    // An answer that stopped part-way still counts for what came back; the caller reads the rest one by one.
                    if (callback.Status < 0 && objects.Count == 0)
                    {
                        throw new WpdException($"Windows couldn't read the iPhone's objects: {WpdErrors.Describe(callback.Status)}.", callback.Status);
                    }

                    return (IReadOnlyList<WpdObject>?)objects;
                }
                finally
                {
                    Marshal.ReleaseComObject(ids);
                }
            }, "read the iPhone's objects");
        }

        public IReadOnlyList<(Guid Category, uint Id)> SupportedCommands() => Guard<IReadOnlyList<(Guid, uint)>>(() =>
        {
            _device.Capabilities(out var capabilities);
            try
            {
                capabilities.GetSupportedCommands(out var commands);
                try
                {
                    return KeysOf(commands);
                }
                finally
                {
                    Marshal.ReleaseComObject(commands);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(capabilities);
            }
        }, "read what the iPhone's camera connection supports");

        public IReadOnlyList<(Guid FormatId, uint Pid)> SupportedProperties(string objectId) => Guard<IReadOnlyList<(Guid, uint)>>(() =>
        {
            _properties.GetSupportedProperties(objectId, out var keys);
            try
            {
                return KeysOf(keys);
            }
            finally
            {
                Marshal.ReleaseComObject(keys);
            }
        }, "read an object's properties");

        public byte[]? ReadAll(string objectId, int maxBytes) => Guard(() =>
        {
            _content.Transfer(out var resources);
            System.Runtime.InteropServices.ComTypes.IStream? stream = null;
            var read = Marshal.AllocCoTaskMem(sizeof(int));
            try
            {
                var key = WpdKeys.ResourceDefault;
                uint optimal = 0;
                resources.GetStream(objectId, ref key, WpdKeys.StgmRead, ref optimal, out stream);
                using var data = new MemoryStream();
                var buffer = new byte[Math.Clamp((int)optimal, 64 * 1024, 1024 * 1024)];
                while (data.Length <= maxBytes)
                {
                    stream.Read(buffer, buffer.Length, read);
                    var n = Marshal.ReadInt32(read);
                    if (n <= 0)
                    {
                        break;
                    }

                    data.Write(buffer, 0, n);
                }

                return data.Length > maxBytes ? null : data.ToArray();
            }
            finally
            {
                Marshal.FreeCoTaskMem(read);
                if (stream is not null)
                {
                    Marshal.ReleaseComObject(stream);
                }

                Marshal.ReleaseComObject(resources);
            }
        }, "read an object on the iPhone");

        private static List<(Guid, uint)> KeysOf(IPortableDeviceKeyCollection keys)
        {
            keys.GetCount(out var count);
            var list = new List<(Guid, uint)>((int)count);
            for (uint i = 0; i < count; i++)
            {
                var key = default(PropertyKey);
                keys.GetAt(i, ref key);
                list.Add((key.FormatId, key.Pid));
            }

            return list;
        }

        public IReadOnlyList<int> Delete(IReadOnlyList<string> objectIds) => Guard(() =>
        {
            var ids = Create<IPortableDevicePropVariantCollection>(WpdClsids.PortableDevicePropVariantCollection);
            IPortableDevicePropVariantCollection? results = null;
            try
            {
                foreach (var id in objectIds)
                {
                    var value = new PropVariant { Vt = PropVariant.VtLpwstr, Pointer = Marshal.StringToCoTaskMemUni(id) };
                    try
                    {
                        ids.Add(ref value);
                    }
                    finally
                    {
                        WpdNative.PropVariantClear(ref value);
                    }
                }

                var hr = _content.Delete(WpdKeys.DeleteNoRecursion, ids, out results);
                var outcome = Enumerable.Repeat(hr < 0 ? hr : 0, objectIds.Count).ToArray();
                if (results is not null)
                {
                    results.GetCount(out var count);
                    for (uint i = 0; i < count && i < outcome.Length; i++)
                    {
                        var value = default(PropVariant);
                        results.GetAt(i, ref value);
                        outcome[i] = value.Vt == PropVariant.VtError ? value.Int32 : outcome[i];
                        WpdNative.PropVariantClear(ref value);
                    }
                }
                else if (hr < 0)
                {
                    throw new WpdException($"The iPhone refused the delete: {WpdErrors.Describe(hr)}.", hr);
                }

                return (IReadOnlyList<int>)outcome;
            }
            finally
            {
                if (results is not null)
                {
                    Marshal.ReleaseComObject(results);
                }

                Marshal.ReleaseComObject(ids);
            }
        }, "delete on the iPhone");

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                _device.Close();
            }
            catch (COMException)
            {
                // Already gone with the cable.
            }

            Marshal.ReleaseComObject(_objectKeys);
            if (_bulkKeys is not null)
            {
                Marshal.ReleaseComObject(_bulkKeys);
            }

            Marshal.ReleaseComObject(_properties);
            Marshal.ReleaseComObject(_content);
            Marshal.ReleaseComObject(_device);
        }
    }

    /// <summary>How long a bulk read may go without an answer before it is given up (then read one by one).</summary>
    internal static readonly TimeSpan BulkStall = TimeSpan.FromSeconds(30);

    /// <summary>One object's properties as read back (a plain read or one entry of a bulk answer).</summary>
    private static WpdObject ObjectFrom(string objectId, IPortableDeviceValues values)
    {
        var contentType = Read(values, WpdKeys.ObjectContentType) as Guid?;
        var kind = contentType == WpdKeys.ContentTypeFolder ? WpdObjectKind.Folder
            : contentType == WpdKeys.ContentTypeFunctionalObject ? WpdObjectKind.Functional
            : WpdObjectKind.File;
        var original = Read(values, WpdKeys.ObjectOriginalFileName) as string;
        var name = Read(values, WpdKeys.ObjectName) as string;
        return new WpdObject(
            objectId,
            Read(values, WpdKeys.ObjectParentId) as string,
            string.IsNullOrEmpty(original) ? name : original,
            kind,
            Read(values, WpdKeys.ObjectSize) is long size ? size : null,
            Read(values, WpdKeys.ObjectCanDelete) as bool?,
            Read(values, WpdKeys.ObjectDateCreated) is DateTimeOffset created ? created : null,
            Read(values, WpdKeys.ObjectPersistentUniqueId) as string);
    }

    /// <summary>
    /// Collects a bulk read's answers as Windows reports them (on its own thread): each entry carries WPD_OBJECT_ID and the
    /// properties asked for. An entry without an id can't be placed and is dropped (the caller reads that object again).
    /// </summary>
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    internal sealed class BulkCallback(Action<int>? progress) : IPortableDevicePropertiesBulkCallback, IDisposable
    {
        private readonly Lock _gate = new();
        private readonly List<WpdObject> _objects = [];

        public int Count
        {
            get
            {
                lock (_gate)
                {
                    return _objects.Count;
                }
            }
        }

        public ManualResetEventSlim Ended { get; } = new(false);

        public int Status { get; private set; }

        public List<WpdObject> Objects
        {
            get
            {
                lock (_gate)
                {
                    return [.. _objects];
                }
            }
        }

        public int OnStart(ref Guid context) => 0;

        public int OnProgress(ref Guid context, IPortableDeviceValuesCollection results)
        {
            try
            {
                results.GetCount(out var count);
                var read = new List<WpdObject>((int)count);
                for (uint i = 0; i < count; i++)
                {
                    results.GetAt(i, out var values);
                    try
                    {
                        if (Read(values, WpdKeys.ObjectId) is string { Length: > 0 } id)
                        {
                            read.Add(ObjectFrom(id, values));
                        }
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(values);
                    }
                }

                int total;
                lock (_gate)
                {
                    _objects.AddRange(read);
                    total = _objects.Count;
                }

                progress?.Invoke(total);
                return 0;
            }
            catch (Exception ex)
            {
                // Never let an exception cross back into Windows' thread; the objects not collected are read again.
                return ex.HResult < 0 ? ex.HResult : WpdErrors.GenFailure;
            }
            finally
            {
                Marshal.ReleaseComObject(results);
            }
        }

        public int OnEnd(ref Guid context, int status)
        {
            Status = status;
            Ended.Set();
            return 0;
        }

        public void Dispose() => Ended.Dispose();
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ProgressSlot(IntPtr self, ref Guid context, IntPtr results);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EndSlot(IntPtr self, ref Guid context, int status);

    /// <summary>
    /// Feeds a bulk answer built from WPD's in-process objects (no device needed) through <see cref="BulkCallback"/> the
    /// way Windows calls it back: from native code, through the callback's COM interface (QueryInterface on the object
    /// Windows is handed, then OnProgress and OnEnd from its vtable). One entry with an id, a name and a size, one without
    /// an id. Returns the QueryInterface result and what the callback collected.
    /// </summary>
    internal static (int QueryInterface, IReadOnlyList<WpdObject> Objects, int Status, int Progress) BulkSelfTest()
    {
        var results = Create<IPortableDeviceValuesCollection>(WpdClsids.PortableDeviceValuesCollection);
        var withId = Create<IPortableDeviceValues>(WpdClsids.PortableDeviceValues);
        var withoutId = Create<IPortableDeviceValues>(WpdClsids.PortableDeviceValues);
        var key = WpdKeys.ObjectId;
        withId.SetStringValue(ref key, "o2A");
        key = WpdKeys.ObjectOriginalFileName;
        withId.SetStringValue(ref key, "IMG_0002.MOV");
        withoutId.SetStringValue(ref key, "IMG_0003.MOV");
        key = WpdKeys.ObjectSize;
        withId.SetUnsignedIntegerValue(ref key, 123_456);
        results.Add(withId);
        results.Add(withoutId);
        Marshal.ReleaseComObject(withId);
        Marshal.ReleaseComObject(withoutId);

        var seen = 0;
        using var callback = new BulkCallback(n => seen = n);
        var context = Guid.NewGuid();
        var unknown = Marshal.GetIUnknownForObject(callback);
        var resultsPointer = Marshal.GetComInterfaceForObject(results, typeof(IPortableDeviceValuesCollection));
        try
        {
            var iid = typeof(IPortableDevicePropertiesBulkCallback).GUID;
            var hr = Marshal.QueryInterface(unknown, in iid, out var itf);
            if (hr < 0)
            {
                return (hr, [], 0, 0);
            }

            try
            {
                // IUnknown's three slots, then OnStart (3), OnProgress (4), OnEnd (5).
                var vtable = Marshal.ReadIntPtr(itf);
                var progress = Marshal.GetDelegateForFunctionPointer<ProgressSlot>(Marshal.ReadIntPtr(vtable, 4 * IntPtr.Size));
                var end = Marshal.GetDelegateForFunctionPointer<EndSlot>(Marshal.ReadIntPtr(vtable, 5 * IntPtr.Size));
                var progressHr = progress(itf, ref context, resultsPointer);
                end(itf, ref context, 0);
                return (progressHr, callback.Objects, callback.Status, seen);
            }
            finally
            {
                Marshal.Release(itf);
            }
        }
        finally
        {
            Marshal.Release(resultsPointer);
            Marshal.Release(unknown);
            try
            {
                Marshal.ReleaseComObject(results);
            }
            catch (InvalidComObjectException)
            {
                // The callback released the same object already (it releases what Windows hands it).
            }
        }
    }

    /// <summary>
    /// Round-trips values through WPD's own in-process objects (PortableDeviceTypes.dll; no device needed), so a test can
    /// check that the COM declarations and the PROPVARIANT layout line up. Returns what it read back.
    /// </summary>
    internal static (string? Name, long? Size, bool? CanDelete, uint Count, PropertyKey SecondKey, string? Id, int Error) SelfTest()
    {
        var values = Create<IPortableDeviceValues>(WpdClsids.PortableDeviceValues);
        var keys = Keys(WpdKeys.ObjectOriginalFileName, WpdKeys.ObjectSize, WpdKeys.ObjectCanDelete);
        var ids = Create<IPortableDevicePropVariantCollection>(WpdClsids.PortableDevicePropVariantCollection);
        // A collection holds one type (TYPE_E_TYPEMISMATCH otherwise): ids in one, a delete's errors in another.
        var errors = Create<IPortableDevicePropVariantCollection>(WpdClsids.PortableDevicePropVariantCollection);
        try
        {
            var name = WpdKeys.ObjectOriginalFileName;
            values.SetStringValue(ref name, "IMG_0001.HEIC");
            var size = WpdKeys.ObjectSize;
            values.SetUnsignedIntegerValue(ref size, 1234);
            values.GetCount(out var count);

            var second = default(PropertyKey);
            keys.GetAt(1, ref second);

            var id = new PropVariant { Vt = PropVariant.VtLpwstr, Pointer = Marshal.StringToCoTaskMemUni("o1A2B") };
            ids.Add(ref id);
            WpdNative.PropVariantClear(ref id);
            var error = new PropVariant { Vt = PropVariant.VtError, Int32 = WpdErrors.AccessDenied };
            errors.Add(ref error);
            var back = default(PropVariant);
            ids.GetAt(0, ref back);
            var readId = back.Vt == PropVariant.VtLpwstr ? Marshal.PtrToStringUni(back.Pointer) : null;
            WpdNative.PropVariantClear(ref back);
            errors.GetAt(0, ref back);
            var readError = back.Vt == PropVariant.VtError ? back.Int32 : 0;

            return (Read(values, name) as string, Read(values, size) as long?, Read(values, WpdKeys.ObjectCanDelete) as bool?, count, second, readId, readError);
        }
        finally
        {
            Marshal.ReleaseComObject(errors);
            Marshal.ReleaseComObject(ids);
            Marshal.ReleaseComObject(keys);
            Marshal.ReleaseComObject(values);
        }
    }

    /// <summary>A value as string, long, bool, Guid or DateTimeOffset; null when missing or of another type.</summary>
    private static object? Read(IPortableDeviceValues values, PropertyKey key)
    {
        if (values.GetValue(ref key, out var value) < 0)
        {
            return null;
        }

        try
        {
            return value.Vt switch
            {
                PropVariant.VtLpwstr => Marshal.PtrToStringUni(value.Pointer),
                PropVariant.VtUi8 or PropVariant.VtI8 => value.Int64,
                PropVariant.VtUi4 or PropVariant.VtI4 => (long)(uint)value.Int32,
                PropVariant.VtBool => value.Bool != 0,
                PropVariant.VtClsid when value.Pointer != IntPtr.Zero => Marshal.PtrToStructure<Guid>(value.Pointer),
                PropVariant.VtDate => new DateTimeOffset(DateTime.SpecifyKind(DateTime.FromOADate(value.Double), DateTimeKind.Local)),
                _ => null,
            };
        }
        finally
        {
            WpdNative.PropVariantClear(ref value);
        }
    }

    private static IPortableDeviceKeyCollection Keys(params PropertyKey[] keys)
    {
        var collection = Create<IPortableDeviceKeyCollection>(WpdClsids.PortableDeviceKeyCollection);
        foreach (var k in keys)
        {
            var key = k;
            collection.Add(ref key);
        }

        return collection;
    }

    private delegate int ManagerCall(string pnpDeviceId, IntPtr buffer, ref uint chars);

    private static string? ManagerString(ManagerCall call, string pnpDeviceId)
    {
        // The first call only asks how long the text is.
        uint chars = 0;
        _ = call(pnpDeviceId, IntPtr.Zero, ref chars);
        if (chars == 0)
        {
            return null;
        }

        var buffer = Marshal.AllocCoTaskMem((int)chars * 2);
        try
        {
            return call(pnpDeviceId, buffer, ref chars) < 0 ? null : Marshal.PtrToStringUni(buffer);
        }
        finally
        {
            Marshal.FreeCoTaskMem(buffer);
        }
    }

    /// <summary>
    /// The serial number of the USB device behind a WPD interface path: "\\?\usb#vid_05ac&amp;pid_12a8&amp;mi_00#7&amp;…#{…}" is
    /// the interface USB\VID_05AC&amp;PID_12A8&amp;MI_00\7&amp;…, whose parent is USB\VID_05AC&amp;PID_12A8\&lt;serial&gt;.
    /// </summary>
    internal static string? UsbSerialOf(string pnpId)
    {
        var instanceId = WpdPnp.InstanceIdOf(pnpId);
        if (instanceId is null)
        {
            return null;
        }

        try
        {
            if (WpdNative.CmLocateDevNode(out var node, instanceId, 0) != WpdNative.CrSuccess)
            {
                return null;
            }

            // A composite device's interface (MI_xx) hangs off the device itself; a plain one is the device.
            var id = DeviceIdOf(node);
            if (id is not null && id.Contains("&MI_", StringComparison.OrdinalIgnoreCase)
                && WpdNative.CmGetParent(out var parent, node, 0) == WpdNative.CrSuccess)
            {
                id = DeviceIdOf(parent);
            }

            return WpdPnp.SerialFromInstanceId(id);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    private static string? DeviceIdOf(uint node)
    {
        if (WpdNative.CmGetDeviceIdSize(out var length, node, 0) != WpdNative.CrSuccess)
        {
            return null;
        }

        var buffer = new char[length + 1];
        return WpdNative.CmGetDeviceId(node, buffer, (uint)buffer.Length, 0) == WpdNative.CrSuccess ? new string(buffer, 0, (int)length) : null;
    }

    private static T Create<T>(Guid clsid)
    {
        var type = Type.GetTypeFromCLSID(clsid, throwOnError: true)!;
        return (T)Activator.CreateInstance(type)!;
    }

    private static void Check(int hr, string what)
    {
        if (hr < 0)
        {
            throw new WpdException($"Windows couldn't {what}: {WpdErrors.Describe(hr)}.", hr);
        }
    }

    private static T Guard<T>(Func<T> call, string what)
    {
        try
        {
            return call();
        }
        catch (COMException ex)
        {
            throw new WpdException($"Windows couldn't {what}: {WpdErrors.Describe(ex.HResult)}.", ex.HResult, ex);
        }
        catch (InvalidCastException ex)
        {
            throw new WpdException($"Windows couldn't {what}: Windows Portable Devices isn't available.", ex.HResult, ex);
        }
    }
}

/// <summary>Reading WPD's PnP ids (plain text, any OS).</summary>
public static class WpdPnp
{
    /// <summary>"\\?\usb#vid_05ac&amp;pid_12a8&amp;mi_00#7&amp;2a&amp;0&amp;0000#{guid}" → "USB\VID_05AC&amp;PID_12A8&amp;MI_00\7&amp;2A&amp;0&amp;0000".</summary>
    public static string? InstanceIdOf(string pnpId)
    {
        var path = pnpId.StartsWith(@"\\?\", StringComparison.Ordinal) ? pnpId[4..] : pnpId;
        var parts = path.Split('#');
        return parts.Length >= 3 ? string.Join('\\', parts[..3]).ToUpperInvariant() : null;
    }

    /// <summary>"USB\VID_05AC&amp;PID_12A8\00008110001A2B3C0E12801E" → "00008110001A2B3C0E12801E"; null for a generated id ("7&amp;2a…&amp;0").</summary>
    public static string? SerialFromInstanceId(string? instanceId)
    {
        if (instanceId is null || !instanceId.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var last = instanceId[(instanceId.LastIndexOf('\\') + 1)..];
        return last.Length == 0 || last.Contains('&') ? null : last;
    }
}
