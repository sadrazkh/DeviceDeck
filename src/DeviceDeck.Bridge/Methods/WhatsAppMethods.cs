using DeviceDeck.Bridge.JsonRpc;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.Backups;
using DeviceDeck.Core.Imaging;
using DeviceDeck.Core.Transfers;
using DeviceDeck.Core.WhatsApp;

namespace DeviceDeck.Bridge.Methods;

// docs/bridge-contract.md, "WhatsApp (round 4)".

public sealed record WhatsAppMessagesParams(string BackupId, string ChatId, int Offset, int Limit);

public sealed record WhatsAppMediaParams(string BackupId, string MediaId, int Size = 256);

/// <param name="ChatIds">The chats to save; empty or missing = all of them.</param>
public sealed record WhatsAppExportParams(string BackupId, MessagesFormat Format, string Destination, IReadOnlyList<string>? ChatIds = null);

public sealed record WhatsAppExportMediaParams(string BackupId, string Destination, IReadOnlyList<string>? ChatIds = null);

public sealed record WhatsAppAndroidBackupParams(string DeviceId, string Destination);

/// <summary>A picture as a data: URL the page shows as is; null when there is none.</summary>
public sealed record WhatsAppThumbnailResult(string? DataUrl);

public sealed record WhatsAppMediaResult(string DataUrl, string MimeType, long Size);

/// <summary>Registers whatsapp.*; reading needs the <see cref="BackupService"/>, the Android copy the transfer queue.</summary>
public static class WhatsAppMethods
{
    private const int MaxPage = 1000;

    public static void Register(JsonRpcDispatcher rpc, BridgeServices s)
    {
        BackupService Backups() => s.Backups ?? throw new DeviceException(DeviceErrorCodes.NotSupported, "WhatsApp is read from backups, which aren't available in this build.");

        rpc.Register<BackupIdParams, WhatsAppStatus>("whatsapp.status", (p, ct) => Backups().GetWhatsAppStatusAsync(p.BackupId, ct));
        rpc.Register<BackupIdParams, IReadOnlyList<WhatsAppChat>>("whatsapp.chats", (p, ct) => Backups().GetWhatsAppChatsAsync(p.BackupId, ct));
        rpc.Register<WhatsAppMessagesParams, Page<WhatsAppMessage>>("whatsapp.messages", (p, ct) =>
        {
            if (p.Offset < 0 || p.Limit < 1 || p.Limit > MaxPage)
            {
                throw new ArgumentException($"offset must be 0 or more and limit between 1 and {MaxPage}.");
            }

            return Backups().GetWhatsAppMessagesAsync(p.BackupId, p.ChatId, p.Offset, p.Limit, ct);
        });
        rpc.Register<WhatsAppMediaParams, WhatsAppThumbnailResult>("whatsapp.thumbnail", async (p, ct) =>
        {
            var bytes = await Backups().GetWhatsAppThumbnailAsync(p.BackupId, p.MediaId, Math.Clamp(p.Size, 64, 1024), ct).ConfigureAwait(false);
            return new WhatsAppThumbnailResult(bytes is null ? null : DataUrl(ImageFormats.Sniff(bytes), bytes));
        });
        rpc.Register<WhatsAppMediaParams, WhatsAppMediaResult>("whatsapp.media", async (p, ct) =>
        {
            var file = await Backups().GetWhatsAppMediaAsync(p.BackupId, p.MediaId, ct).ConfigureAwait(false);
            return new WhatsAppMediaResult(DataUrl(file.MimeType, file.Bytes), file.MimeType, file.Size);
        });
        rpc.Register<WhatsAppExportParams, BackupJob>("whatsapp.export", (p, _) =>
            Task.Run(() => Backups().StartWhatsAppExport(p.BackupId, p.ChatIds, p.Format, p.Destination, s.Shell as IPdfPrinter)));
        rpc.Register<WhatsAppExportMediaParams, BackupJob>("whatsapp.exportMedia", (p, _) =>
            Task.Run(() => Backups().StartWhatsAppMediaExport(p.BackupId, p.ChatIds, p.Destination)));

        // Android: what WhatsApp keeps on the phone, and a copy of it through the transfer queue.
        rpc.Register<DeviceParams, WhatsAppAndroidInfo>("whatsapp.android", (p, ct) => WhatsAppAndroid.GetInfoAsync(AndroidWithFiles(s, p.DeviceId), ct));
        rpc.Register<WhatsAppAndroidBackupParams, WhatsAppAndroidBackupResult>("whatsapp.backupAndroid", async (p, ct) =>
        {
            var device = AndroidWithFiles(s, p.DeviceId);
            if (string.IsNullOrWhiteSpace(p.Destination) || !Path.IsPathFullyQualified(p.Destination))
            {
                throw new ArgumentException("destination must be a full folder path on this PC.");
            }

            var folder = WhatsAppAndroid.DatedFolder(p.Destination, device.Summary.Name, DateTimeOffset.Now);
            var job = await s.Transfers.EnqueueAsync(p.DeviceId, TransferKind.WhatsAppMedia, new WhatsAppMediaRequest(folder), s.Settings.Current.ConflictRule, ct)
                .ConfigureAwait(false);
            return new WhatsAppAndroidBackupResult(job.Id, folder);
        });
    }

    private static IDevice AndroidWithFiles(BridgeServices s, string deviceId)
    {
        var device = s.Hub.GetUsableDevice(deviceId);
        WhatsAppAndroid.RequireAndroid(device);
        if ((device.Summary.Capabilities & DeviceCapability.Files) == 0)
        {
            throw new DeviceException(DeviceErrorCodes.NotSupported, $"{device.Summary.Name} doesn't share its files yet.");
        }

        return device;
    }

    private static string DataUrl(string mimeType, byte[] bytes) => $"data:{mimeType};base64,{Convert.ToBase64String(bytes)}";
}
