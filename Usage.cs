using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

[assembly: InternalsVisibleTo("InOutButton.Tests")]

namespace InOutButton;

public sealed record UsagePoint(DateTimeOffset At, double UsedPercent);

// History is null from the readers; UsageStore.WithHistory fills it for the page
public sealed record UsageMeter(string Key, string Name, int WindowMinutes, double UsedPercent,
                                DateTimeOffset ResetsAt, string Severity, string? Note,
                                IReadOnlyList<UsagePoint>? History = null);

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
    // the last ok reading's meters, shown because the live read failed (spec 3.1)
    public const string Stale = "stale";
}

/// <summary>
/// built from settings for each refresh. ForceClaudeRefresh is true only for the
/// claude-refresh action
/// </summary>
public sealed record UsageOptions(string? CodexExecutable, string? ClaudeExecutable, string? AgyExecutable,
                                  bool ClaudeRefreshAuto, bool ForceClaudeRefresh);

// hard is a server's Retry-After and holds even a fresh read; soft is the app's own backoff, which fresh skips
internal enum CooldownKind
{
    Soft,
    Hard,
}

/// <summary>
/// what a reader hands the service beyond the page's view: a cooldown to honour,
/// a log-only note (lengths, never content), and the child pid for the driver tests
/// </summary>
internal sealed record ReaderOutcome(ProviderUsage Usage, TimeSpan? Cooldown = null, string? LogNote = null, int? ProcessId = null,
                                     CooldownKind CooldownKind = CooldownKind.Soft);

/// <summary>
/// redacts secret-shaped text from anything that came from outside the app before it
/// reaches a message, a page, or the log
/// </summary>
public static partial class Scrub
{
    private const int MaxLength = 200;
    private const string Redacted = "[redacted]";

    private static readonly Regex[] Secrets = [Bearer(), SkKey(), Jwt(), QueryString(), KeyValue(), Opaque()];

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

    // quotes optional around the key and the value, so json's "token":"..." is caught too.
    // the prefix covers client_secret and x-api-key, capped at 40 - unbounded it took 30 s on
    // 20 KB of a-a-a- (2026-10-17). the optional word takes Basic's scheme, whose + / = break the opaque run
    [GeneratedRegex(@"(?i)[""']?\b([\w-]{0,40}(?:token|key|secret|password|authorization|cookie|session))\b[""']?\s*[=:]\s*[""']?(?:\w+\s+)?[^\s""',}]+")]
    private static partial Regex KeyValue();

    // safety net for opaque tokens, 24+ of these in a row - also catches a guid or a long file name
    [GeneratedRegex(@"[A-Za-z0-9_\-]{24,}")]
    private static partial Regex Opaque();

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
    private const string ClaudeNotFound = "Claude Code (claude) not found; set ClaudeExecutable";
    private const string ClaudeCouldNotStart = "Claude Code (claude) could not be started";

    internal static readonly ProviderAction RefreshAction = new("claude-refresh", "Refresh via Claude Code");

    // one per process: the once-per-token guard and the 30 min floor live here
    internal static readonly ClaudeRefreshLadder DefaultLadder = new(ClaudeCli.RunStepAsync);

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

    // no ladder: a bare read never starts claude
    public static async Task<ProviderUsage> ReadAsync(HttpClient http, CancellationToken ct)
        => (await ReadOutcomeAsync(http, new UsageOptions(null, null, null, ClaudeRefreshAuto: false, ForceClaudeRefresh: false), null, ct)
            .ConfigureAwait(false)).Usage;

    // a redirect comes back as a 3xx and fails; no cookies; the 10 s timeout covers the body too
    internal static HttpClient CreateHttpClient()
        => new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
        {
            Timeout = TimeSpan.FromSeconds(10),
            MaxResponseContentBufferSize = 1 << 20,
        };

    internal static Task<ReaderOutcome> ReadOutcomeAsync(HttpClient http, UsageOptions options, Action<string>? log, CancellationToken ct)
        => ReadOutcomeAsync(http, CredentialsPath, DefaultLadder, options, log, ct);

