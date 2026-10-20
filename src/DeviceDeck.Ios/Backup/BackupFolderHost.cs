using System.Buffers;
using System.Buffers.Binary;
using DeviceDeck.Ios.Plist;

namespace DeviceDeck.Ios.Backup;

/// <summary>
/// The PC side of mobilebackup2: answers the phone's file requests against one backup folder until the phone sends its
/// result. Paths come relative to the folder ("{udid}/Manifest.db", "{udid}/ab/ab12…") and never leave it; a first
/// segment can be mapped to another folder (restoring from a dated copy). A file the phone sends is written under a
/// temporary name and moved into place, never written into an existing file, so a hard-linked copy of the folder
/// keeps its old content.
/// </summary>
internal sealed class BackupFolderHost(string root, IReadOnlyDictionary<string, string>? aliases = null)
{
    public const byte CodeSuccess = 0x00;
    public const byte CodeErrorLocal = 0x06;
    public const byte CodeErrorRemote = 0x0B;
    public const byte CodeFileData = 0x0C;

    /// <summary>Bytes per block when sending a file to the phone, as iTunes and libimobiledevice do.</summary>
    public const int SendBlock = 32 * 1024;

    public const string TempSuffix = ".ddpart";

    private readonly string _root = Path.GetFullPath(root);

    public long BytesReceived { get; private set; }
    public long BytesSent { get; private set; }
    public int FilesReceived { get; private set; }
    public int FilesSent { get; private set; }

    /// <summary>The phone's estimate for the whole run, 0–100, from the last message that carried one.</summary>
    public double? Percent { get; private set; }

    /// <summary>When true the phone may only read (restore): writes, moves and deletes are refused.</summary>
    public bool ReadOnly { get; init; }

    /// <summary>Raised after every message and file block, on the loop's thread.</summary>
    public event Action? Progress;

    /// <summary>
    /// Serves requests until the phone sends DLMessageProcessMessage (the operation's result, returned) or disconnects
    /// (an empty dictionary).
    /// </summary>
    public async Task<PlistDict> RunAsync(DeviceLinkChannel dl, CancellationToken ct)
    {
        while (true)
        {
            var message = await dl.ReceiveAsync(ct).ConfigureAwait(false);
            var name = DeviceLinkChannel.NameOf(message);
            TrackPercent(name, message);
            switch (name)
            {
                case "DLMessageDownloadFiles":
                    await SendFilesAsync(dl, message, ct).ConfigureAwait(false);
                    break;
                case "DLMessageUploadFiles":
                    await ReceiveFilesAsync(dl, ct).ConfigureAwait(false);
                    break;
                case "DLMessageGetFreeDiskSpace":
                    await dl.SendStatusResponseAsync(0, null, FreeSpace(), ct).ConfigureAwait(false);
                    break;
                case "DLMessagePurgeDiskSpace":
                    await dl.SendStatusResponseAsync(-1, "Operation not supported", new PlistDict(), ct).ConfigureAwait(false);
                    break;
                case "DLContentsOfDirectory":
                    await dl.SendStatusResponseAsync(0, null, Contents(Arg(message, 1)), ct).ConfigureAwait(false);
                    break;
                case "DLMessageCreateDirectory":
                    await RespondAsync(dl, () => Directory.CreateDirectory(Writable(Arg(message, 1))), ct).ConfigureAwait(false);
                    break;
                case "DLMessageMoveFiles" or "DLMessageMoveItems":
                    await RespondAsync(dl, () => Move(message.Count > 1 ? message[1] as PlistDict : null), ct).ConfigureAwait(false);
                    break;
                case "DLMessageRemoveFiles" or "DLMessageRemoveItems":
                    await RespondAsync(dl, () => Remove(message.Count > 1 ? message[1] as List<object> : null), ct).ConfigureAwait(false);
                    break;
                case "DLMessageCopyItem":
                    await RespondAsync(dl, () => Copy(Arg(message, 1), Arg(message, 2)), ct).ConfigureAwait(false);
                    break;
                case "DLMessageProcessMessage":
                    return message.Count > 1 && message[1] is PlistDict result ? result : new PlistDict();
                case "DLMessageDisconnect":
                    return new PlistDict();
                default:
                    throw new FormatException($"mobilebackup2 sent an unknown message '{name ?? "(none)"}'.");
            }
            Progress?.Invoke();
        }
    }

    // ---- paths ----

