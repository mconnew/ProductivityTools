using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using WcfPrTriage.Models;

namespace WcfPrTriage.Services;

/// <summary>Talks to the public GitHub REST API to list open PRs and their azure-pipelines checks.</summary>
public sealed partial class GitHubService
{
    private readonly HttpClient _http;
    private readonly Func<string?> _tokenProvider;

    public GitHubService(HttpClient http, Func<string?> tokenProvider)
    {
        _http = http;
        _tokenProvider = tokenProvider;
    }

    // Rate-limit accounting is read/written from multiple concurrent requests (the background scan
    // fires several calls in parallel), so all access is guarded by this lock. int?/DateTimeOffset?
    // are multi-word structs — an unsynchronized read could otherwise tear.
    private readonly object _rateLock = new();
    private int? _rateLimitRemaining;
    private int? _rateLimitLimit;
    private DateTimeOffset? _rateLimitResetUtc;

    // When set to a future time, we are known to be rate-limited (primary budget exhausted or a
    // Retry-After was returned) and further requests are short-circuited until it passes.
    private DateTimeOffset? _blockUntilUtc;

    /// <summary>Remaining GitHub API calls in the current rate-limit window (or null if unknown).</summary>
    public int? RateLimitRemaining { get { lock (_rateLock) return _rateLimitRemaining; } }

    /// <summary>Total GitHub API calls allowed in the current window (60 anon, 5000 authenticated).</summary>
    public int? RateLimitLimit { get { lock (_rateLock) return _rateLimitLimit; } }

    /// <summary>When the current rate-limit window resets (from <c>X-RateLimit-Reset</c>), or null if unknown.</summary>
    public DateTimeOffset? RateLimitResetUtc { get { lock (_rateLock) return _rateLimitResetUtc; } }

    /// <summary>True while the client is known to be rate-limited and should not issue requests.</summary>
    public bool IsRateLimited { get { lock (_rateLock) return _blockUntilUtc is { } t && t > DateTimeOffset.UtcNow; } }

    /// <summary>The time before which no further calls should be made, or null if not limited.</summary>
    public DateTimeOffset? RetryAtUtc
    {
        get { lock (_rateLock) return _blockUntilUtc is { } t && t > DateTimeOffset.UtcNow ? t : (DateTimeOffset?)null; }
    }

