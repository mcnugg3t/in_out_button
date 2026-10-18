using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace InOutButton;

/// <summary>
/// one WebView2 page from the web folder, served as https://inout.local/. messages go both ways as
/// json objects with a string type; C# owns the state and the page only renders it (spec 2.1)
/// </summary>
public sealed class WebHost : UserControl
{
    private const string HostName = "inout.local";
    private const string Origin = "https://inout.local/";
    private const int MaxQueued = 32;

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private readonly string _page;
    private readonly Action<string> _log;
    // posts made before the page says ready; state messages are whole, so dropping the oldest is safe
    private readonly Queue<string> _queue = new();
    private WebView2? _view;
    private Task<bool>? _init;
    private bool _ready;
    private bool _failed;
    private bool _loggedIgnored;

    public WebHost(string page, Action<string> log)
    {
        _page = page;
        _log = log;
        Dock = DockStyle.Fill;
        Margin = Padding.Empty;
    }

    /// <summary>
    /// object with a string type, cloned so it outlives the parsed document. raised on the ui thread;
    /// ready is raised too, after the queue is flushed
    /// </summary>
    public event Action<JsonElement>? MessageReceived;

    /// <summary>
    /// lazy init, safe to call again. false when the WebView2 runtime is missing and the label shows instead
    /// </summary>
    public Task<bool> EnsureAsync() => _init ??= InitializeAsync();

    /// <summary>
    /// serializes with the web defaults (camelCase, ISO 8601 instants) unless options are given
    /// </summary>
    public void Post(object message, JsonSerializerOptions? options = null)
    {
        if (_failed || IsDisposed)
        {
            return;
        }

        var json = JsonSerializer.Serialize(message, options ?? WebJson);
        if (!_ready || _view?.CoreWebView2 is null)
        {
            if (_queue.Count == MaxQueued)
            {
                _queue.Dequeue();
            }

            _queue.Enqueue(json);
            return;
        }

        Send(json);
    }

    /// <summary>
    /// logs that a message was dropped, once per session per page
    /// </summary>
    public void NoteIgnored()
    {
        if (!_loggedIgnored)
        {
            _loggedIgnored = true;
            _log("web: ignored a malformed message");
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _view?.Dispose();
            _view = null;
        }

        base.Dispose(disposing);
    }

    private async Task<bool> InitializeAsync()
    {
        var view = new WebView2 { Dock = DockStyle.Fill };
        Controls.Add(view);
        _view = view;
        try
        {
            var dataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InOutButton", "WebView2");
            var env = await CoreWebView2Environment.CreateAsync(null, dataFolder);
            await view.EnsureCoreWebView2Async(env);
        }
        catch (Exception ex)
        {
            Fail(ex, "WebView2 runtime not found. Install the Evergreen WebView2 runtime from Microsoft, then restart.");
            return false;
        }

        try
        {
            Configure(view.CoreWebView2);
            view.CoreWebView2.Navigate(Origin + _page);
            return true;
        }
        catch (Exception ex)
        {
            Fail(ex, "This page could not start. The log has the reason.");
            return false;
        }
    }

    private void Configure(CoreWebView2 core)
    {
        // the winforms chrome is light whatever the os theme
        core.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Light;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDevToolsEnabled = true;
        core.Settings.IsBuiltInErrorPageEnabled = true;
        core.SetVirtualHostNameToFolderMapping(HostName, Path.Combine(AppContext.BaseDirectory, "web"), CoreWebView2HostResourceAccessKind.Allow);

        // contained: nothing leaves inout.local, and links never open windows - external urls go through Form1's action map
        core.NavigationStarting += (_, e) =>
        {
            if (!e.Uri.StartsWith(Origin, StringComparison.OrdinalIgnoreCase))
            {
                e.Cancel = true;
                return;
            }

            // a reload starts a new page that hasn't said ready yet
            _ready = false;
        };
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.WebMessageReceived += OnWebMessage;
    }

    // the rest of the app carries on; only this tab shows the label
    private void Fail(Exception ex, string text)
    {
        _failed = true;
        _queue.Clear();
        if (_view is { } view)
        {
            Controls.Remove(view);
            view.Dispose();
            _view = null;
        }

        Controls.Add(new Label { Text = text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter });
        _log($"web: {_page} did not start ({ex.GetType().Name}): {Scrub.Message(ex.Message)}");
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!e.Source.StartsWith(Origin, StringComparison.OrdinalIgnoreCase))
        {
            NoteIgnored();
            return;
        }

        JsonElement message;
        try
        {
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
            {
                NoteIgnored();
                return;
            }

            message = root.Clone();
        }
        catch (JsonException)
        {
            NoteIgnored();
            return;
        }

        if (message.GetProperty("type").GetString() == "ready")
        {
            _ready = true;
            while (_ready && _queue.TryDequeue(out var queued))
            {
                Send(queued);
            }
        }

        MessageReceived?.Invoke(message);
    }

    private void Send(string json)
    {
        try
        {
            _view?.CoreWebView2?.PostWebMessageAsJson(json);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // the view is closing; nothing left to tell
        }
    }
}
