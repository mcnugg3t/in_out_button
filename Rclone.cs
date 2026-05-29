using System.Text.Json;

namespace InOutButton;

/// <summary>
/// Per-repo dataset sync configuration, loaded from <c>.rclone-sync.json</c> at the repo root.
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
    /// Reads <c>.rclone-sync.json</c> from <paramref name="repoPath"/>. Returns <c>false</c>
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

                folders.Add(new RcloneFolder(folder.Local.Trim(), string.IsNullOrWhiteSpace(folder.Remote) ? null : folder.Remote.Trim()));
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
    }
}

public sealed record RcloneFolder(string Local, string? Remote);

public static class RcloneRunner
{
    /// <summary>Runs <c>rclone lsd &lt;remote&gt;:</c> as a connectivity / auth check.</summary>
    public static Task<GitWorkflowResult> TestRemoteAsync(string remote, int timeoutSeconds)
    {
        return ProcessRunner.RunAsync("rclone", Environment.CurrentDirectory, timeoutSeconds, "lsd", $"{remote.TrimEnd(':')}:");
    }

    /// <summary>Checks once that rclone is on PATH; returns its version line, or null if missing.</summary>
    public static async Task<string?> ProbeAsync(int timeoutSeconds)
    {
        var result = await ProcessRunner.RunAsync("rclone", Environment.CurrentDirectory, timeoutSeconds, "--version");
        if (!result.Success)
        {
            return null;
        }

        return result.FullOutput
            .Split([Environment.NewLine, "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(line => line.StartsWith("rclone", StringComparison.OrdinalIgnoreCase)) ?? "rclone (version unknown)";
    }

    /// <summary>Remote → local for every configured folder (sign in).</summary>
    public static Task<GitWorkflowResult> RclonePullAsync(string repoPath, RcloneSyncConfig config, AppSettings settings, int timeoutSeconds)
        => RunFoldersAsync(repoPath, config, settings, timeoutSeconds, pull: true);

    /// <summary>Local → remote for every configured folder (sign out).</summary>
    public static Task<GitWorkflowResult> RclonePushAsync(string repoPath, RcloneSyncConfig config, AppSettings settings, int timeoutSeconds)
        => RunFoldersAsync(repoPath, config, settings, timeoutSeconds, pull: false);

    private static async Task<GitWorkflowResult> RunFoldersAsync(string repoPath, RcloneSyncConfig config, AppSettings settings, int timeoutSeconds, bool pull)
    {
        var remoteName = config.Remote ?? settings.RcloneRemote;
        if (string.IsNullOrWhiteSpace(remoteName))
        {
            var msg = $"rclone {(pull ? "pull" : "push")}: no remote configured (set one in settings or {RcloneSyncConfig.FileName}).";
            return new GitWorkflowResult(false, false, -1, msg, msg, false);
        }

        remoteName = remoteName.TrimEnd(':');

        var outputs = new List<string>();
        var fullOutput = new List<string>();
        var success = true;
        var timedOut = false;
        var hasWarnings = false;
        var exitCode = 0;

        foreach (var folder in config.Folders)
        {
            var localPath = Path.GetFullPath(Path.Combine(repoPath, folder.Local));
            var remoteSpec = $"{remoteName}:{BuildRemotePath(settings.RcloneRemoteRoot, repoPath, folder)}";

            var source = pull ? remoteSpec : localPath;
            var destination = pull ? localPath : remoteSpec;

            var result = await ProcessRunner.RunAsync(
                "rclone",
                repoPath,
                timeoutSeconds,
                "copy",
                source,
                destination,
                "--create-empty-src-dirs");

            outputs.Add(result.Summary);
            fullOutput.Add(result.FullOutput);
            hasWarnings |= result.HasWarnings;
            if (!result.Success)
            {
                success = false;
                timedOut |= result.TimedOut;
                exitCode = result.ExitCode;
            }
        }

        return new GitWorkflowResult(
            success,
            timedOut,
            exitCode,
            ProcessRunner.JoinSummaries(outputs),
            ProcessRunner.JoinFullOutput(fullOutput),
            hasWarnings);
    }

    /// <summary>
    /// Maps a folder to its remote subpath: an explicit <c>remote</c> wins, otherwise
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

    /// <summary>
    /// Best-effort check that <paramref name="localFolder"/> appears in the repo's
    /// <c>.gitignore</c>. Used only to surface a scan-time warning; never blocks.
    /// </summary>
    public static bool IsGitignored(string repoPath, string localFolder)
    {
        var gitignore = Path.Combine(repoPath, ".gitignore");
        if (!File.Exists(gitignore))
        {
            return false;
        }

        var target = localFolder.Replace('\\', '/').Trim('/');
        try
        {
            foreach (var raw in File.ReadLines(gitignore))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#'))
                {
                    continue;
                }

                var entry = line.TrimStart('/').TrimEnd('/');
                if (entry.Length == 0)
                {
                    continue;
                }

                if (target.Equals(entry, StringComparison.OrdinalIgnoreCase)
                    || target.StartsWith(entry + "/", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }
}
