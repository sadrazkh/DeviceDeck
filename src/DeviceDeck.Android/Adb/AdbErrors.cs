using System.Net.Sockets;
using System.Text.RegularExpressions;
using AdvancedSharpAdbClient.Exceptions;
using DeviceDeck.Core.Abstractions;

namespace DeviceDeck.Android.Adb;

/// <summary>Turns adb, sync, shell and package-manager failures into <see cref="DeviceException"/> with one plain sentence.</summary>
internal static partial class AdbErrors
{
    public const string UnauthorizedMessage =
        "Allow USB debugging on the phone: unlock it and tap Allow on the \"Allow USB debugging?\" prompt.";

    /// <summary>Maps a transport-level failure (adb server FAIL text, dropped socket, library exception).</summary>
    public static DeviceException Map(Exception ex, string? serial, string? path = null)
    {
        switch (ex)
        {
            case DeviceException device:
                return device;
            case AdbServerUnavailableException:
                return new DeviceException(DeviceErrorCodes.DriverMissing,
                    "The adb server isn't running; DeviceDeck will try to start it again.", ex);
            case AdbConnectionLostException or IOException or SocketException:
                return new DeviceException(DeviceErrorCodes.Disconnected,
                    "The connection to the phone dropped. Check the cable and try again.", ex);
            case DeviceNotFoundException:
                return new DeviceException(DeviceErrorCodes.NotFound, "The phone isn't connected any more.", ex);
        }

        string text = ex switch
        {
            AdbFailException fail => fail.AdbMessage,
            AdbException { AdbError: { Length: > 0 } adbError } => adbError + " " + ex.Message,
            _ => ex.Message,
        };
        return FromAdbText(text, path, ex) ?? new DeviceException(DeviceErrorCodes.Protocol,
            $"adb reported an error: {FirstLine(text)}", ex);
    }

    /// <summary>Recognises adb server and sync-service error text; null when the text means nothing specific.</summary>
    public static DeviceException? FromAdbText(string text, string? path = null, Exception? inner = null)
    {
        if (Contains(text, "unauthorized"))
        {
            return new DeviceException(DeviceErrorCodes.Unauthorized, UnauthorizedMessage, inner);
        }
        if (Contains(text, "authorizing"))
        {
            return new DeviceException(DeviceErrorCodes.Unauthorized,
                "The phone is still checking this PC's key; look for the \"Allow USB debugging?\" prompt.", inner);
        }
        if (Contains(text, "device offline") || Contains(text, "device still connecting"))
        {
            return new DeviceException(DeviceErrorCodes.Disconnected,
                "The phone is connected but not answering (offline). Unplug it and plug it back in.", inner);
        }
        if ((Contains(text, "not found") && (Contains(text, "device") || Contains(text, "emulator")) && !Contains(text, "No such file"))
            || Contains(text, "no devices/emulators found"))
        {
            return new DeviceException(DeviceErrorCodes.NotFound, "The phone isn't connected any more.", inner);
        }
        return FromFileErrorText(text, path, inner);
    }

    /// <summary>Errno-style text from sync or toybox tools.</summary>
    public static DeviceException? FromFileErrorText(string text, string? path, Exception? inner = null)
    {
        string where = path is null ? "" : $" ({path})";
        if (Contains(text, "No such file or directory") || Contains(text, "does not exist"))
        {
            return new DeviceException(DeviceErrorCodes.PathNotFound, $"That file or folder doesn't exist on the phone{where}.", inner);
        }
        if (Contains(text, "Permission denied") || Contains(text, "Operation not permitted") || Contains(text, "Read-only file system"))
        {
            return new DeviceException(DeviceErrorCodes.PermissionDenied, $"Android doesn't let adb change that location{where}.", inner);
        }
        if (Contains(text, "File exists"))
        {
            return new DeviceException(DeviceErrorCodes.AlreadyExists, $"Something with that name is already there{where}.", inner);
        }
        if (Contains(text, "Directory not empty"))
        {
            return new DeviceException(DeviceErrorCodes.Protocol, $"The folder isn't empty{where}.", inner);
        }
        if (Contains(text, "No space left on device"))
        {
            return new DeviceException(DeviceErrorCodes.Protocol, "The phone's storage is full.", inner);
        }
        if (Contains(text, "Not a directory"))
        {
            return new DeviceException(DeviceErrorCodes.PathNotFound, $"Part of that path is a file, not a folder{where}.", inner);
        }
        return null;
    }