    /// <summary>The full path for a path the phone named; throws for one that would leave the folder.</summary>
    public string Resolve(string relative)
    {
        if (string.IsNullOrEmpty(relative) || relative.Contains('\0') || relative.Contains(':') || relative.StartsWith('/') || relative.StartsWith('\\'))
            throw new UnsafePathException(relative);
        var parts = relative.Split('/', '\\');
        if (parts.Any(p => p is ".." or "."))
            throw new UnsafePathException(relative);
        var baseFolder = aliases is not null && aliases.TryGetValue(parts[0], out var mapped) ? mapped : Path.Combine(_root, parts[0]);
        var full = Path.GetFullPath(Path.Combine([baseFolder, .. parts.Skip(1).Where(p => p.Length > 0)]));
        return full;
    }

    private string Writable(string relative) =>
        ReadOnly ? throw new UnauthorizedAccessException("This backup is being restored; it is read-only.") : Resolve(relative);

    private static string Arg(List<object> message, int index) =>
        message.Count > index && message[index] is string s ? s : throw new FormatException("A mobilebackup2 request is missing a path.");

    private void TrackPercent(string? name, List<object> message)
    {
        // Where the phone puts its overall progress depends on the message (libimobiledevice reads the same places).
        var index = name switch
        {
            "DLMessageUploadFiles" => 2,
            "DLMessageDownloadFiles" or "DLMessageMoveFiles" or "DLMessageMoveItems" or "DLMessageRemoveFiles" or "DLMessageRemoveItems" => 3,
            _ => -1,
        };
        if (index > 0 && message.Count > index && message[index] is double percent && percent is >= 0 and <= 100)
            Percent = percent;
    }

    // ---- phone → PC ----

