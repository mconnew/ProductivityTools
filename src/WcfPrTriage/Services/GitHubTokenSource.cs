using System.Diagnostics;

namespace WcfPrTriage.Services;

/// <summary>
/// Resolves the GitHub token used purely to raise the API rate limit (60/hr anonymous → 5000/hr).
/// Precedence:
///   1. an explicit PAT the user typed in Settings,
///   2. a token borrowed from an authenticated GitHub CLI (<c>gh auth token</c>),
///   3. none — anonymous.
/// The gh CLI token is resolved once and cached, so we never spawn a process per request. Reusing
/// the gh login means no PAT to create or rotate; gh handles OAuth, SSO and refresh for the user.
/// </summary>
public sealed class GitHubTokenSource
{
    public enum TokenKind { None, Pat, GhCli }

    private readonly Func<string?> _explicitToken;
    private string? _ghCliToken;
    private bool _ghResolved;

    public GitHubTokenSource(Func<string?> explicitToken) => _explicitToken = explicitToken;

    /// <summary>Which source the currently-active token comes from.</summary>
    public TokenKind ActiveKind
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_explicitToken()))
                return TokenKind.Pat;
            return string.IsNullOrWhiteSpace(_ghCliToken) ? TokenKind.None : TokenKind.GhCli;
        }
    }

    /// <summary>The token to send with GitHub requests, or null for anonymous access.</summary>
    public string? Resolve()
    {
        var pat = _explicitToken();
        if (!string.IsNullOrWhiteSpace(pat))
            return pat;
        return _ghCliToken;
    }

    /// <summary>
    /// Resolves the gh CLI token once (best-effort). Safe to call repeatedly; only the first call
    /// spawns <c>gh</c>. Never throws — if gh is missing, not on PATH, or not authenticated to
    /// github.com, the token stays null and the app simply runs anonymously.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_ghResolved)
            return;
        _ghResolved = true;
        _ghCliToken = await TryReadGhTokenAsync(ct).ConfigureAwait(false);
    }

    private static async Task<string?> TryReadGhTokenAsync(CancellationToken ct)
    {
        Process? proc = null;
        try
        {
            var psi = new ProcessStartInfo("gh", "auth token --hostname github.com")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            proc = Process.Start(psi);
            if (proc is null)
                return null;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));

            // Drain stdout AND stderr concurrently: if gh writes more to stderr than the pipe buffer
            // holds while we only read stdout, gh would block and we'd hang until the timeout.
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderrTask = proc.StandardError.ReadToEndAsync(timeout.Token);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            await proc.WaitForExitAsync(timeout.Token).ConfigureAwait(false);

            if (proc.ExitCode != 0)
                return null;

            string token = stdoutTask.Result.Trim();
            return string.IsNullOrWhiteSpace(token) ? null : token;
        }
        catch
        {
            // gh not installed / not on PATH / not authenticated / timed out — stay anonymous.
            try { if (proc is { HasExited: false }) proc.Kill(entireProcessTree: true); } catch { }
            return null;
        }
        finally
        {
            proc?.Dispose();
        }
    }
}
