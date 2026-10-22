using System.Diagnostics;
using System.Globalization;

namespace DeviceDeck.Ios.Wpd;

// v0.4.1 (deletejob): reading Windows' view of the camera roll fast enough for a delete. On the owner's iPhone 16
// (iOS 27) every object read took about 50 ms, so walking 3,258 objects one by one took 160 s before the first delete.
// Now: objects are read in bulk where the driver can (one by one where it can't), only the folders that can hold the
// selected items are walked (a full walk when a match is missing), and the listing is kept for a few minutes per phone.

/// <summary>How a read of the camera roll went (for the delete log and the probe).</summary>
public sealed class WpdReadStats
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    /// <summary>Objects whose properties came from a bulk request.</summary>
    public int Bulk { get; set; }

    /// <summary>Objects read one by one (no bulk, or left out of a bulk answer).</summary>
    public int OneByOne { get; set; }

    public int BulkRequests { get; set; }

    /// <summary>The driver said it doesn't do bulk reads.</summary>
    public bool NoBulk { get; set; }

    /// <summary>Camera-roll folders Windows lists.</summary>
    public int FoldersInRoll { get; set; }

    public int FoldersRead { get; set; }
    public int FoldersFromCache { get; set; }

    /// <summary>Every folder had to be walked, fresh: a match was missing from the folders walked first, or the listing kept was out of date.</summary>
    public string? FullWalkBecause { get; set; }

    /// <summary>Objects kept from an earlier read, read again before anything was deleted.</summary>
    public int Verified { get; set; }

    public TimeSpan Elapsed => _clock.Elapsed;

    public override string ToString() =>
        FormattableString.Invariant($"{FoldersRead} of {FoldersInRoll} folders read, {FoldersFromCache} kept from earlier; {Bulk} objects in {BulkRequests} bulk requests, {OneByOne} one by one")
        + (NoBulk ? " (no bulk reads)" : "")
        + (Verified > 0 ? FormattableString.Invariant($"; {Verified} kept objects checked again") : "")
        + (FullWalkBecause is { } why ? "; full walk: " + why : "")
        + FormattableString.Invariant($"; {Elapsed.TotalSeconds:0.0} s");
}

/// <summary>Reads objects' properties: in bulk when the device does it, else one by one.</summary>
public static class WpdObjectReader
{
    /// <summary>Objects per bulk request: small enough for a steady bar and a quick cancel.</summary>
    public const int BulkChunk = 200;

    /// <summary>
    /// The objects that exist, in no particular order; an id Windows no longer knows is left out. <paramref name="progress"/>
    /// is told how many of <paramref name="ids"/> have been read so far.
    /// </summary>
    public static List<WpdObject> Read(IWpdConnection c, IReadOnlyList<string> ids, WpdReadStats stats, Action<int>? progress, CancellationToken ct)
    {
        var read = new List<WpdObject>(ids.Count);
        for (var start = 0; start < ids.Count; start += BulkChunk)
        {
            ct.ThrowIfCancellationRequested();
            var chunk = ids.Skip(start).Take(BulkChunk).ToList();
            IReadOnlyList<WpdObject>? answered = null;
            if (!stats.NoBulk)
            {
                answered = c.GetMany(chunk, n => progress?.Invoke(start + Math.Min(n, chunk.Count)), ct);
                if (answered is null)
                {
                    stats.NoBulk = true;
                }
                else
                {
                    stats.BulkRequests++;
                }
            }

            var wanted = chunk.ToHashSet(StringComparer.Ordinal);
            var got = new HashSet<string>(StringComparer.Ordinal);
            foreach (var o in answered ?? [])
            {
                // Only what was asked for, once each.
                if (wanted.Contains(o.Id) && got.Add(o.Id))
                {
                    read.Add(o);
                    stats.Bulk++;
                }
            }

            var done = got.Count;
            foreach (var id in chunk.Where(id => !got.Contains(id)))
            {
                ct.ThrowIfCancellationRequested();
                stats.OneByOne++;
                if (c.Get(id) is { } o)
                {
                    read.Add(o);
                }

                progress?.Invoke(start + ++done);
            }

            progress?.Invoke(start + chunk.Count);
        }

        return read;
    }
}

/// <summary>
/// Internal Storage › DCIM as far as it has been read on one phone: the storage, every camera-roll folder, and the files
/// of the folders read so far. Object ids stay valid while the phone stays connected (Windows' driver keeps one session
/// per device across connections); every object is still read again just before it is deleted.
/// </summary>
public sealed class WpdListing
{
    private readonly Dictionary<string, List<WpdObject>> _files = new(StringComparer.Ordinal);
    private readonly HashSet<string> _fresh = new(StringComparer.Ordinal);

