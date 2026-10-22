using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DeviceDeck.Ios.Wpd;

// The slice of PortableDeviceApi.h / PortableDevice.h DeviceDeck calls. Interface GUIDs and method order were checked
// against the type libraries in %windir%\System32\PortableDeviceApi.dll and PortableDeviceTypes.dll; property keys
// against PortableDevice.h. Every method up to the last one used is declared, so the vtable slots line up.

[StructLayout(LayoutKind.Sequential)]
internal struct PropertyKey(Guid fmtid, uint pid)
{
    public Guid FormatId = fmtid;
    public uint Pid = pid;
}

/// <summary>A PROPVARIANT: 24 bytes on 64-bit Windows (16 on 32-bit; the trailing field only pads).</summary>
[StructLayout(LayoutKind.Explicit)]
internal struct PropVariant
{
    [FieldOffset(0)] public ushort Vt;
    [FieldOffset(8)] public IntPtr Pointer;
    [FieldOffset(8)] public long Int64;
    [FieldOffset(8)] public int Int32;
    [FieldOffset(8)] public short Bool;
    [FieldOffset(8)] public double Double;
    [FieldOffset(16)] public IntPtr Padding;

    public const ushort VtEmpty = 0;
    public const ushort VtI4 = 3;
    public const ushort VtDate = 7;
    public const ushort VtFiletime = 64;
    public const ushort VtError = 10;
    public const ushort VtBool = 11;
    public const ushort VtUi4 = 19;
    public const ushort VtI8 = 20;
    public const ushort VtUi8 = 21;
    public const ushort VtLpwstr = 31;
    public const ushort VtClsid = 72;
}

internal static class WpdKeys
{
    private static readonly Guid ObjectProperties = new("EF6B490D-5CD8-437A-AFFC-DA8B60EE4A3C");
    private static readonly Guid DeviceProperties = new("26D4979A-E643-4626-9E2B-736DC0C92FDC");
    private static readonly Guid ClientInformation = new("204D9F0C-2292-4080-9F42-40664E70F859");

    public static readonly PropertyKey ObjectId = new(ObjectProperties, 2);
    public static readonly PropertyKey ObjectParentId = new(ObjectProperties, 3);
    public static readonly PropertyKey ObjectName = new(ObjectProperties, 4);
    public static readonly PropertyKey ObjectPersistentUniqueId = new(ObjectProperties, 5);
    public static readonly PropertyKey ObjectContentType = new(ObjectProperties, 7);
    public static readonly PropertyKey ObjectSize = new(ObjectProperties, 11);
    public static readonly PropertyKey ObjectOriginalFileName = new(ObjectProperties, 12);
    public static readonly PropertyKey ObjectDateCreated = new(ObjectProperties, 18);
    public static readonly PropertyKey ObjectCanDelete = new(ObjectProperties, 26);

    public static readonly PropertyKey DeviceFirmwareVersion = new(DeviceProperties, 3);
    public static readonly PropertyKey DeviceProtocol = new(DeviceProperties, 6);
    public static readonly PropertyKey DeviceManufacturer = new(DeviceProperties, 7);
    public static readonly PropertyKey DeviceModel = new(DeviceProperties, 8);
    public static readonly PropertyKey DeviceSerialNumber = new(DeviceProperties, 9);
    public static readonly PropertyKey DeviceFriendlyName = new(DeviceProperties, 12);

    public static readonly PropertyKey ClientName = new(ClientInformation, 2);
    public static readonly PropertyKey ClientMajorVersion = new(ClientInformation, 3);
    public static readonly PropertyKey ClientMinorVersion = new(ClientInformation, 4);
    public static readonly PropertyKey ClientRevision = new(ClientInformation, 5);
    public static readonly PropertyKey ClientSecurityQualityOfService = new(ClientInformation, 8);
    public static readonly PropertyKey ClientDesiredAccess = new(ClientInformation, 9);

    public static readonly Guid ContentTypeFolder = new("27E2E392-A111-48E0-AB0C-E17705A05F85");
    public static readonly Guid ContentTypeFunctionalObject = new("99ED0160-17FF-4C44-9D98-1D7A6F941921");

    public const string DeviceObjectId = "DEVICE";

    /// <summary>WPD_RESOURCE_DEFAULT: an object's data.</summary>
    public static readonly PropertyKey ResourceDefault = new(new Guid("E81E79BE-34F0-41BF-B53F-F1A06AE87842"), 0);
    public const uint StgmRead = 0;
    public const uint DeleteNoRecursion = 0;
    public const uint SecurityImpersonation = 0x00020000;
    public const uint GenericRead = 0x80000000;
}