    /// <summary>A shell command exited non-zero.</summary>
    public static DeviceException FromShell(string command, ShellResult result, string? path = null)
    {
        string text = result.Error.Length > 0 ? result.Error : result.Output;
        var known = FromFileErrorText(text, path);
        if (known != null)
        {
            return known;
        }
        string tool = command.TrimStart().Split(' ', 2)[0];
        if (Contains(text, "inaccessible or not found") || Contains(text, "not found") && Contains(text, tool))
        {
            return new DeviceException(DeviceErrorCodes.NotSupported, $"This phone doesn't have the '{tool}' command.");
        }
        if (Contains(text, "SecurityException"))
        {
            return new DeviceException(DeviceErrorCodes.PermissionDenied,
                "Android refused the request. On Xiaomi phones also turn on \"USB debugging (Security settings)\" in Developer options.");
        }
        string detail = FirstLine(text);
        return new DeviceException(DeviceErrorCodes.Protocol,
            detail.Length == 0 ? $"'{tool}' failed on the phone (exit code {result.ExitCode})." : $"'{tool}' failed on the phone: {detail}");
    }

    [GeneratedRegex(@"\b(?<code>INSTALL_[A-Z0-9_]+|DELETE_FAILED_[A-Z0-9_]+)\b(?::\s*(?<detail>[^\]\r\n]*))?")]
    private static partial Regex FailureCodeRegex();

    /// <summary>
    /// Reads `pm install` / `cmd package install-commit` output. Null when it says Success;
    /// otherwise an install_failed (or a more specific code) with a plain sentence.
    /// </summary>
    public static DeviceException? FromInstallOutput(string output)
    {
        if (output.Contains("Success", StringComparison.Ordinal) && !output.Contains("Failure", StringComparison.Ordinal))
        {
            return null;
        }
        var match = FailureCodeRegex().Match(output);
        if (!match.Success)
        {
            if (Contains(output, "SecurityException"))
            {
                return new DeviceException(DeviceErrorCodes.PermissionDenied,
                    "Android refused the install. On Xiaomi phones turn on \"Install via USB\" in Developer options.");
            }
            string line = FirstLine(output);
            return new DeviceException(DeviceErrorCodes.InstallFailed,
                line.Length == 0 ? "The install failed without a reason from the phone." : $"The install failed: {line}");
        }
        string code = match.Groups["code"].Value;
        string message = InstallMessage(code) ?? $"The install failed ({code}).";
        return new DeviceException(DeviceErrorCodes.InstallFailed, message);
    }

    /// <summary>Reads `pm uninstall` output; null on Success.</summary>
    public static DeviceException? FromUninstallOutput(string output, string packageName)
    {
        if (output.Contains("Success", StringComparison.Ordinal))
        {
            return null;
        }
        if (Contains(output, "Unknown package") || Contains(output, "not installed for"))
        {
            return new DeviceException(DeviceErrorCodes.PathNotFound, $"{packageName} isn't installed.");
        }
        var match = FailureCodeRegex().Match(output);
        return match.Groups["code"].Value switch
        {
            "DELETE_FAILED_DEVICE_POLICY_MANAGER" => new DeviceException(DeviceErrorCodes.PermissionDenied,
                "A device-admin app protects this app; remove its admin rights on the phone first."),
            "DELETE_FAILED_OWNER_BLOCKED" or "DELETE_FAILED_USER_RESTRICTED" => new DeviceException(DeviceErrorCodes.PermissionDenied,
                "The phone's owner or a policy blocks removing this app."),
            "DELETE_FAILED_INTERNAL_ERROR" => new DeviceException(DeviceErrorCodes.InstallFailed,
                "Android refused to uninstall it; apps built into the phone can't be removed."),
            "DELETE_FAILED_APP_PINNED" => new DeviceException(DeviceErrorCodes.PermissionDenied,
                "The app is pinned to the screen; unpin it on the phone first."),
            _ => new DeviceException(DeviceErrorCodes.InstallFailed, $"The uninstall failed: {FirstLine(output)}"),
        };
    }

