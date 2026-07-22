using System.Net.Http;
using System.Text.Json;

namespace WcfPrTriage.Services;

/// <summary>A Helix work item (a single test assembly run on a queue).</summary>
public sealed record HelixWorkItem(
    string Name,
    int ExitCode,
    string? ConsoleUri,
    string State);

/// <summary>Details about a Helix job (one queue/distro for one build configuration).</summary>
public sealed record HelixJobDetails(
    string JobId,
    string QueueId,
    string Source,
    string Name);

/// <summary>Reads the public Helix API (helix.dot.net) — jobs, work items and console logs (all anonymous).</summary>
public sealed class HelixService
{
    private const string ApiVersion = "2019-06-17";
    private readonly HttpClient _http;

    public HelixService(HttpClient http) => _http = http;

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
    }

    // Helix returns PascalCase JSON; be tolerant of casing.
    private static bool TryGetProp(JsonElement obj, string name, out JsonElement value)
    {
        if (obj.TryGetProperty(name, out value))
            return true;
        foreach (var p in obj.EnumerateObject())
        {
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = p.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    private static string GetString(JsonElement obj, string name) =>
        TryGetProp(obj, name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

    public async Task<HelixJobDetails> GetJobDetailsAsync(string jobId, CancellationToken ct)
    {
        string url = $"https://helix.dot.net/api/jobs/{jobId}/details?api-version={ApiVersion}";
        using var doc = await GetJsonAsync(url, ct).ConfigureAwait(false);
        var r = doc.RootElement;
        return new HelixJobDetails(
            JobId: jobId,
            QueueId: GetString(r, "QueueId"),
            Source: GetString(r, "Source"),
            Name: GetString(r, "Name"));
    }

    public async Task<IReadOnlyList<HelixWorkItem>> GetWorkItemsAsync(string jobId, CancellationToken ct)
    {
        string url = $"https://helix.dot.net/api/jobs/{jobId}/workitems?api-version={ApiVersion}";
        using var doc = await GetJsonAsync(url, ct).ConfigureAwait(false);
        var list = new List<HelixWorkItem>();
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var wi in doc.RootElement.EnumerateArray())
        {
            int exit = TryGetProp(wi, "ExitCode", out var ec) && ec.ValueKind == JsonValueKind.Number ? ec.GetInt32() : 0;
            list.Add(new HelixWorkItem(
                Name: GetString(wi, "Name"),
                ExitCode: exit,
                ConsoleUri: TryGetProp(wi, "ConsoleOutputUri", out var cu) && cu.ValueKind == JsonValueKind.String ? cu.GetString() : null,
                State: GetString(wi, "State")));
        }

        return list;
    }

    public async Task<string> GetConsoleAsync(string consoleUri, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(consoleUri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await HttpText.ReadStringCappedAsync(resp.Content, ct).ConfigureAwait(false);
    }
}
