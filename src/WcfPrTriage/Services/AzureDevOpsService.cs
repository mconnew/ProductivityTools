using System.Net.Http;
using System.Text.Json;

namespace WcfPrTriage.Services;

/// <summary>A single node in an Azure DevOps build timeline.</summary>
public sealed record TimelineRecord(
    string Id,
    string? ParentId,
    string Type,
    string Name,
    string? Result,
    int? LogId);

/// <summary>Basic Azure DevOps build metadata.</summary>
public sealed record BuildInfo(
    long Id,
    string BuildNumber,
    string? Status,
    string? Result,
    string DefinitionName,
    string WebUrl);

/// <summary>
/// Reads Azure DevOps builds, timelines and step logs from the public dnceng-public organization.
/// All endpoints used here allow anonymous read (the test-management APIs, which need auth, are not used).
/// </summary>
public sealed class AzureDevOpsService
{
    public const string Organization = "dnceng-public";
    public const string Project = "public";
    private static readonly string Base = $"https://dev.azure.com/{Organization}/{Project}/_apis";

    private readonly HttpClient _http;

    public AzureDevOpsService(HttpClient http) => _http = http;

    public static string BuildWebUrl(long buildId) =>
        $"https://dev.azure.com/{Organization}/{Project}/_build/results?buildId={buildId}&view=results";

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
    }

    public async Task<BuildInfo> GetBuildAsync(long buildId, CancellationToken ct)
    {
        string url = $"{Base}/build/builds/{buildId}?api-version=7.1";
        using var doc = await GetJsonAsync(url, ct).ConfigureAwait(false);
        var r = doc.RootElement;
        return new BuildInfo(
            Id: buildId,
            BuildNumber: r.TryGetProperty("buildNumber", out var bn) ? bn.GetString() ?? buildId.ToString() : buildId.ToString(),
            Status: r.TryGetProperty("status", out var st) ? st.GetString() : null,
            Result: r.TryGetProperty("result", out var res) ? res.GetString() : null,
            DefinitionName: r.TryGetProperty("definition", out var def) && def.ValueKind == JsonValueKind.Object
                ? def.GetProperty("name").GetString() ?? "pipeline"
                : "pipeline",
            WebUrl: BuildWebUrl(buildId));
    }

    public async Task<IReadOnlyList<TimelineRecord>> GetTimelineAsync(long buildId, CancellationToken ct)
    {
        string url = $"{Base}/build/builds/{buildId}/timeline?api-version=7.1";
        using var doc = await GetJsonAsync(url, ct).ConfigureAwait(false);
        var list = new List<TimelineRecord>();
        if (!doc.RootElement.TryGetProperty("records", out var records) || records.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var rec in records.EnumerateArray())
        {
            int? logId = null;
            if (rec.TryGetProperty("log", out var log) && log.ValueKind == JsonValueKind.Object &&
                log.TryGetProperty("id", out var lid) && lid.ValueKind == JsonValueKind.Number)
                logId = lid.GetInt32();

            list.Add(new TimelineRecord(
                Id: rec.GetProperty("id").GetString() ?? string.Empty,
                ParentId: rec.TryGetProperty("parentId", out var pid) && pid.ValueKind == JsonValueKind.String ? pid.GetString() : null,
                Type: rec.TryGetProperty("type", out var t) ? t.GetString() ?? string.Empty : string.Empty,
                Name: rec.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty,
                Result: rec.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null,
                LogId: logId));
        }

        return list;
    }

    public async Task<string> GetLogTextAsync(long buildId, int logId, CancellationToken ct)
    {
        string url = $"{Base}/build/builds/{buildId}/logs/{logId}?api-version=7.1";
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await HttpText.ReadStringCappedAsync(resp.Content, ct).ConfigureAwait(false);
    }
}
