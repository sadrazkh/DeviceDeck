using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeviceDeck.Core.Transfers;

namespace DeviceDeck.Core.LocalSend;

// LocalSend protocol v2 (https://github.com/localsend/protocol, MIT), written from the spec: the phone runs the
// LocalSend app, which is the receiver (an HTTPS server on port 53317); DeviceDeck is a sender only.

public static class LocalSendProtocol
{
    public const int DefaultPort = 53317;
    public const string MulticastGroup = "224.0.0.167";
    public const string Version = "2.2";
    public const string ApiBase = "/api/localsend/v2/";

    /// <summary>The wire's JSON: camelCase, nulls left out (the receiver treats a missing field as null).</summary>
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>A certificate's fingerprint as LocalSend computes it: SHA-256 of the DER bytes, hex.</summary>
    public static string Fingerprint(X509Certificate certificate) => Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));

    public static bool SameFingerprint(string? a, string? b) =>
        a is { Length: > 0 } && b is { Length: > 0 } && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
}

/// <summary>Who a LocalSend peer is: the multicast announcement and the bodies of /register and /info.</summary>
public sealed record LocalSendInfo
{
    public required string Alias { get; init; }
    public string? Version { get; init; } = LocalSendProtocol.Version;
    public string? DeviceModel { get; init; }

    /// <summary>mobile, desktop, web, headless or server; anything else counts as desktop.</summary>
    public string? DeviceType { get; init; }

    /// <summary>With HTTPS the SHA-256 of the receiver's certificate; with HTTP a random string.</summary>
    public required string Fingerprint { get; init; }

    public int? Port { get; init; }

    /// <summary>"https" (the default) or "http".</summary>
    public string? Protocol { get; init; }

    public bool? Download { get; init; }

    /// <summary>Multicast only: true asks the others to answer.</summary>
    public bool? Announce { get; init; }
}

public sealed record LocalSendFileMetadata(DateTimeOffset? Modified, DateTimeOffset? Accessed);

/// <summary>One file in /prepare-upload; <see cref="FileType"/> is a MIME type ("image/heic").</summary>
public sealed record LocalSendFile(string Id, string FileName, long Size, string FileType, string? Sha256, string? Preview, LocalSendFileMetadata? Metadata);

public sealed record LocalSendPrepareRequest(LocalSendInfo Info, IReadOnlyDictionary<string, LocalSendFile> Files);

/// <summary>The receiver's answer: a session and one token per file it wants.</summary>
public sealed record LocalSendSession(string SessionId, IReadOnlyDictionary<string, string> Files);

/// <summary>How the PC reaches LocalSend on the phone: through the cable (usbmux) or over the network.</summary>
public enum LocalSendRoute { Usb, Wifi }

/// <summary>
/// The receiver a job sends to. Over USB the phone is known, so only its certificate is pinned; over Wi-Fi the address
/// comes from discovery. The fingerprint is pinned for HTTPS: a different certificate fails the job.
/// </summary>
/// <param name="Address">The receiver's IP address on the network (Wi-Fi only).</param>
public sealed record LocalSendTarget(
    LocalSendRoute Route,
    int Port,
    string Protocol,
    string? Fingerprint = null,
    string? Address = null,
    string? Alias = null,
    string? DeviceModel = null)
{
    [JsonIgnore]
    public bool Https => !string.Equals(Protocol, "http", StringComparison.OrdinalIgnoreCase);
}

/// <summary>The transfer request of a localSend job: PC files to LocalSend on the phone (images and videos land in Photos).</summary>
public sealed record LocalSendRequest(LocalSendTarget Target, IReadOnlyList<string> Files);

/// <summary>A LocalSend receiver found for a phone.</summary>
/// <param name="Id">Stable within a discovery: route plus fingerprint.</param>
/// <param name="Likely">Over USB always; over Wi-Fi when it looks like this phone (its name, or an iPhone that is the only one).</param>
public sealed record LocalSendReceiver(
    string Id,
    LocalSendRoute Route,
    string Alias,
    string? DeviceModel,
    string? DeviceType,
    string? Address,
    int Port,
    string Protocol,
    string Fingerprint,
    bool Likely)
{
    public LocalSendTarget ToTarget() => new(Route, Port, Protocol, Fingerprint, Address, Alias, DeviceModel);
}

/// <summary>What the cable route found: LocalSend answering, closed (not open on the phone), open but silent, or no cable route at all.</summary>
public enum LocalSendUsbState { Found, NotListening, NotAnswering, Unavailable }

/// <param name="Usb">The cable route's state; <paramref name="UsbReceiver"/> is set when it is Found.</param>
/// <param name="Wifi">Receivers seen on the network (empty until a Wi-Fi search ran), the likely ones first.</param>
/// <param name="WifiSearching">A Wi-Fi search is still running; ask again for more.</param>
public sealed record LocalSendDiscovery(
    string DeviceId,
    LocalSendUsbState Usb,
    string? UsbDetail,
    LocalSendReceiver? UsbReceiver,
    IReadOnlyList<LocalSendReceiver> Wifi,
    bool WifiSearching,
    DateTimeOffset? WifiSearchedAt);

/// <summary>Where a send is: reaching the phone, waiting for the Accept tap, sending, or finished.</summary>
public enum LocalSendPhase { Connecting, WaitingForAccept, Sending, Done, Failed, Cancelled }

public enum LocalSendFileState { Queued, Sending, Done, Failed, Skipped }

public sealed record LocalSendFileResult(int Index, string Name, long Size, LocalSendFileState State, TransferError? Error);

/// <summary>localsend.job and the localsend.updated event: one send's phase and, for the job call, each file's result.</summary>
public sealed record LocalSendJobStatus(
    string JobId,
    string DeviceId,
    LocalSendPhase Phase,
    string? ReceiverAlias,
    bool PinGiven,
    TransferError? Error,
    IReadOnlyList<LocalSendFileResult>? Files);

/// <summary>The error codes of a send (DeviceException.Code; the page has a next step for each).</summary>
public static class LocalSendErrors
{
    /// <summary>Nothing answers on the phone's port 53317: LocalSend isn't open, or iOS suspended it.</summary>
    public const string NotListening = "localsend_not_listening";

    /// <summary>The connection dropped, or LocalSend stopped taking data, in the middle of a file.</summary>
    public const string ConnectionLost = "localsend_connection_lost";

    public const string PinRequired = "localsend_pin_required";
    public const string PinWrong = "localsend_pin_wrong";
    public const string Rejected = "localsend_rejected";
    public const string Busy = "localsend_busy";
    public const string Checksum = "localsend_checksum";

    /// <summary>The receiver no longer knows the session (cancelled on the phone, or LocalSend restarted).</summary>
    public const string SessionLost = "localsend_session_lost";

    /// <summary>The certificate isn't the one found before: a different LocalSend (or something else) answered.</summary>
    public const string Fingerprint = "localsend_fingerprint";

    public const string Failed = "localsend_failed";

    /// <summary>Errors that end the whole session: every file still to go gets the same answer without asking again.</summary>
    public static bool EndsSession(string code) => code is NotListening or PinRequired or PinWrong or Rejected or Busy or SessionLost or Fingerprint;
}
