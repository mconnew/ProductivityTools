using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using WcfPrTriage.Models;

namespace WcfPrTriage.Services;

/// <summary>
/// Orchestrates the full drill-down for a PR: GitHub checks → failed Azure DevOps builds →
/// timeline task logs (Helix job ids + friendly queue names) → Helix work items → console logs →
/// parsed per-test failures. Produces a <see cref="PrTriageResult"/> the UI renders as a tree.
/// </summary>
public sealed partial class TriageService
{
    private readonly GitHubService _github;
    private readonly AzureDevOpsService _azdo;
    private readonly HelixService _helix;

    public TriageService(GitHubService github, AzureDevOpsService azdo, HelixService helix)
    {
        _github = github;
        _azdo = azdo;
        _helix = helix;
    }

    [GeneratedRegex(@"helix\.dot\.net/api/jobs/(?<id>[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})")]
    private static partial Regex HelixJobUrlRegex();

    [GeneratedRegex(@"Waiting for completion of job (?<id>[0-9a-fA-F-]{36}) on (?<queue>\S+)")]
    private static partial Regex WaitingOnQueueRegex();

    private static readonly string[] NoiseTaskNames =
    {
        "Checkout", "Initialize", "Finalize", "Post-job", "Report", "Telemetry",
        "Update", "Download", "Cleanup", "Prepare", "Component Detection",
    };

    public async Task<PrTriageResult> TriagePullRequestAsync(
        string owner, string repo, PullRequestInfo pr, CancellationToken ct,
        IReadOnlyList<CheckBuild>? checkBuilds = null)
    {
        var notes = new List<string>();
        var builds = new List<BuildFailure>();

        if (checkBuilds is null)
        {
            try
            {
                checkBuilds = await _github.GetBuildsForCommitAsync(owner, repo, pr.HeadSha, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new PrTriageResult(builds, new[] { "Could not load checks: " + ex.Message }, CiState.Unknown);
            }
        }

        if (checkBuilds.Count == 0)
        {
            notes.Add("No Azure Pipelines checks were found on the head commit yet.");
            return new PrTriageResult(builds, notes, CiState.Pending);
        }

        int running = checkBuilds.Count(b => b.State is CiState.Running or CiState.Pending);
        if (running > 0)
            notes.Add($"{running} build(s) still running — results may be incomplete.");

        foreach (var cb in checkBuilds.Where(b => b.State == CiState.Failure))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var bf = await ProcessBuildAsync(cb, notes, ct).ConfigureAwait(false);
                builds.Add(bf);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                notes.Add($"Build {cb.BuildId} ({cb.PipelineName}): {ex.Message}");
            }
        }

        if (builds.Count == 0 && checkBuilds.All(b => b.State != CiState.Failure))
            notes.Add("No failing builds — nothing to triage. 🎉");

        CiState overall = CiStateAggregation.Overall(checkBuilds);

