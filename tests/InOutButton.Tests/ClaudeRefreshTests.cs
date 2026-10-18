using System.Net;

namespace InOutButton.Tests;

// spec tests 13 to 16 (docs/2026-10-18-burn-pace-gemini-token-spec.md, section 9). no network and no
// claude: the credentials path, the usage endpoint, the ladder's steps and its clock are all fakes
public sealed class ClaudeRefreshTests : IDisposable
{
    private const string OldToken = "sk-ant-oat01-TEST-OLD";
    private const string NewToken = "sk-ant-oat01-TEST-NEW";
    // 2025-10-09, long past; 2100-01-01, far off
    private const long PastMs = 1_760_000_000_000;
    private const long FutureMs = 4_102_444_800_000;
    private const string ExpiredNotReplaced = "Claude Code token expired; the refresh ladder did not replace it";
    private const string RejectedNotReplaced = "Claude Code token rejected; the refresh ladder did not replace it";

    private static readonly UsageOptions Auto = new(null, null, null, ClaudeRefreshAuto: true, ForceClaudeRefresh: false);
    private static readonly UsageOptions Forced = Auto with { ForceClaudeRefresh = true };
    private static readonly UsageOptions Off = Auto with { ClaudeRefreshAuto = false };

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "InOutButton.Tests", Guid.NewGuid().ToString("N"));
    private readonly List<ClaudeStep> _steps = [];
    private readonly List<string> _log = [];
    private DateTimeOffset _now = new(2026, 10, 19, 7, 0, 0, TimeSpan.Zero);

    public ClaudeRefreshTests()
    {
        Directory.CreateDirectory(_folder);
    }

    private string CredentialsPath => Path.Combine(_folder, ".credentials.json");

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    // the shape Claude Code writes, with the fields the tray app drops
    private static string Credentials(string token, long expiresAt) => $$"""
        {"claudeAiOauth":{"accessToken":"{{token}}","refreshToken":"sk-ant-ort01-TEST","expiresAt":{{expiresAt}},"refreshTokenExpiresAt":{{FutureMs}},"scopes":["user:inference"],"subscriptionType":"max","rateLimitTier":"default_claude_max_20x"},"organizationUuid":"00000000-0000-0000-0000-000000000000"}
        """;

    private void WriteCredentials(string token, long expiresAt) => File.WriteAllText(CredentialsPath, Credentials(token, expiresAt));

    // a fake Claude Code: each step may rewrite the file, as a real refresh would
    private ClaudeRefreshLadder Ladder(Action<ClaudeStep>? effect = null, StepStatus status = StepStatus.Exited)
        => new((step, _, _) =>
        {
            _steps.Add(step);
            effect?.Invoke(step);
            return Task.FromResult(new StepResult(status, status == StepStatus.Exited ? 0 : null));
        }, () => _now);

    private Task<ReaderOutcome> ReadAsync(ClaudeRefreshLadder ladder, FakeEndpoint endpoint, UsageOptions options)
        => AnthropicUsage.ReadOutcomeAsync(new HttpClient(endpoint), CredentialsPath, ladder, options, _log.Add, CancellationToken.None);

    private static void AssertRefreshable(ReaderOutcome outcome, string message)
    {
        Assert.Equal("unavailable", outcome.Usage.State);
        Assert.Equal(message, outcome.Usage.Message);
        Assert.Equal(["claude-refresh"], outcome.Usage.Actions.Select(a => a.Id));
        Assert.Equal("Refresh via Claude Code", outcome.Usage.Actions[0].Label);
        // from the file's rateLimitTier
        Assert.Equal("Claude Max 20x", outcome.Usage.Plan);
    }

    // test 13: step 2 replaces the token, so the ladder stops and the read goes on with the new one
    [Fact]
    public async Task AStepThatReplacesTheTokenLeadsToTheHttpRead()
    {
        WriteCredentials(OldToken, PastMs);
        var ladder = Ladder(step =>
        {
            if (step == ClaudeStep.Prompt)
            {
                WriteCredentials(NewToken, FutureMs);
            }
        });
        var endpoint = new FakeEndpoint(token => token == NewToken ? HttpStatusCode.OK : HttpStatusCode.Unauthorized);

        var outcome = await ReadAsync(ladder, endpoint, Auto);

        Assert.Equal("ok", outcome.Usage.State);
        Assert.Equal(3, outcome.Usage.Meters.Count);
        Assert.Equal([ClaudeStep.AuthStatus, ClaudeStep.Prompt], _steps);
        Assert.Equal([NewToken], endpoint.Tokens);
        Assert.Contains(_log, line => line.StartsWith("token refresh: claude -p replaced the token (expiresAt ", StringComparison.Ordinal));
        Assert.Contains(_log, line => line.StartsWith("token refresh: claude auth status exit 0", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WhenStepOneReplacesItStepTwoNeverRuns()
    {
        WriteCredentials(OldToken, PastMs);
        var ladder = Ladder(_ => WriteCredentials(NewToken, FutureMs));
        var endpoint = new FakeEndpoint(_ => HttpStatusCode.OK);

        var outcome = await ReadAsync(ladder, endpoint, Auto);

        Assert.Equal("ok", outcome.Usage.State);
        Assert.Equal([ClaudeStep.AuthStatus], _steps);
    }

    // test 13, the other half
    [Fact]
    public async Task StepsThatLeaveTheTokenKeepTheExpiredOutcome()
    {
        WriteCredentials(OldToken, PastMs);
        var endpoint = new FakeEndpoint(_ => HttpStatusCode.OK);

        var outcome = await ReadAsync(Ladder(), endpoint, Auto);

        AssertRefreshable(outcome, ExpiredNotReplaced);
        Assert.Equal([ClaudeStep.AuthStatus, ClaudeStep.Prompt], _steps);
        Assert.Empty(endpoint.Tokens);
        Assert.Equal(2, _log.Count(line => line.StartsWith("token refresh: ", StringComparison.Ordinal)));
    }

    // test 14
    [Fact]
    public async Task TheAutomaticLadderRunsOncePerTokenAndAClickRunsItAgain()
    {
        WriteCredentials(OldToken, PastMs);
        var ladder = Ladder();
        var endpoint = new FakeEndpoint(_ => HttpStatusCode.OK);

        await ReadAsync(ladder, endpoint, Auto);
        Assert.Equal(2, _steps.Count);

        // past the floor, same token: no second automatic run
        _now = _now.AddMinutes(31);
        var second = await ReadAsync(ladder, endpoint, Auto);
        Assert.Equal(2, _steps.Count);
        AssertRefreshable(second, ExpiredNotReplaced);

        // the click bypasses the once-per-token guard
        var clicked = await ReadAsync(ladder, endpoint, Forced);
        Assert.Equal(4, _steps.Count);
        AssertRefreshable(clicked, ExpiredNotReplaced);
    }

    [Fact]
    public async Task AClickInsideTheFloorReadsWithoutTheLadder()
    {
        WriteCredentials(OldToken, PastMs);
        var ladder = Ladder();
        var endpoint = new FakeEndpoint(_ => HttpStatusCode.OK);
        await ReadAsync(ladder, endpoint, Auto);

        _now = _now.AddMinutes(12);
        var clicked = await ReadAsync(ladder, endpoint, Forced);

        Assert.Equal(2, _steps.Count);
        AssertRefreshable(clicked, "Claude Code token expired; refresh ladder ran 12 min ago; try again later");
    }

    [Fact]
    public async Task WithAutoOffOnlyAClickRunsTheLadder()
    {
        WriteCredentials(OldToken, PastMs);
        var ladder = Ladder();
        var endpoint = new FakeEndpoint(_ => HttpStatusCode.OK);

        var automatic = await ReadAsync(ladder, endpoint, Off);

        Assert.Empty(_steps);
        AssertRefreshable(automatic, "Claude Code token expired; run any claude command to refresh it");

        await ReadAsync(ladder, endpoint, Off with { ForceClaudeRefresh = true });
        Assert.Equal(2, _steps.Count);
    }

    [Fact]
    public async Task AMissingClaudeSkipsTheLadderAndSpendsNoRun()
    {
        WriteCredentials(OldToken, PastMs);
        var ladder = Ladder(status: StepStatus.NotFound);
        var endpoint = new FakeEndpoint(_ => HttpStatusCode.OK);

        var first = await ReadAsync(ladder, endpoint, Auto);
        var second = await ReadAsync(ladder, endpoint, Auto);

        AssertRefreshable(first, "Claude Code (claude) not found; set ClaudeExecutable");
        AssertRefreshable(second, "Claude Code (claude) not found; set ClaudeExecutable");
        // looked for again each time, so installing claude is picked up
        Assert.Equal([ClaudeStep.AuthStatus, ClaudeStep.AuthStatus], _steps);
        Assert.Null(ladder.LastRun);
    }

    [Fact]
    public async Task ARejectedTokenRunsTheLadderAndRetriesWithTheNewOne()
    {
        WriteCredentials(OldToken, FutureMs);
        var ladder = Ladder(step =>
        {
            if (step == ClaudeStep.Prompt)
            {
                WriteCredentials(NewToken, FutureMs);
            }
        });
        var endpoint = new FakeEndpoint(token => token == NewToken ? HttpStatusCode.OK : HttpStatusCode.Unauthorized);

        var outcome = await ReadAsync(ladder, endpoint, Auto);

        Assert.Equal("ok", outcome.Usage.State);
        Assert.Equal([OldToken, NewToken], endpoint.Tokens);
    }

    [Fact]
    public async Task ARejectedTokenTheLadderLeavesIsReportedWithTheAction()
    {
        WriteCredentials(OldToken, FutureMs);
        var endpoint = new FakeEndpoint(_ => HttpStatusCode.Unauthorized);

        var outcome = await ReadAsync(Ladder(), endpoint, Auto);

        AssertRefreshable(outcome, RejectedNotReplaced);
        // one request, the re-read found the same token, then the ladder
        Assert.Equal([OldToken], endpoint.Tokens);
        Assert.Equal(2, _steps.Count);
    }

    // a replaced token that is rejected again must not run a second ladder in the same read
    [Fact]
    public async Task OneLadderPerRead()
    {
        WriteCredentials(OldToken, FutureMs);
        var ladder = Ladder(step =>
        {
            if (step == ClaudeStep.AuthStatus)
            {
                WriteCredentials(NewToken, FutureMs);
            }
        });
        var endpoint = new FakeEndpoint(_ => HttpStatusCode.Unauthorized);

        var outcome = await ReadAsync(ladder, endpoint, Forced);

        Assert.Equal("unavailable", outcome.Usage.State);
        Assert.Equal(["claude-refresh"], outcome.Usage.Actions.Select(a => a.Id));
        Assert.Equal([ClaudeStep.AuthStatus], _steps);
        Assert.Equal([OldToken, NewToken], endpoint.Tokens);
    }

    // test 15: valid, expired and rejected paths never write the file
    [Theory]
    [InlineData(FutureMs, HttpStatusCode.OK)]
    [InlineData(PastMs, HttpStatusCode.OK)]
    [InlineData(FutureMs, HttpStatusCode.Unauthorized)]
    public async Task TheCredentialsFileIsNeverWritten(long expiresAt, HttpStatusCode status)
    {
        WriteCredentials(OldToken, expiresAt);
        var before = File.ReadAllBytes(CredentialsPath);
        var written = File.GetLastWriteTimeUtc(CredentialsPath);

        await ReadAsync(Ladder(), new FakeEndpoint(_ => status), Auto);

        Assert.Equal(before, File.ReadAllBytes(CredentialsPath));
        Assert.Equal(written, File.GetLastWriteTimeUtc(CredentialsPath));
    }

    // test 16: the token string is the test, not expiresAt
    [Fact]
    public async Task ANewTokenWithTheSameExpiryCountsAsReplaced()
    {
        WriteCredentials(OldToken, PastMs);
        var ladder = Ladder(_ => WriteCredentials(NewToken, PastMs));
        var endpoint = new FakeEndpoint(_ => HttpStatusCode.OK);

        var outcome = await ReadAsync(ladder, endpoint, Auto);

        Assert.Equal("ok", outcome.Usage.State);
        Assert.Equal([ClaudeStep.AuthStatus], _steps);
        Assert.Equal([NewToken], endpoint.Tokens);
    }

    [Fact]
    public async Task AMovedExpiryWithTheSameTokenIsNotReplaced()
    {
        WriteCredentials(OldToken, PastMs);
        var ladder = Ladder(_ => WriteCredentials(OldToken, FutureMs));
        var endpoint = new FakeEndpoint(_ => HttpStatusCode.OK);

        var outcome = await ReadAsync(ladder, endpoint, Auto);

        AssertRefreshable(outcome, ExpiredNotReplaced);
        Assert.Equal([ClaudeStep.AuthStatus, ClaudeStep.Prompt], _steps);
        Assert.Empty(endpoint.Tokens);
    }

    [Fact]
    public async Task AStepStillRunningAtItsDeadlineIsLoggedAndCountsAsNotReplaced()
    {
        WriteCredentials(OldToken, PastMs);

        var outcome = await ReadAsync(Ladder(status: StepStatus.StillRunning), new FakeEndpoint(_ => HttpStatusCode.OK), Auto);

        AssertRefreshable(outcome, ExpiredNotReplaced);
        Assert.Contains("token refresh: claude auth status step still running after 60 s, left to finish, 0.0 s, stdout 0 chars, stderr 0 chars", _log);
        Assert.Contains(_log, line => line.StartsWith("token refresh: claude -p step still running after 90 s", StringComparison.Ordinal));
    }

    // spec 6.1: a server's Retry-After is hard, the app's own 10 s after two 5xx is soft.
    // no Retry-After header means the 60 s default
    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, 60, true, 1)]
    [InlineData(HttpStatusCode.ServiceUnavailable, 10, false, 2)]
    public async Task EndpointCooldownsCarryTheirKind(HttpStatusCode status, int seconds, bool hard, int requests)
    {
        WriteCredentials(OldToken, FutureMs);
        var endpoint = new FakeEndpoint(_ => status);

        var outcome = await ReadAsync(Ladder(), endpoint, Auto);

        Assert.Equal(TimeSpan.FromSeconds(seconds), outcome.Cooldown);
        Assert.Equal(hard ? CooldownKind.Hard : CooldownKind.Soft, outcome.CooldownKind);
        Assert.Equal(requests, endpoint.Tokens.Count);
        Assert.Empty(_steps);
    }

    // spec 5.2, written out from the spec by hand
    [Fact]
    public void ThePromptStepLoadsNoSettingsAndNoMcp()
    {
        Assert.Equal(["auth", "status"], ClaudeCli.Arguments(ClaudeStep.AuthStatus));
        Assert.Equal(
            ["-p", "Reply with the single word OK.", "--model", "haiku", "--max-turns", "1", "--tools", "",
             "--strict-mcp-config", "--no-session-persistence", "--setting-sources", "", "--output-format", "json",
             "--system-prompt", "You reply with one word."],
            ClaudeCli.Arguments(ClaudeStep.Prompt));
        Assert.DoesNotContain("--bare", ClaudeCli.Arguments(ClaudeStep.Prompt));
    }

    // spec 5.3: the npm shims resolve to the native exe beside them, or to nothing
    [Fact]
    public void ShimsResolveToTheNativeExe()
    {
        var shim = Path.Combine(_folder, "claude");
        var cmd = Path.Combine(_folder, "claude.cmd");
        File.WriteAllText(shim, "");
        File.WriteAllText(cmd, "");

        Assert.Null(ClaudeCli.Resolve(shim));
        Assert.Null(ClaudeCli.Resolve(cmd));

        var native = Path.Combine(_folder, "node_modules", "@anthropic-ai", "claude-code", "bin", "claude.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(native)!);
        File.WriteAllText(native, "");

        Assert.Equal(native, ClaudeCli.Resolve(shim));
        Assert.Equal(native, ClaudeCli.Resolve(cmd));
        Assert.Equal(@"D:\tools\claude.exe", ClaudeCli.Resolve(@"D:\tools\claude.exe"));
    }

    // the anthropic fixture for 200, nothing for anything else; records each bearer token
    private sealed class FakeEndpoint(Func<string, HttpStatusCode> statusFor) : HttpMessageHandler
    {
        private static readonly string Body = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "anthropic-usage-2026-10-13.json"));

        public List<string> Tokens { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var token = request.Headers.Authorization?.Parameter ?? "";
            Tokens.Add(token);
            var status = statusFor(token);
            var response = new HttpResponseMessage(status);
            if (status == HttpStatusCode.OK)
            {
                response.Content = new StringContent(Body);
            }

            return Task.FromResult(response);
        }
    }
}
