using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Win32;

namespace InOutButton;

public sealed class Form1 : Form
{
    private const int GitTimeoutSeconds = 120;

    private readonly AppSettings _settings;
    private readonly BindingSource _repoBinding = new();
    private readonly BindingSource _rootBinding = new();
    private readonly List<RepoRow> _repos = [];
    private readonly List<string> _roots = [];

    private Button _signInButton = null!;
    private Button _signOutButton = null!;
    private Button _gitPullAllButton = null!;
    private Button _pullDataAllButton = null!;
    private Button _pushDataAllButton = null!;
    private Button _pullSelectedButton = null!;
    private Button _commitSyncSelectedButton = null!;
    private Button _discardPullSelectedButton = null!;
    private Button _pullDataSelectedButton = null!;
    private Button _pushDataSelectedButton = null!;
    private Button _scanButton = null!;
    private Button _addRootButton = null!;
    private Button _removeRootButton = null!;
    private Button _testRemoteButton = null!;
    private CheckBox _startupCheckBox = null!;
    private CheckBox _activeOnlyCheckBox = null!;
    private DataGridView _repoGrid = null!;
    private ListBox _rootList = null!;
    private Label _rootsHeader = null!;
    private TextBox _logBox = null!;
    private TextBox _rcloneRemoteBox = null!;
    private TextBox _rcloneRootBox = null!;
    private NumericUpDown _rcloneTimeoutBox = null!;
    private NumericUpDown _activeDaysBox = null!;
    private ToolStripStatusLabel _statusLabel = null!;

    private bool _rcloneAvailable;

    public Form1()
    {
        _settings = SettingsStore.Load();
        _roots.AddRange(_settings.SearchRoots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase));

