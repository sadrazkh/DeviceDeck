using DeviceDeck.Bridge.JsonRpc;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.Backups;
using DeviceDeck.Core.Backups.Contacts;
using DeviceDeck.Core.Backups.Messages;

namespace DeviceDeck.Bridge.Methods;

// docs/bridge-contract.md, "Backup, contacts and messages" (round 2).

public sealed record BackupListParams(string? DeviceId = null);

public sealed record BackupStartParams(string DeviceId, bool Full = false);

public sealed record BackupRestoreParams(string DeviceId, string BackupId, string ConfirmName, string? Password = null, bool RestoreSettings = true);

public sealed record BackupEncryptionParams(string DeviceId, bool Enable, string? Password = null, string? OldPassword = null);

public sealed record BackupIdParams(string BackupId);

public sealed record BackupIdsParams(IReadOnlyList<string> BackupIds);

public sealed record BackupUnlockParams(string BackupId, string Password);

public sealed record BackupFilesParams(string BackupId, string Domain, string Path = "");

public sealed record BackupExtractParams(string BackupId, IReadOnlyList<BackupItemRef> Items, string Destination);

public sealed record ContactsExportParams(string BackupId, ContactsFormat Format, string Destination, IReadOnlyList<string>? ContactIds = null);

public sealed record MessagesListParams(string BackupId, string ConversationId, int Offset, int Limit);

public sealed record MessagesExportParams(string BackupId, IReadOnlyList<string> ConversationIds, MessagesFormat Format, string Destination);

/// <summary>Registers the backup, contacts and messages methods; without a <see cref="BackupService"/> they answer not_supported.</summary>
public static class BackupMethods
{
    private const int MaxMessagesPage = 1000;

    public static void Register(JsonRpcDispatcher rpc, BridgeServices s)
    {
        BackupService Backups() => s.Backups ?? throw new DeviceException(DeviceErrorCodes.NotSupported, "Backups aren't available in this build.");

        rpc.Register<BackupListParams, IReadOnlyList<BackupInfo>>("backup.list", (p, _) => Task.Run(() => Backups().List(p.DeviceId)));
        rpc.Register<DeviceParams, BackupDeviceInfo>("backup.device", (p, ct) => Backups().GetDeviceInfoAsync(p.DeviceId, ct));
        rpc.Register<BackupStartParams, BackupJob>("backup.start", (p, _) => Task.FromResult(Backups().StartBackup(p.DeviceId, p.Full)));
        rpc.Register<BackupRestoreParams, BackupJob>("backup.restore", (p, _) =>
            Task.Run(() => Backups().StartRestore(p.DeviceId, p.BackupId, p.ConfirmName, p.Password, p.RestoreSettings)));
        rpc.Register<BackupEncryptionParams, BackupJob>("backup.setEncryption", (p, _) =>
            Task.FromResult(Backups().StartEncryptionChange(p.DeviceId, p.Enable, p.Password, p.OldPassword)));
        rpc.Register("backup.jobs", _ => Task.FromResult(Backups().Jobs()));
        rpc.Register<JobParams, Empty>("backup.cancel", (p, _) =>
        {
            Backups().Cancel(p.JobId);
            return Task.FromResult(Empty.Value);
        });
        rpc.Register<DeviceParams, PrunePlan>("backup.prunePlan", (p, _) => Task.Run(() => Backups().GetPrunePlan(p.DeviceId)));
        rpc.Register<BackupIdsParams, PrunePlan>("backup.deletePlan", (p, _) => Task.Run(() => Backups().GetDeletePlan(p.BackupIds)));
        rpc.Register<BackupIdsParams, BackupDeleteResult>("backup.delete", (p, _) => Task.Run(() => Backups().Delete(p.BackupIds)));

        rpc.Register<BackupUnlockParams, Empty>("backup.unlock", async (p, ct) =>
        {
            await Backups().UnlockAsync(p.BackupId, p.Password, ct).ConfigureAwait(false);
            return Empty.Value;
        });
        rpc.Register<BackupIdParams, Empty>("backup.lock", (p, _) =>
        {
            Backups().Lock(p.BackupId);
            return Task.FromResult(Empty.Value);
        });
        rpc.Register<BackupIdParams, IReadOnlyList<BackupDomain>>("backup.domains", (p, ct) => Backups().GetDomainsAsync(p.BackupId, ct));
        rpc.Register<BackupFilesParams, IReadOnlyList<BackupEntry>>("backup.files", (p, ct) => Backups().GetFilesAsync(p.BackupId, p.Domain, p.Path, ct));
        rpc.Register<BackupExtractParams, BackupJob>("backup.extract", (p, _) =>
            Task.Run(() => Backups().StartExtract(p.BackupId, p.Items, p.Destination)));

        rpc.Register<BackupIdParams, IReadOnlyList<Contact>>("contacts.list", (p, ct) => Backups().GetContactsAsync(p.BackupId, ct));
        rpc.Register<ContactsExportParams, BackupJob>("contacts.export", (p, _) =>
            Task.Run(() => Backups().StartContactsExport(p.BackupId, p.ContactIds, p.Format, p.Destination)));
        rpc.Register<BackupIdParams, IReadOnlyList<Conversation>>("messages.conversations", (p, ct) => Backups().GetConversationsAsync(p.BackupId, ct));
        rpc.Register<MessagesListParams, Page<Message>>("messages.list", (p, ct) =>
        {
            if (p.Offset < 0 || p.Limit < 1 || p.Limit > MaxMessagesPage)
            {
                throw new ArgumentException($"offset must be 0 or more and limit between 1 and {MaxMessagesPage}.");
            }

            return Backups().GetMessagesAsync(p.BackupId, p.ConversationId, p.Offset, p.Limit, ct);
        });
        rpc.Register<MessagesExportParams, BackupJob>("messages.export", (p, _) =>
            Task.Run(() => Backups().StartMessagesExport(p.BackupId, p.ConversationIds, p.Format, p.Destination, s.Shell as IPdfPrinter)));
    }
}
