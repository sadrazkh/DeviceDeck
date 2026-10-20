using System.Net.Sockets;
using System.Security.Authentication;
using DeviceDeck.Core.Abstractions;

namespace DeviceDeck.Ios.Protocol;

/// <summary>Turns transport and parser failures into <see cref="DeviceException"/> at the provider's public surface.</summary>
public static class IosErrors
{
    /// <summary>Key in <see cref="Exception.Data"/> holding lockdown's raw "Error" string, when there is one.</summary>
    public const string LockdownErrorKey = "lockdown.error";

    public static DeviceException Protocol(string message, Exception? inner = null) =>
        new(DeviceErrorCodes.Protocol, message, inner);

    public static DeviceException Disconnected(string message, Exception? inner = null) =>
        new(DeviceErrorCodes.Disconnected, message, inner);

    public static DeviceException NotSupported(string message) =>
        new(DeviceErrorCodes.NotSupported, message);

    /// <summary>Maps anything thrown below the provider surface. Cancellation passes through untouched.</summary>
    public static Exception Translate(Exception ex) => ex switch
    {
        DeviceException or OperationCanceledException => ex,
        EndOfStreamException => Disconnected("The phone closed the connection.", ex),
        SocketException or IOException { InnerException: SocketException } =>
            Disconnected("The connection to the phone dropped.", ex),
        AuthenticationException => new DeviceException(DeviceErrorCodes.TrustRequired,
            "The secure session with the phone failed; the pairing may be stale.", ex),
        IOException => Disconnected("The connection to the phone failed: " + ex.Message, ex),
        FormatException => Protocol("The phone sent data DeviceDeck could not read: " + ex.Message, ex),
        ObjectDisposedException => Disconnected("The connection to the phone is closed.", ex),
        _ => ex,
    };

    /// <summary>Runs <paramref name="action"/> and translates what it throws.</summary>
    public static async Task<T> Guard<T>(Func<Task<T>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (Exception ex) when (Translate(ex) is var mapped && !ReferenceEquals(mapped, ex))
        {
            throw mapped;
        }
    }

    public static async Task Guard(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception ex) when (Translate(ex) is var mapped && !ReferenceEquals(mapped, ex))
        {
            throw mapped;
        }
    }

    public static string? LockdownError(this DeviceException ex) => ex.Data[LockdownErrorKey] as string;
}
