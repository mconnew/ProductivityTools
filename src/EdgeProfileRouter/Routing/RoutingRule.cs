using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace EdgeProfileRouter.Routing;

/// <summary>
/// A single routing rule: a set of URL criteria plus the Edge profile to open matching URLs
/// in. A rule matches when <b>every</b> criterion that is specified is satisfied (criteria
/// left blank are ignored). Rules are evaluated top-to-bottom and the <b>first</b> match wins,
/// so more specific rules should be ordered above broader ones.
///
/// <para>Host matching: a plain host such as <c>github.com</c> matches that host and any
/// sub-domain (<c>api.github.com</c>). A pattern containing <c>*</c> or <c>?</c> is treated as
/// a glob against the whole host. Path matching is a case-insensitive, segment-boundary prefix,
/// so <c>/dotnet</c> matches <c>/dotnet</c> and <c>/dotnet/wcf</c> but not <c>/dotnetfoundation</c>.
/// An optional regular expression can match the entire URL for cases that need deeper inspection.</para>
/// </summary>
public sealed class RoutingRule : INotifyPropertyChanged
{
    private string _name = string.Empty;
    private bool _enabled = true;
    private string? _hostPattern;
    private string? _pathPrefix;
    private string? _urlRegex;
    private string _profileDirectory = string.Empty;
    private string? _profileLabel;

    /// <summary>Friendly description of the rule (display only).</summary>
    public string Name
    {
        get => _name;
        set => Set(ref _name, value ?? string.Empty);
    }

    /// <summary>When false the rule is skipped during matching.</summary>
    public bool Enabled
    {
        get => _enabled;
        set => Set(ref _enabled, value);
    }

    /// <summary>Host to match: exact host + sub-domains, or a glob if it contains <c>*</c>/<c>?</c>.</summary>
    public string? HostPattern
    {
        get => _hostPattern;
        set => Set(ref _hostPattern, Trim(value));
    }

    /// <summary>Case-insensitive, segment-boundary path prefix (e.g. <c>/dotnet/</c>).</summary>
    public string? PathPrefix
    {
        get => _pathPrefix;
        set => Set(ref _pathPrefix, Trim(value));
    }

    /// <summary>Optional regular expression matched (case-insensitively) against the whole URL.</summary>
    public string? UrlRegex
    {
        get => _urlRegex;
        set => Set(ref _urlRegex, Trim(value));
    }

    /// <summary>Edge profile directory to open matches in (e.g. <c>Default</c>, <c>Profile 1</c>).</summary>
    public string ProfileDirectory
    {
        get => _profileDirectory;
        set => Set(ref _profileDirectory, value ?? string.Empty);
    }

    /// <summary>Remembered friendly label for the profile (display only; not used for matching).</summary>
    public string? ProfileLabel
    {
        get => _profileLabel;
        set => Set(ref _profileLabel, value);
    }

    /// <summary>True when the rule specifies no criteria at all (it can never match).</summary>
    [JsonIgnore]
    public bool HasNoCriteria =>
        string.IsNullOrWhiteSpace(HostPattern)
        && string.IsNullOrWhiteSpace(PathPrefix)
        && string.IsNullOrWhiteSpace(UrlRegex);

    /// <summary>
    /// Returns true when this enabled rule matches the given absolute URL. A rule with no
    /// criteria never matches, so an empty/half-filled row cannot become an accidental
    /// catch-all that would swallow every link.
    /// </summary>
    public bool Matches(Uri uri, string fullUrl)
    {
        if (!Enabled || HasNoCriteria)
            return false;

        if (!string.IsNullOrWhiteSpace(HostPattern) && !HostMatches(uri.Host, HostPattern!))
            return false;

        if (!string.IsNullOrWhiteSpace(PathPrefix) && !PathMatches(uri.AbsolutePath, PathPrefix!))
            return false;

        if (!string.IsNullOrWhiteSpace(UrlRegex) && !RegexMatches(fullUrl, UrlRegex!))
            return false;

        return true;
    }

    private static bool HostMatches(string host, string pattern)
    {
        host = host.Trim().ToLowerInvariant();
        pattern = pattern.Trim().ToLowerInvariant();
        if (host.Length == 0 || pattern.Length == 0)
            return false;

        if (pattern.IndexOf('*') >= 0 || pattern.IndexOf('?') >= 0)
        {
            string rx = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            return SafeRegex(host, rx);
        }

        // Plain host: match the host itself or any sub-domain of it.
        return host == pattern || host.EndsWith("." + pattern, StringComparison.Ordinal);
    }

    private static bool PathMatches(string path, string prefix)
    {
        if (string.IsNullOrEmpty(path))
            path = "/";
        if (!prefix.StartsWith('/'))
            prefix = "/" + prefix;

        // Normalise a trailing slash so matching is on segment boundaries.
        string norm = prefix.Length > 1 ? prefix.TrimEnd('/') : prefix;

        if (norm == "/")
            return true;

        return path.Equals(norm, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(norm + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool RegexMatches(string input, string pattern) =>
        SafeRegex(input, pattern);

    private static bool SafeRegex(string input, string pattern)
    {
        try
        {
            return Regex.IsMatch(
                input, pattern,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(1));
        }
        catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException)
        {
            Diagnostics.Log.Write("Invalid/slow regex '" + pattern + "' — " + ex.Message);
            return false;
        }
    }

    /// <summary>A short human-readable summary of the rule's criteria (for logs / test output).</summary>
    public string DescribeCriteria()
    {
        var parts = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(HostPattern)) parts.Append("host=").Append(HostPattern).Append(' ');
        if (!string.IsNullOrWhiteSpace(PathPrefix)) parts.Append("path=").Append(PathPrefix).Append(' ');
        if (!string.IsNullOrWhiteSpace(UrlRegex)) parts.Append("regex=").Append(UrlRegex).Append(' ');
        return parts.Length == 0 ? "(no criteria)" : parts.ToString().TrimEnd();
    }

    private static string? Trim(string? value)
    {
        if (value is null) return null;
        string t = value.Trim();
        return t.Length == 0 ? null : t;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
