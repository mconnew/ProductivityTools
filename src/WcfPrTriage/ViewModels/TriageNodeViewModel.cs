using System.Collections.ObjectModel;
using System.Text;
using WcfPrTriage.Models;

namespace WcfPrTriage.ViewModels;

/// <summary>What kind of triage node this is (drives the icon/glyph shown in the tree).</summary>
public enum TriageNodeKind
{
    Build,
    Queue,
    WorkItem,
    Test,
    BuildError,
    Info,
}

/// <summary>A node in the failure tree shown for the selected PR. Selecting one populates the detail pane.</summary>
public sealed class TriageNodeViewModel : ObservableObject
{
    private bool _isExpanded;
    private bool _isSelected;

    public TriageNodeViewModel(TriageNodeKind kind, string title, string? subtitle, string detailHeader, string detailText, string? openUrl)
    {
        Kind = kind;
        Title = title;
        Subtitle = subtitle;
        DetailHeader = detailHeader;
        DetailText = detailText;
        OpenUrl = openUrl;
    }

    public TriageNodeKind Kind { get; }
    public string Title { get; }
    public string? Subtitle { get; }
    public string DetailHeader { get; }
    public string DetailText { get; }
    public string? OpenUrl { get; }

    public ObservableCollection<TriageNodeViewModel> Children { get; } = new();

    public string Glyph => Kind switch
    {
        TriageNodeKind.Build => "🏗️",
        TriageNodeKind.Queue => "🐧",
        TriageNodeKind.WorkItem => "📦",
        TriageNodeKind.Test => "❌",
        TriageNodeKind.BuildError => "⚠️",
        _ => "ℹ️",
    };

    public bool HasOpenUrl => !string.IsNullOrWhiteSpace(OpenUrl);

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    /// <summary>
    /// Above this many failing tests, work-item nodes start collapsed so the tree stays light
    /// (their test children aren't realized until expanded). Build/queue nodes stay expanded.
    /// </summary>
    private const int LargeResultTestThreshold = 300;

    /// <summary>Builds the top-level forest of nodes for a PR triage result.</summary>
    public static ObservableCollection<TriageNodeViewModel> BuildForest(PrTriageResult result)
    {
        var roots = new ObservableCollection<TriageNodeViewModel>();

        int totalTests = 0;
        foreach (var b in result.Builds)
            foreach (var q in b.Queues)
                foreach (var wi in q.FailedWorkItems)
                    totalTests += wi.FailedTests.Count;
        bool expandWorkItems = totalTests <= LargeResultTestThreshold;

        foreach (var note in result.Notes)
        {
            roots.Add(new TriageNodeViewModel(
                TriageNodeKind.Info, note, null, "Note", note, null));
        }

        foreach (var build in result.Builds)
        {
            var buildNode = new TriageNodeViewModel(
                TriageNodeKind.Build,
                $"{build.PipelineName}  #{build.BuildNumber}",
                Pluralize(build.Queues.Count, "failing queue") + (build.OtherFailures.Count > 0 ? $", {Pluralize(build.OtherFailures.Count, "build error")}" : ""),
                $"{build.PipelineName}  #{build.BuildNumber}",
                $"Azure DevOps build {build.BuildId}\n{build.WebUrl}",
                build.WebUrl)
            { IsExpanded = true };

            foreach (var err in build.OtherFailures)
            {
                buildNode.Children.Add(new TriageNodeViewModel(
                    TriageNodeKind.BuildError,
                    err.Name,
                    err.Stage,
                    $"Build error — {err.Name} ({err.Stage})",
                    err.LogTail,
                    err.LogUrl));
            }

            foreach (var q in build.Queues)
            {
                string qTitle = q.QueueName;
                string qSub = q.Configuration;
                var queueNode = new TriageNodeViewModel(
                    TriageNodeKind.Queue,
                    qTitle,
                    qSub,
                    $"{q.QueueName}  ({q.Configuration})",
                    $"Helix job {q.JobId}\n{q.JobDetailsUrl}",
                    q.JobDetailsUrl)
                { IsExpanded = true };

                foreach (var wi in q.FailedWorkItems)
                {
                    var wiNode = new TriageNodeViewModel(
                        TriageNodeKind.WorkItem,
                        wi.Name,
                        $"exit {wi.ExitCode}" + (wi.FailedTests.Count > 0 ? $", {Pluralize(wi.FailedTests.Count, "test")}" : ""),
                        wi.Name,
                        BuildWorkItemDetail(wi),
                        string.IsNullOrWhiteSpace(wi.ConsoleUri) ? null : wi.ConsoleUri)
                    { IsExpanded = expandWorkItems };

                    foreach (var test in wi.FailedTests)
                    {
                        wiNode.Children.Add(new TriageNodeViewModel(
                            TriageNodeKind.Test,
                            test.TestName,
                            null,
                            test.TestName,
                            BuildTestDetail(test),
                            string.IsNullOrWhiteSpace(wi.ConsoleUri) ? null : wi.ConsoleUri));
                    }

                    queueNode.Children.Add(wiNode);
                }

                buildNode.Children.Add(queueNode);
            }

            roots.Add(buildNode);
        }

        return roots;
    }

    private static string BuildTestDetail(TestFailure test)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(test.Message))
        {
            sb.AppendLine("Message");
            sb.AppendLine("───────");
            sb.AppendLine(test.Message.Trim());
        }
        if (!string.IsNullOrWhiteSpace(test.StackTrace))
        {
            if (sb.Length > 0) sb.AppendLine();
            sb.AppendLine("Stack Trace");
            sb.AppendLine("───────────");
            sb.AppendLine(test.StackTrace.Trim());
        }
        if (sb.Length == 0)
            sb.Append(test.RawBlock);
        return sb.ToString().TrimEnd();
    }

    private static string BuildWorkItemDetail(HelixWorkItemFailure wi)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Work item: {wi.Name}");
        sb.AppendLine($"Exit code: {wi.ExitCode}");
        if (!string.IsNullOrWhiteSpace(wi.SummaryLine))
        {
            sb.AppendLine();
            sb.AppendLine(wi.SummaryLine.Trim());
        }
        if (wi.FailedTests.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Failed tests (select one for full detail):");
            foreach (var t in wi.FailedTests)
                sb.AppendLine("  • " + t.TestName);
        }
        else if (!string.IsNullOrWhiteSpace(wi.ConsoleTail))
        {
            sb.AppendLine();
            sb.AppendLine("No xunit failures were parsed — the work item likely crashed or timed out.");
            sb.AppendLine("Console tail:");
            sb.AppendLine();
            sb.AppendLine(wi.ConsoleTail.Trim());
        }
        return sb.ToString().TrimEnd();
    }

    private static string Pluralize(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";
}
