using System.Net.Sockets;
using AdvancedSharpAdbClient.Exceptions;
using DeviceDeck.Android.Adb;
using DeviceDeck.Core.Abstractions;

namespace DeviceDeck.Android.Tests;

public class ErrorMappingTests
{
    [Theory]
    [InlineData("device unauthorized.\nThis adb server's $ADB_VENDOR_KEYS is not set\nTry 'adb kill-server' if that seems wrong.", DeviceErrorCodes.Unauthorized)]
    [InlineData("device still authorizing", DeviceErrorCodes.Unauthorized)]
    [InlineData("device offline", DeviceErrorCodes.Disconnected)]
    [InlineData("device still connecting", DeviceErrorCodes.Disconnected)]
    [InlineData("device 'R5CT20ABCDE' not found", DeviceErrorCodes.NotFound)]
    [InlineData("no devices/emulators found", DeviceErrorCodes.NotFound)]
    [InlineData("closed", DeviceErrorCodes.Protocol)]
    public void Adb_server_fail_messages(string message, string code)
    {
        var mapped = AdbErrors.Map(new AdbFailException(message), "serial");
        Assert.Equal(code, mapped.Code);
        Assert.False(string.IsNullOrWhiteSpace(mapped.Message));
    }

    [Fact]
    public void Unauthorized_message_tells_the_user_what_to_tap()
    {
        var mapped = AdbErrors.Map(new AdbFailException("device unauthorized."), "s");
        Assert.Contains("Allow USB debugging", mapped.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Library_exceptions_are_mapped_by_their_adb_text()
    {
        var unauthorized = new AdbException("An error occurred while reading a response from ADB: device unauthorized.", "device unauthorized.");
        Assert.Equal(DeviceErrorCodes.Unauthorized, AdbErrors.Map(unauthorized, "s").Code);
        Assert.Equal(DeviceErrorCodes.NotFound, AdbErrors.Map(new DeviceNotFoundException("s"), "s").Code);
        Assert.Equal(DeviceErrorCodes.PathNotFound,
            AdbErrors.Map(new AdbException("Failed to pull '/sdcard/x'. No such file or directory"), "s", "/sdcard/x").Code);
        Assert.Equal(DeviceErrorCodes.PermissionDenied,
            AdbErrors.Map(new AdbException("secure_mkdirs() failed: Permission denied"), "s").Code);
    }

    [Fact]
    public void Transport_failures()
    {
        Assert.Equal(DeviceErrorCodes.DriverMissing, AdbErrors.Map(new AdbServerUnavailableException("refused"), "s").Code);
        Assert.Equal(DeviceErrorCodes.Disconnected, AdbErrors.Map(new AdbConnectionLostException("gone"), "s").Code);
        Assert.Equal(DeviceErrorCodes.Disconnected, AdbErrors.Map(new SocketException(10054), "s").Code);
        var existing = new DeviceException(DeviceErrorCodes.Locked, "x");
        Assert.Same(existing, AdbErrors.Map(existing, "s"));
    }

    [Theory]
    [InlineData("Failure [INSTALL_FAILED_USER_RESTRICTED: Install canceled by user]", "Install via USB")]
    [InlineData("Failure [INSTALL_CANCELED_BY_USER]", "Install via USB")]
    [InlineData("Failure [INSTALL_FAILED_INSUFFICIENT_STORAGE]", "enough free space")]
    [InlineData("Failure [INSTALL_FAILED_VERSION_DOWNGRADE: Downgrade detected: Update version code 1 is older than current 2]", "newer version")]
    [InlineData("Failure [INSTALL_FAILED_UPDATE_INCOMPATIBLE: Existing package com.x signatures do not match newer version; ignoring!]", "signed by someone else")]
    [InlineData("Failure [INSTALL_PARSE_FAILED_NO_CERTIFICATES: Failed collecting certificates]", "valid Android app")]
    [InlineData("Failure [INSTALL_FAILED_NO_MATCHING_ABIS: Failed to extract native libraries, res=-113]", "processor")]
    [InlineData("Failure [INSTALL_FAILED_OLDER_SDK: Failed parse during installPackageLI]", "newer version of Android")]
    [InlineData("Failure [INSTALL_FAILED_DEPRECATED_SDK_VERSION: App package must target at least SDK version 23]", "very old Android")]
    [InlineData("Failure [INSTALL_FAILED_MISSING_SPLIT: Missing split for com.x]", "install all of its APK files")]
    [InlineData("adb: failed to install x.apk: Failure [INSTALL_FAILED_VERIFICATION_FAILURE]", "Play Protect")]
    [InlineData("Failure [INSTALL_FAILED_ABORTED: User rejected permissions]", "cancelled")]
    public void Install_failures_get_plain_messages(string output, string fragment)
    {
        var error = AdbErrors.FromInstallOutput(output);
        Assert.NotNull(error);
        Assert.Equal(DeviceErrorCodes.InstallFailed, error.Code);
        Assert.Contains(fragment, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Install_success_and_unknown_failures()
    {
        Assert.Null(AdbErrors.FromInstallOutput("Performing Streamed Install\nSuccess\n"));
        var unknown = AdbErrors.FromInstallOutput("Failure [INSTALL_FAILED_SOMETHING_NEW]");
        Assert.Equal("The install failed (INSTALL_FAILED_SOMETHING_NEW).", unknown!.Message);
        var odd = AdbErrors.FromInstallOutput("Error: java.lang.IllegalStateException: boom");
        Assert.Equal(DeviceErrorCodes.InstallFailed, odd!.Code);
        var security = AdbErrors.FromInstallOutput("Exception occurred while executing 'install':\njava.lang.SecurityException: Permission Denial");
        Assert.Equal(DeviceErrorCodes.PermissionDenied, security!.Code);
    }

    [Theory]
    [InlineData("Success", null)]
    [InlineData("Failure [DELETE_FAILED_INTERNAL_ERROR]", DeviceErrorCodes.InstallFailed)]
    [InlineData("Failure [DELETE_FAILED_DEVICE_POLICY_MANAGER]", DeviceErrorCodes.PermissionDenied)]
    [InlineData("Failure [not installed for 0]", DeviceErrorCodes.PathNotFound)]
    [InlineData("Unknown package: com.nope", DeviceErrorCodes.PathNotFound)]
    public void Uninstall_output(string output, string? code)
    {
        Assert.Equal(code, AdbErrors.FromUninstallOutput(output, "com.x")?.Code);
    }

    [Theory]
    [InlineData("rm: /sdcard/x: No such file or directory", DeviceErrorCodes.PathNotFound)]
    [InlineData("mkdir: '/sdcard/Android/data/x': Permission denied", DeviceErrorCodes.PermissionDenied)]
    [InlineData("mkdir: '/sdcard/DCIM': File exists", DeviceErrorCodes.AlreadyExists)]
    [InlineData("rmdir: '/sdcard/full': Directory not empty", DeviceErrorCodes.Protocol)]
    [InlineData("cp: write: No space left on device", DeviceErrorCodes.Protocol)]
    [InlineData("/system/bin/sh: screencap: inaccessible or not found", DeviceErrorCodes.NotSupported)]
    [InlineData("java.lang.SecurityException: Injecting to another application requires INJECT_EVENTS permission", DeviceErrorCodes.PermissionDenied)]
    [InlineData("something odd", DeviceErrorCodes.Protocol)]
    public void Shell_failures(string stderr, string code)
    {
        var error = AdbErrors.FromShell("screencap -p", new ShellResult(1, "", stderr));
        Assert.Equal(code, error.Code);
    }
}
