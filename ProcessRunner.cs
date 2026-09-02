using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace InOutButton;

/// <summary>
/// outcome of one git or rclone step (or several joined). <c>Summary</c> is the one-line
/// grid text; <c>FullOutput</c> is everything the process printed, for the log.
/// </summary>
public sealed record WorkflowResult(bool Success, bool TimedOut, int ExitCode, string Summary, string FullOutput, bool HasWarnings);

/// <summary>
/// shared, shell-free process runner used by both <see cref="GitRunner"/> and
/// <see cref="RcloneRunner"/>. arguments go through <see cref="ProcessStartInfo.ArgumentList"/>
/// so spaces and unicode in paths need no quoting.
/// </summary>
public static partial class ProcessRunner
{
    // lines that name the root cause; git and rclone both end with hint/stats noise instead
    private static readonly string[] ErrorMarkers = ["fatal:", "error:", "CRITICAL", "ERROR", "[rejected]", "Failed to"];

    public static Task<WorkflowResult> RunAsync(string fileName, string workingDirectory, int timeoutSeconds, params string[] arguments)
        => RunLabeledAsync(fileName, workingDirectory, timeoutSeconds, null, arguments);

    /// <summary>
    /// <paramref name="label"/> replaces the full command line in <c>Summary</c> (rclone
    /// command lines carry two long paths); the full command still heads <c>FullOutput</c>.
    /// </summary>
    public static async Task<WorkflowResult> RunLabeledAsync(string fileName, string workingDirectory, int timeoutSeconds, string? label, params string[] arguments)
    {
        var commandLabel = $"{fileName} {string.Join(' ', arguments)}";
        label ??= commandLabel;

        try
        {
            var (exitCode, text, timedOut) = await ExecuteAsync(fileName, workingDirectory, timeoutSeconds, arguments);
            if (timedOut)
            {
                var msg = $"{label}: timed out after {timeoutSeconds}s";
                return new WorkflowResult(false, true, -1, msg, $"{commandLabel}: timed out after {timeoutSeconds}s", false);
            }

            text = text.Trim();
            var detail = string.IsNullOrWhiteSpace(text)
                ? $"exited {exitCode}"
                : exitCode == 0 ? LastMeaningfulLine(text) : DescribeFailure(text, exitCode);
            var fullOutput = string.IsNullOrWhiteSpace(text)
                ? $"{commandLabel}: exited {exitCode}"
                : $"{commandLabel}:{Environment.NewLine}{text}";

            return new WorkflowResult(exitCode == 0, false, exitCode, $"{label}: {detail}", fullOutput, ContainsWarning(text));
        }
        catch (Exception ex)
        {
            return new WorkflowResult(false, false, -1, $"{label}: {ex.Message}", $"{commandLabel}: {ex}", false);
        }
    }

    /// <summary>
    /// raw exit code + combined output with no summary formatting — for cheap probes
    /// (last-commit time, dirty check) where the caller parses the output itself.
    /// timeout or launch failure returns (-1, "").
    /// </summary>
    public static async Task<(int ExitCode, string Output)> CaptureAsync(string fileName, string workingDirectory, int timeoutSeconds, params string[] arguments)
    {
        try
        {
            var (exitCode, text, timedOut) = await ExecuteAsync(fileName, workingDirectory, timeoutSeconds, arguments);
            return timedOut ? (-1, "") : (exitCode, text);
        }
        catch
        {
            return (-1, "");
        }
    }

    private static async Task<(int ExitCode, string Output, bool TimedOut)> ExecuteAsync(string fileName, string workingDirectory, int timeoutSeconds, string[] arguments)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        var output = new StringBuilder();
        process.OutputDataReceived += (_, args) => Append(output, args.Data);
        process.ErrorDataReceived += (_, args) => Append(output, args.Data);

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var waitTask = process.WaitForExitAsync();
        var completed = await Task.WhenAny(waitTask, Task.Delay(TimeSpan.FromSeconds(timeoutSeconds)));
        if (completed != waitTask)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // process may already have exited
            }

            return (-1, "", true);
        }

        return (process.ExitCode, output.ToString(), false);
    }

    private static void Append(StringBuilder output, string? line)
    {
        if (line is not null)
        {
            lock (output)
            {
                output.AppendLine(line);
            }
        }
    }

    public static string JoinSummaries(IEnumerable<string> summaries)
    {
        return string.Join(" | ", summaries.Where(summary => !string.IsNullOrWhiteSpace(summary)));
    }

    public static string JoinFullOutput(IEnumerable<string> outputs)
    {
        return string.Join($"{Environment.NewLine}{Environment.NewLine}", outputs.Where(output => !string.IsNullOrWhiteSpace(output)));
    }

    public static string[] SplitLines(string text)
    {
        return text.Split([Environment.NewLine, "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string LastMeaningfulLine(string text)
    {
        return SplitLines(text).LastOrDefault() ?? text;
    }

    // first error-marked line is normally the root cause; rclone's retries repeat it,
    // git follows it with "hint:" lines, and both end with stats/hints that say nothing
    public static string DescribeFailure(string text, int exitCode)
    {
        var lines = SplitLines(text);
        var line = lines.FirstOrDefault(l => ErrorMarkers.Any(m => l.Contains(m, StringComparison.Ordinal)))
            ?? lines.LastOrDefault()
            ?? text;
        return $"{CleanLine(line)} (exit {exitCode})";
    }

    // drop rclone's "2026/09/02 17:47:50 " prefix and collapse its column padding
    private static string CleanLine(string line)
    {
        line = RcloneTimestamp().Replace(line, "");
        return Whitespace().Replace(line, " ").Trim();
    }

    private static bool ContainsWarning(string text)
    {
        return SplitLines(text).Any(line => line.StartsWith("warning:", StringComparison.OrdinalIgnoreCase));
    }

    [GeneratedRegex(@"^\d{4}/\d{2}/\d{2} \d{2}:\d{2}:\d{2}\s+")]
    private static partial Regex RcloneTimestamp();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex Whitespace();
}
