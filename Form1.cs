using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace InOutButton;

public sealed class Form1 : Form
{
    private const int GitTimeoutSeconds = 120;
    private const string AppName = "InOutButton";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private readonly AppSettings _settings;
    private readonly BindingSource _repoBinding = new();
    private readonly BindingSource _rootBinding = new();
    private readonly List<RepoRow> _repos = [];
    private readonly List<string> _roots = [];

    private Button _signInButton = null!;
    private Button _signOutButton = null!;
    private Button _pullSelectedButton = null!;
    private Button _commitSyncSelectedButton = null!;
    private Button _discardPullSelectedButton = null!;
    private Button _scanButton = null!;
    private Button _addRootButton = null!;
    private Button _removeRootButton = null!;
    private CheckBox _startupCheckBox = null!;
    private DataGridView _repoGrid = null!;
    private ListBox _rootList = null!;
    private TextBox _logBox = null!;
    private ToolStripStatusLabel _statusLabel = null!;

    public Form1()
    {
        _settings = SettingsStore.Load();
        _roots.AddRange(_settings.SearchRoots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase));

        BuildUi();
        Load += (_, _) =>
        {
            _startupCheckBox.Checked = StartupManager.IsEnabled();
            _rootBinding.ResetBindings(false);
            _ = ScanReposAsync();
        };
    }

    private void BuildUi()
    {
        Text = "In / Out Button";
        MinimumSize = new Size(980, 640);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9F);

        var main = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 3,
            Padding = new Padding(12),
        };
        main.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 280));
        main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 86));
        main.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 150));
        Controls.Add(main);

        _signInButton = new Button { Text = "Sign in", Dock = DockStyle.Fill, Height = 42 };
        _signOutButton = new Button { Text = "Sign out", Dock = DockStyle.Fill, Height = 42 };
        _pullSelectedButton = new Button { Text = "Pull selected", Dock = DockStyle.Fill, Height = 32 };
        _commitSyncSelectedButton = new Button { Text = "Commit + sync selected", Dock = DockStyle.Fill, Height = 32 };
        _discardPullSelectedButton = new Button { Text = "Discard + pull selected", Dock = DockStyle.Fill, Height = 32 };
        _signInButton.Click += async (_, _) => await RunForAllReposAsync(GitWorkflow.SignIn);
        _signOutButton.Click += async (_, _) => await RunForAllReposAsync(GitWorkflow.SignOut);
        _pullSelectedButton.Click += async (_, _) => await RunForSelectedRepoAsync("pull", GitRunner.SignInAsync);
        _commitSyncSelectedButton.Click += async (_, _) => await RunForSelectedRepoAsync("commit + sync", GitRunner.CommitSyncAsync);
        _discardPullSelectedButton.Click += async (_, _) => await DiscardAndPullSelectedRepoAsync();

        var topButtons = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2 };
        topButtons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        topButtons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        topButtons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        topButtons.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        topButtons.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        topButtons.Controls.Add(_signInButton, 0, 0);
        topButtons.SetColumnSpan(_signInButton, 1);
        topButtons.Controls.Add(_signOutButton, 1, 0);
        topButtons.SetColumnSpan(_signOutButton, 2);
        topButtons.Controls.Add(_pullSelectedButton, 0, 1);
        topButtons.Controls.Add(_commitSyncSelectedButton, 1, 1);
        topButtons.Controls.Add(_discardPullSelectedButton, 2, 1);
        main.Controls.Add(topButtons, 1, 0);

        _startupCheckBox = new CheckBox
        {
            Text = "Launch on startup",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        _startupCheckBox.CheckedChanged += (_, _) =>
        {
            StartupManager.SetEnabled(_startupCheckBox.Checked);
            AppendLog(_startupCheckBox.Checked ? "Startup launch enabled." : "Startup launch disabled.");
        };
        main.Controls.Add(_startupCheckBox, 0, 0);

        var rootsPanel = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3 };
        rootsPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        rootsPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        rootsPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        main.Controls.Add(rootsPanel, 0, 1);

        rootsPanel.Controls.Add(new Label
        {
            Text = "Folders to scan",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font(Font, FontStyle.Bold),
        }, 0, 0);

        _rootBinding.DataSource = _roots;
        _rootList = new ListBox { Dock = DockStyle.Fill, DataSource = _rootBinding };
        rootsPanel.Controls.Add(_rootList, 0, 1);

        var rootButtons = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3 };
        rootButtons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33));
        rootButtons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33));
        rootButtons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
        _addRootButton = new Button { Text = "Add", Dock = DockStyle.Fill };
        _removeRootButton = new Button { Text = "Remove", Dock = DockStyle.Fill };
        _scanButton = new Button { Text = "Scan", Dock = DockStyle.Fill };
        _addRootButton.Click += async (_, _) => await AddRootAsync();
        _removeRootButton.Click += async (_, _) => await RemoveSelectedRootAsync();
        _scanButton.Click += async (_, _) => await ScanReposAsync();
        rootButtons.Controls.Add(_addRootButton, 0, 0);
        rootButtons.Controls.Add(_removeRootButton, 1, 0);
        rootButtons.Controls.Add(_scanButton, 2, 0);
        rootsPanel.Controls.Add(rootButtons, 0, 2);

        _repoBinding.DataSource = _repos;
        _repoGrid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AutoGenerateColumns = false,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            ReadOnly = true,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            RowHeadersVisible = false,
            AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells,
            DataSource = _repoBinding,
        };
        _repoGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Repository", DataPropertyName = nameof(RepoRow.Name), Width = 180 });
        _repoGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Path", DataPropertyName = nameof(RepoRow.Path), AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        _repoGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Status", DataPropertyName = nameof(RepoRow.Status), Width = 140 });
        _repoGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Last message", DataPropertyName = nameof(RepoRow.LastMessage), Width = 280 });
        main.Controls.Add(_repoGrid, 1, 1);

        _logBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Font = new Font("Consolas", 9F),
        };
        main.Controls.Add(_logBox, 0, 2);
        main.SetColumnSpan(_logBox, 2);

        var status = new StatusStrip();
        _statusLabel = new ToolStripStatusLabel("Ready");
        status.Items.Add(_statusLabel);
        Controls.Add(status);
    }

    private async Task AddRootAsync()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Select a folder to scan for git repositories.",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        if (!_roots.Contains(dialog.SelectedPath, StringComparer.OrdinalIgnoreCase))
        {
            _roots.Add(dialog.SelectedPath);
            SaveSettings();
            _rootBinding.ResetBindings(false);
            AppendLog($"Added scan root: {dialog.SelectedPath}");
        }

        await ScanReposAsync();
    }

    private async Task RemoveSelectedRootAsync()
    {
        if (_rootList.SelectedItem is not string selected)
        {
            return;
        }

        _roots.Remove(selected);
        SaveSettings();
        _rootBinding.ResetBindings(false);
        AppendLog($"Removed scan root: {selected}");
        await ScanReposAsync();
    }

    private async Task ScanReposAsync()
    {
        SetBusy(true, "Scanning repositories...");
        _repos.Clear();
        _repoBinding.ResetBindings(false);

        try
        {
            var discovered = await Task.Run(() => RepoDiscovery.FindRepositories(_roots));
            foreach (var path in discovered.OrderBy(Path.GetFileName).ThenBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                _repos.Add(new RepoRow(path));
            }

            _repoBinding.ResetBindings(false);
            AppendLog($"Scan complete. Found {_repos.Count} repos.");
            _statusLabel.Text = $"Found {_repos.Count} repos";
        }
        catch (Exception ex)
        {
            AppendLog($"Scan failed: {ex.Message}");
            _statusLabel.Text = "Scan failed";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task RunForAllReposAsync(GitWorkflow workflow)
    {
        if (workflow == GitWorkflow.SignOut)
        {
            await ScanReposAsync();
        }

        if (_repos.Count == 0)
        {
            AppendLog("No repositories found. Add folders to scan, then scan again.");
            return;
        }

        var label = workflow == GitWorkflow.SignIn ? "sign in" : "sign out";
        SetBusy(true, $"Running {label}...");
        AppendLog($"Starting {label} for {_repos.Count} repos.");

        var failures = 0;
        foreach (var repo in _repos)
        {
            repo.Status = "Running";
            repo.LastMessage = "";
            _repoBinding.ResetBindings(false);

            var result = workflow == GitWorkflow.SignIn
                ? await GitRunner.SignInAsync(repo.Path, GitTimeoutSeconds)
                : await GitRunner.SignOutAsync(repo.Path, GitTimeoutSeconds);

            repo.Status = result.Success ? "OK" : result.TimedOut ? "Timed out" : "Failed";
            repo.LastMessage = result.Summary;
            _repoBinding.ResetBindings(false);

            if (!result.Success)
            {
                failures++;
            }

            AppendGitResult(repo, result);
        }

        _statusLabel.Text = failures == 0
            ? $"{label} complete"
            : $"{label} complete with {failures} failure(s)";
        AppendLog($"{label} complete. Failures/timeouts: {failures}.");
        SetBusy(false);
    }

    private async Task RunForSelectedRepoAsync(string label, Func<string, int, Task<GitWorkflowResult>> action)
    {
        var repo = GetSelectedRepo();
        if (repo is null)
        {
            AppendLog("Select a repository first.");
            return;
        }

        SetBusy(true, $"Running {label} for {repo.Name}...");
        repo.Status = "Running";
        repo.LastMessage = "";
        _repoBinding.ResetBindings(false);

        var result = await action(repo.Path, GitTimeoutSeconds);
        repo.Status = result.Success ? "OK" : result.TimedOut ? "Timed out" : "Failed";
        repo.LastMessage = result.Summary;
        _repoBinding.ResetBindings(false);
        AppendGitResult(repo, result);

        _statusLabel.Text = result.Success ? $"{label} complete" : $"{label} failed";
        SetBusy(false);
    }

    private async Task DiscardAndPullSelectedRepoAsync()
    {
        var repo = GetSelectedRepo();
        if (repo is null)
        {
            AppendLog("Select a repository first.");
            return;
        }

        var answer = MessageBox.Show(
            this,
            $"Discard all tracked local changes in {repo.Name}, then run git pull?\n\nThis uses git reset --hard HEAD. Untracked files are left alone.",
            "Discard local changes?",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);

        if (answer != DialogResult.Yes)
        {
            return;
        }

        await RunForSelectedRepoAsync("discard + pull", GitRunner.DiscardAndPullAsync);
    }

    private void SetBusy(bool busy, string? status = null)
    {
        _signInButton.Enabled = !busy;
        _signOutButton.Enabled = !busy;
        _pullSelectedButton.Enabled = !busy;
        _commitSyncSelectedButton.Enabled = !busy;
        _discardPullSelectedButton.Enabled = !busy;
        _scanButton.Enabled = !busy;
        _addRootButton.Enabled = !busy;
        _removeRootButton.Enabled = !busy;
        _startupCheckBox.Enabled = !busy;

        if (!string.IsNullOrWhiteSpace(status))
        {
            _statusLabel.Text = status;
        }
    }

    private void SaveSettings()
    {
        _settings.SearchRoots = [.. _roots];
        SettingsStore.Save(_settings);
    }

    private void AppendLog(string message)
    {
        _logBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
    }

    private void AppendGitResult(RepoRow repo, GitWorkflowResult result)
    {
        AppendLog($"{repo.Name}: {repo.Status} - {result.Summary}");
        if (!string.IsNullOrWhiteSpace(result.FullOutput))
        {
            _logBox.AppendText(result.FullOutput.TrimEnd() + Environment.NewLine);
        }
    }

    private RepoRow? GetSelectedRepo()
    {
        return _repoGrid.CurrentRow?.DataBoundItem as RepoRow;
    }
}

public sealed class RepoRow
{
    public RepoRow(string path)
    {
        Path = path;
        Name = System.IO.Path.GetFileName(path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
    }

    public string Name { get; }
    public string Path { get; }
    public string Status { get; set; } = "Ready";
    public string LastMessage { get; set; } = "";
}

public enum GitWorkflow
{
    SignIn,
    SignOut,
}

public sealed class AppSettings
{
    public List<string> SearchRoots { get; set; } = [];
}

public static class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string SettingsDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "InOutButton");

    public static string SettingsPath { get; } = Path.Combine(SettingsDirectory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return new AppSettings();
            }

            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), JsonOptions) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(SettingsDirectory);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
    }
}

