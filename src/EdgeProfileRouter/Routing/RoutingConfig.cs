using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using EdgeProfileRouter.Diagnostics;
using EdgeProfileRouter.Edge;

namespace EdgeProfileRouter.Routing;

/// <summary>
/// The persisted routing configuration: an ordered list of <see cref="RoutingRule"/> plus an
/// optional override for the Edge executable path. Stored as indented JSON at
/// <c>%APPDATA%\EdgeProfileRouter\config.json</c>.
/// </summary>
public sealed class RoutingConfig
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Optional explicit path to <c>msedge.exe</c>; null uses auto-detection.</summary>
    public string? EdgePathOverride { get; set; }

    /// <summary>Rules in priority order — the first that matches a URL wins.</summary>
    public List<RoutingRule> Rules { get; set; } = new();

    /// <summary>Folder that holds the config file.</summary>
    [JsonIgnore]
    public static string DirectoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "EdgeProfileRouter");

    /// <summary>Full path to the config file.</summary>
    [JsonIgnore]
    public static string FilePath => Path.Combine(DirectoryPath, "config.json");

    /// <summary>True when a config file already exists on disk.</summary>
    public static bool Exists() => File.Exists(FilePath);

    /// <summary>
    /// Loads the config, or returns an empty config (no rules → every URL is handed to Edge)
    /// if the file is missing or unreadable. Never throws.
    /// </summary>
    public static RoutingConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                string json = File.ReadAllText(FilePath);
                RoutingConfig? cfg = JsonSerializer.Deserialize<RoutingConfig>(json, JsonOptions);
                if (cfg is not null)
                {
                    cfg.Rules ??= new List<RoutingRule>();
                    return cfg;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write("Config load failed — " + ex.Message);
        }
        return new RoutingConfig();
    }

    /// <summary>Writes the config to disk as indented JSON, creating the folder if needed.</summary>
    public void Save()
    {
        Directory.CreateDirectory(DirectoryPath);
        string json = JsonSerializer.Serialize(this, JsonOptions);
        File.WriteAllText(FilePath, json);
        Log.Write("Config saved to " + FilePath + " (" + Rules.Count + " rule(s)).");
    }

    /// <summary>Returns the first enabled rule that matches the URL, or null if none do.</summary>
    public RoutingRule? Match(Uri uri, string fullUrl)
    {
        foreach (RoutingRule rule in Rules)
        {
            if (rule.Matches(uri, fullUrl))
                return rule;
        }
        return null;
    }

    /// <summary>
    /// Builds a starter config that demonstrates the two example rules
    /// (<c>github.com/CoreWCF/*</c> → a personal profile, <c>github.com/dotnet/*</c> → a work
    /// profile), pre-selecting sensible profiles when they can be guessed from the signed-in
    /// account e-mail. Used by the settings window the first time it is opened.
    /// </summary>
    public static RoutingConfig CreateSeededExample(IReadOnlyList<EdgeProfile> profiles)
    {
        EdgeProfile? personal = profiles.FirstOrDefault(p => IsConsumerAccount(p.UserName));
        EdgeProfile? work = profiles.FirstOrDefault(
                                p => !IsConsumerAccount(p.UserName) && !string.IsNullOrWhiteSpace(p.UserName))
                            ?? profiles.FirstOrDefault(p => p != personal)
                            ?? profiles.FirstOrDefault();
        personal ??= profiles.FirstOrDefault(p => p != work) ?? profiles.FirstOrDefault();

        return new RoutingConfig
        {
            Rules =
            {
                new RoutingRule
                {
                    Name = "GitHub: CoreWCF → personal",
                    HostPattern = "github.com",
                    PathPrefix = "/CoreWCF/",
                    ProfileDirectory = personal?.Directory ?? string.Empty,
                    ProfileLabel = personal?.DisplayLabel,
                },
                new RoutingRule
                {
                    Name = "GitHub: dotnet → work",
                    HostPattern = "github.com",
                    PathPrefix = "/dotnet/",
                    ProfileDirectory = work?.Directory ?? string.Empty,
                    ProfileLabel = work?.DisplayLabel,
                },
            },
        };
    }

    private static readonly string[] ConsumerDomains =
    {
        "outlook.com", "hotmail.com", "live.com", "msn.com",
        "gmail.com", "googlemail.com", "yahoo.com", "icloud.com", "me.com", "proton.me",
    };

    private static bool IsConsumerAccount(string? userName)
    {
        if (string.IsNullOrWhiteSpace(userName))
            return false;
        int at = userName.LastIndexOf('@');
        if (at < 0 || at == userName.Length - 1)
            return false;
        string domain = userName[(at + 1)..].Trim().ToLowerInvariant();
        return ConsumerDomains.Contains(domain);
    }
}
