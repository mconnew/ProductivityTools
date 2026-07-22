using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using WcfPrTriage.Models;
using WcfPrTriage.Services;

namespace WcfPrTriage.ViewModels;

/// <summary>Root view model: owns the PR list, the failure tree and the detail pane.</summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly HttpClient _http;
    private readonly SettingsStore _settingsStore;
    private readonly GitHubTokenSource _tokenSource;
    private readonly GitHubService _github;
    private readonly TriageService _triage;
    private readonly TriageCacheStore _triageCache;
    private AppSettings _settings;

    private CancellationTokenSource? _triageCts;
    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _manualPrCts;   // cancels a manual refresh of a non-selected PR

    private readonly DispatcherTimer _autoTimer;
    private bool _autoBusy;
    private bool _isRefreshing;   // guards RefreshAsync against overlapping invocations (UI thread only)
    private bool _refreshRequested;   // set when a refresh is asked for while one is in flight (coalesced)
    private DateTimeOffset _lastListRefreshUtc;
    private bool _initialSelectionDone;
    private bool _prListReady;   // true once a full open-PR list has loaded (guards authoritative cache prune)

    // Auto-refresh tuning.
    private const int AutoRefreshPerTickCap = 5;        // max PRs cheaply re-checked per tick
    private const int RateLimitFloorSelectedOnly = 12;  // below this, only the selected PR is refreshed
    private const int RateLimitFloorSkipAll = 4;        // below this, skip auto-refresh entirely
    private const int ScanReserve = 4;                  // keep this many calls free during the initial scan
    private static readonly TimeSpan ListRefreshInterval = TimeSpan.FromMinutes(5);

    private PullRequestViewModel? _selectedPr;
    private TriageNodeViewModel? _selectedNode;
    private PrTriageResult? _renderedResult;
    private bool _isLoadingPrs;
    private bool _isTriaging;
    private string _statusText = "Ready.";
    private string _rateLimitText = "";
    private string _detailHeader = "";
    private string _detailText = "Select a pull request to see its failures.";

    public MainViewModel()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(120) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("WcfPrTriage/1.0");

        _settingsStore = new SettingsStore();
        _settings = _settingsStore.Load();

        _tokenSource = new GitHubTokenSource(() => _settings.GitHubToken);
        _github = new GitHubService(_http, _tokenSource.Resolve);
        var azdo = new AzureDevOpsService(_http);
        var helix = new HelixService(_http);
        _triage = new TriageService(_github, azdo, helix);
        _triageCache = new TriageCacheStore();

        PullRequests = new ObservableCollection<PullRequestViewModel>();
        Nodes = new ObservableCollection<TriageNodeViewModel>();

        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsLoadingPrs);
        OpenSettingsCommand = new RelayCommand(OpenSettings);
        OpenPrCommand = new RelayCommand(OpenPrInBrowser, () => SelectedPr is not null);
        OpenNodeUrlCommand = new RelayCommand(OpenNodeUrl, () => SelectedNode?.HasOpenUrl == true);
        CopyDetailCommand = new RelayCommand(CopyDetail, () => !string.IsNullOrEmpty(SelectedNode?.DetailText));
        RefreshPrCommand = new AsyncRelayCommand<PullRequestViewModel?>(RefreshPrAsync);
        OpenPrExternalCommand = new RelayCommand<PullRequestViewModel?>(OpenPrExternal, pr => pr is not null);
        OpenBuildInDevOpsCommand = new RelayCommand<PullRequestViewModel?>(OpenBuildInDevOps, pr => DevOpsBuildUrl(pr) is not null);
        CopyPrUrlCommand = new RelayCommand<PullRequestViewModel?>(CopyPrUrl, pr => pr is not null);

        _autoTimer = new DispatcherTimer();
        _autoTimer.Tick += (_, _) => _ = AutoRefreshTickAsync();
        ConfigureAutoTimer();
    }

    public ObservableCollection<PullRequestViewModel> PullRequests { get; }
    public ObservableCollection<TriageNodeViewModel> Nodes { get; }

    public AsyncRelayCommand RefreshCommand { get; }
    public RelayCommand OpenSettingsCommand { get; }
    public RelayCommand OpenPrCommand { get; }
    public RelayCommand OpenNodeUrlCommand { get; }
    public RelayCommand CopyDetailCommand { get; }
    public AsyncRelayCommand<PullRequestViewModel?> RefreshPrCommand { get; }
    public RelayCommand<PullRequestViewModel?> OpenPrExternalCommand { get; }
    public RelayCommand<PullRequestViewModel?> OpenBuildInDevOpsCommand { get; }
    public RelayCommand<PullRequestViewModel?> CopyPrUrlCommand { get; }

    public string RepoLabel => $"{_settings.Owner}/{_settings.Repo}";

    public PullRequestViewModel? SelectedPr
    {
        get => _selectedPr;
        set
        {
            if (SetProperty(ref _selectedPr, value))
            {
                OpenPrCommand.RaiseCanExecuteChanged();
                _ = OnPrSelectedAsync(value);
            }
        }
    }

    public TriageNodeViewModel? SelectedNode
    {
        get => _selectedNode;
        set
        {
            if (SetProperty(ref _selectedNode, value))
            {
                DetailHeader = value?.DetailHeader ?? "";
                DetailText = value?.DetailText ?? "Select a node to see the failure detail.";
                OpenNodeUrlCommand.RaiseCanExecuteChanged();
                CopyDetailCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsLoadingPrs
    {
        get => _isLoadingPrs;
        set { if (SetProperty(ref _isLoadingPrs, value)) RefreshCommand.RaiseCanExecuteChanged(); }
    }

    public bool IsTriaging
    {
        get => _isTriaging;
        set => SetProperty(ref _isTriaging, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public string RateLimitText
    {
        get => _rateLimitText;
        set => SetProperty(ref _rateLimitText, value);
    }

    public string DetailHeader
    {
        get => _detailHeader;
        set => SetProperty(ref _detailHeader, value);
    }

    public string DetailText
    {
        get => _detailText;
        set => SetProperty(ref _detailText, value);
    }

    public async Task InitializeAsync()
    {
        // Borrow a token from the GitHub CLI (if the user is logged in) before the first load, so
        // the very first calls already benefit from the 5000/hr authenticated budget.
        await _tokenSource.InitializeAsync().ConfigureAwait(true);
        await RefreshAsync().ConfigureAwait(true);
    }

    private async Task RefreshAsync()
    {
        if (_isRefreshing)
        {
            _refreshRequested = true;   // coalesce: re-run once the in-flight refresh finishes
            return;                     // (e.g. a settings/repo change while loading isn't dropped)
        }
        _isRefreshing = true;
        try
        {
            do
            {
                _refreshRequested = false;
                await RefreshOnceAsync().ConfigureAwait(true);
            }
            while (_refreshRequested);
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private async Task RefreshOnceAsync()
    {
        _scanCts?.Cancel();
        _triageCts?.Cancel();
        _manualPrCts?.Cancel();   // a repo change / reload invalidates any in-flight manual PR refresh
        IsLoadingPrs = true;
        StatusText = $"Loading open PRs for {RepoLabel}…";
        try
        {
            var prs = await _github.GetOpenPullRequestsAsync(
                _settings.Owner, _settings.Repo, _settings.IncludeDrafts, CancellationToken.None).ConfigureAwait(true);

            ReconcilePullRequests(prs);
            HydrateFromCache();
            _lastListRefreshUtc = DateTimeOffset.UtcNow;

            StatusText = $"{PullRequests.Count} open PR(s).";
            UpdateRateLimit();
            _prListReady = true;   // list is authoritative now — cache prune may drop closed PRs

            var scanCts = new CancellationTokenSource();
            _scanCts = scanCts;
            _ = ScanStatusesAsync(scanCts);
        }
        catch (GitHubRateLimitException ex)
        {
            StatusText = ex.Message;
            UpdateRateLimit();
        }
        catch (Exception ex)
        {
            StatusText = "Error loading PRs: " + ex.Message;
        }
        finally
        {
            IsLoadingPrs = false;
        }
    }

    /// <summary>
    /// Merges a freshly fetched PR list into the existing collection in place — reusing existing
    /// row view-models (and therefore their cached triage results) and preserving the selection.
    /// </summary>
    private void ReconcilePullRequests(IReadOnlyList<PullRequestInfo> prs)
    {
        var incoming = new HashSet<int>(prs.Select(p => p.Number));

        // Drop PRs that are no longer open.
        for (int i = PullRequests.Count - 1; i >= 0; i--)
        {
            if (!incoming.Contains(PullRequests[i].Number))
                PullRequests.RemoveAt(i);   // if it was selected, the ListBox clears SelectedPr
        }

        // Add new PRs and update/re-order existing ones to match the incoming (updated-desc) order.
        for (int i = 0; i < prs.Count; i++)
        {
            var info = prs[i];
            int cur = IndexOfNumber(info.Number);
            if (cur < 0)
            {
                PullRequests.Insert(Math.Min(i, PullRequests.Count), new PullRequestViewModel(info));
            }
            else
            {
                PullRequests[cur].UpdateFrom(info);
                if (cur != i)
                    PullRequests.Move(cur, i);
            }
        }
    }

    private int IndexOfNumber(int number)
    {
        for (int i = 0; i < PullRequests.Count; i++)
            if (PullRequests[i].Number == number)
                return i;
        return -1;
    }

    /// <summary>
    /// Restores deep triage results saved by a previous run from the on-disk cache. Only fills rows
    /// that have no in-memory result yet and whose head commit still matches the cached one, so it is
    /// safe to call on every refresh (it never clobbers fresher in-memory data). The background scan
    /// then re-checks each signature and drops anything that changed since it was cached.
    /// </summary>
    private void HydrateFromCache()
    {
        Dictionary<int, TriageCacheEntry> cached;
        try { cached = _triageCache.Load(_settings.Owner, _settings.Repo); }
        catch { return; }
        if (cached.Count == 0)
            return;

        foreach (var pr in PullRequests)
        {
            if (pr.CachedResult is not null)
                continue;   // already have a (fresher) in-memory result
            if (!cached.TryGetValue(pr.Number, out var e) || e.Result is null)
                continue;
            if (!string.Equals(e.HeadSha, pr.Info.HeadSha, StringComparison.OrdinalIgnoreCase))
                continue;   // PR moved to a new commit — cached result is stale

            pr.CachedResult = e.Result;
            pr.CachedSignature = e.Signature;
            pr.LastCheckedUtc = e.LastCheckedUtc;
            pr.LastTriagedUtc = e.LastTriagedUtc;
            pr.State = e.State;
        }
    }

    /// <summary>
    /// Snapshots every open PR that currently has a deep triage result and makes the on-disk cache
    /// authoritative for this repo — so entries for PRs that were closed/merged, or whose failures
    /// have since cleared (result invalidated to green), are dropped rather than left to age out.
    /// Runs off the UI thread; fire-and-forget, and the store swallows any I/O error.
    /// </summary>
    private void PersistCache()
    {
        if (!_prListReady)
            return;   // never prune against a list we haven't fully loaded yet

        var entries = new List<TriageCacheEntry>();
        foreach (var pr in PullRequests)
        {
            if (pr.CachedResult is not { } result)
                continue;
            entries.Add(new TriageCacheEntry
            {
                Owner = _settings.Owner,
                Repo = _settings.Repo,
                Number = pr.Number,
                HeadSha = pr.Info.HeadSha,
                Signature = pr.CachedSignature,
                LastCheckedUtc = pr.LastCheckedUtc,
                LastTriagedUtc = pr.LastTriagedUtc,
                State = pr.State,
                Result = result,   // full result; trimmed off-thread inside SyncRepoAsync
            });
        }

        // Called even when empty: that clears the last stale entry once every failing PR closes/greens.
        _ = _triageCache.SyncRepoAsync(_settings.Owner, _settings.Repo, entries);
    }

    private async Task ScanStatusesAsync(CancellationTokenSource scanCts)
    {
        var ct = scanCts.Token;
        try
        {
            var snapshot = PullRequests.ToList();
            using var gate = new SemaphoreSlim(4);
            int stopped = 0;   // set-once flag shared across worker threads (Volatile for visibility)

            var tasks = snapshot.Select(async pr =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    if (ct.IsCancellationRequested || Volatile.Read(ref stopped) != 0)
                        return;
                    // Leave a little headroom so a nearly-exhausted budget still has room for the
                    // triage of whichever PR the user actually clicks.
                    if (_github.RateLimitRemaining is int rr && rr <= ScanReserve)
                    {
                        Volatile.Write(ref stopped, 1);
                        return;
                    }
                    RunOnUi(() => pr.IsScanning = true);
                    // Fetch off-thread, but apply the result to the row on the UI thread so the row's
                    // mutable state is never written concurrently with a user-triggered triage.
                    var builds = await FetchBuildsAsync(pr, ct).ConfigureAwait(false);
                    if (ct.IsCancellationRequested)
                        return;
                    RunOnUi(() => ApplyCheapCheck(pr, builds));
                }
                catch (OperationCanceledException) { }
                catch (HttpRequestException ex)
                {
                    Volatile.Write(ref stopped, 1);
                    RunOnUi(() => StatusText = ex.Message);
                }
                catch { /* ignore individual PR status errors */ }
                finally
                {
                    // Release the gate first: RunOnUi (a blocking Dispatcher.Invoke) can throw if the
                    // dispatcher is shutting down, and that must not leak a semaphore permit.
                    gate.Release();
                    try { RunOnUi(() => pr.IsScanning = false); } catch { }
                }
            });

            try { await Task.WhenAll(tasks).ConfigureAwait(false); }
            catch (OperationCanceledException) { }

            if (ct.IsCancellationRequested)
                return;

            RunOnUi(() =>
            {
                UpdateRateLimit();
                if (!_initialSelectionDone && SelectedPr is null)
                {
                    _initialSelectionDone = true;
                    var target = PullRequests.FirstOrDefault(p => p.State == CiState.Failure);
                    if (target is not null)
                        SelectedPr = target;   // triggers OnPrSelectedAsync
                }
                else if (SelectedPr is { } sel && sel.CachedResult is null && sel.HasBeenChecked)
                {
                    _ = OnPrSelectedAsync(sel);   // the open PR's results changed during the scan
                }

                // Now that states are up to date, reconcile the disk cache: drop entries for PRs that
                // closed/merged (gone from the list) or turned green (result invalidated during the scan).
                PersistCache();
            });
        }
        finally
        {
            // The CTS is safe to dispose now — every task that used its token has completed. Clear the
            // field (on the UI thread) only if it's still ours, so a newer scan's CTS is left intact.
            try { RunOnUi(() => { if (_scanCts == scanCts) _scanCts = null; }); } catch { }
            scanCts.Dispose();
        }
    }

    /// <summary>
    /// The cheap "first-level" fetch: one GitHub call to get the CI build set + a freshness
    /// signature. Updates the row's state/signature and invalidates the deep cache if it changed.
    /// Never fetches Helix data, so it is safe to run across every PR in the background.
    /// This wrapper is used by callers already on the UI thread (the auto-refresh tick); the
    /// state application in <see cref="ApplyCheapCheck"/> must run on the UI thread.
    /// </summary>
    private async Task<bool> CheapCheckAsync(PullRequestViewModel pr, CancellationToken ct)
    {
        var builds = await FetchBuildsAsync(pr, ct).ConfigureAwait(true);
        if (ct.IsCancellationRequested)
            return false;
        return ApplyCheapCheck(pr, builds);
    }

    /// <summary>Fetches the CI build set for a PR's head commit. Safe to call from any thread.</summary>
    private Task<IReadOnlyList<CheckBuild>> FetchBuildsAsync(PullRequestViewModel pr, CancellationToken ct) =>
        _github.GetBuildsForCommitAsync(_settings.Owner, _settings.Repo, pr.Info.HeadSha, ct);

    /// <summary>
    /// Applies a freshly fetched build set to a PR row. MUST run on the UI thread — it mutates the
    /// row's cached state, which the UI thread also touches during a user-triggered triage.
    /// </summary>
    private bool ApplyCheapCheck(PullRequestViewModel pr, IReadOnlyList<CheckBuild> builds)
    {
        string sig = ComputeSignature(pr.Info.HeadSha, builds);
        bool changed = !string.Equals(sig, pr.CachedSignature, StringComparison.Ordinal);
        if (changed && pr.CachedResult is not null)
            pr.CachedResult = null;   // deeper data is stale — re-triage lazily on next view

        pr.CachedSignature = sig;
        pr.CachedBuilds = builds;
        pr.State = CiStateAggregation.Overall(builds);
        pr.LastCheckedUtc = DateTimeOffset.UtcNow;
        return changed;
    }

    private async Task OnPrSelectedAsync(PullRequestViewModel? pr)
    {
        _triageCts?.Cancel();

        if (pr is null)
        {
            ClearPanels();
            return;
        }

        OpenPrCommand.RaiseCanExecuteChanged();

        // Show cached results instantly — clicking between PRs shouldn't refetch.
        if (pr.CachedResult is { } cached)
            await RenderResultAsync(pr, cached, fromCache: true).ConfigureAwait(true);
        else
            ClearPanels("Triaging…");

        // Trust a recently-checked cache; otherwise do a light freshness check (deep only if changed).
        bool fresh = pr.CachedResult is not null
                     && pr.HasBeenChecked
                     && (DateTimeOffset.UtcNow - pr.LastCheckedUtc) < StaleThreshold;
        if (fresh)
            return;

        await RunSelectedTriageAsync(pr, force: false).ConfigureAwait(true);
    }

    /// <summary>Runs a triage for the selected PR, owning the shared triage cancellation + busy flag.</summary>
    private async Task RunSelectedTriageAsync(PullRequestViewModel pr, bool force)
    {
        var previous = _triageCts;
        var cts = new CancellationTokenSource();
        _triageCts = cts;
        previous?.Cancel();   // previous triage self-disposes in its own finally
        IsTriaging = true;
        try
        {
            await TriagePrAsync(pr, force, cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) { }
        catch (GitHubRateLimitException ex)
        {
            StatusText = ex.Message;
            UpdateRateLimit();
        }
        catch (Exception ex)
        {
            StatusText = "Error: " + ex.Message;
        }
        finally
        {
            // TriagePrAsync has fully unwound, so every use of this token is done — safe to dispose.
            // Clear the field only if a newer triage hasn't already replaced it.
            if (_triageCts == cts)
            {
                _triageCts = null;
                IsTriaging = false;
            }
            cts.Dispose();
        }
    }

    /// <summary>
    /// Cache-aware triage. Always does the cheap check first; only pulls the deep (Helix) data when
    /// the signature changed, the cache is empty, or a refresh was forced. Renders only if the PR is
    /// still the selected one.
    /// </summary>
    private async Task TriagePrAsync(PullRequestViewModel pr, bool force, CancellationToken ct)
    {
        // Reuse the CI builds from a recent cheap check (e.g. the background scan that just ran)
        // instead of re-fetching them — this avoids a redundant GitHub call per PR selection.
        IReadOnlyList<CheckBuild> builds;
        if (!force
            && pr.CachedBuilds is { } cachedBuilds
            && pr.HasBeenChecked
            && (DateTimeOffset.UtcNow - pr.LastCheckedUtc) < StaleThreshold)
        {
            builds = cachedBuilds;
        }
        else
        {
            builds = await _github.GetBuildsForCommitAsync(
                _settings.Owner, _settings.Repo, pr.Info.HeadSha, ct).ConfigureAwait(true);
            if (ct.IsCancellationRequested)
                return;
            pr.CachedBuilds = builds;
            pr.LastCheckedUtc = DateTimeOffset.UtcNow;
        }

        string sig = ComputeSignature(pr.Info.HeadSha, builds);
        pr.State = CiStateAggregation.Overall(builds);

        bool isSelected = pr == SelectedPr;

        // Nothing changed since the last deep triage — reuse it (no Helix refetch).
        if (!force && pr.CachedResult is { } cached && string.Equals(sig, pr.CachedSignature, StringComparison.Ordinal))
        {
            pr.CachedSignature = sig;
            if (isSelected && !ReferenceEquals(cached, _renderedResult))
                await RenderResultAsync(pr, cached, fromCache: true).ConfigureAwait(true);
            return;
        }

        if (isSelected)
            StatusText = $"Triaging PR #{pr.Number} — {pr.Title}";

        // Pass the builds we already have so the triage service doesn't re-call GitHub for them.
        var result = await _triage.TriagePullRequestAsync(
            _settings.Owner, _settings.Repo, pr.Info, ct, builds).ConfigureAwait(true);
        if (ct.IsCancellationRequested)
            return;

        pr.CachedResult = result;
        pr.CachedSignature = sig;
        pr.LastTriagedUtc = DateTimeOffset.UtcNow;
        pr.State = result.OverallState;

        PersistCache();   // survive restarts: reuse this result until the PR's signature changes

        if (pr == SelectedPr)
            await RenderResultAsync(pr, result, fromCache: false).ConfigureAwait(true);
    }

    /// <summary>Above this many failing tests, the node forest is built off the UI thread.</summary>
    private const int OffThreadForestThreshold = 150;

    private async Task RenderResultAsync(PullRequestViewModel pr, PrTriageResult result, bool fromCache)
    {
        if (pr != SelectedPr)
            return;

        int failingTests = result.Builds
            .SelectMany(b => b.Queues)
            .SelectMany(q => q.FailedWorkItems)
            .Sum(w => w.FailedTests.Count);

        // Building the forest allocates a view-model per failing test and formats every detail string,
        // so it is O(failing tests). For large results do that off the UI thread so selecting a heavy PR
        // doesn't jank the dispatcher; small results build inline to avoid an unnecessary thread hop.
        ObservableCollection<TriageNodeViewModel> forest;
        if (failingTests > OffThreadForestThreshold)
        {
            forest = await Task.Run(() => TriageNodeViewModel.BuildForest(result)).ConfigureAwait(true);
            if (pr != SelectedPr)
                return;   // selection changed while we were building — discard this render
        }
        else
        {
            forest = TriageNodeViewModel.BuildForest(result);
        }

        _renderedResult = result;
        Nodes.Clear();
        foreach (var n in forest)
            Nodes.Add(n);

        SelectFirstInteresting(forest);

        string suffix = fromCache ? " · cached" : "";
        StatusText = result.Builds.Count == 0
            ? $"PR #{pr.Number}: no failing builds.{suffix}"
            : $"PR #{pr.Number}: {result.Builds.Count} failing build(s), {failingTests} failing test(s).{suffix}";

        UpdateRateLimit();
    }

    private void ClearPanels(string detail = "Select a pull request to see its failures.")
    {
        _renderedResult = null;
        Nodes.Clear();
        SelectedNode = null;
        DetailHeader = "";
        DetailText = detail;
    }

    /// <summary>Force-refreshes a single PR (the per-row ⟳ button), bypassing the cache.</summary>
    private async Task RefreshPrAsync(PullRequestViewModel? pr)
    {
        if (pr is null || pr.IsScanning)
            return;

        pr.IsScanning = true;
        try
        {
            if (pr == SelectedPr)
            {
                await RunSelectedTriageAsync(pr, force: true).ConfigureAwait(true);
            }
            else
            {
                // Own a cancellable token so a repo change / full refresh (which cancels these) can
                // stop this background triage instead of letting it run against a list the user has
                // already navigated away from.
                var previous = _manualPrCts;
                var cts = new CancellationTokenSource();
                _manualPrCts = cts;
                previous?.Cancel();
                try
                {
                    await TriagePrAsync(pr, force: true, cts.Token).ConfigureAwait(true);
                }
                finally
                {
                    if (_manualPrCts == cts)
                        _manualPrCts = null;
                    cts.Dispose();
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (GitHubRateLimitException ex)
        {
            StatusText = ex.Message;
            UpdateRateLimit();
        }
        catch (Exception ex)
        {
            StatusText = "Error: " + ex.Message;
        }
        finally
        {
            pr.IsScanning = false;
        }
    }

    private async Task AutoRefreshTickAsync()
    {
        if (_autoBusy || IsLoadingPrs || _settings.AutoRefreshSeconds <= 0)
            return;

        // Respect a known rate-limit window: don't fire background calls until it resets.
        if (_github.IsRateLimited)
        {
            RunOnUi(UpdateRateLimit);
            return;
        }

        _autoBusy = true;
        try
        {
            int remaining = _github.RateLimitRemaining ?? int.MaxValue;
            if (remaining < RateLimitFloorSkipAll)
                return;

            var now = DateTimeOffset.UtcNow;
            var staleWindow = TimeSpan.FromSeconds(_settings.AutoRefreshSeconds);

            // Occasionally re-pull the PR list to catch new commits / opened / closed PRs.
            if (remaining > RateLimitFloorSelectedOnly && now - _lastListRefreshUtc >= ListRefreshInterval)
            {
                try
                {
                    var prs = await _github.GetOpenPullRequestsAsync(
                        _settings.Owner, _settings.Repo, _settings.IncludeDrafts, CancellationToken.None).ConfigureAwait(true);
                    ReconcilePullRequests(prs);
                    _lastListRefreshUtc = now;
                }
                catch (Exception ex) { StatusText = ex.Message; /* keep the existing list */ }
            }

            bool lowBudget = remaining < RateLimitFloorSelectedOnly;
            var candidates = PullRequests
                .Where(p => now - p.LastCheckedUtc >= staleWindow)
                .OrderByDescending(p => p == SelectedPr)
                .ThenByDescending(p => p.State is CiState.Running or CiState.Pending)
                .ThenBy(p => p.LastCheckedUtc)
                .ToList();

            candidates = lowBudget
                ? candidates.Where(p => p == SelectedPr).ToList()
                : candidates.Take(AutoRefreshPerTickCap).ToList();

            foreach (var pr in candidates)
            {
                if ((_github.RateLimitRemaining ?? int.MaxValue) < RateLimitFloorSkipAll)
                    break;
                try
                {
                    if (pr == SelectedPr)
                        await RunSelectedTriageAsync(pr, force: false).ConfigureAwait(true);
                    else
                        await CheapCheckAsync(pr, CancellationToken.None).ConfigureAwait(true);
                }
                catch (HttpRequestException ex) { StatusText = ex.Message; break; }
                catch { /* skip this PR */ }
            }

            UpdateRateLimit();

            // Keep the disk cache in step with the just-refreshed list/states (prune closed & greened PRs).
            PersistCache();
        }
        finally
        {
            _autoBusy = false;
        }
    }

    private void ConfigureAutoTimer()
    {
        _autoTimer.Stop();
        int sec = _settings.AutoRefreshSeconds;
        if (sec > 0)
        {
            _autoTimer.Interval = TimeSpan.FromSeconds(Math.Max(sec, 15));
            _autoTimer.Start();
        }
    }

    private TimeSpan StaleThreshold =>
        TimeSpan.FromSeconds(_settings.AutoRefreshSeconds > 0 ? Math.Max(_settings.AutoRefreshSeconds, 30) : 90);

    private static string ComputeSignature(string headSha, IReadOnlyList<CheckBuild> builds) =>
        headSha + "@" + string.Join("|", builds.OrderBy(b => b.BuildId).Select(b => $"{b.BuildId}:{(int)b.State}"));

    private void SelectFirstInteresting(ObservableCollection<TriageNodeViewModel> forest)
    {
        // Depth-first path search so we can expand every ancestor of the auto-selected node —
        // important because large results start with work-item nodes collapsed.
        static bool FindPath(IEnumerable<TriageNodeViewModel> nodes, TriageNodeKind kind, List<TriageNodeViewModel> path)
        {
            foreach (var n in nodes)
            {
                path.Add(n);
                if (n.Kind == kind || FindPath(n.Children, kind, path))
                    return true;
                path.RemoveAt(path.Count - 1);
            }
            return false;
        }

        List<TriageNodeViewModel> PathTo(TriageNodeKind kind)
        {
            var path = new List<TriageNodeViewModel>();
            return FindPath(forest, kind, path) ? path : new List<TriageNodeViewModel>(0);
        }

        var target = PathTo(TriageNodeKind.Test);
        if (target.Count == 0) target = PathTo(TriageNodeKind.BuildError);
        if (target.Count == 0) target = PathTo(TriageNodeKind.WorkItem);
        if (target.Count == 0 && forest.FirstOrDefault() is { } first)
            target = new List<TriageNodeViewModel> { first };

        if (target.Count == 0)
            return;

        for (int i = 0; i < target.Count - 1; i++)
            target[i].IsExpanded = true;

        var node = target[^1];
        node.IsSelected = true;
        SelectedNode = node;
    }

    private void OpenPrInBrowser()
    {
        if (SelectedPr is not null)
            OpenUrl(SelectedPr.HtmlUrl);
    }

    /// <summary>Context-menu action: open the right-clicked PR on GitHub.</summary>
    private void OpenPrExternal(PullRequestViewModel? pr)
    {
        if (pr is not null)
            OpenUrl(pr.HtmlUrl);
    }

    /// <summary>Context-menu action: open the PR's Azure DevOps build-results page.</summary>
    private void OpenBuildInDevOps(PullRequestViewModel? pr)
    {
        if (DevOpsBuildUrl(pr) is { } url)
            OpenUrl(url);
    }

    /// <summary>Context-menu action: copy the PR's GitHub URL to the clipboard.</summary>
    private void CopyPrUrl(PullRequestViewModel? pr)
    {
        if (pr is null)
            return;
        try { Clipboard.SetText(pr.HtmlUrl); }
        catch { /* clipboard can transiently fail */ }
    }

    /// <summary>
    /// Resolves the DevOps build-results URL for a PR from its cached CI builds, preferring a failing
    /// build. Returns null when the PR hasn't been scanned yet (no builds known).
    /// </summary>
    private static string? DevOpsBuildUrl(PullRequestViewModel? pr)
    {
        var builds = pr?.CachedBuilds;
        if (builds is null || builds.Count == 0)
            return null;
        var chosen = builds.FirstOrDefault(b => b.State == CiState.Failure) ?? builds[0];
        return string.IsNullOrWhiteSpace(chosen.DetailsUrl) ? null : chosen.DetailsUrl;
    }

    private void OpenNodeUrl()
    {
        if (SelectedNode?.OpenUrl is { } url)
            OpenUrl(url);
    }

    private void CopyDetail()
    {
        if (!string.IsNullOrEmpty(SelectedNode?.DetailText))
        {
            try { Clipboard.SetText(SelectedNode!.DetailText); }
            catch { /* clipboard can transiently fail */ }
        }
    }

    private void OpenSettings()
    {
        var dialog = new Views.SettingsWindow(Clone(_settings))
        {
            Owner = Application.Current.MainWindow,
        };
        if (dialog.ShowDialog() == true)
        {
            bool repoChanged =
                !string.Equals(_settings.Owner, dialog.Result.Owner, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(_settings.Repo, dialog.Result.Repo, StringComparison.OrdinalIgnoreCase);

            _settings = dialog.Result;
            _settingsStore.Save(_settings);
            OnPropertyChanged(nameof(RepoLabel));
            ConfigureAutoTimer();

            if (repoChanged)
            {
                _initialSelectionDone = false;
                SelectedPr = null;
                PullRequests.Clear();
            }

            _ = RefreshAsync();
        }
    }

    private static AppSettings Clone(AppSettings s) => new()
    {
        Owner = s.Owner,
        Repo = s.Repo,
        GitHubToken = s.GitHubToken,
        IncludeDrafts = s.IncludeDrafts,
        AutoRefreshSeconds = s.AutoRefreshSeconds,
    };

    private void UpdateRateLimit()
    {
        if (_github.RateLimitRemaining is not int rem || _github.RateLimitLimit is not int lim)
            return;

        string src = _tokenSource.ActiveKind switch
        {
            GitHubTokenSource.TokenKind.None => " (anon)",
            GitHubTokenSource.TokenKind.GhCli => " (gh cli)",
            _ => "",
        };
        string reset = rem <= 0 && _github.RetryAtUtc is { } t
            ? $" · resets {t.ToLocalTime():HH:mm}"
            : "";
        RateLimitText = $"GitHub: {rem}/{lim}{src}{reset}";
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* best-effort */ }
    }

    private static void RunOnUi(Action action)
    {
        var app = Application.Current;
        if (app is null)
            action();
        else
            app.Dispatcher.Invoke(action);
    }
}
