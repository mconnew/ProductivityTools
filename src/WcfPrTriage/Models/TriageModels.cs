namespace WcfPrTriage.Models;

/// <summary>The complete triage result for a single pull request.</summary>
public sealed record PrTriageResult(
    IReadOnlyList<BuildFailure> Builds,
    IReadOnlyList<string> Notes,
    CiState OverallState);

/// <summary>A single Azure DevOps build (pipeline run) and everything that failed inside it.</summary>
public sealed record BuildFailure(
    long BuildId,
    string PipelineName,
    string BuildNumber,
    string WebUrl,
    CiState State,
    IReadOnlyList<HelixQueueResult> Queues,
    IReadOnlyList<NonTestFailure> OtherFailures);

/// <summary>
/// One Helix job = one test queue/distro for one build configuration (e.g. Fedora.41 on "Linux Debug").
/// </summary>
public sealed record HelixQueueResult(
    string JobId,
    string QueueName,
    string Configuration,
    string JobDetailsUrl,
    IReadOnlyList<HelixWorkItemFailure> FailedWorkItems);

/// <summary>A failed Helix work item (a single test assembly run on a queue).</summary>
public sealed record HelixWorkItemFailure(
    string Name,
    int ExitCode,
    string ConsoleUri,
    string? SummaryLine,
    IReadOnlyList<TestFailure> FailedTests,
    string ConsoleTail);

/// <summary>An individual failed test parsed from the Helix console (xunit) output.</summary>
public sealed record TestFailure(
    string TestName,
    string Message,
    string StackTrace,
    string RawBlock);

/// <summary>A non-test build failure (e.g. a compile/step error) captured from the AzDO timeline log.</summary>
public sealed record NonTestFailure(
    string Stage,
    string Name,
    string LogTail,
    string? LogUrl);