    private async Task ReceiveFilesAsync(DeviceLinkChannel dl, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(256 * 1024);
        long errorCode = 0;
        string? errorText = null;
        try
        {
            while (true)
            {
                var deviceNameLength = await dl.ReadUInt32Async(ct).ConfigureAwait(false);
                if (deviceNameLength == 0)
                    break;
                if (deviceNameLength > 4096)
                    throw new FormatException($"mobilebackup2 sent a device path of {deviceNameLength} bytes.");
                await dl.ReadStringAsync((int)deviceNameLength, ct).ConfigureAwait(false);
                var nameLength = await dl.ReadUInt32Async(ct).ConfigureAwait(false);
                if (nameLength is 0 or > 4096)
                    throw new FormatException($"mobilebackup2 sent a file name of {nameLength} bytes.");
                var relative = await dl.ReadStringAsync((int)nameLength, ct).ConfigureAwait(false);

                string? target = null;
                try
                {
                    target = Writable(relative);
                }
                catch (Exception ex) when (ex is UnsafePathException or UnauthorizedAccessException)
                {
                    (errorCode, errorText) = (-1, ex.Message);
                }

                var (endOfBatch, error) = await ReceiveOneAsync(dl, target, buffer, ct).ConfigureAwait(false);
                if (error is not null)
                    (errorCode, errorText) = error.Value;
                Progress?.Invoke();
                if (endOfBatch)
                    break;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        await dl.SendStatusResponseAsync(errorCode, errorText, new PlistDict(), ct).ConfigureAwait(false);
    }

    /// <summary>One file's blocks. Returns whether the phone ended the whole batch, and a local error to report.</summary>
    private async Task<(bool EndOfBatch, (long, string)? Error)> ReceiveOneAsync(DeviceLinkChannel dl, string? target, byte[] buffer, CancellationToken ct)
    {
        FileStream? file = null;
        var temp = target is null ? null : target + TempSuffix;
        var gotData = false;
        (long, string)? error = null;
        try
        {
            while (true)
            {
                var length = await dl.ReadUInt32Async(ct).ConfigureAwait(false);
                if (length == 0)
                {
                    // The phone ended the batch without a status block; what arrived is the file.
                    await FinishAsync().ConfigureAwait(false);
                    return (true, error);
                }
                var code = await dl.ReadByteAsync(ct).ConfigureAwait(false);
                var remaining = (long)length - 1;
                switch (code)
                {
                    case CodeFileData:
                        if (file is null && temp is not null)
                        {
                            try
                            {
                                Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
                                file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.Asynchronous);
                            }
                            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                            {
                                error = (ErrnoFor(ex), ex.Message);
                                temp = null;
                            }
                        }
                        while (remaining > 0)
                        {
                            var chunk = (int)Math.Min(remaining, buffer.Length);
                            await dl.ReadExactlyAsync(buffer.AsMemory(0, chunk), ct).ConfigureAwait(false);
                            if (file is not null)
                                await file.WriteAsync(buffer.AsMemory(0, chunk), ct).ConfigureAwait(false);
                            remaining -= chunk;
                            BytesReceived += chunk;
                        }
                        gotData = true;
                        Progress?.Invoke();
                        break;
                    case CodeSuccess:
                        await SkipAsync(dl, remaining, buffer, ct).ConfigureAwait(false);
                        await FinishAsync().ConfigureAwait(false);
                        return (false, error);
                    case CodeErrorRemote:
                        // After data this is just the end marker; on its own it means the phone couldn't read the file.
                        await SkipAsync(dl, remaining, buffer, ct).ConfigureAwait(false);
                        if (gotData)
                            await FinishAsync().ConfigureAwait(false);
                        else
                            await DiscardAsync().ConfigureAwait(false);
                        return (false, error);
                    default:
                        await SkipAsync(dl, remaining, buffer, ct).ConfigureAwait(false);
                        break;
                }
            }
        }
        catch
        {
            await DiscardAsync().ConfigureAwait(false);
            throw;
        }

        async Task FinishAsync()
        {
            if (temp is null || target is null)
                return;
            try
            {
                if (file is not null)
                {
                    await file.DisposeAsync().ConfigureAwait(false);
                    file = null;
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
                    await File.WriteAllBytesAsync(temp, [], ct).ConfigureAwait(false);
                }
                File.Move(temp, target, overwrite: true);
                FilesReceived++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error = (ErrnoFor(ex), ex.Message);
                await DiscardAsync().ConfigureAwait(false);
            }
        }

        async Task DiscardAsync()
        {
            if (file is not null)
            {
                await file.DisposeAsync().ConfigureAwait(false);
                file = null;
            }
            if (temp is not null)
                TryDelete(temp);
        }
    }

    private static async Task SkipAsync(DeviceLinkChannel dl, long count, byte[] buffer, CancellationToken ct)
    {
        while (count > 0)
        {
            var chunk = (int)Math.Min(count, buffer.Length);
            await dl.ReadExactlyAsync(buffer.AsMemory(0, chunk), ct).ConfigureAwait(false);
            count -= chunk;
        }
    }

    // ---- PC → phone ----

    private async Task SendFilesAsync(DeviceLinkChannel dl, List<object> message, CancellationToken ct)
    {
        var paths = message.Count > 1 && message[1] is List<object> list ? list.OfType<string>().ToList() : [];
        var errors = new PlistDict();
        var frame = ArrayPool<byte>.Shared.Rent(5 + SendBlock);
        try
        {
            foreach (var relative in paths)
            {
                var name = System.Text.Encoding.UTF8.GetBytes(relative);
                BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)name.Length);
                await dl.WriteAsync(frame.AsMemory(0, 4), ct).ConfigureAwait(false);
                await dl.WriteAsync(name, ct).ConfigureAwait(false);

                string? failure = null;
                long errno = 0;
                try
                {
                    var path = Resolve(relative);
                    if (!File.Exists(path))
                    {
                        (errno, failure) = (-6, "No such file or directory");
                    }
                    else
                    {
                        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
                        while (true)
                        {
                            var read = await file.ReadAsync(frame.AsMemory(5, SendBlock), ct).ConfigureAwait(false);
                            if (read == 0)
                                break;
                            BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)read + 1);
                            frame[4] = CodeFileData;
                            await dl.WriteAsync(frame.AsMemory(0, 5 + read), ct).ConfigureAwait(false);
                            BytesSent += read;
                            Progress?.Invoke();
                        }
                        FilesSent++;
                    }
                }
                catch (Exception ex) when (ex is UnsafePathException or IOException or UnauthorizedAccessException)
                {
                    (errno, failure) = (ErrnoFor(ex), ex.Message);
                }

