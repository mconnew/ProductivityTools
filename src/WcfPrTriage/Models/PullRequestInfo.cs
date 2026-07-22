namespace WcfPrTriage.Models;

/// <summary>An open pull request as returned by the GitHub REST API.</summary>
public sealed record PullRequestInfo(
    int Number,
    string Title,
    string Author,
    string HeadSha,
    string HtmlUrl,
    DateTimeOffset UpdatedAt,
    bool IsDraft);

/// <summary>
/// An Azure DevOps build referenced by an azure-pipelines GitHub check-run on a PR head commit.
/// </summary>
public sealed record CheckBuild(
    long BuildId,
    string PipelineName,
    string DetailsUrl,
    CiState State);
