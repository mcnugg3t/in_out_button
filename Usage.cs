using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

[assembly: InternalsVisibleTo("InOutButton.Tests")]

namespace InOutButton;

public sealed record UsageMeter(string Key, string Name, int WindowMinutes, double UsedPercent,
                                DateTimeOffset ResetsAt, string Severity, string? Note);

public sealed record ProviderAction(string Id, string Label);

public sealed record ProviderUsage(string Provider, string? Plan, string State, string? Message,
                                   string? Detail, IReadOnlyList<UsageMeter> Meters, string? Footnote,
                                   IReadOnlyList<ProviderAction> Actions, DateTimeOffset? UpdatedAt)
{
    internal static ProviderUsage Unavailable(string provider, string? plan, string message, DateTimeOffset now)
        => new(provider, plan, UsageState.Unavailable, message, null, [], null, [], now);
}

public sealed record UsageSnapshot(DateTimeOffset UpdatedAt, IReadOnlyList<ProviderUsage> Providers);

internal static class UsageState
{
    public const string Ok = "ok";
    public const string Unavailable = "unavailable";
}

/// <summary>
/// what a reader hands the service beyond the page's view: a cooldown to honour,
/// a log-only note (lengths, never content), and the child pid for the driver tests
/// </summary>
internal sealed record ReaderOutcome(ProviderUsage Usage, TimeSpan? Cooldown = null, string? LogNote = null, int? ProcessId = null);

/// <summary>
/// redacts secret-shaped text from anything that came from outside the app before it
/// reaches a message, a page, or the log
/// </summary>
public static partial class Scrub
{
    private const int MaxLength = 200;
    private const string Redacted = "[redacted]";

    private static readonly Regex[] Secrets = [Bearer(), SkKey(), Jwt(), QueryString(), KeyValue()];

    public static string Message(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        var clean = text;
        foreach (var secret in Secrets)
        {
            clean = secret.Replace(clean, Redacted);
        }

        clean = Whitespace().Replace(clean, " ").Trim();
        if (clean.Length <= MaxLength)
        {
            return clean;
        }

        // don't split a surrogate pair at the cut
        var cut = MaxLength - 1;
        if (char.IsHighSurrogate(clean[cut - 1]))
        {
            cut--;
        }

        return string.Concat(clean.AsSpan(0, cut), "…");
    }

    // case-insensitive, a little wider than the spec's Bearer
    [GeneratedRegex(@"Bearer\s+\S+", RegexOptions.IgnoreCase)]
    private static partial Regex Bearer();

    [GeneratedRegex(@"sk-[A-Za-z0-9_-]{8,}")]
    private static partial Regex SkKey();

    [GeneratedRegex(@"eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}")]
    private static partial Regex Jwt();

    [GeneratedRegex(@"\?\S+")]
    private static partial Regex QueryString();

    [GeneratedRegex(@"(?i)\b(token|key|secret|password|authorization)\b\s*[=:]\s*\S+")]
    private static partial Regex KeyValue();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}