internal static class WpdClsids
{
    public static readonly Guid PortableDeviceManager = new("0AF10CEC-2ECD-4B92-9581-34F6AE0637F3");
    public static readonly Guid PortableDeviceFtm = new("F7C0039A-4762-488A-B4B3-760EF9A1BA9B");
    public static readonly Guid PortableDeviceValues = new("0C15D503-D017-47CE-9016-7B3F978721CC");
    public static readonly Guid PortableDeviceKeyCollection = new("DE2D022D-2480-43BE-97F0-D1FA2CF98F4F");
    public static readonly Guid PortableDevicePropVariantCollection = new("08A99E2F-6D6D-4B80-AF5A-BAF2BCBE4CB9");
    public static readonly Guid PortableDeviceValuesCollection = new("3882134D-14CF-4220-9CB4-435F86D83F60");
}

[ComImport, Guid("a1567595-4c2f-4574-a6fa-ecef917b9a40"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPortableDeviceManager
{
    [PreserveSig] int GetDevices([In, Out, MarshalAs(UnmanagedType.LPArray)] IntPtr[]? pnpDeviceIds, ref uint count);
    void RefreshDeviceList();
    [PreserveSig] int GetDeviceFriendlyName([MarshalAs(UnmanagedType.LPWStr)] string pnpDeviceId, IntPtr buffer, ref uint chars);
    [PreserveSig] int GetDeviceDescription([MarshalAs(UnmanagedType.LPWStr)] string pnpDeviceId, IntPtr buffer, ref uint chars);
    [PreserveSig] int GetDeviceManufacturer([MarshalAs(UnmanagedType.LPWStr)] string pnpDeviceId, IntPtr buffer, ref uint chars);
}

[ComImport, Guid("625e2df8-6392-4cf0-9ad1-3cfa5f17775c"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPortableDevice
{
    void Open([MarshalAs(UnmanagedType.LPWStr)] string pnpDeviceId, IPortableDeviceValues clientInfo);
    void SendCommand(uint flags, IPortableDeviceValues parameters, out IPortableDeviceValues results);
    void Content(out IPortableDeviceContent content);
    void Capabilities(out IPortableDeviceCapabilities capabilities);
    void Cancel();
    void Close();
}

[ComImport, Guid("fd8878ac-d841-4d17-891c-e6829cdb6934"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPortableDeviceResources
{
    void GetSupportedResources([MarshalAs(UnmanagedType.LPWStr)] string objectId, out IPortableDeviceKeyCollection keys);
    void GetResourceAttributes([MarshalAs(UnmanagedType.LPWStr)] string objectId, ref PropertyKey key, out IPortableDeviceValues attributes);
    void GetStream([MarshalAs(UnmanagedType.LPWStr)] string objectId, ref PropertyKey key, uint mode, ref uint optimalBufferSize,
        out System.Runtime.InteropServices.ComTypes.IStream stream);
}

[ComImport, Guid("2c8c6dbf-e3dc-4061-becc-8542e810d126"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPortableDeviceCapabilities
{
    void GetSupportedCommands(out IPortableDeviceKeyCollection commands);
}

[ComImport, Guid("6a96ed84-7c73-4480-9938-bf5af477d426"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPortableDeviceContent
{
    void EnumObjects(uint flags, [MarshalAs(UnmanagedType.LPWStr)] string parentObjectId, IPortableDeviceValues? filter, out IEnumPortableDeviceObjectIDs enumerator);
    void Properties(out IPortableDeviceProperties properties);
    void Transfer(out IPortableDeviceResources resources);
    void CreateObjectWithPropertiesOnly(IPortableDeviceValues values, out IntPtr objectId);
    void CreateObjectWithPropertiesAndData(IPortableDeviceValues values, out IntPtr data, ref uint optimalWriteBufferSize, out IntPtr cookie);
    [PreserveSig] int Delete(uint options, IPortableDevicePropVariantCollection objectIds, out IPortableDevicePropVariantCollection? results);
}

[ComImport, Guid("10ece955-cf41-4728-bfa0-41eedf1bbf19"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IEnumPortableDeviceObjectIDs
{
    [PreserveSig] int Next(uint count, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] IntPtr[] objectIds, out uint fetched);
}

[ComImport, Guid("7f6d695c-03df-4439-a809-59266beee3a6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPortableDeviceProperties
{
    void GetSupportedProperties([MarshalAs(UnmanagedType.LPWStr)] string objectId, out IPortableDeviceKeyCollection keys);
    void GetPropertyAttributes([MarshalAs(UnmanagedType.LPWStr)] string objectId, ref PropertyKey key, out IPortableDeviceValues attributes);
    [PreserveSig] int GetValues([MarshalAs(UnmanagedType.LPWStr)] string objectId, IPortableDeviceKeyCollection? keys, out IPortableDeviceValues? values);
}

// Bulk reads (v0.4.1). Not in the type libraries: the IIDs are the ones registered under HKCR\Interface on Windows 11
// (IPortableDevicePropertiesBulk, IPortableDevicePropertiesBulkCallback), the method order PortableDeviceApi.idl's.
// IPortableDeviceValuesCollection's order is in PortableDeviceApi.dll's type library.

[ComImport, Guid("482b05c0-4056-44ed-9e0f-5e23b009da93"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPortableDevicePropertiesBulk
{
    void QueueGetValuesByObjectList(IPortableDevicePropVariantCollection objectIds, IPortableDeviceKeyCollection keys,
        IPortableDevicePropertiesBulkCallback callback, out Guid context);
    void QueueGetValuesByObjectFormat(ref Guid objectFormat, [MarshalAs(UnmanagedType.LPWStr)] string parentObjectId, uint depth,
        IPortableDeviceKeyCollection keys, IPortableDevicePropertiesBulkCallback callback, out Guid context);
    void QueueSetValuesByObjectList(IntPtr objectValues, IPortableDevicePropertiesBulkCallback callback, out Guid context);
    void Start(ref Guid context);
    void Cancel(ref Guid context);
}

[ComImport, Guid("9deacb80-11e8-40e3-a9f3-f557986a7845"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPortableDevicePropertiesBulkCallback
{
    [PreserveSig] int OnStart(ref Guid context);
    [PreserveSig] int OnProgress(ref Guid context, IPortableDeviceValuesCollection results);
    [PreserveSig] int OnEnd(ref Guid context, int status);
}

[ComImport, Guid("6e3f2d79-4e07-48c4-8208-d8c2e5af4a99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPortableDeviceValuesCollection
{
    void GetCount(out uint count);
    void GetAt(uint index, out IPortableDeviceValues values);
    void Add(IPortableDeviceValues values);
    void Clear();
}

[ComImport, Guid("6848f6f2-3155-4f86-b6f5-263eeeab3143"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPortableDeviceValues
{
    void GetCount(out uint count);
    void GetAt(uint index, ref PropertyKey key, ref PropVariant value);
    void SetValue(ref PropertyKey key, ref PropVariant value);
    [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
    void SetStringValue(ref PropertyKey key, [MarshalAs(UnmanagedType.LPWStr)] string value);
    void GetStringValue(ref PropertyKey key, [MarshalAs(UnmanagedType.LPWStr)] out string value);
    void SetUnsignedIntegerValue(ref PropertyKey key, uint value);
}

[ComImport, Guid("dada2357-e0ad-492e-98db-dd61c53ba353"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPortableDeviceKeyCollection
{
    void GetCount(out uint count);
    void GetAt(uint index, ref PropertyKey key);
    void Add(ref PropertyKey key);
}

[ComImport, Guid("89b2e422-4f1b-4316-bcef-a44afea83eb3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPortableDevicePropVariantCollection
{
    void GetCount(out uint count);
    void GetAt(uint index, ref PropVariant value);
    void Add(ref PropVariant value);
}

[SupportedOSPlatform("windows")]
internal static class WpdNative
{
    [DllImport("ole32.dll")]
    public static extern int PropVariantClear(ref PropVariant value);

    public const int CrSuccess = 0;

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Locate_DevNodeW", CharSet = CharSet.Unicode)]
    public static extern int CmLocateDevNode(out uint devInst, string deviceInstanceId, uint flags);

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_Parent")]
    public static extern int CmGetParent(out uint parent, uint devInst, uint flags);

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_ID_Size")]
    public static extern int CmGetDeviceIdSize(out uint length, uint devInst, uint flags);

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_IDW", CharSet = CharSet.Unicode)]
    public static extern int CmGetDeviceId(uint devInst, [Out] char[] buffer, uint bufferLength, uint flags);
}
