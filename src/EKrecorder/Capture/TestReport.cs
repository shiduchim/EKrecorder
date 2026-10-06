using System.Text;
using EKrecorder.Diagnostics;

namespace EKrecorder.Capture;

/// <summary>report.txt: an overall verdict, one PASS/FAIL/WARN/INFO line per check, then the details.</summary>
internal sealed class TestReport
{
    public const string Pass = "PASS";
    public const string Fail = "FAIL";
    public const string Warn = "WARN";
    public const string Info = "INFO";

    private readonly List<(string Status, string Text)> _results = new();
    private readonly List<(string Title, List<string> Lines)> _sections = new();

    public int FailureCount => _results.Count(r => r.Status == Fail);

    public bool HasFailures => FailureCount > 0;

    public string ShortSummary => HasFailures
        ? $"Done: {FailureCount} check(s) failed. See report.txt in the results folder."
        : "Done: all checks passed. See report.txt in the results folder.";

    public void Add(string status, string text)
    {
        _results.Add((status, text));
        Log.Info($"RESULT {status} {text}");
    }

    public void AddSection(string title, IEnumerable<string> lines) => _sections.Add((title, lines.ToList()));

    public void Save(string path)
    {
        var text = new StringBuilder();
        text.AppendLine("EKrecorder capture spike: test report");
        text.AppendLine($"Created {DateTime.Now:yyyy-MM-dd HH:mm:ss} by EKrecorder {EnvironmentInfo.AppVersion}");
        text.AppendLine();
        text.AppendLine(HasFailures ? $"OVERALL: FAIL ({FailureCount} check(s) failed, marked FAIL below)" : "OVERALL: PASS");
        text.AppendLine();
        foreach ((string status, string line) in _results)
        {
            text.AppendLine($"{status}  {line}");
        }

        foreach ((string title, List<string> lines) in _sections)
        {
            text.AppendLine();
            text.AppendLine(title);
            foreach (string line in lines)
            {
                text.AppendLine($"  {line}");
            }
        }

        File.WriteAllText(path, text.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }
}