    public WpdListing(string pnpId, string storageId, string? storageName, string dcimId, int storageCount, IReadOnlyList<WpdObject> folders, DateTimeOffset readAt)
    {
        PnpId = pnpId;
        StorageId = storageId;
        StorageName = storageName;
        DcimId = dcimId;
        StorageCount = storageCount;
        Folders = folders;
        ReadAt = readAt;
    }

    public string PnpId { get; }
    public string StorageId { get; }
    public string? StorageName { get; }
    public string DcimId { get; }
    public int StorageCount { get; }

    /// <summary>Every folder under DCIM (or straight under the storage), read or not.</summary>
    public IReadOnlyList<WpdObject> Folders { get; }

    public DateTimeOffset ReadAt { get; }

    public bool Has(string folderId) => _files.ContainsKey(folderId);

    public bool HasAll => Folders.All(f => _files.ContainsKey(f.Id));

    /// <summary>Read on the connection in use now (not kept from an earlier one).</summary>
    public bool IsFresh(string folderId) => _fresh.Contains(folderId);

    public int FileCount => _files.Values.Sum(f => f.Count);

    public void Set(string folderId, List<WpdObject> files, bool fresh)
    {
        _files[folderId] = files;
        if (fresh)
        {
            _fresh.Add(folderId);
        }
        else
        {
            _fresh.Remove(folderId);
        }
    }

    /// <summary>Marks everything as kept from an earlier connection (the next delete reads what it uses again).</summary>
    public void Age() => _fresh.Clear();

    /// <summary>Drops objects Windows no longer has (deleted ones).</summary>
    public void Forget(IReadOnlySet<string> objectIds)
    {
        foreach (var files in _files.Values)
        {
            files.RemoveAll(o => objectIds.Contains(o.Id));
        }
    }

    /// <summary>The folders read so far, as the map takes them.</summary>
    public WpdDcim ToDcim() => new(StorageId, StorageName, DcimId,
        Folders.Where(f => _files.ContainsKey(f.Id)).Select(f => new WpdFolder(f.Id, f.FileName ?? "", _files[f.Id])).ToList(), StorageCount);
}

/// <summary>
/// The last listing of one phone's camera roll, for <see cref="Lifetime"/> (5 minutes by default). Dropped when the phone
/// goes away or locks, when the listing turns out out of date, and after a delete that didn't go cleanly; after a clean
/// delete only the deleted objects are taken out.
/// </summary>
public sealed class WpdListingCache(TimeProvider? time = null, TimeSpan? lifetime = null)
{
    private readonly Lock _gate = new();
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private WpdListing? _listing;
    private long _generation;

    public TimeSpan Lifetime { get; } = lifetime ?? TimeSpan.FromMinutes(5);

    public DateTimeOffset Now => _time.GetUtcNow();

    /// <summary>The kept listing for this device, if it is recent; <paramref name="generation"/> goes back to <see cref="Keep"/>.</summary>
    public WpdListing? Take(string pnpId, out long generation)
    {
        lock (_gate)
        {
            generation = _generation;
            if (_listing is { } kept && (kept.PnpId != pnpId || _time.GetUtcNow() - kept.ReadAt > Lifetime))
            {
                _listing = null;
            }

            return _listing;
        }
    }

    /// <summary>Keeps a listing, unless the cache was dropped since <see cref="Take"/> (the phone went away meanwhile).</summary>
    public bool Keep(WpdListing listing, long generation)
    {
        lock (_gate)
        {
            if (generation != _generation)
            {
                return false;
            }

            listing.Age();
            _listing = listing;
            return true;
        }
    }

    public void Invalidate()
    {
        lock (_gate)
        {
            _generation++;
            _listing = null;
        }
    }

    /// <summary>Takes deleted objects out of the kept listing.</summary>
    public void Forget(IReadOnlySet<string> objectIds)
    {
        lock (_gate)
        {
            _listing?.Forget(objectIds);
        }
    }

    public bool Holds
    {
        get
        {
            lock (_gate)
            {
                return _listing is not null;
            }
        }
    }
}

/// <summary>
/// Reads the camera roll for a delete: the storage and its folders (kept or fresh), then the files of the folders asked
/// for. The total to read is known before the first object is read, so progress is "N of M".
/// </summary>
public sealed class WpdRollReader(IWpdConnection c, WpdReadStats stats, Action<int, int>? progress, CancellationToken ct)
{
    private int _done;
    private int _total;

