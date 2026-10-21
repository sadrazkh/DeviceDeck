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
            finally
            {
                Marshal.ReleaseComObject(values);
            }
        }, "read an object on the iPhone");

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
            Marshal.ReleaseComObject(_properties);
            Marshal.ReleaseComObject(_content);
            Marshal.ReleaseComObject(_device);
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
        var instanceId = InstanceIdOf(pnpId);
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

            return SerialFromInstanceId(id);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>"\\?\usb#vid_05ac&amp;pid_12a8&amp;mi_00#7&amp;2a&amp;0&amp;0000#{guid}" → "USB\VID_05AC&amp;PID_12A8&amp;MI_00\7&amp;2A&amp;0&amp;0000".</summary>
    internal static string? InstanceIdOf(string pnpId)
    {
        var path = pnpId.StartsWith(@"\\?\", StringComparison.Ordinal) ? pnpId[4..] : pnpId;
        var parts = path.Split('#');
        return parts.Length >= 3 ? string.Join('\\', parts[..3]).ToUpperInvariant() : null;
    }

    /// <summary>"USB\VID_05AC&amp;PID_12A8\00008110001A2B3C0E12801E" → "00008110001A2B3C0E12801E"; null for a generated id ("7&amp;2a…&amp;0").</summary>
    internal static string? SerialFromInstanceId(string? instanceId)
    {
        if (instanceId is null || !instanceId.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var last = instanceId[(instanceId.LastIndexOf('\\') + 1)..];
        return last.Length == 0 || last.Contains('&') ? null : last;
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