                if (failure is null)
                {
                    BinaryPrimitives.WriteUInt32BigEndian(frame, 1);
                    frame[4] = CodeSuccess;
                    await dl.WriteAsync(frame.AsMemory(0, 5), ct).ConfigureAwait(false);
                }
                else
                {
                    errors[relative] = new PlistDict { ["DLFileErrorString"] = failure, ["DLFileErrorCode"] = errno };
                    var text = System.Text.Encoding.UTF8.GetBytes(failure);
                    BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)text.Length + 1);
                    frame[4] = CodeErrorLocal;
                    await dl.WriteAsync(frame.AsMemory(0, 5), ct).ConfigureAwait(false);
                    await dl.WriteAsync(text, ct).ConfigureAwait(false);
                }
                Progress?.Invoke();
            }
            BinaryPrimitives.WriteUInt32BigEndian(frame, 0);
            await dl.WriteAsync(frame.AsMemory(0, 4), ct).ConfigureAwait(false);
            await dl.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(frame);
        }

        if (errors.Count == 0)
            await dl.SendStatusResponseAsync(0, null, new PlistDict(), ct).ConfigureAwait(false);
        else
            await dl.SendStatusResponseAsync(-13, "Multi status", errors, ct).ConfigureAwait(false);
    }

    // ---- folder requests ----

    private object FreeSpace()
    {
        try
        {
            return new DriveInfo(Path.GetPathRoot(_root)!).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return 0L;
        }
    }

    private PlistDict Contents(string relative)
    {
        var result = new PlistDict();
        string path;
        try
        {
            path = Resolve(relative);
        }
        catch (UnsafePathException)
        {
            return result;
        }
        if (!Directory.Exists(path))
            return result;
        foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos())
        {
            if (entry.Name.EndsWith(TempSuffix, StringComparison.Ordinal))
                continue;
            var isDirectory = (entry.Attributes & FileAttributes.Directory) != 0;
            result[entry.Name] = new PlistDict
            {
                ["DLFileType"] = isDirectory ? "DLFileTypeDirectory" : "DLFileTypeRegular",
                ["DLFileSize"] = isDirectory ? 0L : ((FileInfo)entry).Length,
                ["DLFileModificationDate"] = new DateTimeOffset(entry.LastWriteTimeUtc, TimeSpan.Zero),
            };
        }
        return result;
    }

    private void Move(PlistDict? moves)
    {
        if (moves is null)
            throw new FormatException("A mobilebackup2 move has no list of files.");
        Exception? last = null;
        foreach (var (from, value) in moves)
        {
            if (value is not string to)
                continue;
            try
            {
                var source = Writable(from);
                var target = Writable(to);
                if (Directory.Exists(target))
                    Directory.Delete(target, recursive: true);
                else if (File.Exists(target) && File.Exists(source))
                    File.Delete(target);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (Directory.Exists(source))
                    Directory.Move(source, target);
                else
                    File.Move(source, target, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or UnsafePathException)
            {
                last = ex;
            }
        }
        if (last is not null)
            throw last;
    }

    private void Remove(List<object>? paths)
    {
        if (paths is null)
            throw new FormatException("A mobilebackup2 remove has no list of files.");
        Exception? last = null;
        foreach (var relative in paths.OfType<string>())
        {
            try
            {
                var path = Writable(relative);
                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
                else if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or UnsafePathException)
            {
                last = ex;
            }
        }
        if (last is not null)
            throw last;
    }

    private void Copy(string from, string to)
    {
        var source = Resolve(from);
        var target = Writable(to);
        if (Directory.Exists(source))
        {
            CopyDirectory(source, target);
            return;
        }
        if (!File.Exists(source))
            throw new FileNotFoundException("No such file or directory", from);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(source, target + TempSuffix, overwrite: true);
        File.Move(target + TempSuffix, target, overwrite: true);
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            var to = Path.Combine(target, Path.GetFileName(file));
            File.Copy(file, to + TempSuffix, overwrite: true);
            File.Move(to + TempSuffix, to, overwrite: true);
        }
        foreach (var directory in Directory.EnumerateDirectories(source))
            CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)));
    }

    private static async Task RespondAsync(DeviceLinkChannel dl, Action action, CancellationToken ct)
    {
        long code = 0;
        string? text = null;
        try
        {
            action();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or UnsafePathException)
        {
            (code, text) = (ErrnoFor(ex), ex.Message);
        }
        await dl.SendStatusResponseAsync(code, text, new PlistDict(), ct).ConfigureAwait(false);
    }

    /// <summary>The negative errno-style codes the phone expects (libimobiledevice's errno_to_device_error).</summary>
    private static long ErrnoFor(Exception ex) => ex switch
    {
        FileNotFoundException or DirectoryNotFoundException => -6,
        IOException io when (io.HResult & 0xFFFF) is 0x50 or 0xB7 => -7,    // file exists
        IOException io when (io.HResult & 0xFFFF) is 0x70 or 0x27 => -15,   // disk full
        UnauthorizedAccessException => -1,
        _ => -1,
    };

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>The phone named a path that would leave the backup folder.</summary>
internal sealed class UnsafePathException(string path) : Exception($"'{path}' is not a path inside the backup.");