    /// <summary>
    /// an expired or rejected token runs the refresh ladder inside this read (spec 5.1), so the
    /// message shown is the ladder's. tests pass their own credentials path and ladder
    /// </summary>
    internal static async Task<ReaderOutcome> ReadOutcomeAsync(HttpClient http, string credentialsPath, ClaudeRefreshLadder ladder,
                                                               UsageOptions options, Action<string>? log, CancellationToken ct)
    {
        // re-read every call: Claude Code rotates the token under us
        var login = ReadLogin(credentialsPath, out var failure);
        if (login is null)
        {
            return Fail(null, failure!);
        }

        // one ladder per read, so a replaced token that is then rejected can't loop
        var ladderRan = false;
        if (login.ExpiresAtMs is { } expiresAt && expiresAt <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
        {
            ladderRan = true;
            var (replaced, refused) = await RunLadderAsync(ladder, login, credentialsPath, rejected: false, options, log, ct).ConfigureAwait(false);
            if (replaced is null)
            {
                return refused!;
            }

            // a new token is a reason to try, not proof - the usage request decides (spec 5.4)
            login = replaced;
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

                // never retried; a hard cooldown, so fresh doesn't skip the wait either
                if (status == 429)
                {
                    var seconds = RetryAfterSeconds(response.Headers.RetryAfter, now);
                    return new ReaderOutcome(
                        ProviderUsage.Unavailable(Provider, login.Plan, $"rate limited by the usage endpoint; next try in {seconds} s", now),
                        TimeSpan.FromSeconds(seconds),
                        "HTTP 429",
                        CooldownKind: CooldownKind.Hard);
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
                        var again = ReadLogin(credentialsPath, out _);
                        if (again is not null && again.Token != login.Token)
                        {
                            login = again;
                            continue;
                        }
                    }

                    if (!ladderRan)
                    {
                        ladderRan = true;
                        var (replaced, refused) = await RunLadderAsync(ladder, login, credentialsPath, rejected: true, options, log, ct).ConfigureAwait(false);
                        if (replaced is null)
                        {
                            return refused!;
                        }

                        login = replaced;
                        continue;
                    }

                    return Refreshable(login.Plan, Rejected);
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

    // expired and rejected outcomes carry the button that runs the ladder by hand (spec 3.3)
    private static ReaderOutcome Refreshable(string? plan, string message)
        => new(new ProviderUsage(Provider, plan, UsageState.Unavailable, message, null, [], null, [RefreshAction], DateTimeOffset.UtcNow));

    /// <summary>
    /// the refresh ladder (spec 5.2): asks Claude Code to refresh its own login a step at a time and
    /// stops once the token string changes. the new login, or the outcome to report
    /// </summary>
    private static async Task<(ClaudeLogin? Replaced, ReaderOutcome? Refused)> RunLadderAsync(
        ClaudeRefreshLadder ladder, ClaudeLogin trigger, string credentialsPath, bool rejected, UsageOptions options,
        Action<string>? log, CancellationToken ct)
    {
        var word = rejected ? "rejected" : "expired";
        await ladder.Flight.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // a run that finished while this one waited may have replaced it already
            if (ReadLogin(credentialsPath, out _) is { } current && current.Token != trigger.Token)
            {
                return (current, null);
            }

            // keyed on the token string, not expiresAt, which can be absent (spec 5.1, G2)
            var ranForToken = ladder.HasRunFor(trigger.Token);
            if (!options.ForceClaudeRefresh && (!options.ClaudeRefreshAuto || ranForToken))
            {
                return (null, Refreshable(trigger.Plan, ranForToken ? NotReplaced(word) : rejected ? Rejected : Expired));
            }

            // a forced click inside the floor still gets this live read, just no ladder
            var now = ladder.Now();
            if (ladder.LastRun is { } last && now - last < ClaudeRefreshLadder.Floor)
            {
                var minutes = Math.Max(1, (int)(now - last).TotalMinutes);
                return (null, Refreshable(trigger.Plan, $"Claude Code token {word}; refresh ladder ran {minutes} min ago; try again later"));
            }

            foreach (var step in ClaudeCli.Steps)
            {
                var result = await ladder.RunStep(step, options.ClaudeExecutable, ct).ConfigureAwait(false);
                if (result.Status is StepStatus.NotFound or StepStatus.CouldNotStart)
                {
                    return (null, Refreshable(trigger.Plan, result.Status == StepStatus.NotFound ? ClaudeNotFound : ClaudeCouldNotStart));
                }

                // counted once a step has started, so a missing claude spends no run
                ladder.MarkRun(trigger.Token, now);
                log?.Invoke(ClaudeCli.Describe(step, result));

                // the token string is the test: a 401 with a future expiresAt is only cured by a new token
                if (ReadLogin(credentialsPath, out _) is { } after && after.Token != trigger.Token)
                {
                    log?.Invoke($"token refresh: {ClaudeCli.Name(step)} replaced the token (expiresAt {DescribeExpiry(after.ExpiresAtMs)})");
                    return (after, null);
                }
            }

            return (null, Refreshable(trigger.Plan, NotReplaced(word)));
        }
        finally
        {
            ladder.Flight.Release();
        }
    }

    private static string NotReplaced(string word) => $"Claude Code token {word}; the refresh ladder did not replace it";

    private static string DescribeExpiry(double? ms)
        => ms is { } value && value is >= 0 and <= 253_402_300_799_999
            ? DateTimeOffset.FromUnixTimeMilliseconds((long)value).ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
            : "none";

    // a class, not a record: a record's ToString would print the token
    private sealed class ClaudeLogin
    {
        public required string Token { get; init; }
        public string? Plan { get; init; }
        public double? ExpiresAtMs { get; init; }
    }

    // read only, never written; nothing from the file reaches a message
    private static ClaudeLogin? ReadLogin(string credentialsPath, out string? failure)
    {
        failure = null;
        string text;
        try
        {
            // ReadAllText drops a utf-8 bom
            text = File.ReadAllText(credentialsPath, Encoding.UTF8);
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

internal enum ClaudeStep
{
    AuthStatus,
    Prompt,
}

internal enum StepStatus
{
    Exited,
    StillRunning,
    NotFound,
    CouldNotStart,
}

// lengths only: a step's output is never kept or shown
internal sealed record StepResult(StepStatus Status, int? ExitCode = null, TimeSpan Duration = default, int StdoutChars = 0, int StderrChars = 0);

internal delegate Task<StepResult> ClaudeStepRunner(ClaudeStep step, string? executableOverride, CancellationToken ct);

/// <summary>
/// the refresh ladder's state for one process: the tokens it has run for, when it last ran, and
/// its single flight. tests build their own with fake steps and a clock
/// </summary>
internal sealed class ClaudeRefreshLadder(ClaudeStepRunner runStep, Func<DateTimeOffset>? clock = null)
{
    // bounds repeated clicks and how many abandoned steps can exist (spec 5.1, 5.2)
    internal static readonly TimeSpan Floor = TimeSpan.FromMinutes(30);

    // sha-256 of each token, so the set never holds a usable one
    private readonly HashSet<string> _ranFor = new(StringComparer.Ordinal);

    public ClaudeStepRunner RunStep { get; } = runStep;

    // held for a whole run; LastRun and the token set are touched only under it
    public SemaphoreSlim Flight { get; } = new(1, 1);

    public DateTimeOffset? LastRun { get; private set; }

    public DateTimeOffset Now() => clock?.Invoke() ?? DateTimeOffset.UtcNow;

    public bool HasRunFor(string token) => _ranFor.Contains(Hash(token));

    public void MarkRun(string token, DateTimeOffset at)
    {
        _ranFor.Add(Hash(token));
        LastRun = at;
    }

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}

/// <summary>
/// one ladder step, waited on for a bounded time and never killed: killing Claude Code between taking
/// a rotated refresh token and saving it could strand the login (spec 5.2, G6). no job object, whose close kills too
/// </summary>
internal static class ClaudeCli
{
    internal static readonly ClaudeStep[] Steps = [ClaudeStep.AuthStatus, ClaudeStep.Prompt];
    private static readonly TimeSpan DrainWait = TimeSpan.FromSeconds(5);

    // the where.exe find, kept for the session. a miss isn't kept, so a later install is picked up
    private static string? _discovered;

    public static string Name(ClaudeStep step) => step == ClaudeStep.AuthStatus ? "claude auth status" : "claude -p";

    internal static TimeSpan Deadline(ClaudeStep step) => TimeSpan.FromSeconds(step == ClaudeStep.AuthStatus ? 60 : 90);

    // --setting-sources "" loads no settings so no hook runs; --strict-mcp-config without --mcp-config
    // starts no mcp server. never --bare: it skips the oauth login this is here to refresh.
    // measured 2026-10-18: 11.8 s, one haiku turn, about 1,000 input tokens
    internal static IReadOnlyList<string> Arguments(ClaudeStep step) => step == ClaudeStep.AuthStatus
        ? ["auth", "status"]
        : ["-p", "Reply with the single word OK.", "--model", "haiku", "--max-turns", "1", "--tools", "",
           "--strict-mcp-config", "--no-session-persistence", "--setting-sources", "", "--output-format", "json",
           "--system-prompt", "You reply with one word."];

    public static string Describe(ClaudeStep step, StepResult result)
    {
        var how = result.Status == StepStatus.StillRunning
            ? $"step still running after {Deadline(step).TotalSeconds:0} s, left to finish"
            : $"exit {result.ExitCode}";
        return string.Create(CultureInfo.InvariantCulture,
            $"token refresh: {Name(step)} {how}, {result.Duration.TotalSeconds:0.0} s, stdout {result.StdoutChars} chars, stderr {result.StderrChars} chars");
    }

    /// <summary>
    /// the override when it resolves, else where.exe's first hit that does. cancelling ct abandons the wait
    /// </summary>
    public static async Task<string?> FindAsync(string? overridePath, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath) && Resolve(overridePath) is { } chosen)
        {
            return chosen;
        }

        if (Volatile.Read(ref _discovered) is { } known)
        {
            return known;
        }

        ct.ThrowIfCancellationRequested();
        var (exitCode, output) = await ProcessRunner.CaptureAsync("where.exe", AppContext.BaseDirectory, 10, "claude").WaitAsync(ct).ConfigureAwait(false);
        var found = exitCode == 0
            ? ProcessRunner.SplitLines(output).Where(line => Path.IsPathRooted(line) && File.Exists(line)).Select(Resolve).FirstOrDefault(exe => exe is not null)
            : null;
        if (found is not null)
        {
            Volatile.Write(ref _discovered, found);
        }

        return found;
    }

    /// <summary>
    /// an exe as is; an npm shim (claude, claude.cmd) to the native exe it runs, which
    /// UseShellExecute = false can start; else null, so no cmd.exe is ever involved (spec 5.3)
    /// </summary>
    internal static string? Resolve(string hit)
    {
        if (hit.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return hit;
        }

        var native = Path.Combine(Path.GetDirectoryName(hit) ?? "", "node_modules", "@anthropic-ai", "claude-code", "bin", "claude.exe");
        return File.Exists(native) ? native : null;
    }

    public static async Task<StepResult> RunStepAsync(ClaudeStep step, string? overridePath, CancellationToken ct)
    {
        var exe = await FindAsync(overridePath, ct).ConfigureAwait(false);
        if (exe is null)
        {
            return new StepResult(StepStatus.NotFound);
        }

        // the settings folder: no project CLAUDE.md, no git repo
        Directory.CreateDirectory(SettingsStore.SettingsDirectory);
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = exe,
                WorkingDirectory = SettingsStore.SettingsDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };

        foreach (var argument in Arguments(step))
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        var clock = Stopwatch.StartNew();
        try
        {
            process.Start();
        }
        catch (Win32Exception)
        {
            process.Dispose();
            // a cached find that no longer starts is found again next time
            Interlocked.CompareExchange(ref _discovered, null, exe);
            return new StepResult(StepStatus.CouldNotStart);
        }

        // claude -p appends piped stdin to the prompt and would wait for its end
        try
        {
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // already gone
        }

        var stdout = new CharCounter(process.StandardOutput);
        var stderr = new CharCounter(process.StandardError);
        var exited = process.WaitForExitAsync(CancellationToken.None);
        if (await Task.WhenAny(exited, Task.Delay(Deadline(step), ct)).ConfigureAwait(false) != exited)
        {
            // left to finish: the drains keep its pipes from filling, and the Process goes once it exits
            _ = DisposeOnExitAsync(process, exited, stdout.Done, stderr.Done);
            ct.ThrowIfCancellationRequested();
            return new StepResult(StepStatus.StillRunning, null, clock.Elapsed, stdout.Chars, stderr.Chars);
        }

        var exitCode = process.ExitCode;
        try
        {
            // a grandchild holding a pipe open mustn't hold the ladder
            await Task.WhenAll(stdout.Done, stderr.Done).WaitAsync(DrainWait, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // lengths so far
        }
        finally
        {
            process.Dispose();
        }

        return new StepResult(StepStatus.Exited, exitCode, clock.Elapsed, stdout.Chars, stderr.Chars);
    }

    private static async Task DisposeOnExitAsync(Process process, Task exited, Task stdout, Task stderr)
    {
        try
        {
            await exited.ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).WaitAsync(DrainWait).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // a pipe still open; Dispose closes our end
        }
        finally
        {
            process.Dispose();
        }
    }

    // counts and discards, so nothing a step prints is ever kept
    private sealed class CharCounter
    {
        private int _chars;

        public CharCounter(StreamReader reader)
        {
            Done = DrainAsync(reader);
        }

        public Task Done { get; }

        public int Chars => Volatile.Read(ref _chars);

        private async Task DrainAsync(StreamReader reader)
        {
            var buffer = new char[4096];
            try
            {
                int count;
                while ((count = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
                {
                    Interlocked.Add(ref _chars, count);
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                // pipe closed by Dispose
            }
        }
    }
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
    private const string DiscoveryTimedOut = "Codex discovery timed out";

    // the where.exe find, kept for the session. a miss isn't kept, so a later install is picked up
    private static CodexLaunch? _discovered;

    private static readonly string InitializeRequest = JsonSerializer.Serialize(new
    {
        id = 1,
        method = "initialize",
        @params = new { clientInfo = new { name = "InOutButton", version = AppInfo.Version } },
    });

    private const string InitializedNotice = """{"method":"initialized"}""";
    private const string RateLimitsRequest = """{"id":2,"method":"account/rateLimits/read","params":{"excludeResetCreditDetails":true}}""";

    /// <summary>
    /// override first, checked on every call, then the npm install found by where.exe: its native
    /// exe, else node with codex.js. cancelling ct abandons the where.exe wait
    /// </summary>
    public static async Task<CodexLaunch?> FindAsync(string? overridePath, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
        {
            return new CodexLaunch(overridePath, []);
        }

        if (Volatile.Read(ref _discovered) is { } known)
        {
            return known;
        }

        ct.ThrowIfCancellationRequested();
        var (exitCode, output) = await ProcessRunner.CaptureAsync("where.exe", AppContext.BaseDirectory, 10, "codex").WaitAsync(ct).ConfigureAwait(false);
        var found = FromWhere(exitCode == 0 ? output : null);
        if (found is not null)
        {
            Volatile.Write(ref _discovered, found);
        }

        return found;
    }

    private static CodexLaunch? FromWhere(string? output)
    {
        var first = output is not null
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

    // log takes the child-job failure line; the service passes its queue
    internal static Task<ReaderOutcome> ReadOutcomeAsync(string? overridePath, CancellationToken ct, Action<string>? log = null)
        => ReadCoreAsync(overridePath, null, DefaultCap, log, ct);

    internal static Task<ReaderOutcome> ReadOutcomeAsync(CodexLaunch launch, TimeSpan cap, CancellationToken ct, Action<string>? log = null)
        => ReadCoreAsync(null, launch, cap, log, ct);

    /// <summary>
    /// one cap covers discovery (when launch is null) and every read and write; every exit path
    /// ends in the same finally, which kills the tree. cancelling ct throws; the cap returns "timed out"
    /// </summary>
    private static async Task<ReaderOutcome> ReadCoreAsync(string? overridePath, CodexLaunch? launch, TimeSpan cap, Action<string>? log, CancellationToken ct)
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
            if (launch is null)
            {
                try
                {
                    launch = await FindAsync(overridePath, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    return Failed(DiscoveryTimedOut);
                }

                if (launch is null)
                {
                    return Failed(NotFound);
                }
            }

            token.ThrowIfCancellationRequested();
            process = JsonLineProcess.Start(launch.FileName, [.. launch.LeadingArgs, "app-server"], log);
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
            // a cached find that no longer starts is found again next read
            Interlocked.CompareExchange(ref _discovered, null, launch);
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
/// json-rpc lines, or one whole reply, over a child's stdio: stderr kept to 8 KB, stdout to 256 lines
/// or 1 MB. sync pipes ignore cancellation, so a cancelled wait abandons the read and Dispose's kill ends it
/// </summary>
internal sealed class JsonLineProcess : IDisposable
{
    internal const int MaxLines = 256;
    internal const int MaxChars = 1 << 20;
    private const int StderrCap = 8 * 1024;
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private static int _assignFailureLogged;

    private readonly Process _process;
    private readonly ChildJob? _job;
    private readonly Task _stderrDrain;
    private readonly StringBuilder _stderr = new();
    private readonly StringBuilder _pending = new();
    private readonly char[] _chunk = new char[4096];
    private int _stderrChars;
    private int _scanFrom;
    private int _lines;
    private int _chars;
    private bool _eof;

    private JsonLineProcess(Process process, ChildJob? job)
    {
        _process = process;
        _job = job;
        Id = process.Id;
        _stderrDrain = DrainStderrAsync();
    }

    public int Id { get; }

    // only lengths ever leave this class; the content stays in the buffer
    public int StderrChars => Volatile.Read(ref _stderrChars);

    public int SkippedLines { get; private set; }

    public int ExitCode => _process.ExitCode;

    /// <summary>
    /// the child runs in a kill-on-close job, so Dispose also ends a grandchild whose parent has
    /// exited (spec 6.1). when windows refuses the job it runs without one, as before
    /// </summary>
    public static JsonLineProcess Start(string fileName, IEnumerable<string> arguments, Action<string>? log = null)
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

        // right after Start: only what the child starts from here on lands in the job
        var job = ChildJob.TryCreate(log);
        if (job is not null && !job.TryAssign(process))
        {
            if (log is not null && Interlocked.Exchange(ref _assignFailureLogged, 1) == 0)
            {
                log($"child job: could not assign {Path.GetFileName(fileName)} (win32 error {job.LastError}); it runs without a job");
            }

            job.Dispose();
            job = null;
        }

        return new JsonLineProcess(process, job);
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

    /// <summary>
    /// all of stdout as text, for a child that answers once and exits. InvalidDataException past 1 MB
    /// </summary>
    public async Task<string> ReadAllAsync(CancellationToken ct)
    {
        var text = new StringBuilder();
        while (true)
        {
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
                return text.ToString();
            }

            _chars += count;
            if (_chars > MaxChars)
            {
                throw new InvalidDataException("stdout over 1 MB");
            }

            text.Append(_chunk, 0, count);
        }
    }

    /// <summary>
    /// the child's exit and the end of its stderr, so StderrContains has seen all it will
    /// </summary>
    public async Task WaitForExitAsync(CancellationToken ct)
    {
        await _process.WaitForExitAsync(ct).ConfigureAwait(false);
        await _stderrDrain.WaitAsync(ct).ConfigureAwait(false);
    }

    // a fixed marker against the kept 8 KB; the text itself never leaves this class
    public bool StderrContains(string marker)
    {
        lock (_stderr)
        {
            return _stderr.ToString().Contains(marker, StringComparison.Ordinal);
        }
    }

    public void CloseInput()
    {
        try
        {
            _process.StandardInput.Close();
        }
        catch (IOException)
        {
            // already gone
        }
    }

    public async Task CloseInputAndWaitAsync(TimeSpan wait, CancellationToken ct)
    {
        CloseInput();

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
        // closing the job kills whatever the child left running
        _job?.Dispose();
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
                lock (_stderr)
                {
                    var room = StderrCap - _stderr.Length;
                    if (room > 0)
                    {
                        _stderr.Append(buffer, 0, Math.Min(room, count));
                    }
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

public sealed record AgyLaunch(string FileName, IReadOnlyList<string> LeadingArgs);

/// <summary>
/// Google meters from the Antigravity CLI's `agy -p /usage` report (spec 6). only the cli talks to
/// Google; the app never sees agy's token
/// </summary>
public static class GeminiUsage
{
    internal const string Provider = "Google";
    internal static readonly TimeSpan DefaultCap = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan ShortBackoff = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan LongBackoff = TimeSpan.FromMinutes(60);
    private const string Plan = "Antigravity CLI";

    private const string NotFound = "No local source on this machine.";
    private const string NotInstalled = "The Gemini CLI stopped serving AI Pro on 2026-06-18. The Antigravity CLI (agy) is not installed.";
    private const string CouldNotStart = "Antigravity CLI (agy) could not be started";
    private const string PrintTimedOut = "Antigravity CLI timed out after 20 s";
    private const string DidNotReport = "Antigravity CLI did not report usage; run agy in a terminal to check its sign-in";
    private const string ShapeChanged = "usage report changed shape (no buckets)";

    // agy's own --print-timeout exits 0 with status SUCCESS and no command.data, and writes
    // "[agy] print timeout after 20s ..." to stderr (2026-10-18). the only thing ever matched in stderr
    private const string PrintTimeoutMarker = "print timeout";

    // the where.exe find, kept for the session. a miss isn't kept, so a later install is picked up
    private static AgyLaunch? _discovered;
    private static int _failures;

    // overwritten on every run; holds the account email and trace ids, no token. never read back
    internal static string LogPath { get; } = Path.Combine(SettingsStore.SettingsDirectory, "agy-usage.log");

    internal static Task<ReaderOutcome> ReadOutcomeAsync(string? overridePath, CancellationToken ct)
        => ReadCoreAsync(overridePath, null, DefaultCap, null, ct);

    // log takes skipped buckets and the child-job failure line; the service passes its queue
    internal static Task<ReaderOutcome> ReadOutcomeAsync(string? overridePath, Action<string>? log, CancellationToken ct)
        => ReadCoreAsync(overridePath, null, DefaultCap, log, ct);

    internal static Task<ReaderOutcome> ReadOutcomeAsync(AgyLaunch launch, TimeSpan cap, CancellationToken ct, Action<string>? log = null)
        => ReadCoreAsync(null, launch, cap, log, ct);

    /// <summary>
    /// override first, checked on every call, then where.exe's first exe. cancelling ct abandons the wait
    /// </summary>
    public static async Task<AgyLaunch?> FindAsync(string? overridePath, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
        {
            return new AgyLaunch(overridePath, []);
        }

        if (Volatile.Read(ref _discovered) is { } known)
        {
            return known;
        }

        ct.ThrowIfCancellationRequested();
        var (exitCode, output) = await ProcessRunner.CaptureAsync("where.exe", AppContext.BaseDirectory, 10, "agy").WaitAsync(ct).ConfigureAwait(false);
        // an exe only: anything else would need cmd.exe
        var hit = exitCode == 0
            ? ProcessRunner.SplitLines(output).FirstOrDefault(line => Path.IsPathRooted(line) && line.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(line))
            : null;
        var found = hit is null ? null : new AgyLaunch(hit, []);
        if (found is not null)
        {
            Volatile.Write(ref _discovered, found);
        }

        return found;
    }

    // 10 min, then 60 from the third failure in a row (spec 6.1)
    internal static TimeSpan Backoff(int consecutiveFailures) => consecutiveFailures >= 3 ? LongBackoff : ShortBackoff;

    /// <summary>
    /// one run's stdout and exit code to a usage, before the stderr marker is considered. detail is
    /// the scrubbed first 200 chars of stdout when it isn't json, else of response
    /// </summary>
    internal static ProviderUsage Interpret(string stdout, int exitCode, DateTimeOffset now, Action<string>? log = null)
    {
        JsonDocument? doc = null;
        try
        {
            doc = JsonDocument.Parse(stdout);
        }
        catch (JsonException)
        {
            // not json
        }

        using (doc)
        {
            if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Failure(DidNotReport, Clip(stdout), now);
            }

            if (exitCode != 0 || JsonRead.String(doc.RootElement, "status") != "SUCCESS")
            {
                return Failure(DidNotReport, Clip(JsonRead.String(doc.RootElement, "response")), now);
            }
        }

        return Parse(stdout, now, log);
    }

    /// <summary>
    /// the whole envelope in; each command.data.groups[].buckets[] entry becomes a meter (spec 6.2).
    /// response text is never read. any exception is the shape-change failure, never a crash
    /// </summary>
    internal static ProviderUsage Parse(string replyJson, DateTimeOffset now, Action<string>? log = null)
    {
        try
        {
            using var doc = JsonDocument.Parse(replyJson);
            var meters = new List<UsageMeter>();
            if (JsonRead.Object(doc.RootElement, "command", out var command)
                && JsonRead.Object(command, "data", out var data)
                && data.TryGetProperty("groups", out var groups) && groups.ValueKind == JsonValueKind.Array)
            {
                foreach (var group in groups.EnumerateArray())
                {
                    if (group.ValueKind != JsonValueKind.Object
                        || !group.TryGetProperty("buckets", out var buckets) || buckets.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    var groupName = JsonRead.String(group, "name");
                    foreach (var bucket in buckets.EnumerateArray())
                    {
                        if (Meter(bucket, groupName, meters, log) is { } meter)
                        {
                            meters.Add(meter);
                        }
                    }
                }
            }

            return meters.Count == 0
                ? Failure(ShapeChanged, null, now)
                : new ProviderUsage(Provider, Plan, UsageState.Ok, null, null, meters, null, [], now);
        }
        catch (Exception)
        {
            return Failure(ShapeChanged, null, now);
        }
    }

    // null skips the bucket with one log line; ids, the window identity, are kept verbatim
    private static UsageMeter? Meter(JsonElement bucket, string? groupName, List<UsageMeter> kept, Action<string>? log)
    {
        var id = JsonRead.String(bucket, "id");
        if (string.IsNullOrEmpty(id))
        {
            log?.Invoke("usage: Google skipped a bucket with no id");
            return null;
        }

        void Skip(string why) => log?.Invoke($"usage: Google skipped bucket {Scrub.Message(id)}: {why}");

        if (kept.Any(meter => meter.Key == id))
        {
            Skip("repeated id, the first kept");
            return null;
        }

        var window = JsonRead.String(bucket, "window");
        int? minutes = window switch
        {
            "weekly" => 10080,
            "5h" => 300,
            _ => null,
        };
        if (minutes is null)
        {
            Skip($"window {Scrub.Message(window ?? "missing")} unknown");
            return null;
        }

        if (JsonRead.Number(bucket, "remaining_fraction") is not { } remaining)
        {
            Skip("remaining_fraction missing or not a number");
            return null;
        }

        if (!DateTimeOffset.TryParse(JsonRead.String(bucket, "reset_time"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var resetsAt))
        {
            Skip("reset_time unreadable");
            return null;
        }

        var used = Math.Clamp(Math.Round((1 - remaining) * 100, 1, MidpointRounding.AwayFromZero), 0, 100);
        // to the second, so the store's window identity can't split on a fraction
        var reset = DateTimeOffset.FromUnixTimeSeconds((long)Math.Round(resetsAt.ToUnixTimeMilliseconds() / 1000.0, MidpointRounding.AwayFromZero));
        return new UsageMeter(id, Scrub.Message(groupName ?? id), minutes.Value, used, reset, CodexUsage.Severity(used, reached: false), null);
    }

    /// <summary>
    /// one cap covers discovery (when launch is null), the run, and its exit; every exit path ends in
    /// the same finally, whose Dispose closes the job. cancelling ct throws; the cap returns "timed out"
    /// </summary>
    private static async Task<ReaderOutcome> ReadCoreAsync(string? overridePath, AgyLaunch? launch, TimeSpan cap, Action<string>? log, CancellationToken ct)
    {
        using var capSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
        capSource.CancelAfter(cap);
        var token = capSource.Token;
        JsonLineProcess? process = null;
        string? stdout = null;

        // every failure here backs off, parse failures included (spec 6.2)
        ReaderOutcome Failed(string message, string? detail = null) => new(
            Failure(message, detail, DateTimeOffset.UtcNow),
            Backoff(Interlocked.Increment(ref _failures)),
            process is null ? null : $"stdout {stdout?.Length ?? 0} chars, stderr {process.StderrChars} chars",
            process?.Id);

        string TimedOut() => $"Antigravity CLI timed out after {cap.TotalSeconds:0} s";

        try
        {
            if (launch is null)
            {
                try
                {
                    launch = await FindAsync(overridePath, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    return Failed(TimedOut());
                }

                if (launch is null)
                {
                    return NotFoundOutcome();
                }
            }

            token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(SettingsStore.SettingsDirectory);
            // never --disable-slash-commands: /usage would go to the model as a prompt and spend five calls
            process = JsonLineProcess.Start(launch.FileName,
                [.. launch.LeadingArgs, "-p", "/usage", "--output-format", "json", "--print-timeout", "20s", "--log-file", LogPath], log);
            process.CloseInput();
            stdout = await process.ReadAllAsync(token).ConfigureAwait(false);
            await process.WaitForExitAsync(token).ConfigureAwait(false);

            var usage = Interpret(stdout, process.ExitCode, DateTimeOffset.UtcNow, log);
            if (usage.State == UsageState.Ok)
            {
                Interlocked.Exchange(ref _failures, 0);
                return new ReaderOutcome(usage, ProcessId: process.Id);
            }

            // agy's print timeout otherwise reads as a shape change (G3)
            return process.StderrContains(PrintTimeoutMarker) ? Failed(PrintTimedOut) : Failed(usage.Message!, usage.Detail);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Failed(TimedOut());
        }
        catch (InvalidDataException)
        {
            // over 1 MB is not one json object either
            return Failed(DidNotReport);
        }
        catch (Win32Exception ex)
        {
            // a cached find that no longer starts is found again next read
            Interlocked.CompareExchange(ref _discovered, null, launch);
            // file or folder not found: agy isn't there, which is the empty state, not a failure
            return ex.NativeErrorCode is 2 or 3 ? NotFoundOutcome() : Failed(CouldNotStart);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Failed(Scrub.Message($"Antigravity read failed: {ex.Message}"));
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static ReaderOutcome NotFoundOutcome()
    {
        ProviderAction[] actions = [new("gemini-usage", "Open usage page"), new("antigravity-setup", "Set up Antigravity")];
        return new ReaderOutcome(new ProviderUsage(Provider, null, UsageState.Unavailable, NotFound, NotInstalled, [], null, actions, DateTimeOffset.UtcNow));
    }

    private static ProviderUsage Failure(string message, string? detail, DateTimeOffset now)
        => new(Provider, null, UsageState.Unavailable, message, detail, [], null, [], now);

    // the first 200 chars, scrubbed; null when nothing is left
    private static string? Clip(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var clean = Scrub.Message(text.Length > 200 ? text[..200] : text);
        return clean.Length == 0 ? null : clean;
    }
}

internal delegate Task<ReaderOutcome> UsageReader(UsageOptions options, Action<string> log, CancellationToken ct);

// the three readers the service calls; tests swap in fakes
internal sealed record UsageReaders(UsageReader Anthropic, UsageReader OpenAI, UsageReader Google);

/// <summary>
/// cache, cooldown and single flight per provider; providers read in parallel.
/// call RefreshAsync from the ui thread: log, Updated and BusyChanged fire on the caller's
/// context. it never throws, and after cancellation nothing fires
/// </summary>
public sealed class UsageService
{
    internal const string NotReadYet = "not read yet since the app started";
    internal static readonly TimeSpan StaleWindow = TimeSpan.FromDays(7);
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    private readonly Action<string> _log;
    private readonly UsageStore _store;
    private readonly UsageReaders _readers;
    private readonly HttpClient _http = AnthropicUsage.CreateHttpClient();
    // readers finish on pool threads and AppendLog isn't thread-safe, so lines queue here
    private readonly ConcurrentQueue<string> _logQueue = new();
    private readonly ProviderSlot _anthropic;
    private readonly ProviderSlot _openai;
    private readonly ProviderSlot _google;
    private int _busyCount;

    public UsageService(UsageStore store, Action<string> log)
        : this(store, log, null)
    {
    }

    /// <summary>
    /// takes a loaded store and re-points its log at this queue: its saves run under its lock on pool
    /// threads. Last starts as the stored readings, stale
    /// </summary>
    internal UsageService(UsageStore store, Action<string> log, UsageReaders? readers)
    {
        _log = log;
        _store = store;
        store.Log = _logQueue.Enqueue;
        _readers = readers ?? new UsageReaders(
            (options, line, ct) => AnthropicUsage.ReadOutcomeAsync(_http, options, line, ct),
            (options, line, ct) => CodexUsage.ReadOutcomeAsync(options.CodexExecutable, ct, line),
            (options, line, ct) => GeminiUsage.ReadOutcomeAsync(options.AgyExecutable, line, ct));
        _anthropic = new ProviderSlot(AnthropicUsage.Provider, store, _logQueue);
        _openai = new ProviderSlot(CodexUsage.Provider, store, _logQueue);
        _google = new ProviderSlot(GeminiUsage.Provider, store, _logQueue);
        Last = Seed(DateTimeOffset.UtcNow);
    }

    public UsageSnapshot? Last { get; private set; }

    public bool Busy => Volatile.Read(ref _busyCount) > 0;

    // success or failure, so a failing provider can't drive a tight loop
    public DateTimeOffset? LastCompleted { get; private set; }

    public event Action<UsageSnapshot>? Updated;

    public event Action<bool>? BusyChanged;

    /// <summary>
    /// fresh skips the 60 s cache and a soft cooldown, never a hard one
    /// </summary>
    public async Task RefreshAsync(bool fresh, UsageOptions options, CancellationToken ct)
    {
        if (Interlocked.Increment(ref _busyCount) == 1)
        {
            BusyChanged?.Invoke(true);
        }

        UsageSnapshot? produced = null;
        try
        {
            var providers = await Task.WhenAll(
                _anthropic.GetAsync(fresh, c => _readers.Anthropic(options, _logQueue.Enqueue, c), ct),
                _openai.GetAsync(fresh, c => _readers.OpenAI(options, _logQueue.Enqueue, c), ct),
                _google.GetAsync(fresh, c => _readers.Google(options, _logQueue.Enqueue, c), ct));

            // through the store on every post, so history is a list, never null
            Last = produced = new UsageSnapshot(DateTimeOffset.UtcNow, providers.Select(_store.WithHistory).ToList());
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

    // last night's numbers within a second of opening the tab, dated, with no actions (spec 8)
    private UsageSnapshot? Seed(DateTimeOffset now)
    {
        var seeded = new List<ProviderUsage>();
        foreach (var provider in new[] { AnthropicUsage.Provider, CodexUsage.Provider, GeminiUsage.Provider })
        {
            if (_store.LastOk(provider, StaleWindow, now) is { } stored)
            {
                seeded.Add(_store.WithHistory(stored with { State = UsageState.Stale, Message = NotReadYet, Detail = null, Actions = [] }));
            }
        }

        // dated by the newest reading, not now: nothing has been read yet
        return seeded.Count == 0 ? null : new UsageSnapshot(seeded.Max(usage => usage.UpdatedAt!.Value), seeded);
    }

    private sealed class ProviderSlot(string provider, UsageStore store, ConcurrentQueue<string> log)
    {
        private readonly object _gate = new();
        private Task<ProviderUsage>? _pending;
        private ProviderUsage? _last;
        private DateTimeOffset _lastAt;
        private DateTimeOffset _hardUntil;
        private DateTimeOffset _softUntil;
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
                if (_last is not null && (now < _hardUntil || (!fresh && (now < _softUntil || now - _lastAt < CacheTtl))))
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

            // outside the gate: the store has its own lock and writes a file
            var usage = Settle(outcome.Usage);
            lock (_gate)
            {
                _last = usage;
                _lastAt = DateTimeOffset.UtcNow;
                // each live read replaces both, so a fresh success clears a soft backoff
                _hardUntil = _softUntil = DateTimeOffset.MinValue;
                if (outcome.Cooldown is { } wait)
                {
                    if (outcome.CooldownKind == CooldownKind.Hard)
                    {
                        _hardUntil = _lastAt + wait;
                    }
                    else
                    {
                        _softUntil = _lastAt + wait;
                    }

                    log.Enqueue($"usage: {provider} cooling down {wait.TotalSeconds:0} s{(outcome.LogNote is { } why ? $" after {why}" : "")}");
                }

                // quiet by default: a line on the first result and on each ok, unavailable or stale flip
                if (usage.State != _loggedState)
                {
                    _loggedState = usage.State;
                    log.Enqueue(Describe(usage, outcome));
                }

                _pending = null;
            }

            return usage;
        }

        /// <summary>
        /// a live ok reading goes to the store, a null plan taking the stored one. a failure with an
        /// ok reading under 7 days old becomes stale: stored meters, plan, footnote and time, the
        /// failure's message, detail and actions
        /// </summary>
        private ProviderUsage Settle(ProviderUsage live)
        {
            var stored = store.LastOk(provider, StaleWindow, DateTimeOffset.UtcNow);
            if (live.State == UsageState.Ok)
            {
                // the tray app's rewrite nulls subscriptionType; keep the caption between its rewrites (spec 5.5)
                if (live.Plan is null && stored?.Plan is { } plan)
                {
                    live = live with { Plan = plan };
                }

                store.RecordOk(live);
                return live;
            }

            return stored is null
                ? live
                : stored with
                {
                    State = UsageState.Stale,
                    Plan = live.Plan ?? stored.Plan,
                    Message = live.Message,
                    Detail = live.Detail,
                    Actions = live.Actions,
                };
        }

        private string Describe(ProviderUsage usage, ReaderOutcome outcome)
        {
            var note = outcome.Cooldown is null && outcome.LogNote is { } text ? $" ({text})" : "";
            return usage.State switch
            {
                UsageState.Ok => $"usage: {provider} ok, {usage.Meters.Count} meter{(usage.Meters.Count == 1 ? "" : "s")}",
                UsageState.Stale => $"usage: {provider} stale, showing the reading of {usage.UpdatedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}: {usage.Message}{note}",
                _ => $"usage: {provider} unavailable: {usage.Message}{note}",
            };
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
