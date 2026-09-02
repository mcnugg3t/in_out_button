using System.Text.Json;
using System.Text.RegularExpressions;

namespace InOutButton;

/// <summary>
/// per-repo dataset sync configuration, loaded from <c>.rclone-sync.json</c> at the repo root.
/// </summary>
public sealed record RcloneSyncConfig(string? Remote, IReadOnlyList<RcloneFolder> Folders)
{
    public const string FileName = ".rclone-sync.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// reads <c>.rclone-sync.json</c> from <paramref name="repoPath"/>. returns <c>false</c>
    /// (without throwing) when the file is missing or invalid; invalid files invoke
    /// <paramref name="warn"/> with a human-readable reason.
    /// </summary>
    public static bool TryLoad(string repoPath, out RcloneSyncConfig? config, Action<string>? warn = null)
    {
        config = null;
        var path = Path.Combine(repoPath, FileName);
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            var dto = JsonSerializer.Deserialize<Dto>(File.ReadAllText(path), JsonOptions);
            if (dto?.Folders is null || dto.Folders.Count == 0)
            {
                warn?.Invoke($"{Path.GetFileName(repoPath)}: {FileName} has no folders; skipping rclone sync.");
                return false;
            }

            var folders = new List<RcloneFolder>(dto.Folders.Count);
            foreach (var folder in dto.Folders)
            {
                if (string.IsNullOrWhiteSpace(folder.Local))
                {
                    warn?.Invoke($"{Path.GetFileName(repoPath)}: {FileName} has a folder with no 'local' path; skipping rclone sync.");
                    return false;
                }

                var excludes = folder.Exclude is { Count: > 0 }
                    ? folder.Exclude.Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e.Trim()).ToList()
                    : (IReadOnlyList<string>)Array.Empty<string>();
                folders.Add(new RcloneFolder(folder.Local.Trim(), string.IsNullOrWhiteSpace(folder.Remote) ? null : folder.Remote.Trim(), excludes));
            }

            config = new RcloneSyncConfig(string.IsNullOrWhiteSpace(dto.Remote) ? null : dto.Remote.Trim(), folders);
            return true;
        }
        catch (Exception ex)
        {
            warn?.Invoke($"{Path.GetFileName(repoPath)}: failed to read {FileName} ({ex.Message}); skipping rclone sync.");
            return false;
        }
    }

    private sealed class Dto
    {
        public string? Remote { get; set; }
        public List<FolderDto>? Folders { get; set; }
    }

    private sealed class FolderDto
    {
        public string? Local { get; set; }
        public string? Remote { get; set; }
        public List<string>? Exclude { get; set; }
    }
}

/// <param name="Local">path relative to the repo root; a folder or a single file.</param>
/// <param name="Exclude">rclone <c>--exclude</c> patterns (relative to the folder root), e.g. a
/// raw dataset file that should stay local. applied to both pull and push.</param>
public sealed record RcloneFolder(string Local, string? Remote, IReadOnlyList<string> Exclude);

public static partial class RcloneRunner
{
    // rclone exit code for "source directory not found" — on a pull this means nothing has been pushed yet
    private const int ExitDirectoryNotFound = 3;

    /// <summary>runs <c>rclone lsd &lt;remote&gt;:</c> as a connectivity / auth check.</summary>
    public static Task<WorkflowResult> TestRemoteAsync(string remote, int timeoutSeconds)
    {
        return ProcessRunner.RunAsync("rclone", Environment.CurrentDirectory, timeoutSeconds, "lsd", $"{remote.TrimEnd(':')}:");
    }

    /// <summary>checks once that rclone is on PATH; returns its version line, or null if missing.</summary>
    public static async Task<string?> ProbeAsync(int timeoutSeconds)
    {
        var (exitCode, output) = await ProcessRunner.CaptureAsync("rclone", Environment.CurrentDirectory, timeoutSeconds, "--version");
        if (exitCode != 0)
        {
            return null;
        }

        return ProcessRunner.SplitLines(output)
            .FirstOrDefault(line => line.StartsWith("rclone", StringComparison.OrdinalIgnoreCase)) ?? "rclone (version unknown)";
    }