        return new PrTriageResult(builds, notes, overall);
    }

    private async Task<BuildFailure> ProcessBuildAsync(CheckBuild cb, List<string> notes, CancellationToken ct)
    {
        var info = await _azdo.GetBuildAsync(cb.BuildId, ct).ConfigureAwait(false);
        var timeline = await _azdo.GetTimelineAsync(cb.BuildId, ct).ConfigureAwait(false);
        var byId = timeline.ToDictionary(r => r.Id, r => r, StringComparer.OrdinalIgnoreCase);

        var failedTasks = timeline
            .Where(r => string.Equals(r.Type, "Task", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(r.Result, "failed", StringComparison.OrdinalIgnoreCase)
                        && r.LogId is not null)
            .ToList();

        // jobId -> (friendlyQueue, configuration). Deduplicated across tasks.
        var helixJobs = new Dictionary<string, (string? Queue, string Config)>(StringComparer.OrdinalIgnoreCase);
        var otherFailures = new List<NonTestFailure>();

        foreach (var task in failedTasks)
        {
            ct.ThrowIfCancellationRequested();
            string config = GetConfiguration(task, byId);
            string log;
            try
            {
                log = await _azdo.GetLogTextAsync(cb.BuildId, task.LogId!.Value, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                notes.Add($"Could not read log for '{task.Name}' in build {cb.BuildId}: {ex.Message}");
                continue;
            }

            var found = ExtractHelixJobs(log);
            if (found.Count > 0)
            {
                foreach (var (jobId, queue) in found)
                {
                    if (!helixJobs.TryGetValue(jobId, out var existing) || (existing.Queue is null && queue is not null))
                        helixJobs[jobId] = (queue, config);
                }
            }
            else if (!IsNoise(task.Name))
            {
                otherFailures.Add(new NonTestFailure(
                    Stage: config,
                    Name: task.Name,
                    LogTail: Tail(log, 80),
                    LogUrl: info.WebUrl));
            }
        }

        var queues = await ProcessHelixJobsAsync(helixJobs, notes, ct).ConfigureAwait(false);

        return new BuildFailure(
            BuildId: cb.BuildId,
            PipelineName: string.IsNullOrWhiteSpace(cb.PipelineName) ? info.DefinitionName : cb.PipelineName,
            BuildNumber: info.BuildNumber,
            WebUrl: info.WebUrl,
            State: CiState.Failure,
            Queues: queues,
            OtherFailures: otherFailures);
    }

    private async Task<IReadOnlyList<HelixQueueResult>> ProcessHelixJobsAsync(
        Dictionary<string, (string? Queue, string Config)> helixJobs, List<string> notes, CancellationToken ct)
    {
        var results = new ConcurrentBag<(int Order, HelixQueueResult Result)>();
        var extraNotes = new ConcurrentBag<string>();
        using var gate = new SemaphoreSlim(4);
        int order = 0;
        var tasks = new List<Task>();

        // Acquire the gate inside the task (before any other await) so a cancellation while waiting can
        // never bypass the Task.WhenAll below — which would otherwise leave an in-flight task releasing a
        // disposed semaphore. The work is I/O-bound and the gate bounds concurrency, so no Task.Run hop
        // onto the thread pool is needed.
        async Task RunGuardedAsync(int myOrder, string jobId, string? queue, string config)
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var qr = await ProcessSingleHelixJobAsync(jobId, queue, config, ct).ConfigureAwait(false);
                if (qr is not null)
                    results.Add((myOrder, qr));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                extraNotes.Add($"Helix job {jobId}: {ex.Message}");
            }
            finally
            {
                gate.Release();
            }
        }

        foreach (var kv in helixJobs)
        {
            int myOrder = order++;
            string jobId = kv.Key;
            (string? queue, string config) = kv.Value;
            tasks.Add(RunGuardedAsync(myOrder, jobId, queue, config));
        }

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }   // individual tasks fault with OCE on cancel; surfaced to caller below

        ct.ThrowIfCancellationRequested();

        foreach (var n in extraNotes)
            notes.Add(n);

        return results.OrderBy(r => r.Order).Select(r => r.Result).ToList();
    }

    private async Task<HelixQueueResult?> ProcessSingleHelixJobAsync(
        string jobId, string? friendlyQueue, string config, CancellationToken ct)
    {
        string queueName = friendlyQueue ?? string.Empty;
        if (string.IsNullOrEmpty(queueName))
        {
            try
            {
                var details = await _helix.GetJobDetailsAsync(jobId, ct).ConfigureAwait(false);
                queueName = details.QueueId;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                queueName = "(unknown queue)";
            }
        }

        var workItems = await _helix.GetWorkItemsAsync(jobId, ct).ConfigureAwait(false);
        var failedItems = workItems.Where(w => w.ExitCode != 0).ToList();
        if (failedItems.Count == 0)
            return null;

        var itemFailures = new List<HelixWorkItemFailure>();
        foreach (var wi in failedItems)
        {
            ct.ThrowIfCancellationRequested();
            IReadOnlyList<TestFailure> tests = Array.Empty<TestFailure>();
            string? summary = null;
            string tail = string.Empty;

            if (!string.IsNullOrWhiteSpace(wi.ConsoleUri))
            {
                try
                {
                    string console = await _helix.GetConsoleAsync(wi.ConsoleUri!, ct).ConfigureAwait(false);
                    var parsed = ConsoleLogParser.Parse(console);
                    tests = parsed.Failures;
                    summary = parsed.SummaryLine;
                    tail = parsed.Tail;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    tail = "Could not download console log: " + ex.Message;
                }
            }

            itemFailures.Add(new HelixWorkItemFailure(
                Name: wi.Name,
                ExitCode: wi.ExitCode,
                ConsoleUri: wi.ConsoleUri ?? string.Empty,
                SummaryLine: summary,
                FailedTests: tests,
                ConsoleTail: tail));
        }

        return new HelixQueueResult(
            JobId: jobId,
            QueueName: string.IsNullOrEmpty(queueName) ? "(unknown queue)" : queueName,
            Configuration: config,
            JobDetailsUrl: $"https://helix.dot.net/api/jobs/{jobId}/details?api-version=2019-06-17",
            FailedWorkItems: itemFailures);
    }

    private static IReadOnlyList<(string JobId, string? Queue)> ExtractHelixJobs(string log)
    {
        var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (Match m in WaitingOnQueueRegex().Matches(log))
        {
            string id = m.Groups["id"].Value.ToLowerInvariant();
            string queue = CleanQueueName(m.Groups["queue"].Value);
            map[id] = queue;
        }

        foreach (Match m in HelixJobUrlRegex().Matches(log))
        {
            string id = m.Groups["id"].Value.ToLowerInvariant();
            map.TryAdd(id, null);
        }

        return map.Select(kv => (kv.Key, kv.Value)).ToList();
    }

    private static string CleanQueueName(string raw)
    {
        raw = raw.Trim();
        // Containerized queues appear as "(Fedora.41.Amd64.Open)ubuntu.2204...@mcr.microsoft.com/...";
        // the parenthesized prefix is the friendly distro name.
        var paren = Regex.Match(raw, @"^\(([^)]+)\)");
        if (paren.Success)
            return paren.Groups[1].Value.Trim();
        int at = raw.IndexOf('@');
        return at > 0 ? raw[..at] : raw;
    }

    private static string GetConfiguration(TimelineRecord task, Dictionary<string, TimelineRecord> byId)
    {
        // Walk up the parent chain to the nearest Job (or Phase) record; its name is the build config.
        string? cursor = task.ParentId;
        string? phaseName = null;
        // Guard against a malformed timeline whose parent links form a cycle.
        for (int hops = 0; cursor is not null && hops < 256 && byId.TryGetValue(cursor, out var rec); hops++)
        {
            if (string.Equals(rec.Type, "Job", StringComparison.OrdinalIgnoreCase))
                return CleanConfig(rec.Name);
            if (phaseName is null && string.Equals(rec.Type, "Phase", StringComparison.OrdinalIgnoreCase))
                phaseName = rec.Name;
            cursor = rec.ParentId;
        }
        return CleanConfig(phaseName ?? task.Name);
    }

    private static string CleanConfig(string name)
    {
        name = name.Trim();
        // Arcade job names often look like "Build Linux Debug" or "Linux Debug Build".
        if (name.StartsWith("Build ", StringComparison.OrdinalIgnoreCase))
            name = name["Build ".Length..];
        return name;
    }

    private static bool IsNoise(string taskName)
    {
        foreach (var n in NoiseTaskNames)
        {
            if (taskName.Contains(n, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string Tail(string text, int lines)
    {
        string[] all = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        int start = Math.Max(0, all.Length - lines);
        return string.Join('\n', all[start..]).TrimEnd();
    }
}
