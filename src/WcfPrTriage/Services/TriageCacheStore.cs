using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using WcfPrTriage.Models;

namespace WcfPrTriage.Services;

/// <summary>One persisted deep-triage record for a single pull request.</summary>
public sealed class TriageCacheEntry
{
    public string Owner { get; set; } = "";
    public string Repo { get; set; } = "";
    public int Number { get; set; }

    /// <summary>Head commit the result was produced for — a new commit invalidates the entry.</summary>
    public string HeadSha { get; set; } = "";

    /// <summary>Freshness signature (head SHA + CI build set + states); a change forces a re-triage.</summary>
    public string? Signature { get; set; }

    public DateTimeOffset LastCheckedUtc { get; set; }
    public DateTimeOffset LastTriagedUtc { get; set; }
    public CiState State { get; set; }

    /// <summary>The full triage result (failure tree). Immutable, so it is safe to share across threads.</summary>
    public PrTriageResult? Result { get; set; }
}

/// <summary>
/// Persists deep triage results under <c>%APPDATA%\WcfPrTriage\triage-cache.json</c> so a fresh app
/// start can reuse prior results instead of re-pulling Azure DevOps + Helix. Entries are keyed by
/// owner/repo/PR and carry the head-SHA + build signature, so a stale entry (new commit or a re-run
/// that changed the build set) is ignored and the PR is re-triaged automatically.
///
/// The store is best-effort: any read/write failure is swallowed so a bad cache never breaks the app.
/// </summary>
public sealed class TriageCacheStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Hard cap on stored records so the file can't grow without bound.</summary>
    private const int MaxEntries = 400;

    /// <summary>Records older than this (since last checked/triaged) are pruned on save.</summary>
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(14);

    private readonly string _dir;
    private readonly string _path;
    private readonly SemaphoreSlim _ioGate = new(1, 1);

    public TriageCacheStore()
    {
        _dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WcfPrTriage");
        _path = Path.Combine(_dir, "triage-cache.json");
    }

    public string FilePath => _path;

    private sealed class CacheFile
    {
        public int Version { get; set; } = 1;
        public List<TriageCacheEntry> Entries { get; set; } = new();
    }

    /// <summary>Loads all cached entries for the given owner/repo, keyed by PR number. Never throws.</summary>
    public Dictionary<int, TriageCacheEntry> Load(string owner, string repo)
    {
        var map = new Dictionary<int, TriageCacheEntry>();
        try
        {
            if (!File.Exists(_path))
                return map;

            var file = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(_path), JsonOptions);
            if (file?.Entries is null)
                return map;

            foreach (var e in file.Entries)
            {
                if (e.Result is not null
                    && string.Equals(e.Owner, owner, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(e.Repo, repo, StringComparison.OrdinalIgnoreCase))
                {
                    map[e.Number] = e;
                }
            }
        }
        catch { /* ignore a corrupt/unreadable cache */ }
        return map;
    }

    /// <summary>
    /// Makes the on-disk cache authoritative for a single owner/repo: the stored entries for that
    /// repo become exactly <paramref name="keep"/> (so PRs that were closed/merged, or whose failures
    /// have cleared, are dropped), while other repos' entries are left intact. Age/size pruning is
    /// still applied. File I/O runs off the calling thread and an unchanged file is not rewritten, so
    /// this is cheap to call on every refresh. Never throws — a failed write just means the next run
    /// re-triages.
    /// </summary>
    public async Task SyncRepoAsync(string owner, string repo, IReadOnlyList<TriageCacheEntry> keep)
    {
        await _ioGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                CacheFile file = new();
                try
                {
                    if (File.Exists(_path))
                        file = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(_path), JsonOptions) ?? new();
                }
                catch { file = new(); }

                static bool IsRepo(TriageCacheEntry e, string o, string r) =>
                    string.Equals(e.Owner, o, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(e.Repo, r, StringComparison.OrdinalIgnoreCase);

                // Trim this repo's fresh entries here (off the UI thread) rather than in the caller:
                // TrimForStorage deep-clones the record graph, which is non-trivial for large PRs.
                foreach (var e in keep)
                {
                    if (e.Result is not null)
                        e.Result = TrimForStorage(e.Result);
                }

                // Authoritative replace for this repo: keep other repos' entries, swap in `keep`.
                var merged = file.Entries.Where(e => !IsRepo(e, owner, repo)).ToList();
                merged.AddRange(keep.Where(e => e.Result is not null));

                var cutoff = DateTimeOffset.UtcNow - MaxAge;
                var final = merged
                    .Where(e => e.Result is not null
                                && (e.LastTriagedUtc >= cutoff || e.LastCheckedUtc >= cutoff))
                    .OrderByDescending(e => e.LastTriagedUtc)
                    .Take(MaxEntries)
                    .ToList();

                // Don't re-serialize a multi-MB payload when nothing meaningful changed.
                if (Fingerprint(final) == Fingerprint(file.Entries))
                    return;

                file.Entries = final;
                Directory.CreateDirectory(_dir);
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(file, JsonOptions));
                File.Move(tmp, _path, overwrite: true);
            }).ConfigureAwait(false);
        }
        catch
        {
            /* best-effort — a failed write just means the next run re-triages this PR */
        }
        finally { _ioGate.Release(); }
    }

    /// <summary>A cheap identity of the stored set — changes when any PR entry is added, dropped, or
    /// its freshness signature (head SHA + build states) changes. Also includes a per-entry day bucket
    /// of the last-checked time so that a plain re-check on a later day still rewrites the file, keeping
    /// the persisted <see cref="TriageCacheEntry.LastCheckedUtc"/>/<see cref="TriageCacheEntry.LastTriagedUtc"/>
    /// fresh — otherwise a long-lived, signature-stable PR could be age-pruned on stale on-disk times.</summary>
    private static string Fingerprint(IEnumerable<TriageCacheEntry> entries) =>
        string.Join("\n", entries
            .Select(e => $"{e.Owner}/{e.Repo}#{e.Number}@{e.Signature}~{DayBucket(e.LastCheckedUtc)}")
            .OrderBy(s => s, StringComparer.Ordinal));

    /// <summary>Whole-day bucket (UTC) used to bound how stale persisted timestamps may become.</summary>
    private static long DayBucket(DateTimeOffset t) => t.UtcTicks / TimeSpan.TicksPerDay;

    /// <summary>
    /// Produces a storage-optimized clone of a triage result that keeps everything the detail pane can
    /// actually display but drops redundant raw log text — the dominant cost for large PRs, where the
    /// stored file can otherwise reach tens of MB:
    /// <list type="bullet">
    ///   <item>A failed test's full raw console block (<see cref="TestFailure.RawBlock"/>) is dropped
    ///   whenever a parsed message or stack trace is present. The raw block is a superset of those two
    ///   and is only ever shown as a fallback when both are empty (see BuildTestDetail).</item>
    ///   <item>A work item's console tail (<see cref="HelixWorkItemFailure.ConsoleTail"/>) is dropped
    ///   whenever it has parsed test failures. The tail is only shown for crashed/timed-out items with
    ///   no parsed tests (see BuildWorkItemDetail).</item>
    /// </list>
    /// The transform is display-lossless, and it leaves the freshness <see cref="TriageCacheEntry.Signature"/>
    /// untouched, so a hydrated trimmed result is not spuriously re-triaged.
    /// </summary>
    public static PrTriageResult TrimForStorage(PrTriageResult result) =>
        result with
        {
            Builds = result.Builds.Select(b => b with
            {
                Queues = b.Queues.Select(q => q with
                {
                    FailedWorkItems = q.FailedWorkItems.Select(wi => wi with
                    {
                        ConsoleTail = wi.FailedTests.Count > 0 ? string.Empty : wi.ConsoleTail,
                        FailedTests = wi.FailedTests.Select(t =>
                            string.IsNullOrWhiteSpace(t.Message) && string.IsNullOrWhiteSpace(t.StackTrace)
                                ? t
                                : t with { RawBlock = string.Empty }).ToList(),
                    }).ToList(),
                }).ToList(),
            }).ToList(),
        };
}