        BuildUi();
        Load += (_, _) =>
        {
            _startupCheckBox.Checked = StartupManager.IsEnabled();
            _rootBinding.ResetBindings(false);
            UpdateRootsHeader();
            _ = InitRcloneAsync();
            _ = ScanReposAsync();
        };
    }

    // ---- theme ------------------------------------------------------------

    private static class Theme
    {
        public static readonly Color WindowBg = Color.FromArgb(241, 243, 246);
        public static readonly Color CardBg = Color.White;
        public static readonly Color Text = Color.FromArgb(24, 33, 47);
        public static readonly Color SubtleText = Color.FromArgb(120, 128, 140);
        public static readonly Color Border = Color.FromArgb(216, 220, 227);
        public static readonly Color Accent = Color.FromArgb(37, 99, 235);
        public static readonly Color AccentHover = Color.FromArgb(29, 78, 216);
        public static readonly Color Slate = Color.FromArgb(51, 65, 85);
        public static readonly Color SlateHover = Color.FromArgb(30, 41, 59);
        public static readonly Color Hover = Color.FromArgb(237, 241, 247);
        public static readonly Color DisabledBg = Color.FromArgb(203, 208, 216);
        public static readonly Color Ok = Color.FromArgb(21, 128, 61);
        public static readonly Color Warn = Color.FromArgb(180, 83, 9);
        public static readonly Color Fail = Color.FromArgb(220, 38, 38);
        public static readonly Color Selection = Color.FromArgb(219, 234, 254);
        public static readonly Color AltRow = Color.FromArgb(248, 249, 251);
        public static readonly Color LogBg = Color.FromArgb(15, 23, 42);
        public static readonly Color LogFg = Color.FromArgb(203, 213, 225);
    }

    // panel with a 1px border; the "card" building block of the layout
    private sealed class CardPanel : Panel
    {
        public CardPanel()
        {
            ResizeRedraw = true;
            DoubleBuffered = true;
            BackColor = Theme.CardBg;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using var pen = new Pen(Theme.Border);
            e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }
    }

    private static Button CreatePrimaryButton(string text, Color back, Color hover)
    {
        var button = new Button
        {
            Text = text,
            Dock = DockStyle.Fill,
            FlatStyle = FlatStyle.Flat,
            BackColor = back,
            ForeColor = Color.White,
            Font = new Font("Segoe UI Semibold", 10.5F),
            Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false,
        };
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = hover;
        button.FlatAppearance.MouseDownBackColor = hover;
        button.EnabledChanged += (_, _) => button.BackColor = button.Enabled ? back : Theme.DisabledBg;
        return button;
    }

    private static Button CreateSecondaryButton(string text)
    {
        var button = new Button
        {
            Text = text,
            Dock = DockStyle.Fill,
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.CardBg,
            ForeColor = Theme.Text,
            Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false,
        };
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.BorderColor = Theme.Border;
        button.FlatAppearance.MouseOverBackColor = Theme.Hover;
        button.FlatAppearance.MouseDownBackColor = Theme.Hover;
        return button;
    }

    private static Label CreateSectionLabel(string text)
    {
        return new Label
        {
            Text = text,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Theme.Text,
            Font = new Font("Segoe UI Semibold", 9.75F),
        };
    }

    private static Label CreateFieldLabel(string text)
    {
        return new Label
        {
            Text = text,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Theme.SubtleText,
        };
    }

    // ---- ui construction --------------------------------------------------

    private void BuildUi()
    {
        Text = "In / Out Button";
        MinimumSize = new Size(1060, 700);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9.75F);
        BackColor = Theme.WindowBg;
        ForeColor = Theme.Text;

        var main = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 3,
            Padding = new Padding(14, 14, 14, 6),
        };
        main.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 300));
        main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 104));
        main.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 160));
        Controls.Add(main);

        // top-left: title + startup toggle
        var titlePanel = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, Margin = new Padding(3, 3, 8, 3) };
        titlePanel.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        titlePanel.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        titlePanel.Controls.Add(new Label
        {
            Text = "In / Out Button",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.BottomLeft,
            Font = new Font("Segoe UI Semibold", 14F),
            ForeColor = Theme.Text,
        }, 0, 0);
        _startupCheckBox = new CheckBox
        {
            Text = "Launch on startup",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Theme.SubtleText,
        };
        _startupCheckBox.CheckedChanged += (_, _) =>
        {
            StartupManager.SetEnabled(_startupCheckBox.Checked);
            AppendLog(_startupCheckBox.Checked ? "Startup launch enabled." : "Startup launch disabled.");
        };
        titlePanel.Controls.Add(_startupCheckBox, 0, 1);
        main.Controls.Add(titlePanel, 0, 0);

        // top-right: batch actions. row 1 = combined sign in/out, row 2 = git-only / data-only
        _signInButton = CreatePrimaryButton("Sign in", Theme.Accent, Theme.AccentHover);
        _signOutButton = CreatePrimaryButton("Sign out", Theme.Slate, Theme.SlateHover);
        _gitPullAllButton = CreateSecondaryButton("Git pull (all)");
        _pullDataAllButton = CreateSecondaryButton("Pull data (all)");
        _pushDataAllButton = CreateSecondaryButton("Push data (all)");
        _signInButton.Click += async (_, _) => await SignInAllAsync();
        _signOutButton.Click += async (_, _) => await SignOutAllAsync();
        _gitPullAllButton.Click += async (_, _) => await GitPullAllAsync();
        _pullDataAllButton.Click += async (_, _) => await RunDataForAllAsync(pull: true);
        _pushDataAllButton.Click += async (_, _) => await RunDataForAllAsync(pull: false);

        var topButtons = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 6, RowCount = 2 };
        for (var i = 0; i < 6; i++)
        {
            topButtons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F / 6));
        }
        topButtons.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        topButtons.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        topButtons.Controls.Add(_signInButton, 0, 0);
        topButtons.SetColumnSpan(_signInButton, 3);
        topButtons.Controls.Add(_signOutButton, 3, 0);
        topButtons.SetColumnSpan(_signOutButton, 3);
        topButtons.Controls.Add(_gitPullAllButton, 0, 1);
        topButtons.SetColumnSpan(_gitPullAllButton, 2);
        topButtons.Controls.Add(_pullDataAllButton, 2, 1);
        topButtons.SetColumnSpan(_pullDataAllButton, 2);
        topButtons.Controls.Add(_pushDataAllButton, 4, 1);
        topButtons.SetColumnSpan(_pushDataAllButton, 2);
        main.Controls.Add(topButtons, 1, 0);

        // left column: folders card over rclone card
        var leftPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(3, 3, 8, 3) };
        leftPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        leftPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 268));
        main.Controls.Add(leftPanel, 0, 1);

        leftPanel.Controls.Add(BuildRootsCard(), 0, 0);
        leftPanel.Controls.Add(BuildRcloneCard(), 0, 1);

        // right: selected-repo toolbar over repo grid
        var rightPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        rightPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        rightPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        main.Controls.Add(rightPanel, 1, 1);

        rightPanel.Controls.Add(BuildSelectedToolbar(), 0, 0);
        rightPanel.Controls.Add(BuildRepoGridCard(), 0, 1);

        // bottom: log
        var logCard = new Panel { Dock = DockStyle.Fill, BackColor = Theme.LogBg, Padding = new Padding(10, 8, 10, 8), Margin = new Padding(3, 8, 3, 3) };
        _logBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            BackColor = Theme.LogBg,
            ForeColor = Theme.LogFg,
            ScrollBars = ScrollBars.Vertical,
            Font = new Font("Consolas", 9F),
        };
        logCard.Controls.Add(_logBox);
        main.Controls.Add(logCard, 0, 2);
        main.SetColumnSpan(logCard, 2);

        var status = new StatusStrip { BackColor = Theme.WindowBg, SizingGrip = false };
        _statusLabel = new ToolStripStatusLabel("Ready") { ForeColor = Theme.SubtleText };
        status.Items.Add(_statusLabel);
        Controls.Add(status);
    }

    private Control BuildRootsCard()
    {
        var card = new CardPanel { Dock = DockStyle.Fill, Padding = new Padding(12, 10, 12, 12), Margin = new Padding(0, 0, 0, 8) };

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        card.Controls.Add(layout);

        _rootsHeader = CreateSectionLabel("Folders to scan");
        layout.Controls.Add(_rootsHeader, 0, 0);

        _rootBinding.DataSource = _roots;
        _rootList = new ListBox
        {
            Dock = DockStyle.Fill,
            DataSource = _rootBinding,
            BorderStyle = BorderStyle.None,
            IntegralHeight = false,
            BackColor = Theme.CardBg,
            ForeColor = Theme.Text,
        };
        layout.Controls.Add(_rootList, 0, 1);

        var rootButtons = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3 };
        rootButtons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33));
        rootButtons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33));
        rootButtons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
        _addRootButton = CreateSecondaryButton("Add");
        _removeRootButton = CreateSecondaryButton("Remove");
        _scanButton = CreateSecondaryButton("Rescan");
        _addRootButton.Click += async (_, _) => await AddRootAsync();
        _removeRootButton.Click += async (_, _) => await RemoveSelectedRootAsync();
        _scanButton.Click += async (_, _) => await ScanReposAsync();
        rootButtons.Controls.Add(_addRootButton, 0, 0);
        rootButtons.Controls.Add(_removeRootButton, 1, 0);
        rootButtons.Controls.Add(_scanButton, 2, 0);
        layout.Controls.Add(rootButtons, 0, 2);

        return card;
    }

    private Control BuildRcloneCard()
    {
        var card = new CardPanel { Dock = DockStyle.Fill, Padding = new Padding(12, 10, 12, 12) };

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 7 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        card.Controls.Add(layout);

        var header = CreateSectionLabel("Data sync (rclone)");
        layout.Controls.Add(header, 0, 0);
        layout.SetColumnSpan(header, 2);

        layout.Controls.Add(CreateFieldLabel("Remote"), 0, 1);
        _rcloneRemoteBox = new TextBox { Dock = DockStyle.Fill, Text = _settings.RcloneRemote ?? "", PlaceholderText = "e.g. gdrive" };
        _rcloneRemoteBox.TextChanged += (_, _) =>
        {
            _settings.RcloneRemote = string.IsNullOrWhiteSpace(_rcloneRemoteBox.Text) ? null : _rcloneRemoteBox.Text.Trim();
            SettingsStore.Save(_settings);
        };
        layout.Controls.Add(_rcloneRemoteBox, 1, 1);

        layout.Controls.Add(CreateFieldLabel("Remote root"), 0, 2);
        _rcloneRootBox = new TextBox { Dock = DockStyle.Fill, Text = _settings.RcloneRemoteRoot };
        _rcloneRootBox.TextChanged += (_, _) =>
        {
            _settings.RcloneRemoteRoot = string.IsNullOrWhiteSpace(_rcloneRootBox.Text) ? "InOutButtonData" : _rcloneRootBox.Text.Trim();
            SettingsStore.Save(_settings);
        };
        layout.Controls.Add(_rcloneRootBox, 1, 2);

        layout.Controls.Add(CreateFieldLabel("Timeout (s)"), 0, 3);
        _rcloneTimeoutBox = new NumericUpDown
        {
            Dock = DockStyle.Left,
            Width = 90,
            Minimum = 30,
            Maximum = 86400,
            Increment = 60,
            Value = Math.Clamp(_settings.RcloneTimeoutSeconds, 30, 86400),
        };
        _rcloneTimeoutBox.ValueChanged += (_, _) =>
        {
            _settings.RcloneTimeoutSeconds = (int)_rcloneTimeoutBox.Value;
            SettingsStore.Save(_settings);
        };
        layout.Controls.Add(_rcloneTimeoutBox, 1, 3);

        layout.Controls.Add(CreateFieldLabel("Active (days)"), 0, 4);
        _activeDaysBox = new NumericUpDown
        {
            Dock = DockStyle.Left,
            Width = 90,
            Minimum = 1,
            Maximum = 365,
            Value = Math.Clamp(_settings.RcloneActiveDays, 1, 365),
        };
        _activeDaysBox.ValueChanged += (_, _) =>
        {
            _settings.RcloneActiveDays = (int)_activeDaysBox.Value;
            SettingsStore.Save(_settings);
            RefreshActivity();
        };
        layout.Controls.Add(_activeDaysBox, 1, 4);

        _activeOnlyCheckBox = new CheckBox
        {
            Text = "Only sync data for active repos",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Checked = _settings.RcloneActiveOnly,
            ForeColor = Theme.SubtleText,
        };
        _activeOnlyCheckBox.CheckedChanged += (_, _) =>
        {
            _settings.RcloneActiveOnly = _activeOnlyCheckBox.Checked;
            SettingsStore.Save(_settings);
            RefreshActivity();
        };
        layout.Controls.Add(_activeOnlyCheckBox, 0, 5);
        layout.SetColumnSpan(_activeOnlyCheckBox, 2);

        _testRemoteButton = CreateSecondaryButton("Test remote");
        _testRemoteButton.Click += async (_, _) => await TestRemoteAsync();
        layout.Controls.Add(_testRemoteButton, 1, 6);

        return card;
    }

    private Control BuildSelectedToolbar()
    {
        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(0, 2, 0, 0),
        };

        toolbar.Controls.Add(new Label
        {
            Text = "Selected repo:",
            ForeColor = Theme.SubtleText,
            AutoSize = true,
            Margin = new Padding(3, 8, 6, 0),
        });

        Button MakeToolButton(string text)
        {
            var button = CreateSecondaryButton(text);
            button.Dock = DockStyle.None;
            button.AutoSize = true;
            button.Padding = new Padding(6, 1, 6, 1);
            button.Margin = new Padding(0, 2, 6, 2);
            return button;
        }

        _pullSelectedButton = MakeToolButton("Pull");
        _commitSyncSelectedButton = MakeToolButton("Commit + sync");
        _discardPullSelectedButton = MakeToolButton("Discard + pull");
        _pullDataSelectedButton = MakeToolButton("Pull data");
        _pushDataSelectedButton = MakeToolButton("Push data");
        _pullSelectedButton.Click += async (_, _) => await RunForSelectedRepoAsync("pull", GitRunner.SignInAsync);
        _commitSyncSelectedButton.Click += async (_, _) => await RunForSelectedRepoAsync("commit + sync", GitRunner.CommitSyncAsync);
        _discardPullSelectedButton.Click += async (_, _) => await DiscardAndPullSelectedRepoAsync();
        _pullDataSelectedButton.Click += async (_, _) => await RunRcloneForSelectedRepoAsync(pull: true);
        _pushDataSelectedButton.Click += async (_, _) => await RunRcloneForSelectedRepoAsync(pull: false);

        toolbar.Controls.Add(_pullSelectedButton);
        toolbar.Controls.Add(_commitSyncSelectedButton);
        toolbar.Controls.Add(_discardPullSelectedButton);
        toolbar.Controls.Add(_pullDataSelectedButton);
        toolbar.Controls.Add(_pushDataSelectedButton);
        return toolbar;
    }

    private Control BuildRepoGridCard()
    {
        var card = new CardPanel { Dock = DockStyle.Fill, Padding = new Padding(1) };

        _repoBinding.DataSource = _repos;
        _repoGrid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AutoGenerateColumns = false,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            ReadOnly = true,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            RowHeadersVisible = false,
            BorderStyle = BorderStyle.None,
            BackgroundColor = Theme.CardBg,
            GridColor = Color.FromArgb(233, 235, 239),
            CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
            ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single,
            EnableHeadersVisualStyles = false,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
            ColumnHeadersHeight = 32,
            DataSource = _repoBinding,
        };
        _repoGrid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Theme.CardBg,
            ForeColor = Theme.SubtleText,
            SelectionBackColor = Theme.CardBg,
            Font = new Font("Segoe UI Semibold", 9F),
            Padding = new Padding(4, 0, 0, 0),
        };
        _repoGrid.DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Theme.CardBg,
            ForeColor = Theme.Text,
            SelectionBackColor = Theme.Selection,
            SelectionForeColor = Theme.Text,
            Padding = new Padding(4, 0, 0, 0),
        };
        _repoGrid.AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle { BackColor = Theme.AltRow };
        _repoGrid.RowTemplate.Height = 28;

        _repoGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Repository", DataPropertyName = nameof(RepoRow.Name), Width = 175 });
        _repoGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Last commit", DataPropertyName = nameof(RepoRow.LastCommitDisplay), Width = 95 });
        _repoGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Data", DataPropertyName = nameof(RepoRow.DataDisplay), Width = 55 });
        _repoGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Status", DataPropertyName = nameof(RepoRow.Status), Width = 120 });
        _repoGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Last message", DataPropertyName = nameof(RepoRow.LastMessage), AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });

        _repoGrid.CellFormatting += (_, e) =>
        {
            if (e.RowIndex < 0 || e.RowIndex >= _repos.Count || e.CellStyle is null)
            {
                return;
            }

            var repo = _repos[e.RowIndex];
            switch (e.ColumnIndex)
            {
                case 1:
                    e.CellStyle.ForeColor = repo.IsActive ? Theme.Text : Theme.SubtleText;
                    break;
                case 2:
                    e.CellStyle.ForeColor = repo.WillSync ? Theme.Ok : Theme.SubtleText;
                    break;
                case 3:
                    e.CellStyle.ForeColor = repo.Status switch
                    {
                        "OK" => Theme.Ok,
                        "OK with warnings" => Theme.Warn,
                        "Failed" or "Timed out" => Theme.Fail,
                        "Running" => Theme.Accent,
                        _ => Theme.SubtleText,
                    };
                    break;
            }
        };

        _repoGrid.CellToolTipTextNeeded += (_, e) =>
        {
            if (e.RowIndex < 0 || e.RowIndex >= _repos.Count)
            {
                return;
            }

            var repo = _repos[e.RowIndex];
            e.ToolTipText = e.ColumnIndex switch
            {
                0 => repo.Path,
                1 => repo.HasChanges ? "● = uncommitted changes" : "",
                2 => repo.HasDataSync
                    ? (repo.WillSync ? "dataset folders will sync" : "data sync configured, repo inactive — skipped")
                    : "",
                4 => repo.LastMessage,
                _ => "",
            };
        };

        card.Controls.Add(_repoGrid);
        return card;
    }

    // ---- scan roots -------------------------------------------------------

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
            UpdateRootsHeader();
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
        UpdateRootsHeader();
        AppendLog($"Removed scan root: {selected}");
        await ScanReposAsync();
    }

    private void UpdateRootsHeader()
    {
        _rootsHeader.Text = _roots.Count == 1 ? "Folders to scan (1)" : $"Folders to scan ({_roots.Count})";
    }

    // ---- scanning ---------------------------------------------------------

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

            var warnings = await ProbeReposAsync(_repos);
            RefreshActivity();

            foreach (var warning in warnings)
            {
                AppendLog(warning);
            }
            WarnUnignoredDataFolders();

            var dataCount = _repos.Count(repo => repo.HasDataSync);
            var activeCount = _repos.Count(repo => repo.IsActive);
            AppendLog($"Scan complete. Found {_repos.Count} repos ({dataCount} with data sync, {activeCount} active).");
            _statusLabel.Text = $"{_repos.Count} repos  ·  {dataCount} with data sync  ·  {activeCount} active";
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

    // probes last-commit time, working-tree changes, and data-sync config; bounded parallelism.
    // warnings are collected (not logged) because this runs off the ui thread.
    private static async Task<IReadOnlyList<string>> ProbeReposAsync(IReadOnlyList<RepoRow> repos)
    {
        var warnings = new ConcurrentQueue<string>();
        using var gate = new SemaphoreSlim(8);
        await Task.WhenAll(repos.Select(async repo =>
        {
            await gate.WaitAsync();
            try
            {
                repo.LastCommit = await GitRunner.GetLastCommitTimeAsync(repo.Path, 30);
                repo.HasChanges = await GitRunner.HasUncommittedChangesAsync(repo.Path, 30);
                repo.HasDataSync = RcloneSyncConfig.TryLoad(repo.Path, out var config, warnings.Enqueue) && config is not null;
                repo.SyncConfig = config;
            }
            finally
            {
                gate.Release();
            }
        }));
        return [.. warnings];
    }

    // active = recent commit or uncommitted changes. the dirty check matters at sign-out:
    // today's work isn't committed yet, so commit age alone would skip the repo being worked on.
    private bool ComputeActive(RepoRow repo)
    {
        return repo.HasChanges
            || (repo.LastCommit is { } last && (DateTime.Now - last).TotalDays <= _settings.RcloneActiveDays);
    }

    private void RefreshActivity()
    {
        foreach (var repo in _repos)
        {
            repo.IsActive = ComputeActive(repo);
            repo.WillSync = repo.HasDataSync && (repo.IsActive || !_settings.RcloneActiveOnly);
        }

        _repoBinding.ResetBindings(false);
    }

    private void WarnUnignoredDataFolders()
    {
        foreach (var repo in _repos)
        {
            if (repo.SyncConfig is null)
            {
                continue;
            }

            foreach (var folder in repo.SyncConfig.Folders)
            {
                if (!RcloneRunner.IsGitignored(repo.Path, folder.Local))
                {
                    AppendLog($"Warning: {repo.Name}/{folder.Local} is set for rclone sync but is not in .gitignore.");
                }
            }
        }
    }

    // ---- batch actions ----------------------------------------------------

    // runs an action across all repos; a null result means "skipped" and leaves the row untouched.
    private async Task RunBatchAsync(string label, bool rescanFirst, Func<RepoRow, Task<GitWorkflowResult?>> action)
    {
        if (rescanFirst)
        {
            await ScanReposAsync();
        }

        if (_repos.Count == 0)
        {
            AppendLog("No repositories found. Add folders to scan, then scan again.");
            return;
        }

        SetBusy(true, $"Running {label}...");
        AppendLog($"Starting {label} ({_repos.Count} repos).");

        var failures = 0;
        var ran = 0;
        foreach (var repo in _repos)
        {
            var previousStatus = repo.Status;
            var previousMessage = repo.LastMessage;
            repo.Status = "Running";
            repo.LastMessage = "";
            _repoBinding.ResetBindings(false);

            var result = await action(repo);
            if (result is null)
            {
                repo.Status = previousStatus;
                repo.LastMessage = previousMessage;
                _repoBinding.ResetBindings(false);
                continue;
            }

            ran++;
            repo.Status = StatusFromResult(result);
            repo.LastMessage = result.Summary;
            _repoBinding.ResetBindings(false);

            if (!result.Success)
            {
                failures++;
            }

            AppendGitResult(repo, result);
        }

        _statusLabel.Text = failures == 0
            ? $"{label} complete ({ran} repos)"
            : $"{label} complete with {failures} failure(s)";
        AppendLog($"{label} complete. Ran {ran}/{_repos.Count} repos; failures/timeouts: {failures}.");
        SetBusy(false);
    }

    private Task SignInAllAsync()
    {
        // sign in: git pull, then pull dataset folders (remote -> local) for active repos
        return RunBatchAsync("sign in", rescanFirst: false, async repo =>
        {
            var result = await GitRunner.SignInAsync(repo.Path, GitTimeoutSeconds);
            var dataPull = await TryRcloneForRepoAsync(repo, pull: true);
            return dataPull is null ? result : MergeResults(result, dataPull);
        });
    }

    private Task SignOutAllAsync()
    {
        // sign out: push dataset folders first so a future commit can reference them,
        // then commit/push git. the two run independently.
        return RunBatchAsync("sign out", rescanFirst: true, async repo =>
        {
            var dataPush = await TryRcloneForRepoAsync(repo, pull: false);
            var git = await GitRunner.SignOutAsync(repo.Path, GitTimeoutSeconds);
            return dataPush is null ? git : MergeResults(dataPush, git);
        });
    }

    private Task GitPullAllAsync()
    {
        // git only — no rclone, regardless of config
        return RunBatchAsync("git pull (all)", rescanFirst: false,
            async repo => await GitRunner.SignInAsync(repo.Path, GitTimeoutSeconds));
    }

    private async Task RunDataForAllAsync(bool pull)
    {
        if (!_rcloneAvailable)
        {
            AppendLog("rclone is not available; dataset sync disabled.");
            return;
        }

        await RunBatchAsync(pull ? "pull data (all)" : "push data (all)", rescanFirst: false,
            repo => TryRcloneForRepoAsync(repo, pull));
    }

    // ---- selected-repo actions --------------------------------------------

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
        repo.Status = StatusFromResult(result);
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

    private async Task RunRcloneForSelectedRepoAsync(bool pull)
    {
        var repo = GetSelectedRepo();
        if (repo is null)
        {
            AppendLog("Select a repository first.");
            return;
        }

        if (!_rcloneAvailable)
        {
            AppendLog("rclone is not available; dataset sync disabled.");
            return;
        }

        if (!RcloneSyncConfig.TryLoad(repo.Path, out var config, AppendLog) || config is null)
        {
            AppendLog($"{repo.Name}: no {RcloneSyncConfig.FileName} found.");
            return;
        }

        if (string.IsNullOrWhiteSpace(config.Remote ?? _settings.RcloneRemote))
        {
            AppendLog($"{repo.Name}: no rclone remote configured.");
            return;
        }

        var label = pull ? "pull data" : "push data";
        SetBusy(true, $"Running {label} for {repo.Name}...");
        repo.Status = "Running";
        repo.LastMessage = "";
        _repoBinding.ResetBindings(false);

        var result = pull
            ? await RcloneRunner.RclonePullAsync(repo.Path, config, _settings, _settings.RcloneTimeoutSeconds)
            : await RcloneRunner.RclonePushAsync(repo.Path, config, _settings, _settings.RcloneTimeoutSeconds);

        repo.Status = StatusFromResult(result);
        repo.LastMessage = result.Summary;
        _repoBinding.ResetBindings(false);
        AppendGitResult(repo, result);

        _statusLabel.Text = result.Success ? $"{label} complete" : $"{label} failed";
        SetBusy(false);
    }

    // ---- rclone plumbing --------------------------------------------------

    private async Task InitRcloneAsync()
    {
        var version = await RcloneRunner.ProbeAsync(15);
        _rcloneAvailable = version is not null;
        AppendLog(_rcloneAvailable
            ? $"rclone detected: {version}"
            : "rclone not found on PATH — dataset sync disabled.");
        ApplyRcloneAvailability();
    }

    private async Task TestRemoteAsync()
    {
        var remote = _rcloneRemoteBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(remote))
        {
            AppendLog("Enter a remote name before testing.");
            return;
        }

        SetBusy(true, $"Testing remote {remote}...");
        var result = await RcloneRunner.TestRemoteAsync(remote, _settings.RcloneTimeoutSeconds);
        AppendLog($"Test remote {remote}: {(result.Success ? "OK" : "FAILED")} - {result.Summary}");
        if (!string.IsNullOrWhiteSpace(result.FullOutput))
        {
            _logBox.AppendText(result.FullOutput.TrimEnd() + Environment.NewLine);
        }

        _statusLabel.Text = result.Success ? "Remote OK" : "Remote test failed";
        SetBusy(false);
    }

    /// <summary>
    /// runs the rclone side of a batch action for one repo, or returns <c>null</c> to skip:
    /// rclone missing, repo inactive (when the active-only gate is on), no config, or no remote.
    /// config is reloaded from disk (not the scan cache) because a git pull may have just changed it.
    /// </summary>
    private async Task<GitWorkflowResult?> TryRcloneForRepoAsync(RepoRow repo, bool pull)
    {
        if (!_rcloneAvailable)
        {
            return null;
        }

        if (_settings.RcloneActiveOnly && !repo.IsActive)
        {
            if (repo.HasDataSync)
            {
                AppendLog($"{repo.Name}: data sync skipped — not active (last commit {repo.LastCommitDisplay}, window {_settings.RcloneActiveDays}d).");
            }

            return null;
        }

        if (!RcloneSyncConfig.TryLoad(repo.Path, out var config, AppendLog) || config is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(config.Remote ?? _settings.RcloneRemote))
        {
            AppendLog($"{repo.Name}: {RcloneSyncConfig.FileName} present but no remote configured; skipping data sync.");
            return null;
        }

        return pull
            ? await RcloneRunner.RclonePullAsync(repo.Path, config, _settings, _settings.RcloneTimeoutSeconds)
            : await RcloneRunner.RclonePushAsync(repo.Path, config, _settings, _settings.RcloneTimeoutSeconds);
    }

    private static GitWorkflowResult MergeResults(GitWorkflowResult first, GitWorkflowResult second)
    {
        return new GitWorkflowResult(
            first.Success && second.Success,
            first.TimedOut || second.TimedOut,
            !first.Success ? first.ExitCode : second.ExitCode,
            ProcessRunner.JoinSummaries([first.Summary, second.Summary]),
            ProcessRunner.JoinFullOutput([first.FullOutput, second.FullOutput]),
            first.HasWarnings || second.HasWarnings);
    }

    // ---- shared ui plumbing -----------------------------------------------

    private void SetBusy(bool busy, string? status = null)
    {
        _signInButton.Enabled = !busy;
        _signOutButton.Enabled = !busy;
        _gitPullAllButton.Enabled = !busy;
        _pullSelectedButton.Enabled = !busy;
        _commitSyncSelectedButton.Enabled = !busy;
        _discardPullSelectedButton.Enabled = !busy;
        _scanButton.Enabled = !busy;
        _addRootButton.Enabled = !busy;
        _removeRootButton.Enabled = !busy;
        _startupCheckBox.Enabled = !busy;
        ApplyRcloneAvailability();

        if (!string.IsNullOrWhiteSpace(status))
        {
            _statusLabel.Text = status;
        }
    }

    private void ApplyRcloneAvailability()
    {
        // _scanButton.Enabled is false while a workflow is busy; reuse it as the idle signal.
        var idle = _scanButton.Enabled;
        _testRemoteButton.Enabled = _rcloneAvailable && idle;
        _pullDataAllButton.Enabled = _rcloneAvailable && idle;
        _pushDataAllButton.Enabled = _rcloneAvailable && idle;
        _pullDataSelectedButton.Enabled = _rcloneAvailable && idle;
        _pushDataSelectedButton.Enabled = _rcloneAvailable && idle;
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

    private static string StatusFromResult(GitWorkflowResult result)
    {
        if (result.Success)
        {
            return result.HasWarnings ? "OK with warnings" : "OK";
        }

        return result.TimedOut ? "Timed out" : "Failed";
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

    // populated during scan
    public DateTime? LastCommit { get; set; }
    public bool HasChanges { get; set; }
    public bool HasDataSync { get; set; }
    public bool IsActive { get; set; }
    public bool WillSync { get; set; }
    public RcloneSyncConfig? SyncConfig { get; set; }

    // "● " prefix marks uncommitted working-tree changes
    public string LastCommitDisplay
    {
        get
        {
            var marker = HasChanges ? "● " : "";
            if (LastCommit is null)
            {
                return HasChanges ? marker + "new" : "—";
            }

            var age = DateTime.Now - LastCommit.Value;
            var text = age.TotalDays switch
            {
                < 1 => "today",
                < 2 => "1d ago",
                < 14 => $"{(int)age.TotalDays}d ago",
                < 70 => $"{(int)(age.TotalDays / 7)}w ago",
                < 365 => $"{(int)(age.TotalDays / 30)}mo ago",
                _ => $"{(int)(age.TotalDays / 365)}y ago",
            };
            return marker + text;
        }
    }

    public string DataDisplay => HasDataSync ? (WillSync ? "sync" : "idle") : "";
}

public enum GitWorkflow
{
    SignIn,
    SignOut,
}

public sealed class AppSettings
{
    public List<string> SearchRoots { get; set; } = [];

    public string? RcloneRemote { get; set; }              // e.g. "gdrive"
    public string RcloneRemoteRoot { get; set; } = "InOutButtonData";
    public int RcloneTimeoutSeconds { get; set; } = 600;    // datasets can be slow

    public int RcloneActiveDays { get; set; } = 14;         // commit-recency window for "active"
    public bool RcloneActiveOnly { get; set; } = true;      // gate batch data sync to active repos
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
        var hasWarnings = add.HasWarnings;
        if (!add.Success)
        {
            return add with { Summary = JoinSummaries(outputs), FullOutput = JoinFullOutput(fullOutput), HasWarnings = hasWarnings };
        }

        var hasStagedChanges = await RunGitAsync(repoPath, timeoutSeconds, "diff", "--cached", "--quiet");
        if (hasStagedChanges.ExitCode == 1)
        {
            var message = $"{DateTime.Now:MM-dd-yy} updates";
            var commit = await RunGitAsync(repoPath, timeoutSeconds, "commit", "-m", message);
            outputs.Add(commit.Summary);
            fullOutput.Add(commit.FullOutput);
            hasWarnings |= commit.HasWarnings;
            if (!commit.Success)
            {
                return commit with { Summary = JoinSummaries(outputs), FullOutput = JoinFullOutput(fullOutput), HasWarnings = hasWarnings };
            }
        }
        else if (!hasStagedChanges.Success)
        {
            outputs.Add(hasStagedChanges.Summary);
            fullOutput.Add(hasStagedChanges.FullOutput);
            hasWarnings |= hasStagedChanges.HasWarnings;
            return hasStagedChanges with { Summary = JoinSummaries(outputs), FullOutput = JoinFullOutput(fullOutput), HasWarnings = hasWarnings };
        }
        else
        {
            outputs.Add("No staged changes to commit.");
        }

        var push = await RunGitAsync(repoPath, timeoutSeconds, "push");
        outputs.Add(push.Summary);
        fullOutput.Add(push.FullOutput);
        hasWarnings |= push.HasWarnings;
        return push with { Summary = JoinSummaries(outputs), FullOutput = JoinFullOutput(fullOutput), HasWarnings = hasWarnings };
    }

    public static async Task<GitWorkflowResult> CommitSyncAsync(string repoPath, int timeoutSeconds)
    {
        var outputs = new List<string>();
        var fullOutput = new List<string>();

        var add = await RunGitAsync(repoPath, timeoutSeconds, "add", "-A");
        outputs.Add(add.Summary);
        fullOutput.Add(add.FullOutput);
        var hasWarnings = add.HasWarnings;
        if (!add.Success)
        {
            return add with { Summary = JoinSummaries(outputs), FullOutput = JoinFullOutput(fullOutput), HasWarnings = hasWarnings };
        }

        var hasStagedChanges = await RunGitAsync(repoPath, timeoutSeconds, "diff", "--cached", "--quiet");
        if (hasStagedChanges.ExitCode == 1)
        {
            var message = $"{DateTime.Now:MM-dd-yy} updates";
            var commit = await RunGitAsync(repoPath, timeoutSeconds, "commit", "-m", message);
            outputs.Add(commit.Summary);
            fullOutput.Add(commit.FullOutput);
            hasWarnings |= commit.HasWarnings;
            if (!commit.Success)
            {
                return commit with { Summary = JoinSummaries(outputs), FullOutput = JoinFullOutput(fullOutput), HasWarnings = hasWarnings };
            }
        }
        else if (!hasStagedChanges.Success)
        {
            outputs.Add(hasStagedChanges.Summary);
            fullOutput.Add(hasStagedChanges.FullOutput);
            hasWarnings |= hasStagedChanges.HasWarnings;
            return hasStagedChanges with { Summary = JoinSummaries(outputs), FullOutput = JoinFullOutput(fullOutput), HasWarnings = hasWarnings };
        }
        else
        {
            outputs.Add("No staged changes to commit.");
        }

        var pull = await RunGitAsync(repoPath, timeoutSeconds, "pull", "--rebase");
        outputs.Add(pull.Summary);
        fullOutput.Add(pull.FullOutput);
        hasWarnings |= pull.HasWarnings;
        if (!pull.Success)
        {
            return pull with { Summary = JoinSummaries(outputs), FullOutput = JoinFullOutput(fullOutput), HasWarnings = hasWarnings };
        }

        var push = await RunGitAsync(repoPath, timeoutSeconds, "push");
        outputs.Add(push.Summary);
        fullOutput.Add(push.FullOutput);
        hasWarnings |= push.HasWarnings;
        return push with { Summary = JoinSummaries(outputs), FullOutput = JoinFullOutput(fullOutput), HasWarnings = hasWarnings };
    }

    public static async Task<GitWorkflowResult> DiscardAndPullAsync(string repoPath, int timeoutSeconds)
    {
        var outputs = new List<string>();
        var fullOutput = new List<string>();

        var reset = await RunGitAsync(repoPath, timeoutSeconds, "reset", "--hard", "HEAD");
        outputs.Add(reset.Summary);
        fullOutput.Add(reset.FullOutput);
        var hasWarnings = reset.HasWarnings;
        if (!reset.Success)
        {
            return reset with { Summary = JoinSummaries(outputs), FullOutput = JoinFullOutput(fullOutput), HasWarnings = hasWarnings };
        }

        var pull = await SignInAsync(repoPath, timeoutSeconds);
        outputs.Add(pull.Summary);
        fullOutput.Add(pull.FullOutput);
        hasWarnings |= pull.HasWarnings;
        return pull with { Summary = JoinSummaries(outputs), FullOutput = JoinFullOutput(fullOutput), HasWarnings = hasWarnings };
    }

    /// <summary>committer time of HEAD, or null (no commits, timeout, not a repo).</summary>
    public static async Task<DateTime?> GetLastCommitTimeAsync(string repoPath, int timeoutSeconds)
    {
        var (exitCode, output) = await ProcessRunner.CaptureAsync("git", repoPath, timeoutSeconds, "log", "-1", "--format=%ct");
        if (exitCode != 0)
        {
            return null;
        }

        var line = output.Trim().Split('\n').FirstOrDefault()?.Trim();
        return long.TryParse(line, out var epoch)
            ? DateTimeOffset.FromUnixTimeSeconds(epoch).LocalDateTime
            : null;
    }

    /// <summary>true when the working tree has staged, unstaged, or untracked changes.</summary>
    public static async Task<bool> HasUncommittedChangesAsync(string repoPath, int timeoutSeconds)
    {
        var (exitCode, output) = await ProcessRunner.CaptureAsync("git", repoPath, timeoutSeconds, "status", "--porcelain");
        return exitCode == 0 && output.Trim().Length > 0;
    }

    private static Task<GitWorkflowResult> RunGitAsync(string workingDirectory, int timeoutSeconds, params string[] arguments)
    {
        return ProcessRunner.RunAsync("git", workingDirectory, timeoutSeconds, arguments);
    }

    private static string JoinSummaries(IEnumerable<string> summaries) => ProcessRunner.JoinSummaries(summaries);

    private static string JoinFullOutput(IEnumerable<string> outputs) => ProcessRunner.JoinFullOutput(outputs);
}

public sealed record GitWorkflowResult(bool Success, bool TimedOut, int ExitCode, string Summary, string FullOutput, bool HasWarnings);

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