/// <summary>
/// Claude subscription meters from the oauth usage endpoint, using the login Claude Code
/// stores on this machine. port of claude-usage-mcp's usage.mjs (parse 100-230, request 453-542)
/// </summary>
public static partial class AnthropicUsage
{
    internal const string Provider = "Anthropic";
    private const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";
    private const int DefaultRetryAfterSeconds = 60;
    private static readonly TimeSpan TransientCooldown = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);

    private const string CredentialsMissing = "Claude Code credentials not found; sign in to Claude Code on this machine";
    private const string CredentialsUnreadable = "Claude Code credentials could not be read";
    private const string CredentialsNotJson = "Claude Code credentials file is not valid JSON";
    private const string NoLogin = "Claude Code credentials hold no login; sign in to Claude Code";
    private const string Expired = "Claude Code token expired; run any claude command to refresh it";
    private const string Rejected = "token rejected; run any claude command so Claude Code refreshes it";

    public static string CredentialsPath
    {
        get
        {
            var dir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            if (string.IsNullOrEmpty(dir))
            {
                dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
            }

            return Path.Combine(dir, ".credentials.json");
        }
    }

    /// <summary>
    /// pure. a body that isn't json or has neither limits[] nor five_hour comes back unavailable
    /// </summary>
    public static ProviderUsage Parse(string json, string? plan, DateTimeOffset now)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return ProviderUsage.Unavailable(Provider, plan, "usage response was not JSON", now);
        }

        using (doc)
        {
            var root = doc.RootElement;
            var hasLimits = root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("limits", out var limits) && limits.ValueKind == JsonValueKind.Array;
            if (!hasLimits && !JsonRead.Object(root, "five_hour", out _))
            {
                return ProviderUsage.Unavailable(Provider, plan, "usage response changed shape (no limits or five_hour)", now);
            }

            var meters = hasLimits ? MetersFromLimits(root) : MetersFromWindows(root);
            if (meters.Count == 0)
            {
                return ProviderUsage.Unavailable(Provider, plan, "usage response had no meters this app recognises", now);
            }

            return new ProviderUsage(Provider, plan, UsageState.Ok, null, null, meters, ExtraUsageFootnote(root), [], now);
        }
    }

    public static async Task<ProviderUsage> ReadAsync(HttpClient http, CancellationToken ct)
        => (await ReadOutcomeAsync(http, ct).ConfigureAwait(false)).Usage;

    // a redirect comes back as a 3xx and fails; no cookies; the 10 s timeout covers the body too
    internal static HttpClient CreateHttpClient()
        => new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
        {
            Timeout = TimeSpan.FromSeconds(10),
            MaxResponseContentBufferSize = 1 << 20,
        };

    internal static async Task<ReaderOutcome> ReadOutcomeAsync(HttpClient http, CancellationToken ct)
    {
        // re-read every call: Claude Code rotates the token under us
        var login = ReadLogin(out var failure);
        if (login is null)
        {
            return Fail(null, failure!);
        }

        if (login.ExpiresAtMs is { } expiresAt && expiresAt <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
        {
            return Fail(login.Plan, Expired);
        }

        var retriedTransient = false;
        var rereadAfterReject = false;
        while (true)
        {
            HttpResponseMessage response;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Headers.Add("anthropic-beta", "oauth-2025-04-20");
                request.Headers.UserAgent.Add(new ProductInfoHeaderValue("InOutButton", AppInfo.Version));
                response = await http.SendAsync(request, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                // connection failure or the 10 s timeout: one more try
                if (!retriedTransient)
                {
                    retriedTransient = true;
                    await Task.Delay(RetryDelay, ct).ConfigureAwait(false);
                    continue;
                }

                return Fail(login.Plan, ex is TaskCanceledException
                    ? "usage request timed out after 10 s"
                    : Scrub.Message($"usage request failed: {ex.Message}"));
            }

            using (response)
            {
                var status = (int)response.StatusCode;
                var now = DateTimeOffset.UtcNow;

                // never retried; fresh doesn't skip the wait either
                if (status == 429)
                {
                    var seconds = RetryAfterSeconds(response.Headers.RetryAfter, now);
                    return new ReaderOutcome(
                        ProviderUsage.Unavailable(Provider, login.Plan, $"rate limited by the usage endpoint; next try in {seconds} s", now),
                        TimeSpan.FromSeconds(seconds),
                        "HTTP 429");
                }

                if (status >= 500)
                {
                    if (!retriedTransient)
                    {
                        retriedTransient = true;
                        await Task.Delay(RetryDelay, ct).ConfigureAwait(false);
                        continue;
                    }

                    return new ReaderOutcome(
                        ProviderUsage.Unavailable(Provider, login.Plan, $"usage endpoint failed twice (HTTP {status}); next try in 10 s", now),
                        TransientCooldown,
                        $"HTTP {status}");
                }

                if (status is 401 or 403)
                {
                    // the login may have rotated since we read it
                    if (!rereadAfterReject)
                    {
                        rereadAfterReject = true;
                        var again = ReadLogin(out _);
                        if (again is not null && again.Token != login.Token)
                        {
                            login = again;
                            continue;
                        }
                    }

                    return Fail(login.Plan, Rejected);
                }

                if (!response.IsSuccessStatusCode)
                {
                    return Fail(login.Plan, $"usage endpoint answered HTTP {status}");
                }

                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                return new ReaderOutcome(Parse(body, login.Plan, now));
            }
        }
    }

    /// <summary>
    /// "Claude Max 20x" when the tier reads like default_claude_max_20x, else the raw strings
    /// </summary>
    internal static string? PlanName(string? subscriptionType, string? rateLimitTier)
    {
        if (rateLimitTier is not null && TierPattern().Match(rateLimitTier) is { Success: true } match)
        {
            var name = $"Claude {char.ToUpperInvariant(match.Groups[1].Value[0])}{match.Groups[1].Value[1..]}";
            return match.Groups[2].Success ? $"{name} {match.Groups[2].Value}" : name;
        }

        var raw = string.Join(" · ", new[] { subscriptionType, rateLimitTier }.Where(s => !string.IsNullOrWhiteSpace(s)));
        return raw.Length == 0 ? null : Scrub.Message(raw);
    }

    // seconds or an http date; absent or unreadable means 60 s; floor 1 s, ceiling a day
    internal static int RetryAfterSeconds(RetryConditionHeaderValue? header, DateTimeOffset now)
    {
        double? seconds = header?.Delta?.TotalSeconds ?? (header?.Date - now)?.TotalSeconds;
        return seconds is { } s ? (int)Math.Clamp(Math.Ceiling(s), 1, 86_400) : DefaultRetryAfterSeconds;
    }

    private static ReaderOutcome Fail(string? plan, string message)
        => new(ProviderUsage.Unavailable(Provider, plan, message, DateTimeOffset.UtcNow));

    // a class, not a record: a record's ToString would print the token
    private sealed class ClaudeLogin
    {
        public required string Token { get; init; }
        public string? Plan { get; init; }
        public double? ExpiresAtMs { get; init; }
    }

    // read only, never written; nothing from the file reaches a message
    private static ClaudeLogin? ReadLogin(out string? failure)
    {
        failure = null;
        string text;
        try
        {
            // ReadAllText drops a utf-8 bom
            text = File.ReadAllText(CredentialsPath, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            failure = CredentialsMissing;
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failure = CredentialsUnreadable;
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(text);
            var token = JsonRead.Object(doc.RootElement, "claudeAiOauth", out var oauth) ? JsonRead.String(oauth, "accessToken") : null;
            if (string.IsNullOrEmpty(token))
            {
                failure = NoLogin;
                return null;
            }

            return new ClaudeLogin
            {
                Token = token,
                Plan = PlanName(JsonRead.String(oauth, "subscriptionType"), JsonRead.String(oauth, "rateLimitTier")),
                ExpiresAtMs = JsonRead.Number(oauth, "expiresAt"),
            };
        }
        catch (JsonException)
        {
            failure = CredentialsNotJson;
            return null;
        }
    }

    private static List<UsageMeter> MetersFromLimits(JsonElement root)
    {
        // allowance for a scoped meter is joined on its index in limits[]
        var allowances = new Dictionary<int, double>();
        if (root.TryGetProperty("weekly_scoped_shares", out var shares) && shares.ValueKind == JsonValueKind.Array)
        {
            foreach (var share in shares.EnumerateArray())
            {
                if (JsonRead.Long(share, "limit_index") is { } index && JsonRead.Number(share, "allowance_percent_of_weekly") is { } allowance)
                {
                    allowances[(int)index] = allowance;
                }
            }
        }

        var meters = new List<UsageMeter>();
        var position = -1;
        foreach (var limit in root.GetProperty("limits").EnumerateArray())
        {
            position++;
            var kind = JsonRead.String(limit, "kind");
            var model = ScopedModel(limit);
            (string Key, string Name, string? Window, int Minutes)? shape = kind switch
            {
                "session" => ("session", "Session", "five_hour", 300),
                "weekly_all" => ("weekly", "Weekly, all models", "seven_day", 10080),
                "weekly_scoped" => ($"weekly_{Whitespace().Replace(model.ToLowerInvariant(), "_")}", $"Weekly, {model} only", null, 10080),
                _ => null,
            };
            if (shape is not { } meter)
            {
                continue;
            }

            // the window object carries the float; limits[] rounds to an integer
            var window = meter.Window is not null && JsonRead.Object(root, meter.Window, out var w) ? w : default;
            var percent = JsonRead.Number(window, "utilization") ?? JsonRead.Number(limit, "percent");
            var resetsAt = ParseInstant(JsonRead.String(limit, "resets_at") ?? JsonRead.String(window, "resets_at"));
            if (percent is null || resetsAt is null)
            {
                continue;
            }

            string? note = null;
            if (kind == "weekly_scoped" && allowances.TryGetValue(position, out var allowance))
            {
                note = $"headroom {Percent((100 - percent.Value) * allowance / 100)}% of weekly";
            }

            meters.Add(new UsageMeter(meter.Key, meter.Name, meter.Minutes, percent.Value, resetsAt.Value,
                Severity(JsonRead.String(limit, "severity")), note));
        }

        return meters;
    }

    // the pre-limits[] shape: one object per window, no severity
    private static List<UsageMeter> MetersFromWindows(JsonElement root)
    {
        var meters = new List<UsageMeter>();
        foreach (var (windowKey, key, name, minutes) in new[] { ("five_hour", "session", "Session", 300), ("seven_day", "weekly", "Weekly, all models", 10080) })
        {
            if (JsonRead.Object(root, windowKey, out var window)
                && JsonRead.Number(window, "utilization") is { } percent
                && ParseInstant(JsonRead.String(window, "resets_at")) is { } resetsAt)
            {
                meters.Add(new UsageMeter(key, name, minutes, percent, resetsAt, "normal", null));
            }
        }

        return meters;
    }

    private static string ScopedModel(JsonElement limit)
        => JsonRead.Object(limit, "scope", out var scope) && JsonRead.Object(scope, "model", out var model)
            ? JsonRead.String(model, "display_name") ?? "scoped"
            : "scoped";

    private static string? ExtraUsageFootnote(JsonElement root)
    {
        if (!JsonRead.Object(root, "extra_usage", out var extra)
            || !extra.TryGetProperty("is_enabled", out var enabled) || enabled.ValueKind != JsonValueKind.True)
        {
            return null;
        }

        var scale = Math.Pow(10, JsonRead.Long(extra, "decimal_places") ?? 2);
        var currency = JsonRead.String(extra, "currency") is { Length: > 0 } c ? c : "USD";
        var usd = currency == "USD";
        string Money(double? minor) => (usd ? "$" : "") + (minor is { } v ? (v / scale).ToString("0.00", CultureInfo.InvariantCulture) : "?");

        var text = $"Extra usage on · {Money(JsonRead.Number(extra, "used_credits"))} of {Money(JsonRead.Number(extra, "monthly_limit"))}{(usd ? "" : $" {currency}")} used";
        return extra.TryGetProperty("spend_limit_reached", out var reached) && reached.ValueKind == JsonValueKind.True
            ? $"{text} · cap reached"
            : text;
    }

    private static string Severity(string? value) => value?.ToLowerInvariant() switch
    {
        "warning" => "warning",
        "critical" => "critical",
        _ => "normal",
    };

    // rounded to the second, as usage.mjs does
    private static DateTimeOffset? ParseInstant(string? value)
    {
        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var instant))
        {
            return null;
        }

        var seconds = Math.Round(instant.ToUnixTimeMilliseconds() / 1000.0, MidpointRounding.AwayFromZero);
        return DateTimeOffset.FromUnixTimeSeconds((long)seconds);
    }

    // one decimal, dropped when whole: 49.5, 50
    private static string Percent(double value)
    {
        var rounded = Math.Round(value, 1, MidpointRounding.AwayFromZero);
        return rounded.ToString(rounded == Math.Floor(rounded) ? "0" : "0.0", CultureInfo.InvariantCulture);
    }

    [GeneratedRegex(@"(?:^|_)claude_(max|pro|team|enterprise)(?:_(\d+x))?$", RegexOptions.IgnoreCase)]
    private static partial Regex TierPattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}

