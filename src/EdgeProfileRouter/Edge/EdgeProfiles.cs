using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using EdgeProfileRouter.Diagnostics;

namespace EdgeProfileRouter.Edge;

/// <summary>
/// Enumerates the Microsoft Edge profiles configured for the current user by reading Edge's
/// <c>Local State</c> file (<c>%LOCALAPPDATA%\Microsoft\Edge\User Data\Local State</c>).
///
/// The <c>profile.info_cache</c> object maps each profile's on-disk directory name to its
/// metadata (friendly <c>name</c>, signed-in <c>user_name</c>). Reading is best-effort: any
/// failure yields an empty list so callers degrade gracefully rather than throwing.
/// </summary>
internal static class EdgeProfiles
{
    /// <summary>Full path to Edge's Local State file for the current user (may not exist).</summary>
    internal static string LocalStatePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Microsoft", "Edge", "User Data", "Local State");

    /// <summary>The directory of the profile Edge used most recently, or null if unknown.</summary>
    internal static string? LastUsedDirectory { get; private set; }

    /// <summary>
    /// Returns the configured Edge profiles, ordered with <c>Default</c> first and the
    /// remaining <c>Profile N</c> folders in numeric order. Returns an empty list if Edge is
    /// not installed or its Local State cannot be read.
    /// </summary>
    internal static IReadOnlyList<EdgeProfile> Enumerate()
    {
        var result = new List<EdgeProfile>();
        LastUsedDirectory = null;

        string path = LocalStatePath;
        if (!File.Exists(path))
        {
            Log.Write("EdgeProfiles: Local State not found at " + path);
            return result;
        }

        try
        {
            using FileStream fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using JsonDocument doc = JsonDocument.Parse(fs);

            if (!doc.RootElement.TryGetProperty("profile", out JsonElement profile))
                return result;

            if (profile.TryGetProperty("last_used", out JsonElement lastUsed)
                && lastUsed.ValueKind == JsonValueKind.String)
            {
                LastUsedDirectory = lastUsed.GetString();
            }

            if (profile.TryGetProperty("info_cache", out JsonElement cache)
                && cache.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty entry in cache.EnumerateObject())
                {
                    string dir = entry.Name;
                    string name = GetStr(entry.Value, "name");
                    string user = GetStr(entry.Value, "user_name");
                    result.Add(new EdgeProfile(dir, name, user));
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write("EdgeProfiles: failed to parse Local State — " + ex.Message);
            return new List<EdgeProfile>();
        }

        result.Sort(CompareByDirectory);
        return result;
    }

    private static string GetStr(JsonElement obj, string prop) =>
        obj.ValueKind == JsonValueKind.Object
        && obj.TryGetProperty(prop, out JsonElement v)
        && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty
            : string.Empty;

    /// <summary>Orders "Default" first, then "Profile N" numerically, then anything else by name.</summary>
    private static int CompareByDirectory(EdgeProfile a, EdgeProfile b)
    {
        int ra = Rank(a.Directory, out int na);
        int rb = Rank(b.Directory, out int nb);
        if (ra != rb) return ra.CompareTo(rb);
        if (ra == 1) return na.CompareTo(nb);
        return string.Compare(a.Directory, b.Directory, StringComparison.OrdinalIgnoreCase);
    }

    private static int Rank(string dir, out int number)
    {
        number = 0;
        if (string.Equals(dir, "Default", StringComparison.OrdinalIgnoreCase))
            return 0;
        if (dir.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(dir.AsSpan(8), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
            return 1;
        return 2;
    }
}
