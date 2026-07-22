using System.Text;
using System.Text.RegularExpressions;
using WcfPrTriage.Models;

namespace WcfPrTriage.Services;

/// <summary>
/// Parses the xunit console output that Helix produces for a test work item into structured
/// per-test failures. The observed format for a failing test is:
/// <code>
///     Namespace.Class.Method [FAIL]
///       System.SomeException : message
///       ---- Inner exception
///       Stack Trace:
///            at Frame1(...)
///            at Frame2(...)
/// </code>
/// followed later by a "=== TEST EXECUTION SUMMARY ===" block.
/// </summary>
public static partial class ConsoleLogParser
{
    [GeneratedRegex(@"^(?<indent>\s*)(?<name>\S.*?)\s\[FAIL\]\s*$")]
    private static partial Regex FailLineRegex();

    [GeneratedRegex(@"\s\[(FAIL|SKIP|PASS)\]\s*$")]
    private static partial Regex AnyOutcomeRegex();

    [GeneratedRegex(@"Total:\s*\d+.*?Failed:\s*\d+", RegexOptions.IgnoreCase)]
    private static partial Regex SummaryRegex();

    public sealed record ParseResult(IReadOnlyList<TestFailure> Failures, string? SummaryLine, string Tail);

    public static ParseResult Parse(string console, int tailLines = 200)
    {
        string[] lines = console.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        var failures = new List<TestFailure>();
        for (int i = 0; i < lines.Length; i++)
        {
            var m = FailLineRegex().Match(lines[i]);
            if (!m.Success)
                continue;

            int indent = m.Groups["indent"].Value.Length;
            // Ignore stray matches inside deeply-indented stack/output text.
            if (indent > 8)
                continue;

            string name = m.Groups["name"].Value.Trim();

            // Collect the body: following lines that are blank or more-indented than the title,
            // stopping at the next outcome marker or a dedent to a sibling/section line.
            var body = new List<string>();
            int j = i + 1;
            for (; j < lines.Length; j++)
            {
                string line = lines[j];
                if (line.Trim().Length == 0)
                {
                    body.Add(line);
                    continue;
                }

                int lineIndent = LeadingWhitespace(line);
                if (lineIndent <= indent)
                    break; // sibling test, "Finished:", "=== ... ===", etc.
                if (AnyOutcomeRegex().IsMatch(line) && lineIndent <= indent + 2)
                    break; // another test outcome at the same logical level

                body.Add(line);
            }

            // Trim trailing blank lines from the body.
            while (body.Count > 0 && body[^1].Trim().Length == 0)
                body.RemoveAt(body.Count - 1);

            var (message, stack) = SplitMessageAndStack(body);
            string raw = name + " [FAIL]\n" + string.Join('\n', Dedent(body));
            failures.Add(new TestFailure(name, message, stack, raw.TrimEnd()));
        }

        string? summary = null;
        foreach (var line in lines)
        {
            if (SummaryRegex().IsMatch(line))
            {
                string trimmed = line.Trim();
                summary = summary is null ? trimmed : summary + "\n" + trimmed;
            }
        }

        string tail = BuildTail(lines, tailLines);
        return new ParseResult(failures, summary, tail);
    }

    private static (string Message, string Stack) SplitMessageAndStack(List<string> body)
    {
        int stackIdx = -1;
        for (int k = 0; k < body.Count; k++)
        {
            if (body[k].Trim().Equals("Stack Trace:", StringComparison.OrdinalIgnoreCase))
            {
                stackIdx = k;
                break;
            }
        }

        if (stackIdx < 0)
            return (string.Join('\n', Dedent(body)).TrimEnd(), string.Empty);

        var messageLines = body.GetRange(0, stackIdx);

        int end = body.Count;
        for (int k = stackIdx + 1; k < body.Count; k++)
        {
            if (body[k].Trim().Equals("Output:", StringComparison.OrdinalIgnoreCase))
            {
                end = k;
                break;
            }
        }

        var stackLines = body.GetRange(stackIdx + 1, end - (stackIdx + 1));
        return (
            string.Join('\n', Dedent(messageLines)).TrimEnd(),
            string.Join('\n', Dedent(stackLines)).TrimEnd());
    }

    private static IEnumerable<string> Dedent(IReadOnlyList<string> lines)
    {
        int min = int.MaxValue;
        foreach (var l in lines)
        {
            if (l.Trim().Length == 0)
                continue;
            min = Math.Min(min, LeadingWhitespace(l));
        }
        if (min is int.MaxValue or 0)
            return lines;

        var outList = new List<string>(lines.Count);
        foreach (var l in lines)
            outList.Add(l.Length >= min ? l[min..] : l.TrimStart());
        return outList;
    }

    private static int LeadingWhitespace(string s)
    {
        int n = 0;
        foreach (char c in s)
        {
            if (c == ' ') n++;
            else if (c == '\t') n += 4;
            else break;
        }
        return n;
    }

    private static string BuildTail(string[] lines, int tailLines)
    {
        int start = Math.Max(0, lines.Length - tailLines);
        var sb = new StringBuilder();
        for (int i = start; i < lines.Length; i++)
            sb.Append(lines[i]).Append('\n');
        return sb.ToString().TrimEnd();
    }
}