    [GeneratedRegex(@"buildId=(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex BuildIdRegex();

    private HttpRequestMessage NewRequest(string url)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        req.Headers.UserAgent.ParseAdd("WcfPrTriage/1.0");
        req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        var token = _tokenProvider();
        if (!string.IsNullOrWhiteSpace(token))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return req;
    }

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
    {
        // Proactively stay below the limit: if we already know the budget is spent, don't fire
        // another request that would just come back 403 — fail fast with the reset time instead.
        ThrowIfRateLimited();

        using var req = NewRequest(url);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        CaptureRateLimit(resp);

        if (!resp.IsSuccessStatusCode)
        {
            string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            // Primary (budget) or secondary (abuse) rate limits: honor Retry-After / X-RateLimit-Reset.
            if (resp.StatusCode == HttpStatusCode.Forbidden || (int)resp.StatusCode == 429)
            {
                DateTimeOffset? until = null;
                if (resp.Headers.RetryAfter is { } ra)
                {
                    if (ra.Delta is { } delta) until = DateTimeOffset.UtcNow + delta;
                    else if (ra.Date is { } date) until = date;
                }
                if (until is null && RateLimitRemaining is <= 0 && RateLimitResetUtc is { } reset)
                    until = reset;

                if (until is { } u && u > DateTimeOffset.UtcNow)
                {
                    lock (_rateLock) _blockUntilUtc = u;
                    throw new GitHubRateLimitException(RateLimitMessage(u), u);
                }
            }

            throw new HttpRequestException($"GitHub {url} returned {(int)resp.StatusCode} {resp.StatusCode}.\n{Truncate(body, 400)}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
    }

    private void ThrowIfRateLimited()
    {
        DateTimeOffset? until;
        lock (_rateLock) until = _blockUntilUtc;
        if (until is { } u && u > DateTimeOffset.UtcNow)
            throw new GitHubRateLimitException(RateLimitMessage(u), u);
    }

    private void CaptureRateLimit(HttpResponseMessage resp)
    {
        int? rem = null, lim = null;
        DateTimeOffset? reset = null;
        if (resp.Headers.TryGetValues("X-RateLimit-Remaining", out var remHdr) && int.TryParse(System.Linq.Enumerable.FirstOrDefault(remHdr), out var r))
            rem = r;
        if (resp.Headers.TryGetValues("X-RateLimit-Limit", out var limHdr) && int.TryParse(System.Linq.Enumerable.FirstOrDefault(limHdr), out var l))
            lim = l;
        if (resp.Headers.TryGetValues("X-RateLimit-Reset", out var rstHdr)
            && long.TryParse(System.Linq.Enumerable.FirstOrDefault(rstHdr), out var epoch))
            reset = DateTimeOffset.FromUnixTimeSeconds(epoch);

        lock (_rateLock)
        {
            if (lim is not null)
                _rateLimitLimit = lim;

            // Parallel responses can arrive out of order. Track the window by its reset time: a newer
            // reset means a fresh window (trust its remaining); within the same window keep the most
            // pessimistic (minimum) remaining so a stale higher count can't mask an exhausted budget.
            if (reset is { } rs)
            {
                if (_rateLimitResetUtc is not { } known || rs > known)
                {
                    _rateLimitResetUtc = rs;
                    _rateLimitRemaining = rem;
                }
                else if (rs == known && rem is { } rv)
                {
                    _rateLimitRemaining = _rateLimitRemaining is { } cur ? Math.Min(cur, rv) : rv;
                }
            }
            else if (rem is { } rv2)
            {
                _rateLimitRemaining = _rateLimitRemaining is { } cur2 ? Math.Min(cur2, rv2) : rv2;
            }

            // Keep the "known rate-limited" flag in sync with the (now monotonic) state.
            if (_rateLimitRemaining is <= 0 && _rateLimitResetUtc is { } reset2 && reset2 > DateTimeOffset.UtcNow)
                _blockUntilUtc = reset2;             // budget spent — block until the window resets
            else if (_rateLimitRemaining is > 0)
                _blockUntilUtc = null;               // budget available again (reset passed, or token added)
        }
    }

    private string RateLimitMessage(DateTimeOffset until)
    {
        int mins = Math.Max(1, (int)Math.Ceiling((until - DateTimeOffset.UtcNow).TotalMinutes));
        string local = until.ToLocalTime().ToString("HH:mm");
        string tokenHint = string.IsNullOrWhiteSpace(_tokenProvider())
            ? " Sign in with the GitHub CLI (gh auth login) or add a token in Settings to raise the limit to 5000/hr."
            : string.Empty;
        return $"GitHub rate limit reached — resets in ~{mins} min (at {local}).{tokenHint}";
    }

    public async Task<IReadOnlyList<PullRequestInfo>> GetOpenPullRequestsAsync(
        string owner, string repo, bool includeDrafts, CancellationToken ct)
    {
        var result = new List<PullRequestInfo>();
        // Cap at 5 pages × 100 = 500 open PRs. dotnet/wcf never approaches this; a larger repo would
        // silently truncate here (the oldest-updated PRs beyond 500 would be omitted).
        for (int page = 1; page <= 5; page++)
        {
            string url = $"https://api.github.com/repos/{owner}/{repo}/pulls?state=open&sort=updated&direction=desc&per_page=100&page={page}";
            using var doc = await GetJsonAsync(url, ct).ConfigureAwait(false);
            int count = 0;
            foreach (var pr in doc.RootElement.EnumerateArray())
            {
                count++;
                bool draft = pr.TryGetProperty("draft", out var d) && d.GetBoolean();
                if (draft && !includeDrafts)
                    continue;

                result.Add(new PullRequestInfo(
                    Number: pr.GetProperty("number").GetInt32(),
                    Title: pr.GetProperty("title").GetString() ?? "(no title)",
                    Author: pr.TryGetProperty("user", out var u) && u.ValueKind == JsonValueKind.Object
                        ? u.GetProperty("login").GetString() ?? "unknown"
                        : "unknown",
                    HeadSha: pr.GetProperty("head").GetProperty("sha").GetString() ?? string.Empty,
                    HtmlUrl: pr.GetProperty("html_url").GetString() ?? string.Empty,
                    UpdatedAt: pr.GetProperty("updated_at").GetDateTimeOffset(),
                    IsDraft: draft));
            }

            if (count < 100)
                break;
        }

        return result;
    }

    /// <summary>
    /// Returns the distinct Azure DevOps builds referenced by azure-pipelines check-runs on the
    /// given head commit, each with an aggregated pass/fail state.
    /// </summary>
    public async Task<IReadOnlyList<CheckBuild>> GetBuildsForCommitAsync(
        string owner, string repo, string sha, CancellationToken ct)
    {
        string url = $"https://api.github.com/repos/{owner}/{repo}/commits/{sha}/check-runs?per_page=100";
        using var doc = await GetJsonAsync(url, ct).ConfigureAwait(false);

        var byBuild = new Dictionary<long, (string Name, string Url, CiState State)>();
        if (!doc.RootElement.TryGetProperty("check_runs", out var runs) || runs.ValueKind != JsonValueKind.Array)
            return Array.Empty<CheckBuild>();

        foreach (var run in runs.EnumerateArray())
        {
            string? appSlug = run.TryGetProperty("app", out var app) && app.ValueKind == JsonValueKind.Object
                ? app.GetProperty("slug").GetString()
                : null;
            if (!string.Equals(appSlug, "azure-pipelines", StringComparison.OrdinalIgnoreCase))
                continue;

            string detailsUrl = run.TryGetProperty("details_url", out var du) ? du.GetString() ?? string.Empty : string.Empty;
            var m = BuildIdRegex().Match(detailsUrl);
            if (!m.Success || !long.TryParse(m.Groups[1].Value, out long buildId))
                continue;

            string name = run.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
            string status = run.TryGetProperty("status", out var s) ? s.GetString() ?? string.Empty : string.Empty;
            string? conclusion = run.TryGetProperty("conclusion", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
            CiState state = MapCheckState(status, conclusion);

            // The top-level pipeline check-run has no parenthetical suffix; prefer it for the name.
            bool isTopLevel = !name.Contains('(');
            if (byBuild.TryGetValue(buildId, out var existing))
            {
                string chosenName = isTopLevel && existing.Name.Contains('(') ? name : existing.Name;
                byBuild[buildId] = (chosenName, existing.Url, Worse(existing.State, state));
            }
            else
            {
                byBuild[buildId] = (name, detailsUrl, state);
            }
        }

        return byBuild
            .Select(kv => new CheckBuild(kv.Key, StripSuffix(kv.Value.Name), kv.Value.Url, kv.Value.State))
            .OrderBy(b => b.PipelineName)
            .ToList();
    }

    private static string StripSuffix(string name)
    {
        int i = name.IndexOf(" (", StringComparison.Ordinal);
        return i >= 0 ? name[..i] : name;
    }

    private static CiState MapCheckState(string status, string? conclusion)
    {
        if (!string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
            return CiState.Running;

        return conclusion switch
        {
            "success" or "neutral" or "skipped" => CiState.Success,
            "failure" or "timed_out" or "cancelled" or "action_required" or "startup_failure" => CiState.Failure,
            _ => CiState.Unknown,
        };
    }

    /// <summary>Returns the more "severe" of two states for aggregation (Failure &gt; Running &gt; Success).</summary>
    public static CiState Worse(CiState a, CiState b)
    {
        static int Rank(CiState s) => s switch
        {
            CiState.Failure => 4,
            CiState.Running => 3,
            CiState.Pending => 2,
            CiState.Success => 1,
            _ => 0,
        };
        return Rank(a) >= Rank(b) ? a : b;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}

/// <summary>
/// Thrown when GitHub reports (or we already know from the last response headers) that the API
/// rate limit is exhausted. Carries the reset time so the UI can tell the user when to retry.
/// </summary>
public sealed class GitHubRateLimitException : HttpRequestException
{
    public GitHubRateLimitException(string message, DateTimeOffset? resetUtc)
        : base(message) => ResetUtc = resetUtc;

    /// <summary>When the rate-limit window resets, or null if unknown.</summary>
    public DateTimeOffset? ResetUtc { get; }
}