public static class RepoDiscovery
{
    private static readonly HashSet<string> IgnoredDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git",
        "bin",
        "obj",
        ".vs",
        ".idea",
    };

    public static IReadOnlyList<string> FindRepositories(IEnumerable<string> roots)
    {
        var repos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots.Where(Directory.Exists))
        {
            Walk(root, repos);
        }

        return [.. repos];
    }

    private static void Walk(string directory, HashSet<string> repos)
    {
        if (Directory.Exists(Path.Combine(directory, ".git")))
        {
            repos.Add(directory);
        }

        IEnumerable<string> children;
        try
        {
            children = Directory.EnumerateDirectories(directory);
        }
        catch
        {
            return;
        }

        foreach (var child in children)
        {
            var name = Path.GetFileName(child);
            if (IgnoredDirectoryNames.Contains(name))
            {
                continue;
            }

            try
            {
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }
            }
            catch
            {
                continue;
            }

            Walk(child, repos);
        }
    }
}

public static class GitRunner
{
    public static async Task<GitWorkflowResult> SignInAsync(string repoPath, int timeoutSeconds)
    {
        return await RunGitAsync(repoPath, timeoutSeconds, "pull");
    }

    public static async Task<GitWorkflowResult> SignOutAsync(string repoPath, int timeoutSeconds)
    {
        var outputs = new List<string>();
        var fullOutput = new List<string>();

        var add = await RunGitAsync(repoPath, timeoutSeconds, "add", "-A");
        outputs.Add(add.Summary);
        fullOutput.Add(add.FullOutput);
        if (!add.Success)
        {
            return add with { Summary = JoinSummaries(outputs), FullOutput = JoinFullOutput(fullOutput) };
        }

        var hasStagedChanges = await RunGitAsync(repoPath, timeoutSeconds, "diff", "--cached", "--quiet");
        if (hasStagedChanges.ExitCode == 1)
        {
            var message = $"{DateTime.Now:MM-dd-yy} updates";
            var commit = await RunGitAsync(repoPath, timeoutSeconds, "commit", "-m", message);
            outputs.Add(commit.Summary);
            fullOutput.Add(commit.FullOutput);
            if (!commit.Success)
            {
                return commit with { Summary = JoinSummaries(outputs), FullOutput = JoinFullOutput(fullOutput) };
            }
        }
        else if (!hasStagedChanges.Success)
        {
            outputs.Add(hasStagedChanges.Summary);
            fullOutput.Add(hasStagedChanges.FullOutput);
            return hasStagedChanges with { Summary = JoinSummaries(outputs), FullOutput = JoinFullOutput(fullOutput) };
        }
        else
        {
            outputs.Add("No staged changes to commit.");
        }

        var push = await RunGitAsync(repoPath, timeoutSeconds, "push");
        outputs.Add(push.Summary);
        fullOutput.Add(push.FullOutput);
        return push with { Summary = JoinSummaries(outputs), FullOutput = JoinFullOutput(fullOutput) };
    }

