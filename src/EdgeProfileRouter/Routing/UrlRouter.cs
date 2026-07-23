using System;
using System.Diagnostics;
using EdgeProfileRouter.Diagnostics;
using EdgeProfileRouter.Edge;

namespace EdgeProfileRouter.Routing;

/// <summary>The outcome of evaluating a URL against the routing rules (no side effects).</summary>
/// <param name="Target">The URL/path being routed (trimmed).</param>
/// <param name="Rule">The matched rule, or null when none matched.</param>
/// <param name="ProfileDirectory">Profile to force, or empty string to let Edge decide.</param>
/// <param name="Explanation">Human-readable reason (for the test panel and logs).</param>
public sealed record RouteDecision(string Target, RoutingRule? Rule, string ProfileDirectory, string Explanation)
{
    /// <summary>True when a specific Edge profile will be forced for this URL.</summary>
    public bool ForcesProfile => !string.IsNullOrEmpty(ProfileDirectory);
}

/// <summary>
/// Evaluates URLs against the configured rules and launches Microsoft Edge accordingly.
///
/// <para>Design guarantees:</para>
/// <list type="bullet">
///   <item>A matched rule opens the URL in a fixed profile via <c>--profile-directory</c>.</item>
///   <item>No match hands the URL to Edge <b>without</b> a profile flag, so Edge's own
///     last-used / automatic-profile behaviour applies — a transparent pass-through.</item>
///   <item>The URL is always passed to Edge as a single, isolated argv entry after a <c>--</c>
///     end-of-switches marker, so a hostile link can never smuggle in extra Edge switches.</item>
/// </list>
/// </summary>
internal static class UrlRouter
{
    /// <summary>Works out what would happen for <paramref name="url"/> without launching anything.</summary>
    internal static RouteDecision Decide(RoutingConfig config, string? url)
    {
        string target = (url ?? string.Empty).Trim();

        if (target.Length == 0)
            return new RouteDecision(target, null, string.Empty, "No URL supplied.");

        if (!Uri.TryCreate(target, UriKind.Absolute, out Uri? uri))
            return new RouteDecision(target, null, string.Empty,
                "Not an absolute URL — handed to Edge to decide the profile.");

        bool isWeb = uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;
        if (!isWeb)
            return new RouteDecision(target, null, string.Empty,
                $"Non-web URL ({uri.Scheme}) — handed to Edge to decide the profile.");

        RoutingRule? rule = config.Match(uri, target);
        if (rule is null)
            return new RouteDecision(target, null, string.Empty,
                "No rule matched — handed to Edge to decide the profile.");

        if (string.IsNullOrEmpty(rule.ProfileDirectory))
            return new RouteDecision(target, rule, string.Empty,
                $"Matched \"{rule.Name}\" but it has no profile set — handed to Edge to decide.");

        return new RouteDecision(target, rule, rule.ProfileDirectory,
            $"Matched \"{rule.Name}\" → opening in profile \"{rule.ProfileDirectory}\".");
    }

    /// <summary>
    /// Routes <paramref name="url"/>: opens it in the matched profile, or hands it to Edge when
    /// no rule matches. Returns true if Edge was launched.
    /// </summary>
    internal static bool LaunchForUrl(RoutingConfig config, string? url)
    {
        RouteDecision decision = Decide(config, url);
        Log.Write("Route: " + decision.Target + " — " + decision.Explanation);
        return StartEdge(config, decision.ProfileDirectory, decision.Target);
    }

    /// <summary>
    /// Launches Edge with no forced profile (used for the "open the browser itself" verb and as
    /// the hand-off path). <paramref name="url"/> may be null to just open a new Edge window.
    /// </summary>
    internal static bool LaunchPlain(RoutingConfig config, string? url) =>
        StartEdge(config, string.Empty, url);

    private static bool StartEdge(RoutingConfig config, string profileDirectory, string? url)
    {
        string? edge = EdgeLocator.Resolve(config.EdgePathOverride);
        if (edge is null)
        {
            Log.Write("StartEdge: Edge not found; cannot launch.");
            return false;
        }

        try
        {
            var psi = new ProcessStartInfo(edge) { UseShellExecute = false };

            if (!string.IsNullOrEmpty(profileDirectory))
                psi.ArgumentList.Add("--profile-directory=" + profileDirectory);

            if (!string.IsNullOrEmpty(url))
            {
                // "--" ends Chromium switch parsing; the URL is then an inert positional arg.
                psi.ArgumentList.Add("--");
                psi.ArgumentList.Add(url);
            }

            Process.Start(psi);
            Log.Write("Launched Edge: \"" + edge + "\""
                + (string.IsNullOrEmpty(profileDirectory) ? " (no profile flag)" : " --profile-directory=" + profileDirectory)
                + (string.IsNullOrEmpty(url) ? string.Empty : " -- " + url));
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("StartEdge failed — " + ex.Message);
            return false;
        }
    }
}