    /// <summary>remote → local for every configured entry (sign in).</summary>
    public static Task<WorkflowResult> RclonePullAsync(string repoPath, RcloneSyncConfig config, AppSettings settings, int timeoutSeconds)
        => RunFoldersAsync(repoPath, config, settings, timeoutSeconds, pull: true);

    /// <summary>local → remote for every configured entry (sign out).</summary>
    public static Task<WorkflowResult> RclonePushAsync(string repoPath, RcloneSyncConfig config, AppSettings settings, int timeoutSeconds)
        => RunFoldersAsync(repoPath, config, settings, timeoutSeconds, pull: false);

    // one rclone process per entry. a missing side is normal, not a failure: nothing local yet
    // (fresh clone) skips the push, nothing remote yet (never pushed) skips the pull.
    private static async Task<WorkflowResult> RunFoldersAsync(string repoPath, RcloneSyncConfig config, AppSettings settings, int timeoutSeconds, bool pull)
    {
        var remoteName = config.Remote ?? settings.RcloneRemote;
        if (string.IsNullOrWhiteSpace(remoteName))
        {
            var msg = $"rclone {(pull ? "pull" : "push")}: no remote configured (set one in settings or {RcloneSyncConfig.FileName}).";
            return new WorkflowResult(false, false, -1, msg, msg, false);
        }

        remoteName = remoteName.TrimEnd(':');

        var summaries = new List<string>();
        var fullOutput = new List<string>();
        var success = true;
        var timedOut = false;
        var hasWarnings = false;
        var exitCode = 0;

        foreach (var folder in config.Folders)
        {
            var localPath = Path.GetFullPath(Path.Combine(repoPath, folder.Local));
            var remoteSpec = $"{remoteName}:{BuildRemotePath(settings.RcloneRemoteRoot, repoPath, folder)}";
            var label = $"rclone {(pull ? "pull" : "push")} {folder.Local}";

            bool isFile;
            if (File.Exists(localPath))
            {
                isFile = true;
            }
            else if (Directory.Exists(localPath))
            {
                isFile = false;
            }
            else if (!pull)
            {
                summaries.Add($"{folder.Local}: not present locally, nothing to push");
                continue;
            }
            else
            {
                // first pull on this machine: the remote decides whether the entry is a file or a folder
                var (kind, detail) = await StatRemoteAsync(remoteSpec, repoPath, timeoutSeconds);
                if (kind == RemoteKind.Missing)
                {
                    summaries.Add($"{folder.Local}: nothing on remote yet ({remoteSpec})");
                    continue;
                }

                if (kind == RemoteKind.Error)
                {
                    // auth/network/typo — a failure, not a skip, or the user works on without the data
                    summaries.Add($"{label}: {detail}");
                    success = false;
                    exitCode = -1;
                    continue;
                }

                isFile = kind == RemoteKind.File;
            }

            var source = pull ? remoteSpec : localPath;
            var destination = pull ? localPath : remoteSpec;

            // `copy` reads a file source as "copy into destination folder" and dies when the
            // destination is a file ("is a file not a directory"); `copyto` maps file → file.
            // for folders `copyto` behaves like `copy` but rejects --create-empty-src-dirs.
            var args = isFile
                ? new List<string> { "copyto", source, destination, "--stats-log-level", "NOTICE" }
                : new List<string> { "copy", source, destination, "--create-empty-src-dirs", "--stats-log-level", "NOTICE" };
            foreach (var pattern in folder.Exclude)
            {
                args.Add("--exclude");
                args.Add(pattern);
            }

            var result = await ProcessRunner.RunLabeledAsync("rclone", repoPath, timeoutSeconds, label, args.ToArray());
            fullOutput.Add(result.FullOutput);
            hasWarnings |= result.HasWarnings;

            if (result.Success)
            {
                summaries.Add($"{folder.Local}: {DescribeTransfer(result.FullOutput)}");
                continue;
            }

            if (pull && result.ExitCode == ExitDirectoryNotFound)
            {
                // local exists, remote doesn't: usually a dataset that hasn't been pushed yet, but a
                // wrong remote root looks identical — amber, not green, so it gets a glance
                summaries.Add($"{folder.Local}: nothing on remote yet ({remoteSpec}); check the remote root if unexpected");
                hasWarnings = true;
                continue;
            }

            summaries.Add(result.Summary);
            success = false;
            timedOut |= result.TimedOut;
            exitCode = result.ExitCode;
        }

        return new WorkflowResult(
            success,
            timedOut,
            exitCode,
            ProcessRunner.JoinSummaries(summaries),
            ProcessRunner.JoinFullOutput(fullOutput),
            hasWarnings);
    }

