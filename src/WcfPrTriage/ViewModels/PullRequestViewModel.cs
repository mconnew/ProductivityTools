using WcfPrTriage.Models;

namespace WcfPrTriage.ViewModels;

/// <summary>One row in the pull-request list.</summary>
public sealed class PullRequestViewModel : ObservableObject
{
    private CiState _state = CiState.Unknown;
    private bool _isScanning;

    public PullRequestViewModel(PullRequestInfo info) => Info = info;

    public PullRequestInfo Info { get; private set; }

    public int Number => Info.Number;
    public string Title => Info.Title;
    public string Author => Info.Author;
    public string HtmlUrl => Info.HtmlUrl;
    public bool IsDraft => Info.IsDraft;

    public string Header => $"#{Info.Number}";
    public string SubHeader => $"{Info.Author} · updated {Relative(Info.UpdatedAt)}" + (Info.IsDraft ? " · draft" : "");

    // ---- Local cache (keeps triage results between selections & powers the cheap freshness check) ----

    /// <summary>
    /// A cheap "freshness key" derived from the head commit + the set of CI builds and their states.
    /// If a re-check produces the same signature, no deeper (Helix) re-fetch is needed.
    /// </summary>
    public string? CachedSignature { get; set; }

    /// <summary>The last full triage result for this PR, reused when the signature is unchanged.</summary>
    public PrTriageResult? CachedResult { get; set; }

    /// <summary>The CI builds from the most recent cheap check — reused to avoid re-fetching on select.</summary>
    public IReadOnlyList<CheckBuild>? CachedBuilds { get; set; }

    /// <summary>When the cheap freshness check last ran (drives staleness-based auto-refresh).</summary>
    public DateTimeOffset LastCheckedUtc { get; set; }

    /// <summary>When a full (deep) triage last ran.</summary>
    public DateTimeOffset LastTriagedUtc { get; set; }

    /// <summary>True once at least one freshness check or triage has populated the CI state.</summary>
    public bool HasBeenChecked => LastCheckedUtc != default;

    /// <summary>
    /// Updates this row from a freshly fetched PR (title/author/updated/head SHA can all change).
    /// Returns true when the head commit changed — the caller should then treat the cache as stale.
    /// </summary>
    public bool UpdateFrom(PullRequestInfo info)
    {
        bool headChanged = !string.Equals(Info.HeadSha, info.HeadSha, StringComparison.OrdinalIgnoreCase);
        Info = info;
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(SubHeader));
        OnPropertyChanged(nameof(IsDraft));
        if (headChanged)
        {
            CachedResult = null;
            CachedSignature = null;
            CachedBuilds = null;
        }
        return headChanged;
    }

    /// <summary>Aggregated CI state, filled lazily by a background status scan or when selected.</summary>
    public CiState State
    {
        get => _state;
        set
        {
            if (SetProperty(ref _state, value))
                OnPropertyChanged(nameof(StateGlyph));
        }
    }

    public bool IsScanning
    {
        get => _isScanning;
        set => SetProperty(ref _isScanning, value);
    }

    public string StateGlyph => State switch
    {
        CiState.Failure => "●",
        CiState.Success => "●",
        CiState.Running => "◐",
        CiState.Pending => "○",
        _ => "○",
    };

    private static string Relative(DateTimeOffset when)
    {
        var d = DateTimeOffset.UtcNow - when;
        if (d.TotalMinutes < 1) return "just now";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes}m ago";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours}h ago";
        return $"{(int)d.TotalDays}d ago";
    }
}