    public static async Task<GitWorkflowResult> CommitSyncAsync(string repoPath, int timeoutSeconds)
    {
        var outputs = new List<string>();
        var fullOutput = new List<string>();

        var add = await RunGitAsync(repoPath, timeoutSeconds, "add", "-A");
        outputs.Add(add.Summary);
        fullOutput.Add(add.FullOutput);
        if (!add.Success)
        {
            return add with { Summary = JoinSummaries(outputs), FullOutput = JoinFullOutput(fullOutput) };
        }

        var hasStagedChanges = await RunGitAsync(repoPath, timeoutSeconds, "diff", "--cached", "--quiet");
        if (hasStagedChanges.ExitCode == 1)
        {
            var message = $"{DateTime.Now:MM-dd-yy} updates";
            var commit = await RunGitAsync(repoPath, timeoutSeconds, "commit", "-m", message);
            outputs.Add(commit.Summary);
            fullOutput.Add(commit.FullOutput);
            if (!commit.Success)
            {
                return commit with { Summary = JoinSummaries(outputs), FullOutput = JoinFullOutput(fullOutput) };
            }
        }
        else if (!hasStagedChanges.Success)
        {
            outputs.Add(hasStagedChanges.Summary);
            fullOutput.Add(hasStagedChanges.FullOutput);
            return hasStagedChanges with { Summary = JoinSummaries(outputs), FullOutput = JoinFullOutput(fullOutput) };
        }
        else
        {
            outputs.Add("No staged changes to commit.");
        }

        var pull = await RunGitAsync(repoPath, timeoutSeconds, "pull", "--rebase");
        outputs.Add(pull.Summary);
        fullOutput.Add(pull.FullOutput);
        if (!pull.Success)
        {
            return pull with { Summary = JoinSummaries(outputs), FullOutput = JoinFullOutput(fullOutput) };
        }

        var push = await RunGitAsync(repoPath, timeoutSeconds, "push");
        outputs.Add(push.Summary);
        fullOutput.Add(push.FullOutput);
        return push with { Summary = JoinSummaries(outputs), FullOutput = JoinFullOutput(fullOutput) };
    }

