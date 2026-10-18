using System.Diagnostics;
using System.Text.Json.Nodes;

namespace InOutButton.Tests;

// expected values are read off the fixtures by hand, never from the parser
public class UsageTests
{
    private const string AnthropicFixture = "anthropic-usage-2026-10-13.json";
    private const string CodexFixture = "codex-ratelimits-2026-10-17.json";
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 3, 2, 10, TimeSpan.Zero);

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", name));

    private static JsonNode FixtureNode(string name) => JsonNode.Parse(Fixture(name))!;

    // spec test 1
    [Fact]
    public void AnthropicParsesTheFixture()
    {
        var usage = AnthropicUsage.Parse(Fixture(AnthropicFixture), "Claude Max 20x", Now);

        Assert.Equal("Anthropic", usage.Provider);
        Assert.Equal("ok", usage.State);
        Assert.Null(usage.Message);
        Assert.Equal("Claude Max 20x", usage.Plan);
        Assert.Equal(Now, usage.UpdatedAt);
        Assert.Equal(3, usage.Meters.Count);

        var session = usage.Meters[0];
        Assert.Equal("session", session.Key);
        Assert.Equal("Session", session.Name);
        Assert.Equal(300, session.WindowMinutes);
        Assert.Equal(2.0, session.UsedPercent);                                          // five_hour.utilization
        Assert.Equal(DateTimeOffset.Parse("2026-10-13T05:50:00Z"), session.ResetsAt);  // limits[0].resets_at, .224467 rounds down
        Assert.Equal("normal", session.Severity);                                       // limits[0].severity
        Assert.Null(session.Note);

        var weekly = usage.Meters[1];
        Assert.Equal("weekly", weekly.Key);
        Assert.Equal("Weekly, all models", weekly.Name);
        Assert.Equal(10080, weekly.WindowMinutes);
        Assert.Equal(1.0, weekly.UsedPercent);                                           // seven_day.utilization
        Assert.Equal(DateTimeOffset.Parse("2026-10-18T00:00:00Z"), weekly.ResetsAt);   // limits[1].resets_at
        Assert.Equal("normal", weekly.Severity);
        Assert.Null(weekly.Note);

        var fable = usage.Meters[2];
        Assert.Equal("weekly_fable", fable.Key);                                         // limits[2].scope.model.display_name
        Assert.Equal("Weekly, Fable only", fable.Name);
        Assert.Equal(10080, fable.WindowMinutes);
        Assert.Equal(1.0, fable.UsedPercent);                                            // limits[2].percent, no window float
        Assert.Equal(DateTimeOffset.Parse("2026-10-18T00:00:00Z"), fable.ResetsAt);    // limits[2].resets_at, .224670 rounds down
        Assert.Equal("normal", fable.Severity);
        // (100 - 1) * 50 / 100, allowance from weekly_scoped_shares[0] (limit_index 2)
        Assert.Equal("headroom 49.5% of weekly", fable.Note);

        // extra_usage: used_credits 0.0, monthly_limit 20000, decimal_places 2, USD
        Assert.Equal("Extra usage on · $0.00 of $200.00 used", usage.Footnote);
        Assert.Null(usage.Detail);
        Assert.Empty(usage.Actions);
    }

    // the fixture's floats equal its integers, so nudge one to prove the float wins
    [Fact]
    public void AnthropicPrefersTheWindowFloat()
    {
        var node = FixtureNode(AnthropicFixture);
        node["five_hour"]!["utilization"] = 2.4;

        var usage = AnthropicUsage.Parse(node.ToJsonString(), null, Now);

        Assert.Equal(2.4, usage.Meters[0].UsedPercent);
    }

    [Fact]
    public void AnthropicMapsSeverityAndIgnoresUnknownKinds()
    {
        var node = FixtureNode(AnthropicFixture);
        var limits = node["limits"]!.AsArray();
        limits[0]!["severity"] = "WARNING";
        limits[1]!["severity"] = "something_new";
        limits.Add(JsonNode.Parse("""{"kind":"monthly_mystery","percent":50,"severity":"critical","resets_at":"2026-11-01T00:00:00+00:00"}"""));

        var usage = AnthropicUsage.Parse(node.ToJsonString(), null, Now);

        Assert.Equal(3, usage.Meters.Count);
        Assert.Equal("warning", usage.Meters[0].Severity);
        Assert.Equal("normal", usage.Meters[1].Severity);
    }

    // spec test 2
    [Fact]
    public void AnthropicFallsBackToTheWindowObjects()
    {
        var node = FixtureNode(AnthropicFixture);
        node.AsObject().Remove("limits");

        var usage = AnthropicUsage.Parse(node.ToJsonString(), null, Now);

        Assert.Equal("ok", usage.State);
        Assert.Equal(2, usage.Meters.Count);
        Assert.Equal("session", usage.Meters[0].Key);
        Assert.Equal(300, usage.Meters[0].WindowMinutes);
        Assert.Equal(2.0, usage.Meters[0].UsedPercent);                                         // five_hour.utilization
        Assert.Equal(DateTimeOffset.Parse("2026-10-13T05:50:00Z"), usage.Meters[0].ResetsAt);  // five_hour.resets_at
        Assert.Equal("weekly", usage.Meters[1].Key);
        Assert.Equal(10080, usage.Meters[1].WindowMinutes);
        Assert.Equal(1.0, usage.Meters[1].UsedPercent);                                         // seven_day.utilization
        Assert.Equal(DateTimeOffset.Parse("2026-10-18T00:00:00Z"), usage.Meters[1].ResetsAt);  // seven_day.resets_at
        Assert.Equal("Extra usage on · $0.00 of $200.00 used", usage.Footnote);
    }

    [Fact]
    public void AnthropicShapeChangeIsUnavailableNotACrash()
    {
        Assert.Equal("unavailable", AnthropicUsage.Parse("<html>", null, Now).State);
        Assert.Equal("unavailable", AnthropicUsage.Parse("""{"something":"else"}""", null, Now).State);
        Assert.Equal("unavailable", AnthropicUsage.Parse("[]", null, Now).State);
    }

    // subscriptionType and rateLimitTier as this machine's credentials file held them, 2026-10-17
    [Fact]
    public void AnthropicPlanName()
    {
        Assert.Equal("Claude Max 20x", AnthropicUsage.PlanName("max", "default_claude_max_20x"));
        Assert.Equal("Claude Pro", AnthropicUsage.PlanName("pro", "default_claude_pro"));
        Assert.Equal("team · odd_tier", AnthropicUsage.PlanName("team", "odd_tier"));
        Assert.Null(AnthropicUsage.PlanName(null, null));
    }

    // spec test 3
    [Fact]
    public void CodexParsesTheCapturedReply()
    {
        var usage = CodexUsage.Parse(Fixture(CodexFixture), Now);

        Assert.Equal("OpenAI", usage.Provider);
        Assert.Equal("ok", usage.State);
        Assert.Equal("prolite", usage.Plan);                         // result.rateLimits.planType
        var meter = Assert.Single(usage.Meters);                     // result.rateLimits.secondary is null
        Assert.Equal("Weekly", meter.Name);
        Assert.Equal("codex_weekly", meter.Key);
        Assert.Equal(10080, meter.WindowMinutes);                    // primary.windowDurationMins
        Assert.Equal(6.0, meter.UsedPercent);                        // primary.usedPercent
        // primary.resetsAt 1792876098 unix seconds
        Assert.Equal(DateTimeOffset.Parse("2026-10-24T21:08:18Z"), meter.ResetsAt);
        Assert.Equal("normal", meter.Severity);
        Assert.DoesNotContain(usage.Meters, m => m.Name == "5-hour");
    }

    // spec test 4
    [Fact]
    public void CodexLabelsAThreeHundredMinuteSecondary()
    {
        const string reply = """
            {"id":2,"result":{"rateLimits":{
              "primary":{"usedPercent":40,"windowDurationMins":10080,"resetsAt":1792876098},
              "secondary":{"usedPercent":85,"windowDurationMins":300,"resetsAt":1792700000},
              "planType":"pro","rateLimitReachedType":null}}}
            """;

        var usage = CodexUsage.Parse(reply, Now);

        Assert.Equal(2, usage.Meters.Count);
        Assert.Equal("Weekly", usage.Meters[0].Name);
        Assert.Equal("5-hour", usage.Meters[1].Name);
        Assert.Equal("codex_5-hour", usage.Meters[1].Key);
        Assert.Equal(85.0, usage.Meters[1].UsedPercent);
        Assert.Equal("warning", usage.Meters[1].Severity);
    }

    // spec test 5; 285 and 315 sit exactly on the 5% edge, 1368 is 1440 less 5%
    [Theory]
    [InlineData(300, "5-hour")]
    [InlineData(285, "5-hour")]
    [InlineData(315, "5-hour")]
    [InlineData(1440, "Daily")]
    [InlineData(1368, "Daily")]
    [InlineData(1367, "23-hour")]
    [InlineData(10080, "Weekly")]
    [InlineData(10500, "Weekly")]
    [InlineData(720, "12-hour")]
    public void CodexWindowLabel(int minutes, string expected)
    {
        Assert.Equal(expected, CodexUsage.WindowLabel(minutes));
    }

    // spec test 6
    [Theory]
    [InlineData(79, false, "normal")]
    [InlineData(80, false, "warning")]
    [InlineData(100, false, "critical")]
    [InlineData(6, true, "critical")]
    public void CodexSeverity(double usedPercent, bool reached, string expected)
    {
        Assert.Equal(expected, CodexUsage.Severity(usedPercent, reached));
    }

    [Fact]
    public void CodexReachedTypeMakesTheMeterCritical()
    {
        var node = FixtureNode(CodexFixture);
        node["result"]!["rateLimits"]!["rateLimitReachedType"] = "primary";

        var meter = Assert.Single(CodexUsage.Parse(node.ToJsonString(), Now).Meters);

        Assert.Equal("critical", meter.Severity);
    }

    // spec test 17
    [Fact]
    public void CodexErrorReplyNeverCarriesTheToken()
    {
        const string jwt = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJzeW50aGV0aWMifQ.c3ludGhldGljc2ln";
        var reply = $$$"""{"id":2,"error":{"code":-32603,"message":"rate limit read failed: upstream sent Bearer {{{jwt}}} back"}}""";

        var usage = CodexUsage.Parse(reply, Now);

        Assert.Equal("unavailable", usage.State);
        Assert.NotNull(usage.Message);
        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiJ9", usage.Message);
        Assert.DoesNotContain("c3ludGhldGljc2ln", usage.Message);
        Assert.Contains("[redacted]", usage.Message);
        Assert.Empty(usage.Meters);
    }

    // the spec's rule is "login" or "auth"; "Not logged in" alone falls to the scrubbed path
    [Fact]
    public void CodexSignInErrorIsAFixedMessage()
    {
        const string reply = """{"id":2,"error":{"code":-32600,"message":"login required: Bearer sk-SYNTHETIC0123456789 expired"}}""";

        var usage = CodexUsage.Parse(reply, Now);

        Assert.Equal("unavailable", usage.State);
        Assert.Contains("not signed in", usage.Message);
        Assert.DoesNotContain("SYNTHETIC", usage.Message);
    }

    // spec test 14
    [Theory]
    [InlineData("request failed: Authorization: Bearer abcDEF123456789xyz", "abcDEF123456789xyz")]
    [InlineData("key sk-ant-oat01-SYNTHETICabcdef123456 leaked", "SYNTHETICabcdef123456")]
    [InlineData("jwt eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJ0ZXN0In0.c2ln in the text", "eyJzdWIiOiJ0ZXN0In0")]
    [InlineData("GET https://example.com/oauth?code=SYNTH123&state=xyz failed", "SYNTH123")]
    [InlineData("config token=SYNTHsecret99 rejected", "SYNTHsecret99")]
    [InlineData("password: hunter2SYNTH was wrong", "hunter2SYNTH")]
    public void ScrubRedactsSecrets(string text, string secret)
    {
        var clean = Scrub.Message(text);

        Assert.DoesNotContain(secret, clean);
        Assert.Contains("[redacted]", clean);
    }

    [Fact]
    public void ScrubPassesPlainTextAndCapsLength()
    {
        Assert.Equal("Codex app-server exited early", Scrub.Message("Codex app-server exited early"));
        Assert.Equal("two lines here", Scrub.Message("two\r\n lines \t here"));
        Assert.Equal("", Scrub.Message(null));

        var cut = Scrub.Message(new string('x', 1000));
        Assert.Equal(200, cut.Length);
        Assert.EndsWith("…", cut);
    }

    // spec test 15. the driver appends app-server; the ; lets Start-Sleep run first
    [Fact]
    public async Task CodexDriverTimesOutAndKillsTheChild()
    {
        var launch = new CodexLaunch("powershell.exe", ["-NoProfile", "-Command", "Start-Sleep 60;"]);
        var clock = Stopwatch.StartNew();

        var outcome = await CodexUsage.ReadOutcomeAsync(launch, TimeSpan.FromSeconds(2), CancellationToken.None);

        clock.Stop();
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"took {clock.Elapsed.TotalSeconds:0.0} s");
        Assert.Equal("unavailable", outcome.Usage.State);
        Assert.Contains("timed out", outcome.Usage.Message);
        Assert.NotNull(outcome.ProcessId);
        Assert.False(IsAlive(outcome.ProcessId.Value, "powershell"));
    }

    // spec test 16
    [Fact]
    public async Task CodexDriverSurvivesGarbageAndExit()
    {
        var launch = new CodexLaunch("cmd.exe", ["/c", "echo", "not-json"]);

        var outcome = await CodexUsage.ReadOutcomeAsync(launch, TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.Equal("unavailable", outcome.Usage.State);
        Assert.True(
            outcome.Usage.Message!.Contains("exited early") || outcome.Usage.Message.Contains("unexpected output"),
            outcome.Usage.Message);
        Assert.NotNull(outcome.ProcessId);
        Assert.False(IsAlive(outcome.ProcessId.Value, "cmd"));
    }

    [Fact]
    public async Task CodexDriverCapsStdoutLines()
    {
        // 300 junk lines, over the 256-line cap, then a long sleep
        var launch = new CodexLaunch("powershell.exe", ["-NoProfile", "-Command", "1..300 | ForEach-Object { 'junk' }; Start-Sleep 60;"]);

        var outcome = await CodexUsage.ReadOutcomeAsync(launch, TimeSpan.FromSeconds(15), CancellationToken.None);

        Assert.Equal("unavailable", outcome.Usage.State);
        Assert.Contains("unexpected output", outcome.Usage.Message);
        Assert.False(IsAlive(outcome.ProcessId!.Value, "powershell"));
    }

    // a stand-in app-server: answers id 1 only after reading it, then a notification and the fixture reply
    [Fact]
    public async Task CodexDriverReadsAScriptedReply()
    {
        var reply = JsonNode.Parse(Fixture(CodexFixture))!.ToJsonString();
        var script =
            "$null = [Console]::In.ReadLine(); " +
            "[Console]::Out.WriteLine('not json, skipped'); " +
            "[Console]::Out.WriteLine('{\"id\":1,\"result\":{}}'); " +
            "$null = [Console]::In.ReadLine(); $null = [Console]::In.ReadLine(); " +
            "[Console]::Out.WriteLine('{\"method\":\"account/updated\",\"params\":{}}'); " +
            $"[Console]::Out.WriteLine('{reply}'); " +
            "exit 0;";
        var launch = new CodexLaunch("powershell.exe", ["-NoProfile", "-Command", script]);

        var outcome = await CodexUsage.ReadOutcomeAsync(launch, TimeSpan.FromSeconds(15), CancellationToken.None);

        Assert.Equal("ok", outcome.Usage.State);
        var meter = Assert.Single(outcome.Usage.Meters);
        Assert.Equal("Weekly", meter.Name);
        Assert.Equal(6.0, meter.UsedPercent);
    }

    [Fact]
    public void GeminiIsAnEmptyStateWithTwoActions()
    {
        var usage = GeminiUsage.Read();

        Assert.Equal("Google", usage.Provider);
        Assert.Equal("unavailable", usage.State);
        Assert.Equal("No local source on this machine.", usage.Message);
        Assert.NotNull(usage.Detail);
        Assert.Equal(["gemini-usage", "antigravity-setup"], usage.Actions.Select(a => a.Id));
    }

    private static bool IsAlive(int pid, string name)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            // a live process under another name is a reused pid
            return !process.HasExited && process.ProcessName.Equals(name, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
