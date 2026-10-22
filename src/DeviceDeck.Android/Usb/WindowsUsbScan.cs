using System.Runtime.InteropServices;
using System.Text;

namespace DeviceDeck.Android.Usb;

/// <summary>
/// Windows' list of USB devices present now (Configuration Manager, cfgmgr32), read only: no driver is touched or installed.
/// Takes a few milliseconds; empty on other systems or when Windows won't say.
/// </summary>
internal static class WindowsUsbScan
{
    private const int CrSuccess = 0;
    private const int CrBufferSmall = 0x1A;
    private const uint FilterEnumerator = 0x1;
    private const uint FilterPresent = 0x100;
    private const uint DnHasProblem = 0x400;
    private const uint DrpDeviceDesc = 0x1;
    private const uint DrpCompatibleIds = 0x3;
    private const uint DrpFriendlyName = 0xD;

    public static IReadOnlyList<UsbNode> Scan()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        try
        {
            return ScanWindows();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or ExternalException)
        {
            return [];
        }
    }

    private static List<UsbNode> ScanWindows()
    {
        var nodes = new List<UsbNode>();
        foreach (string id in DeviceIds("USB"))
        {
            if (CM_Locate_DevNodeW(out uint node, id, 0) != CrSuccess)
            {
                continue;
            }

            bool problem = CM_Get_DevNode_Status(out uint status, out _, node, 0) == CrSuccess && (status & DnHasProblem) != 0;
            string? parent = CM_Get_Parent(out uint parentNode, node, 0) == CrSuccess ? DeviceId(parentNode) : null;
            string? name = StringProperty(node, DrpFriendlyName) ?? StringProperty(node, DrpDeviceDesc);
            var compatible = MultiStringProperty(node, DrpCompatibleIds);
            nodes.Add(new UsbNode(id, parent, name, compatible, problem));
        }

        return nodes;
    }

    private static IEnumerable<string> DeviceIds(string enumerator)
    {
        const uint flags = FilterEnumerator | FilterPresent;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (CM_Get_Device_ID_List_SizeW(out uint length, enumerator, flags) != CrSuccess || length == 0)
            {
                return [];
            }

            var buffer = new char[length];
            int result = CM_Get_Device_ID_ListW(enumerator, buffer, length, flags);
            if (result == CrBufferSmall)
            {
                continue; // a device arrived between the two calls
            }

            return result == CrSuccess ? SplitMulti(buffer) : [];
        }

        return [];
    }

    private static string? DeviceId(uint node)
    {
        var buffer = new char[512];
        return CM_Get_Device_IDW(node, buffer, (uint)buffer.Length, 0) == CrSuccess ? new string(buffer).TrimEnd('\0') : null;
    }

    private static string? StringProperty(uint node, uint property)
    {
        var bytes = PropertyBytes(node, property);
        if (bytes is null)
        {
            return null;
        }

        string text = Encoding.Unicode.GetString(bytes).TrimEnd('\0');
        return text.Length == 0 ? null : text;
    }

    private static IReadOnlyList<string> MultiStringProperty(uint node, uint property) =>
        PropertyBytes(node, property) is { } bytes ? SplitMulti(Encoding.Unicode.GetString(bytes).ToCharArray()) : [];

    private static byte[]? PropertyBytes(uint node, uint property)
    {
        uint length = 0;
        int result = CM_Get_DevNode_Registry_PropertyW(node, property, out _, null, ref length, 0);
        if (result != CrBufferSmall || length == 0)
        {
            return null;
        }

        var buffer = new byte[length];
        return CM_Get_DevNode_Registry_PropertyW(node, property, out _, buffer, ref length, 0) == CrSuccess ? buffer : null;
    }

    private static List<string> SplitMulti(char[] buffer) =>
        new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID_List_SizeW(out uint length, string? filter, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID_ListW(string? filter, [Out] char[] buffer, uint length, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNodeW(out uint node, string deviceId, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_DevNode_Status(out uint status, out uint problem, uint node, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_Parent(out uint parent, uint node, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_IDW(uint node, [Out] char[] buffer, uint length, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_DevNode_Registry_PropertyW(uint node, uint property, out uint type, [Out] byte[]? buffer, ref uint length, uint flags);
}