    public static async Task<GitWorkflowResult> DiscardAndPullAsync(string repoPath, int timeoutSeconds)
    {
        var outputs = new List<string>();
        var fullOutput = new List<string>();

        var reset = await RunGitAsync(repoPath, timeoutSeconds, "reset", "--hard", "HEAD");
        outputs.Add(reset.Summary);
        fullOutput.Add(reset.FullOutput);
        if (!reset.Success)
        {
            return reset with { Summary = JoinSummaries(outputs), FullOutput = JoinFullOutput(fullOutput) };
        }

        var pull = await SignInAsync(repoPath, timeoutSeconds);
        outputs.Add(pull.Summary);
        fullOutput.Add(pull.FullOutput);
        return pull with { Summary = JoinSummaries(outputs), FullOutput = JoinFullOutput(fullOutput) };
    }

    private static async Task<GitWorkflowResult> RunGitAsync(string workingDirectory, int timeoutSeconds, params string[] arguments)
    {
        var commandLabel = $"git {string.Join(' ', arguments)}";
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "git",
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

                return new GitWorkflowResult(false, true, -1, $"{commandLabel}: timed out after {timeoutSeconds}s", $"{commandLabel}: timed out after {timeoutSeconds}s");
            }

            var text = output.ToString().Trim();
            var detail = string.IsNullOrWhiteSpace(text)
                ? $"exited {process.ExitCode}"
                : LastMeaningfulLine(text);
            var summary = $"{commandLabel}: {detail}";
            var fullOutput = string.IsNullOrWhiteSpace(text)
                ? $"{commandLabel}: exited {process.ExitCode}"
                : $"{commandLabel}:{Environment.NewLine}{text}";

