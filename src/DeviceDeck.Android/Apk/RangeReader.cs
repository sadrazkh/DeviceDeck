using DeviceDeck.Android.Adb;
using DeviceDeck.Android.Parsing;

namespace DeviceDeck.Android.Apk;

/// <summary>Random access to a file's bytes; lets us read a few entries of an APK on the phone without pulling it.</summary>
internal interface IRangeReader
{
    long Length { get; }

    Task<byte[]> ReadAsync(long offset, int count, CancellationToken ct);
}

internal sealed class ByteArrayRangeReader(byte[] data) : IRangeReader
{
    public long Length => data.Length;

    public Task<byte[]> ReadAsync(long offset, int count, CancellationToken ct)
    {
        if (offset < 0 || offset > data.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }
        int available = (int)Math.Min(count, data.Length - offset);
        return Task.FromResult(data.AsSpan((int)offset, available).ToArray());
    }
}

/// <summary>A local file (an APK the user is about to install).</summary>
internal sealed class FileRangeReader(string path) : IRangeReader
{
    public long Length { get; } = new FileInfo(path).Length;

    public async Task<byte[]> ReadAsync(long offset, int count, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, useAsync: true);
        stream.Seek(offset, SeekOrigin.Begin);
        byte[] buffer = new byte[(int)Math.Min(count, Length - offset)];
        await stream.ReadExactlyAsync(buffer, ct).ConfigureAwait(false);
        return buffer;
    }
}

/// <summary>
/// A file on the phone read in ranges with toybox dd (block-aligned, then trimmed). APKs are world-readable,
/// so the shell can read /data/app/.../base.apk without root.
/// </summary>
internal sealed class RemoteFileRangeReader(IAdbChannel channel, string path, long length) : IRangeReader
{
    private const int BlockSize = 4096;

    public long Length { get; } = length;

    public static async Task<RemoteFileRangeReader> OpenAsync(IAdbChannel channel, string path, CancellationToken ct)
    {
        string output = await channel.ShellCheckedAsync($"stat -L -c %s {ShellQuote.Quote(path)}", ct).ConfigureAwait(false);
        if (!long.TryParse(output.Trim(), out long length) || length <= 0)
        {
            throw new InvalidDataException($"Couldn't read the size of {path}.");
        }
        return new RemoteFileRangeReader(channel, path, length);
    }

    public static string DdCommand(string path, long offset, int count, out long blockStart)
    {
        long firstBlock = offset / BlockSize;
        long lastBlock = (offset + Math.Max(count, 1) - 1) / BlockSize;
        blockStart = firstBlock * BlockSize;
        return $"dd if={ShellQuote.Quote(path)} bs={BlockSize} skip={firstBlock} count={lastBlock - firstBlock + 1} 2>/dev/null";
    }

    public async Task<byte[]> ReadAsync(long offset, int count, CancellationToken ct)
    {
        count = (int)Math.Min(count, Length - offset);
        if (count <= 0)
        {
            return [];
        }
        string command = DdCommand(path, offset, count, out long blockStart);
        using var buffer = new MemoryStream();
        var result = await channel.ShellToStreamAsync(command, buffer, ct).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw AdbErrors.FromShell("dd", result, path);
        }
        int skip = (int)(offset - blockStart);
        if (buffer.Length < skip + count)
        {
            throw new InvalidDataException($"{path} ended early while reading it.");
        }
        return buffer.GetBuffer().AsSpan(skip, count).ToArray();
    }
}
