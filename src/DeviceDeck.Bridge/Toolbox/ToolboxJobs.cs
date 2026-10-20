using System.Collections.Concurrent;
using System.Diagnostics;
using DeviceDeck.Core.Toolbox;

namespace DeviceDeck.Bridge.Toolbox;

/// <summary>
/// Jobs that run on the PC only (HEIC → JPEG). They don't need a phone, so they live here rather than in the
/// transfer queue; progress goes to the page as toolbox.job, at most ten times a second plus the final state.
/// </summary>
internal sealed class ToolboxJobs
{
    private const int KeepOutputs = 20;
    private const int KeepErrors = 20;
    private static readonly TimeSpan PublishEvery = TimeSpan.FromMilliseconds(100);

    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new(StringComparer.Ordinal);

    public int Running => _running.Count;

    public ToolboxJob StartHeic(IReadOnlyList<HeicItem> items, IHeicConverter converter, int quality, Action<string, object> publish)
    {
        var id = "heic-" + Guid.NewGuid().ToString("N")[..10];
        var cts = new CancellationTokenSource();
        _running[id] = cts;
        var gate = new object();
        var done = 0;
        var failed = 0;
        string? current = null;
        var outputs = new List<string>();
        var errors = new List<string>();
        var lastPublish = Stopwatch.StartNew();

        ToolboxJob Snapshot(ToolboxJobState state)
        {
            lock (gate)
            {
                return new ToolboxJob(id, "heic", state, items.Count, done, failed, current, outputs.ToArray(), errors.ToArray());
            }
        }

        void Progress(bool force)
        {
            lock (gate)
            {
                if (!force && lastPublish.Elapsed < PublishEvery)
                {
                    return;
                }

                lastPublish.Restart();
            }

            publish("toolbox.job", Snapshot(ToolboxJobState.Running));
        }

        var first = Snapshot(ToolboxJobState.Running);
        _ = Task.Run(async () =>
        {
            var cancelled = false;
            try
            {
                var parallel = new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 3), CancellationToken = cts.Token };
                await Parallel.ForEachAsync(items, parallel, async (item, ct) =>
                {
                    lock (gate)
                    {
                        current = Path.GetFileName(item.Source);
                    }

                    Progress(force: false);
                    var partial = item.Destination + ".ddpart";
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(item.Destination)!);
                        await converter.ConvertAsync(item.Source, partial, quality, ct).ConfigureAwait(false);
                        File.Move(partial, item.Destination, overwrite: false);
                        lock (gate)
                        {
                            done++;
                            if (outputs.Count < KeepOutputs)
                            {
                                outputs.Add(item.Destination);
                            }
                        }
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        TryDelete(partial);
                        throw;
                    }
                    catch (Exception ex)
                    {
                        TryDelete(partial);
                        lock (gate)
                        {
                            failed++;
                            if (errors.Count < KeepErrors)
                            {
                                errors.Add($"{Path.GetFileName(item.Source)}: {ex.Message}");
                            }
                        }
                    }

                    Progress(force: false);
                }).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }
            finally
            {
                _running.TryRemove(id, out _);
                cts.Dispose();
            }

            lock (gate)
            {
                current = null;
            }

            publish("toolbox.job", Snapshot(cancelled ? ToolboxJobState.Cancelled : done == 0 && failed > 0 ? ToolboxJobState.Failed : ToolboxJobState.Done));
        });
        return first;
    }

    public bool Cancel(string jobId)
    {
        if (!_running.TryGetValue(jobId, out var cts))
        {
            return false;
        }

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        return true;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