            return new GitWorkflowResult(process.ExitCode == 0, false, process.ExitCode, summary, fullOutput);
        }
        catch (Exception ex)
        {
            return new GitWorkflowResult(false, false, -1, $"{commandLabel}: {ex.Message}", $"{commandLabel}: {ex}");
        }
    }

    private static string JoinSummaries(IEnumerable<string> summaries)
    {
        return string.Join(" | ", summaries.Where(summary => !string.IsNullOrWhiteSpace(summary)));
    }

    private static string JoinFullOutput(IEnumerable<string> outputs)
    {
        return string.Join($"{Environment.NewLine}{Environment.NewLine}", outputs.Where(output => !string.IsNullOrWhiteSpace(output)));
    }

    private static string LastMeaningfulLine(string text)
    {
        return text.Split([Environment.NewLine, "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? text;
    }
}

public sealed record GitWorkflowResult(bool Success, bool TimedOut, int ExitCode, string Summary, string FullOutput);

public static class StartupManager
{
    private const string AppName = "InOutButton";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return string.Equals(key?.GetValue(AppName) as string, Application.ExecutablePath, StringComparison.OrdinalIgnoreCase);
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);

        if (enabled)
        {
            key.SetValue(AppName, Application.ExecutablePath);
        }
        else
        {
            key.DeleteValue(AppName, throwOnMissingValue: false);
        }
    }
}