    internal static string? InstallMessage(string code) => code switch
    {
        "INSTALL_FAILED_USER_RESTRICTED" or "INSTALL_CANCELED_BY_USER" =>
            "The phone blocked the install. On Xiaomi, Redmi and POCO phones turn on \"Install via USB\" in Developer options; on other phones accept the prompt on the screen.",
        "INSTALL_FAILED_ABORTED" => "The install was cancelled on the phone.",
        "INSTALL_FAILED_INSUFFICIENT_STORAGE" => "There isn't enough free space on the phone.",
        "INSTALL_FAILED_VERSION_DOWNGRADE" => "A newer version of this app is already installed.",
        "INSTALL_FAILED_UPDATE_INCOMPATIBLE" or "INSTALL_FAILED_INCONSISTENT_CERTIFICATES" or "INSTALL_PARSE_FAILED_INCONSISTENT_CERTIFICATES" =>
            "A copy of this app signed by someone else is installed; uninstall it first.",
        "INSTALL_FAILED_OLDER_SDK" => "This app needs a newer version of Android.",
        "INSTALL_FAILED_DEPRECATED_SDK_VERSION" => "This app was built for a very old Android version, which this phone no longer installs.",
        "INSTALL_FAILED_NO_MATCHING_ABIS" => "This app isn't built for this phone's processor.",
        "INSTALL_FAILED_MISSING_SPLIT" => "Part of this app is missing; install all of its APK files together.",
        "INSTALL_FAILED_TEST_ONLY" => "This is a test-only build, which Android won't install this way.",
        "INSTALL_FAILED_VERIFICATION_FAILURE" or "INSTALL_FAILED_VERIFICATION_TIMEOUT" =>
            "Play Protect or the phone's app verifier blocked this app.",
        "INSTALL_FAILED_DUPLICATE_PERMISSION" or "INSTALL_FAILED_CONFLICTING_PROVIDER" or "INSTALL_FAILED_DUPLICATE_PACKAGE" =>
            "This app conflicts with an app that's already installed.",
        "INSTALL_FAILED_ALREADY_EXISTS" => "This app is already installed.",
        "INSTALL_FAILED_INVALID_APK" or "INSTALL_FAILED_INVALID_URI" or "INSTALL_FAILED_BAD_SIGNATURE" or "INSTALL_FAILED_BAD_DEX_METADATA" =>
            "The file isn't a valid Android app package.",
        "INSTALL_FAILED_MEDIA_UNAVAILABLE" or "INSTALL_FAILED_INTERNAL_ERROR" => "Android hit an internal error while installing; try again.",
        "INSTALL_FAILED_SHARED_USER_INCOMPATIBLE" => "This app's shared user doesn't match the installed one.",
        "INSTALL_FAILED_MISSING_SHARED_LIBRARY" => "The phone is missing a library this app needs.",
        "INSTALL_FAILED_CPU_ABI_INCOMPATIBLE" => "This app isn't built for this phone's processor.",
        "INSTALL_FAILED_SESSION_INVALID" => "The install session expired; try again.",
        _ when code.StartsWith("INSTALL_PARSE_FAILED_", StringComparison.Ordinal) => "The file isn't a valid Android app package.",
        _ => null,
    };

    private static bool Contains(string text, string value) => text.Contains(value, StringComparison.OrdinalIgnoreCase);

    internal static string FirstLine(string text)
    {
        foreach (string line in text.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.Length > 0)
            {
                return trimmed.Length > 200 ? trimmed[..200] : trimmed;
            }
        }
        return "";
    }
}