    private enum RemoteKind
    {
        File,
        Folder,
        Missing,
        Error,
    }

    /// <summary>
    /// <c>rclone lsjson --stat</c> on one remote path. exit 3 is "not there"; any other failure
    /// (auth, network, bad remote name) is <see cref="RemoteKind.Error"/> with a one-line reason.
    /// </summary>
    private static async Task<(RemoteKind Kind, string Detail)> StatRemoteAsync(string remoteSpec, string workingDirectory, int timeoutSeconds)
    {
        var (exitCode, output) = await ProcessRunner.CaptureAsync("rclone", workingDirectory, timeoutSeconds, "lsjson", "--stat", remoteSpec);
        if (exitCode == ExitDirectoryNotFound)
        {
            return (RemoteKind.Missing, "");
        }

        if (exitCode != 0)
        {
            return (RemoteKind.Error, exitCode == -1 || string.IsNullOrWhiteSpace(output)
                ? "rclone lsjson failed to run or timed out"
                : ProcessRunner.DescribeFailure(output, exitCode));
        }

        try
        {
            // stdout and stderr are merged; a stray NOTICE line before the json must not sink the probe
            var jsonStart = output.IndexOfAny(['{', '[']);
            using var doc = JsonDocument.Parse(jsonStart > 0 ? output[jsonStart..] : output);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                root = root.EnumerateArray().FirstOrDefault();
            }

            if (root.ValueKind != JsonValueKind.Object)
            {
                return (RemoteKind.Error, "unexpected rclone lsjson output");
            }

            var isDir = root.TryGetProperty("IsDir", out var flag) && flag.ValueKind == JsonValueKind.True;
            return (isDir ? RemoteKind.Folder : RemoteKind.File, "");
        }
        catch (Exception ex)
        {
            return (RemoteKind.Error, $"could not read rclone lsjson output ({ex.Message})");
        }
    }

    // final stats block is forced by --stats-log-level NOTICE; no file-count line means nothing moved
    private static string DescribeTransfer(string output)
    {
        var count = TransferCount().Match(output);
        if (!count.Success || !int.TryParse(count.Groups[1].Value, out var files) || files == 0)
        {
            return "up to date";
        }

        var bytes = TransferBytes().Match(output);
        var size = bytes.Success ? $" ({bytes.Groups[1].Value})" : "";
        return $"{files} file{(files == 1 ? "" : "s")} transferred{size}";
    }

    /// <summary>
    /// maps an entry to its remote subpath: an explicit <c>remote</c> wins, otherwise
    /// <c>&lt;RemoteRoot&gt;/&lt;repo-name&gt;/&lt;local&gt;</c> with forward slashes.
    /// </summary>
    private static string BuildRemotePath(string remoteRoot, string repoPath, RcloneFolder folder)
    {
        if (!string.IsNullOrWhiteSpace(folder.Remote))
        {
            return folder.Remote.Replace('\\', '/').Trim('/');
        }

        var repoName = Path.GetFileName(repoPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var localNormalized = folder.Local.Replace('\\', '/').Trim('/');
        var root = remoteRoot.Replace('\\', '/').Trim('/');
        return $"{root}/{repoName}/{localNormalized}";
    }

    // "Transferred:            1 / 1, 100%" — the file-count line (the byte line has a unit before the slash)
    [GeneratedRegex(@"Transferred:\s+(\d+)\s*/\s*\d+,")]
    private static partial Regex TransferCount();

    // "Transferred:   	   10.570 MiB / 10.570 MiB, 100%, ..."
    [GeneratedRegex(@"Transferred:\s+([\d.]+\s*[KMGTPE]?i?B)\s*/")]
    private static partial Regex TransferBytes();
}
