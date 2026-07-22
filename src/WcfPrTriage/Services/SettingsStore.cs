using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WcfPrTriage.Services;

/// <summary>User-editable settings for the tool.</summary>
public sealed class AppSettings
{
    /// <summary>GitHub owner of the repository to watch.</summary>
    public string Owner { get; set; } = "dotnet";

    /// <summary>GitHub repository name to watch.</summary>
    public string Repo { get; set; } = "wcf";

    /// <summary>
    /// Optional GitHub token (classic or fine-grained, read-only is enough). Purely to raise the
    /// unauthenticated rate limit from 60/hr to 5000/hr — all data used is public.
    /// </summary>
    [JsonIgnore]
    public string? GitHubToken { get; set; }

    /// <summary>Whether draft PRs are shown in the list.</summary>
    public bool IncludeDrafts { get; set; } = true;

    /// <summary>
    /// How often (seconds) to auto-refresh PR statuses/results in the background. 0 disables
    /// auto-refresh. Each cycle only re-checks PRs that haven't been looked at within this window,
    /// and backs off when the GitHub rate-limit budget is low.
    /// </summary>
    public int AutoRefreshSeconds { get; set; } = 120;
}

/// <summary>Loads/saves <see cref="AppSettings"/> under %APPDATA%\WcfPrTriage, DPAPI-protecting the token.</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _dir;
    private readonly string _path;

    public SettingsStore()
    {
        _dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WcfPrTriage");
        _path = Path.Combine(_dir, "settings.json");
    }

    public string FilePath => _path;

    // On-disk shape: token stored separately, DPAPI-encrypted.
    private sealed class Persisted
    {
        public string Owner { get; set; } = "dotnet";
        public string Repo { get; set; } = "wcf";
        public bool IncludeDrafts { get; set; } = true;
        public int AutoRefreshSeconds { get; set; } = 120;
        public string? ProtectedGitHubToken { get; set; }
    }

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_path))
                return new AppSettings();

            var p = JsonSerializer.Deserialize<Persisted>(File.ReadAllText(_path), JsonOptions) ?? new Persisted();
            string? token = null;
            if (!string.IsNullOrWhiteSpace(p.ProtectedGitHubToken))
            {
                try { token = Dpapi.Unprotect(p.ProtectedGitHubToken); }
                catch { token = null; /* machine/user changed — ignore stored token */ }
            }

            return new AppSettings
            {
                Owner = string.IsNullOrWhiteSpace(p.Owner) ? "dotnet" : p.Owner,
                Repo = string.IsNullOrWhiteSpace(p.Repo) ? "wcf" : p.Repo,
                IncludeDrafts = p.IncludeDrafts,
                AutoRefreshSeconds = p.AutoRefreshSeconds < 0 ? 0 : p.AutoRefreshSeconds,
                GitHubToken = token,
            };
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(_dir);
        var p = new Persisted
        {
            Owner = settings.Owner,
            Repo = settings.Repo,
            IncludeDrafts = settings.IncludeDrafts,
            AutoRefreshSeconds = settings.AutoRefreshSeconds,
            ProtectedGitHubToken = string.IsNullOrWhiteSpace(settings.GitHubToken)
                ? null
                : Dpapi.Protect(settings.GitHubToken!),
        };
        File.WriteAllText(_path, JsonSerializer.Serialize(p, JsonOptions));
    }
}
