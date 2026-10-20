using DeviceDeck.Core.Abstractions;
using DeviceDeck.Ios.Plist;
using DeviceDeck.Ios.Protocol;
using DeviceDeck.Ios.Protocol.Lockdown;
using DeviceDeck.Ios.Protocol.Usbmux;

namespace DeviceDeck.Ios.Device;

/// <summary>
/// Asks the phone to trust this PC: lockdown Pair with fresh certificates. The phone shows
/// "Trust This Computer?"; until the user answers, Pair returns PairingDialogResponsePending and is
/// sent again (with the same certificates) on the next retry. Nothing here skips or answers the prompt.
/// </summary>
internal static class IosPairing
{
    public sealed record Attempt(string HostId, string SystemBuid, PairingCertificates Certificates, string? WifiMac);

    public enum Outcome { Paired, Pending, Denied, Locked }

    public static async Task<(Outcome Outcome, Attempt? Attempt)> TryPairAsync(
        DeviceConnector connector, IUsbmuxConnector usbmux, PairRecordStore store, Attempt? pending, CancellationToken ct)
    {
        await using var lockdown = await connector.OpenLockdownAsync(ct).ConfigureAwait(false);
        var attempt = pending ?? await NewAttemptAsync(lockdown, usbmux, store, ct).ConfigureAwait(false);

        PlistDict reply;
        try
        {
            reply = await lockdown.PairAsync(attempt.Certificates.ToPairRequestRecord(attempt.HostId, attempt.SystemBuid), ct)
                .ConfigureAwait(false);
        }
        catch (DeviceException ex) when (ex.LockdownError() is "PairingDialogResponsePending")
        {
            return (Outcome.Pending, attempt);
        }
        catch (DeviceException ex) when (ex.LockdownError() is "UserDeniedPairing")
        {
            return (Outcome.Denied, null);
        }
        catch (DeviceException ex) when (ex.LockdownError() is "PasswordProtected")
        {
            return (Outcome.Locked, attempt);
        }

        var record = attempt.Certificates.ToStoredRecord(attempt.HostId, attempt.SystemBuid, reply.GetData("EscrowBag"), attempt.WifiMac);
        await store.SaveAsync(connector.Udid, connector.UsbmuxDeviceId, record, ct).ConfigureAwait(false);
        return (Outcome.Paired, attempt);
    }

    private static async Task<Attempt> NewAttemptAsync(LockdownClient lockdown, IUsbmuxConnector usbmux, PairRecordStore store, CancellationToken ct)
    {
        var publicKey = await lockdown.GetValueAsync(null, "DevicePublicKey", ct).ConfigureAwait(false) as byte[]
                        ?? throw IosErrors.Protocol("The phone did not give its public key for pairing.");
        var wifiMac = await lockdown.GetValueAsync(null, "WiFiAddress", ct).ConfigureAwait(false) as string;
        var buid = await SystemBuidAsync(usbmux, store, ct).ConfigureAwait(false);
        return new Attempt(Guid.NewGuid().ToString().ToUpperInvariant(), buid, PairingCertificates.Create(publicKey), wifiMac);
    }

    /// <summary>The PC's SystemBUID from usbmux, else the one in any existing pair record, else a new one.</summary>
    private static async Task<string> SystemBuidAsync(IUsbmuxConnector usbmux, PairRecordStore store, CancellationToken ct)
    {
        try
        {
            await using var client = await usbmux.ConnectAsync(ct).ConfigureAwait(false);
            if (await client.ReadBuidAsync(ct).ConfigureAwait(false) is { Length: > 0 } buid)
                return buid;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
        }
        foreach (var file in store.ListFiles())
        {
            try
            {
                using var record = PairRecord.Parse(await File.ReadAllBytesAsync(file.Path, ct).ConfigureAwait(false));
                return record.SystemBuid;
            }
            catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
            {
            }
        }
        return Guid.NewGuid().ToString().ToUpperInvariant();
    }
}