public sealed record CodexLaunch(string FileName, IReadOnlyList<string> LeadingArgs);

/// <summary>
/// codex rate-limit windows from `codex app-server` over json-rpc lines on stdio.
/// the app never reads auth.json or refreshes a token; codex does
/// </summary>
public static class CodexUsage
{
    internal const string Provider = "OpenAI";
    internal static readonly TimeSpan DefaultCap = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(5);

    private const string NotFound = "Codex CLI not found on PATH";
    private const string NotSignedIn = "Codex is not signed in; run codex login";
    private const string ExitedEarly = "Codex app-server exited early";
    private const string UnexpectedOutput = "Codex app-server sent unexpected output";
    private const string CouldNotStart = "Codex app-server could not be started";

    private static readonly string InitializeRequest = JsonSerializer.Serialize(new
    {
        id = 1,
        method = "initialize",
        @params = new { clientInfo = new { name = "InOutButton", version = AppInfo.Version } },
    });

    private const string InitializedNotice = """{"method":"initialized"}""";
    private const string RateLimitsRequest = """{"id":2,"method":"account/rateLimits/read","params":{"excludeResetCreditDetails":true}}""";

    /// <summary>
    /// override first, then the npm install found by where.exe: its native exe, else node with codex.js
    /// </summary>
    public static async Task<CodexLaunch?> FindAsync(string? overridePath, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
        {
            return new CodexLaunch(overridePath, []);
        }

        ct.ThrowIfCancellationRequested();
        var (exitCode, output) = await ProcessRunner.CaptureAsync("where.exe", AppContext.BaseDirectory, 10, "codex").ConfigureAwait(false);
        var first = exitCode == 0
            ? ProcessRunner.SplitLines(output).FirstOrDefault(line => Path.IsPathRooted(line) && File.Exists(line))
            : null;
        if (first is null)
        {
            return null;
        }

        if (first.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return new CodexLaunch(first, []);
        }

        // npm puts codex (sh), codex.cmd and codex.ps1 side by side and where.exe lists the
        // extensionless one first (2026-10-17), so derive from the folder, not the extension
        var shimDir = Path.GetDirectoryName(first)!;
        var package = Path.Combine(shimDir, "node_modules", "@openai", "codex");
        var native = Path.Combine(package, "node_modules", "@openai", "codex-win32-x64", "vendor", "x86_64-pc-windows-msvc", "bin", "codex.exe");
        if (File.Exists(native))
        {
            return new CodexLaunch(native, []);
        }

        var script = Path.Combine(package, "bin", "codex.js");
        return File.Exists(script) ? new CodexLaunch("node", [script]) : null;
    }