    /// <summary>
    /// The storage, DCIM and its folders: <paramref name="kept"/> when given (its folders are still listed), else read now.
    /// Null, with why, when Windows shows no camera roll.
    /// </summary>
    public WpdListing? Structure(WpdListing? kept, string pnpId, DateTimeOffset now, out WpdDcimWalker.Missing missing)
    {
        if (kept is not null)
        {
            missing = WpdDcimWalker.Missing.None;
            return kept;
        }

        if (WpdDcimWalker.FindDcim(c, ct, out missing) is not { } found)
        {
            return null;
        }

        var ids = c.ChildIds(found.DcimId, ct);
        var order = Order(ids);
        var folders = WpdObjectReader.Read(c, ids, stats, null, ct).Where(o => o.Kind == WpdObjectKind.Folder)
            .OrderBy(o => order[o.Id]).ToList();
        return new WpdListing(pnpId, found.StorageId, found.StorageName, found.DcimId, found.StorageCount, folders, now);
    }

    /// <summary>Reads the files of these folders, unless the listing has them already (kept or read earlier on this connection).</summary>
    public void Load(WpdListing listing, IEnumerable<WpdObject> folders, bool fresh = false)
    {
        var todo = new List<(WpdObject Folder, IReadOnlyList<string> Ids)>();
        foreach (var folder in folders.DistinctBy(f => f.Id))
        {
            if (listing.Has(folder.Id) && (!fresh || listing.IsFresh(folder.Id)))
            {
                stats.FoldersFromCache += listing.IsFresh(folder.Id) ? 0 : 1;
                continue;
            }

            ct.ThrowIfCancellationRequested();
            todo.Add((folder, c.ChildIds(folder.Id, ct)));
        }

        _total += todo.Sum(t => t.Ids.Count);
        Report();
        foreach (var (folder, ids) in todo)
        {
            var start = _done;
            var objects = WpdObjectReader.Read(c, ids, stats, n =>
            {
                _done = start + n;
                Report();
            }, ct);
            _done = start + ids.Count;
            Report();
            // Files only, in Windows' order; sub-folders are never part of the camera roll's files.
            var order = Order(ids);
            listing.Set(folder.Id, objects.Where(o => o.Kind == WpdObjectKind.File).OrderBy(o => order[o.Id]).ToList(), fresh: true);
            stats.FoldersRead++;
        }
    }

    /// <summary>Reads these objects again (kept ones a delete is about to use); objects Windows no longer has are left out.</summary>
    public IReadOnlyList<WpdObject> ReadAgain(IReadOnlyList<string> ids)
    {
        _total += ids.Count;
        var start = _done;
        var objects = WpdObjectReader.Read(c, ids, stats, n =>
        {
            _done = start + n;
            Report();
        }, ct);
        _done = start + ids.Count;
        stats.Verified += ids.Count;
        Report();
        return objects;
    }

    private void Report() => progress?.Invoke(Math.Min(_done, _total), _total);

    private static Dictionary<string, int> Order(IReadOnlyList<string> ids)
    {
        var order = new Dictionary<string, int>(ids.Count, StringComparer.Ordinal);
        for (var i = 0; i < ids.Count; i++)
        {
            order.TryAdd(ids[i], i);
        }

        return order;
    }
}

/// <summary>Which camera-roll folders can hold a picture: the month folders recent iOS shows the PC, against when it was taken.</summary>
public static class WpdMonths
{
    /// <summary>How far a phone's clock can be from UTC (UTC−12 … UTC+14), so a picture's month is the same in every zone or one of two.</summary>
    public static readonly TimeSpan AnyZone = TimeSpan.FromHours(14);

    /// <summary>The month a folder is named after: "202610_a" (iOS 26 and 27), "2021__06"; null for any other name (100APPLE).</summary>
    public static (int Year, int Month)? MonthOf(string? folderName)
    {
        if (folderName is not { Length: 8 } n || !n.Take(4).All(char.IsAsciiDigit))
        {
            return null;
        }

        var year = int.Parse(n[..4], CultureInfo.InvariantCulture);
        string month;
        if (char.IsAsciiDigit(n[4]) && char.IsAsciiDigit(n[5]) && n[6] == '_' && char.IsAsciiLetter(n[7]))
        {
            month = n[4..6];
        }
        else if (n[4] == '_' && n[5] == '_' && char.IsAsciiDigit(n[6]) && char.IsAsciiDigit(n[7]))
        {
            month = n[6..8];
        }
        else
        {
            return null;
        }

        var m = int.Parse(month, CultureInfo.InvariantCulture);
        return m is >= 1 and <= 12 && year >= 1990 ? (year, m) : null;
    }

    /// <summary>The months a picture taken at this instant may be filed under, whatever zone the phone was in: one, or two at a month's edge.</summary>
    public static IEnumerable<(int Year, int Month)> MonthsFor(DateTimeOffset taken)
    {
        var utc = taken.UtcDateTime;
        var early = utc - AnyZone;
        var late = utc + AnyZone;
        yield return (early.Year, early.Month);
        if ((late.Year, late.Month) != (early.Year, early.Month))
        {
            yield return (late.Year, late.Month);
        }
    }
}
