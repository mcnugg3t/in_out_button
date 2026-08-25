using System.Diagnostics;
using System.Text;

namespace InOutButton;

/// <summary>
/// Shared, shell-free process runner used by both <see cref="GitRunner"/> and
/// <see cref="RcloneRunner"/>. Arguments are passed via <see cref="ProcessStartInfo.ArgumentList"/>
/// so spaces and unicode in paths need no quoting.
/// </summary>
public static class ProcessRunner
{
    public static async Task<GitWorkflowResult> RunAsync(string fileName, string workingDirectory, int timeoutSeconds, params string[] arguments)
    {
        var commandLabel = $"{fileName} {string.Join(' ', arguments)}";
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
        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                output.AppendLine(args.Data);
            }
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                output.AppendLine(args.Data);
            }
        };

        try
        {
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
                    // Process may already have exited.
                }

                return new GitWorkflowResult(false, true, -1, $"{commandLabel}: timed out after {timeoutSeconds}s", $"{commandLabel}: timed out after {timeoutSeconds}s", false);
            }

            var text = output.ToString().Trim();
            var detail = string.IsNullOrWhiteSpace(text)
                ? $"exited {process.ExitCode}"
                : LastMeaningfulLine(text);
            var summary = $"{commandLabel}: {detail}";
            var fullOutput = string.IsNullOrWhiteSpace(text)
                ? $"{commandLabel}: exited {process.ExitCode}"
                : $"{commandLabel}:{Environment.NewLine}{text}";

            return new GitWorkflowResult(process.ExitCode == 0, false, process.ExitCode, summary, fullOutput, ContainsWarning(text));
        }
        catch (Exception ex)
        {
            return new GitWorkflowResult(false, false, -1, $"{commandLabel}: {ex.Message}", $"{commandLabel}: {ex}", false);
        }
    }

    /// <summary>
    /// raw exit code + combined output with no summary formatting — for cheap probes
    /// (last-commit time, dirty check) where the caller parses the output itself.
    /// timeout or launch failure returns (-1, "").
    /// </summary>
    public static async Task<(int ExitCode, string Output)> CaptureAsync(string fileName, string workingDirectory, int timeoutSeconds, params string[] arguments)
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
        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                output.AppendLine(args.Data);
            }
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                output.AppendLine(args.Data);
            }
        };

        try
        {
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

                return (-1, "");
            }

            return (process.ExitCode, output.ToString());
        }
        catch
        {
            return (-1, "");
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

    private static string LastMeaningfulLine(string text)
    {
        return text.Split([Environment.NewLine, "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? text;
    }

    private static bool ContainsWarning(string text)
    {
        return text.Split([Environment.NewLine, "\n"], StringSplitOptions.RemoveEmptyEntries)
            .Any(line => line.TrimStart().StartsWith("warning:", StringComparison.OrdinalIgnoreCase));
    }
}