    /// <summary>
    /// codex's own rule: within 5% of 300, 1440 or 10080 minutes, else whole hours
    /// </summary>
    public static string WindowLabel(int minutes)
    {
        if (Near(minutes, 300))
        {
            return "5-hour";
        }

        if (Near(minutes, 1440))
        {
            return "Daily";
        }

        if (Near(minutes, 10080))
        {
            return "Weekly";
        }

        var hours = Math.Max(1, (int)Math.Round(minutes / 60.0, MidpointRounding.AwayFromZero));
        return $"{hours}-hour";

        static bool Near(int minutes, int window) => 20L * Math.Abs(minutes - window) <= window;
    }

    // 80 mirrors the warning threshold Anthropic's api applied on 2026-10-17
    public static string Severity(double usedPercent, bool reached)
        => reached || usedPercent >= 100 ? "critical" : usedPercent >= 80 ? "warning" : "normal";

    /// <summary>
    /// pure. takes the whole json-rpc reply object, result or error
    /// </summary>
    public static ProviderUsage Parse(string replyJson, DateTimeOffset now)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(replyJson);
        }
        catch (JsonException)
        {
            return ProviderUsage.Unavailable(Provider, null, UnexpectedOutput, now);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (ReplyError(root) is { } error)
            {
                return ProviderUsage.Unavailable(Provider, null, error, now);
            }

            if (!JsonRead.Object(root, "result", out var result) || !JsonRead.Object(result, "rateLimits", out var limits))
            {
                return ProviderUsage.Unavailable(Provider, null, UnexpectedOutput, now);
            }

            var reached = limits.TryGetProperty("rateLimitReachedType", out var reachedType) && reachedType.ValueKind != JsonValueKind.Null;
            var meters = new List<UsageMeter>();

            // a null window is just absent; 2026-10-17 reported only a weekly primary
            foreach (var slot in new[] { "primary", "secondary" })
            {
                if (!JsonRead.Object(limits, slot, out var window)
                    || JsonRead.Number(window, "usedPercent") is not { } used
                    || JsonRead.Long(window, "windowDurationMins") is not { } minutes || minutes is <= 0 or > int.MaxValue
                    || JsonRead.Long(window, "resetsAt") is not { } resetsAt || resetsAt is < 0 or > 253_402_300_799)
                {
                    continue;
                }

                var label = WindowLabel((int)minutes);
                meters.Add(new UsageMeter($"codex_{label.ToLowerInvariant()}", label, (int)minutes, used,
                    DateTimeOffset.FromUnixTimeSeconds(resetsAt), Severity(used, reached), null));
            }

            return new ProviderUsage(Provider, JsonRead.String(limits, "planType"), UsageState.Ok, null, null, meters, null, [], now);
        }
    }

    public static async Task<ProviderUsage> ReadAsync(CodexLaunch launch, TimeSpan cap, CancellationToken ct)
        => (await ReadOutcomeAsync(launch, cap, ct).ConfigureAwait(false)).Usage;

    internal static async Task<ReaderOutcome> ReadOutcomeAsync(string? overridePath, CancellationToken ct)
    {
        var launch = await FindAsync(overridePath, ct).ConfigureAwait(false);
        return launch is null
            ? new ReaderOutcome(ProviderUsage.Unavailable(Provider, null, NotFound, DateTimeOffset.UtcNow))
            : await ReadOutcomeAsync(launch, DefaultCap, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// one cap covers every read and write; every exit path ends in the same finally,
    /// which kills the tree. cancelling ct throws; the cap returns "timed out"
    /// </summary>
    internal static async Task<ReaderOutcome> ReadOutcomeAsync(CodexLaunch launch, TimeSpan cap, CancellationToken ct)
    {
        using var capSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
        capSource.CancelAfter(cap);
        var token = capSource.Token;
        JsonLineProcess? process = null;

        ReaderOutcome Done(ProviderUsage usage) => new(
            usage,
            LogNote: usage.State == UsageState.Ok || process is null
                ? null
                : $"stderr {process.StderrChars} chars, {process.SkippedLines} non-json lines skipped",
            ProcessId: process?.Id);

        ReaderOutcome Failed(string message) => Done(ProviderUsage.Unavailable(Provider, null, message, DateTimeOffset.UtcNow));

        try
        {
            process = JsonLineProcess.Start(launch.FileName, [.. launch.LeadingArgs, "app-server"]);
            await process.WriteLineAsync(InitializeRequest, token).ConfigureAwait(false);
            var init = await process.ReadReplyAsync(1, token).ConfigureAwait(false);
            if (init is null)
            {
                return Failed(ExitedEarly);
            }

            if (ReplyError(init) is { } initError)
            {
                return Failed(initError);
            }

            await process.WriteLineAsync(InitializedNotice, token).ConfigureAwait(false);
            await process.WriteLineAsync(RateLimitsRequest, token).ConfigureAwait(false);
            var reply = await process.ReadReplyAsync(2, token).ConfigureAwait(false);
            if (reply is null)
            {
                return Failed(ExitedEarly);
            }

            var usage = Parse(reply, DateTimeOffset.UtcNow);
            try
            {
                await process.CloseInputAndWaitAsync(ExitWait, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // the answer is in; the finally kills a slow exit
            }

            return Done(usage);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Failed($"Codex app-server timed out after {cap.TotalSeconds:0} s");
        }
        catch (InvalidDataException)
        {
            return Failed(UnexpectedOutput);
        }
        catch (IOException)
        {
            // stdin pipe closed under a write: the child is gone
            return Failed(ExitedEarly);
        }
        catch (Win32Exception)
        {
            return Failed(CouldNotStart);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Failed(Scrub.Message($"Codex read failed: {ex.Message}"));
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static string? ReplyError(string replyJson)
    {
        using var doc = JsonDocument.Parse(replyJson);
        return ReplyError(doc.RootElement);
    }

    // fixed text for not signed in; anything else is scrubbed and capped at 200
    private static string? ReplyError(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("error", out var error) || error.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        var message = JsonRead.String(error, "message");
        if (string.IsNullOrWhiteSpace(message))
        {
            return "Codex returned an error";
        }

        return message.Contains("login", StringComparison.OrdinalIgnoreCase) || message.Contains("auth", StringComparison.OrdinalIgnoreCase) || message.Contains("logged in", StringComparison.OrdinalIgnoreCase) || message.Contains("log in", StringComparison.OrdinalIgnoreCase)
            ? NotSignedIn
            : Scrub.Message($"Codex error: {message}");
    }
}

/// <summary>
/// json-rpc lines over a child's stdio: stderr kept to 8 KB, stdout to 256 lines or 1 MB.
/// sync pipes ignore cancellation, so a cancelled wait abandons the read and Dispose's kill ends it
/// </summary>
internal sealed class JsonLineProcess : IDisposable
{
    internal const int MaxLines = 256;
    internal const int MaxChars = 1 << 20;
    private const int StderrCap = 8 * 1024;
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly Process _process;
    private readonly StringBuilder _stderr = new();
    private readonly StringBuilder _pending = new();
    private readonly char[] _chunk = new char[4096];
    private int _stderrChars;
    private int _scanFrom;
    private int _lines;
    private int _chars;
    private bool _eof;

    private JsonLineProcess(Process process)
    {
        _process = process;
        Id = process.Id;
        _ = DrainStderrAsync();
    }

    public int Id { get; }

    // only lengths ever leave this class; the content stays in the buffer
    public int StderrChars => Volatile.Read(ref _stderrChars);

    public int SkippedLines { get; private set; }

    public static JsonLineProcess Start(string fileName, IEnumerable<string> arguments)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                // no bom on stdin, or the first request line starts with one
                StandardInputEncoding = Utf8,
                StandardOutputEncoding = Utf8,
                StandardErrorEncoding = Utf8,
            },
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        try
        {
            process.Start();
        }
        catch
        {
            process.Dispose();
            throw;
        }

        return new JsonLineProcess(process);
    }

    public async Task WriteLineAsync(string json, CancellationToken ct)
    {
        var write = WriteCoreAsync(json);
        try
        {
            await write.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Observe(write);
            throw;
        }
    }

    /// <summary>
    /// the reply object for id as raw text, skipping notifications and non-json lines.
    /// null when stdout ends first; InvalidDataException past the caps
    /// </summary>
    public async Task<string?> ReadReplyAsync(int id, CancellationToken ct)
    {
        while (await ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            if (IsReply(line, id))
            {
                return line;
            }
        }

        return null;
    }

    public async Task CloseInputAndWaitAsync(TimeSpan wait, CancellationToken ct)
    {
        try
        {
            _process.StandardInput.Close();
        }
        catch (IOException)
        {
            // already gone
        }

        try
        {
            await _process.WaitForExitAsync(ct).WaitAsync(wait, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Dispose kills it
        }
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(2000);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // exited between the check and the kill
        }

        _process.Dispose();
    }

    private async Task WriteCoreAsync(string json)
    {
        // \n, not WriteLine's \r\n
        await _process.StandardInput.WriteAsync(json + "\n").ConfigureAwait(false);
        await _process.StandardInput.FlushAsync().ConfigureAwait(false);
    }

    private async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        while (true)
        {
            for (var i = _scanFrom; i < _pending.Length; i++)
            {
                if (_pending[i] == '\n')
                {
                    var line = _pending.ToString(0, i).TrimEnd('\r');
                    _pending.Remove(0, i + 1);
                    _scanFrom = 0;
                    return CountLine(line);
                }
            }

            _scanFrom = _pending.Length;
            if (_eof)
            {
                if (_pending.Length == 0)
                {
                    return null;
                }

                var rest = _pending.ToString();
                _pending.Clear();
                _scanFrom = 0;
                return CountLine(rest);
            }

            var read = _process.StandardOutput.ReadAsync(_chunk.AsMemory(), CancellationToken.None).AsTask();
            int count;
            try
            {
                count = await read.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Observe(read);
                throw;
            }

            if (count == 0)
            {
                _eof = true;
                continue;
            }

            // counted in chars, a close enough stand-in for bytes
            _chars += count;
            if (_chars > MaxChars)
            {
                throw new InvalidDataException("stdout over 1 MB");
            }

            _pending.Append(_chunk, 0, count);
        }
    }

    private string CountLine(string line)
    {
        if (++_lines > MaxLines)
        {
            throw new InvalidDataException("stdout over 256 lines");
        }

        return line;
    }

    // a response carries result or error; a server request with the same id carries method
    private bool IsReply(string line, int id)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && JsonRead.Long(root, "id") == id
                && !root.TryGetProperty("method", out _)
                && (root.TryGetProperty("result", out _) || root.TryGetProperty("error", out _));
        }
        catch (JsonException)
        {
            SkippedLines++;
            return false;
        }
    }

    private async Task DrainStderrAsync()
    {
        var buffer = new char[4096];
        try
        {
            int count;
            while ((count = await _process.StandardError.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
            {
                Interlocked.Add(ref _stderrChars, count);
                var room = StderrCap - _stderr.Length;
                if (room > 0)
                {
                    _stderr.Append(buffer, 0, Math.Min(room, count));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // pipe closed by Dispose
        }
    }

    // a stranded read or write finishes after the kill; its fault must not go unobserved
    private static void Observe(Task task)
        => task.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
}

public static class GeminiUsage
{
    internal const string Provider = "Google";

    // the Gemini CLI stopped serving AI Pro on 2026-06-18; parsing `agy -p /usage` is a follow-up
    public static ProviderUsage Read()
    {
        var detail = AgyOnPath()
            ? "Antigravity CLI found; reading its usage report is not implemented yet."
            : "The Gemini CLI stopped serving AI Pro on 2026-06-18. The Antigravity CLI (agy) is not installed.";
        ProviderAction[] actions = [new("gemini-usage", "Open usage page"), new("antigravity-setup", "Set up Antigravity")];
        return new ProviderUsage(Provider, null, UsageState.Unavailable, "No local source on this machine.", detail, [], null, actions, DateTimeOffset.UtcNow);
    }

    private static bool AgyOnPath()
    {
        var extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';', StringSplitOptions.RemoveEmptyEntries);
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return dirs.Any(dir => extensions.Any(ext => File.Exists(Path.Combine(dir.Trim('"'), "agy" + ext))));
    }
}

/// <summary>
/// cache, cooldown and single flight per provider; providers read in parallel.
/// call RefreshAsync from the ui thread: log, Updated and BusyChanged fire on the caller's
/// context. it never throws, and after cancellation nothing fires
/// </summary>
public sealed class UsageService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    private readonly Action<string> _log;
    private readonly HttpClient _http = AnthropicUsage.CreateHttpClient();
    // readers finish on pool threads and AppendLog isn't thread-safe, so lines queue here
    private readonly ConcurrentQueue<string> _logQueue = new();
    private readonly ProviderSlot _anthropic;
    private readonly ProviderSlot _openai;
    private readonly ProviderSlot _google;
    private int _busyCount;

    public UsageService(Action<string> log)
    {
        _log = log;
        _anthropic = new ProviderSlot(AnthropicUsage.Provider, _logQueue);
        _openai = new ProviderSlot(CodexUsage.Provider, _logQueue);
        _google = new ProviderSlot(GeminiUsage.Provider, _logQueue);
    }

    public UsageSnapshot? Last { get; private set; }

    public bool Busy => Volatile.Read(ref _busyCount) > 0;

    // success or failure, so a failing provider can't drive a tight loop
    public DateTimeOffset? LastCompleted { get; private set; }

    public event Action<UsageSnapshot>? Updated;

    public event Action<bool>? BusyChanged;

    /// <summary>
    /// fresh skips the 60 s cache, never a cooldown. codexExecutableOverride is
    /// settings.CodexExecutable
    /// </summary>
    public async Task RefreshAsync(bool fresh, string? codexExecutableOverride, CancellationToken ct)
    {
        if (Interlocked.Increment(ref _busyCount) == 1)
        {
            BusyChanged?.Invoke(true);
        }

        UsageSnapshot? produced = null;
        try
        {
            var providers = await Task.WhenAll(
                _anthropic.GetAsync(fresh, c => AnthropicUsage.ReadOutcomeAsync(_http, c), ct),
                _openai.GetAsync(fresh, c => CodexUsage.ReadOutcomeAsync(codexExecutableOverride, c), ct),
                _google.GetAsync(fresh, _ => Task.FromResult(new ReaderOutcome(GeminiUsage.Read())), ct));

            Last = produced = new UsageSnapshot(DateTimeOffset.UtcNow, providers);
        }
        catch (OperationCanceledException)
        {
            // form closing
        }
        finally
        {
            LastCompleted = DateTimeOffset.UtcNow;
            var idle = Interlocked.Decrement(ref _busyCount) == 0;
            if (!ct.IsCancellationRequested)
            {
                while (_logQueue.TryDequeue(out var line))
                {
                    _log(line);
                }

                if (produced is not null)
                {
                    Updated?.Invoke(produced);
                }

                if (idle)
                {
                    BusyChanged?.Invoke(false);
                }
            }
        }
    }

    private sealed class ProviderSlot(string provider, ConcurrentQueue<string> log)
    {
        private readonly object _gate = new();
        private Task<ProviderUsage>? _pending;
        private ProviderUsage? _last;
        private DateTimeOffset _lastAt;
        private DateTimeOffset _cooldownUntil;
        private string? _loggedState;

        public Task<ProviderUsage> GetAsync(bool fresh, Func<CancellationToken, Task<ReaderOutcome>> read, CancellationToken ct)
        {
            lock (_gate)
            {
                if (_pending is not null)
                {
                    return _pending;
                }

                var now = DateTimeOffset.UtcNow;
                if (_last is not null && (now < _cooldownUntil || (!fresh && now - _lastAt < CacheTtl)))
                {
                    return Task.FromResult(_last);
                }

                // Task.Run so a reader that finishes synchronously can't run under the lock
                return _pending = Task.Run(() => ReadAndRecordAsync(read, ct), CancellationToken.None);
            }
        }

        private async Task<ProviderUsage> ReadAndRecordAsync(Func<CancellationToken, Task<ReaderOutcome>> read, CancellationToken ct)
        {
            ReaderOutcome outcome;
            try
            {
                outcome = await read(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                lock (_gate)
                {
                    _pending = null;
                }

                throw;
            }
            catch (Exception ex)
            {
                outcome = new ReaderOutcome(ProviderUsage.Unavailable(provider, null, Scrub.Message(ex.Message), DateTimeOffset.UtcNow));
            }

            var usage = outcome.Usage;
            lock (_gate)
            {
                _last = usage;
                _lastAt = DateTimeOffset.UtcNow;
                if (outcome.Cooldown is { } wait)
                {
                    _cooldownUntil = _lastAt + wait;
                    log.Enqueue($"usage: {provider} cooling down {wait.TotalSeconds:0} s{(outcome.LogNote is { } why ? $" after {why}" : "")}");
                }

                // quiet by default: a line on the first result and on each ok/unavailable flip
                if (usage.State != _loggedState)
                {
                    _loggedState = usage.State;
                    log.Enqueue(usage.State == UsageState.Ok
                        ? $"usage: {provider} ok, {usage.Meters.Count} meter{(usage.Meters.Count == 1 ? "" : "s")}"
                        : $"usage: {provider} unavailable: {usage.Message}{(outcome.Cooldown is null && outcome.LogNote is { } note ? $" ({note})" : "")}");
                }

                _pending = null;
            }

            return usage;
        }
    }
}

internal static class AppInfo
{
    public static readonly string Version = typeof(AppInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}

/// <summary>
/// kind-checked reads that return null instead of throwing on a shape change
/// </summary>
internal static class JsonRead
{
    public static bool Object(JsonElement parent, string name, out JsonElement value)
    {
        value = default;
        return parent.ValueKind == JsonValueKind.Object
            && parent.TryGetProperty(name, out value)
            && value.ValueKind == JsonValueKind.Object;
    }

    public static string? String(JsonElement parent, string name)
        => parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static double? Number(JsonElement parent, string name)
        => parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number)
            ? number
            : null;

    public static long? Long(JsonElement parent, string name)
        => parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            ? number
            : null;
}
