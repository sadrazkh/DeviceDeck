using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace DeviceDeck.Android.PlatformTools;

/// <summary>
/// Who signed a Windows executable: the organisation (O=) of a valid Authenticode signature, checked by Windows itself
/// (WinVerifyTrust), or null when the file isn't signed or the signature doesn't hold. Google signs adb.exe,
/// AdbWinApi.dll and AdbWinUsbApi.dll as "Google LLC".
/// </summary>
internal static class AuthenticodeSigner
{
    public const string Google = "Google LLC";

    public static string? OrganizationOf(string path)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(path) || !Verify(path))
        {
            return null;
        }

        try
        {
#pragma warning disable SYSLIB0057 // reads the signer of a signed file; X509CertificateLoader has no equivalent
            using var signer = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            foreach (var part in signer.SubjectName.EnumerateRelativeDistinguishedNames())
            {
                if (part.GetSingleElementType().Value == "2.5.4.10")
                {
                    return part.GetSingleElementValue();
                }
            }

            return null;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    /// <summary>WinVerifyTrust's Authenticode policy, without any UI and without asking for revocation lists.</summary>
    private static bool Verify(string path)
    {
        var file = new WinTrustFileInfo
        {
            StructSize = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
            FilePath = path,
        };
        var fileHandle = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(file, fileHandle, false);
            var data = new WinTrustData
            {
                StructSize = (uint)Marshal.SizeOf<WinTrustData>(),
                UiChoice = 2, // WTD_UI_NONE
                RevocationChecks = 0, // WTD_REVOKE_NONE
                UnionChoice = 1, // WTD_CHOICE_FILE
                FileInfo = fileHandle,
                StateAction = 0, // WTD_STATEACTION_IGNORE
                ProvFlags = 0x10, // WTD_REVOCATION_CHECK_NONE
            };
            var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2
            return WinVerifyTrust(IntPtr.Zero, ref action, ref data) == 0;
        }
        finally
        {
            Marshal.DestroyStructure<WinTrustFileInfo>(fileHandle);
            Marshal.FreeHGlobal(fileHandle);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint StructSize;
        [MarshalAs(UnmanagedType.LPWStr)] public string FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint StructSize;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr FileInfo;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProvFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref WinTrustData data);
}
