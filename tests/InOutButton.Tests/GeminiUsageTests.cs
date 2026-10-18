using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;

namespace InOutButton.Tests;

// spec tests 1 to 6, 12 and 21 (docs/2026-10-18-burn-pace-gemini-token-spec.md, section 9).
// expected values are read off fixtures/agy-usage-2026-10-18.json by hand, never from the parser
public class GeminiUsageTests
{
    private const string DidNotReport = "Antigravity CLI did not report usage; run agy in a terminal to check its sign-in";
    private const string ShapeChanged = "usage report changed shape (no buckets)";
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 21, 23, 16, TimeSpan.Zero);
    private static readonly DateTimeOffset WeeklyReset = DateTimeOffset.Parse("2026-10-25T21:23:16Z", CultureInfo.InvariantCulture);
    private static readonly DateTimeOffset FiveHourReset = DateTimeOffset.Parse("2026-10-19T02:23:16Z", CultureInfo.InvariantCulture);
    private static readonly DateTimeOffset ArgonReset = DateTimeOffset.Parse("2026-10-25T21:19:47Z", CultureInfo.InvariantCulture);

    private static string FixturePath => Path.Combine(AppContext.BaseDirectory, "fixtures", "agy-usage-2026-10-18.json");

    private static string Fixture() => File.ReadAllText(FixturePath);

    private static JsonNode FixtureNode() => JsonNode.Parse(Fixture())!;

    private static JsonArray Buckets(JsonNode node, int group) => node["command"]!["data"]!["groups"]![group]!["buckets"]!.AsArray();

    // the driver appends agy's arguments after the script; the trailing # comments them out
    private static AgyLaunch PowerShell(string script) => new("powershell.exe", ["-NoProfile", "-Command", script + " #"]);

    // test 1
    [Fact]
    public void ParsesTheCapturedReport()
    {
        var log = new List<string>();

        var usage = GeminiUsage.Parse(Fixture(), Now, log.Add);

        Assert.Equal("Google", usage.Provider);
        Assert.Equal("ok", usage.State);
        Assert.Equal("Antigravity CLI", usage.Plan);
        Assert.Null(usage.Message);
        Assert.Null(usage.Footnote);
        Assert.Empty(usage.Actions);
        Assert.Equal(Now, usage.UpdatedAt);
        Assert.Empty(log);

        // command.data.groups[].buckets[] in report order
        Assert.Equal(["gemini-weekly", "gemini-5h", "3p-weekly", "3p-5h", "gemini-argon-wtu-weekly"], usage.Meters.Select(m => m.Key));
        Assert.Equal(["Gemini Models", "Gemini Models", "Claude and GPT models", "Claude and GPT models", "Gemini 4 Argon"], usage.Meters.Select(m => m.Name));
        // window weekly, 5h, weekly, 5h, weekly
        Assert.Equal([10080, 300, 10080, 300, 10080], usage.Meters.Select(m => m.WindowMinutes));
        // remaining_fraction 1, 1, 1, 1, 0.9898666739463806: (1 - 0.98987) * 100 = 1.013, rounds to 1.0
        Assert.Equal([0.0, 0.0, 0.0, 0.0, 1.0], usage.Meters.Select(m => m.UsedPercent));
        Assert.Equal([WeeklyReset, FiveHourReset, WeeklyReset, FiveHourReset, ArgonReset], usage.Meters.Select(m => m.ResetsAt));
        Assert.All(usage.Meters, meter =>
        {
            Assert.Equal("normal", meter.Severity);
            Assert.Null(meter.Note);
        });
    }

    // test 2 and the clamp. codex's thresholds: 80 and up is a warning, 100 critical
    [Theory]
    [InlineData(0.2, 80.0, "warning")]
    [InlineData(0.21, 79.0, "normal")]
    [InlineData(0.0, 100.0, "critical")]
    [InlineData(1.5, 0.0, "normal")]
    [InlineData(-0.5, 100.0, "critical")]
    public void RemainingFractionBecomesPercentUsed(double remaining, double used, string severity)
    {
        var node = FixtureNode();
        Buckets(node, 0)[0]!["remaining_fraction"] = remaining;

        var meter = GeminiUsage.Parse(node.ToJsonString(), Now).Meters[0];

        Assert.Equal("gemini-weekly", meter.Key);
        Assert.Equal(used, meter.UsedPercent);
        Assert.Equal(severity, meter.Severity);
    }

    // test 3
    [Fact]
    public void AnUnknownWindowSkipsThatBucketAndKeepsTheRest()
    {
        var node = FixtureNode();
        Buckets(node, 0)[1]!["window"] = "daily";
        var log = new List<string>();

        var usage = GeminiUsage.Parse(node.ToJsonString(), Now, log.Add);

        Assert.Equal("ok", usage.State);
        Assert.Equal(["gemini-weekly", "3p-weekly", "3p-5h", "gemini-argon-wtu-weekly"], usage.Meters.Select(m => m.Key));
        var line = Assert.Single(log);
        Assert.Contains("gemini-5h", line);
        Assert.Contains("daily", line);
    }

    [Fact]
    public void BadFractionsAndResetTimesSkipWithALineEach()
    {
        var node = FixtureNode();
        Buckets(node, 0)[0]!["remaining_fraction"] = "most of it";
        Buckets(node, 0)[1]!.AsObject().Remove("remaining_fraction");
        Buckets(node, 1)[0]!["reset_time"] = "next tuesday";
        var log = new List<string>();

        var usage = GeminiUsage.Parse(node.ToJsonString(), Now, log.Add);

        Assert.Equal(["3p-5h", "gemini-argon-wtu-weekly"], usage.Meters.Select(m => m.Key));
        Assert.Equal(3, log.Count);
    }

    [Fact]
    public void ARepeatedIdKeepsTheFirstBucket()
    {
        var node = FixtureNode();
        // the argon bucket again, under the id of the first gemini bucket
        var copy = Buckets(node, 2)[0]!.DeepClone();
        copy["id"] = "gemini-weekly";
        Buckets(node, 2).Add(copy);
        var log = new List<string>();

        var usage = GeminiUsage.Parse(node.ToJsonString(), Now, log.Add);

        Assert.Equal(5, usage.Meters.Count);
        var kept = Assert.Single(usage.Meters, m => m.Key == "gemini-weekly");
        Assert.Equal("Gemini Models", kept.Name);
        Assert.Equal(0.0, kept.UsedPercent);
        Assert.Contains("gemini-weekly", Assert.Single(log));
    }

    [Theory]
    [InlineData("<html>")]
    [InlineData("""{"status":"SUCCESS","response":""}""")]
    [InlineData("""{"status":"SUCCESS","command":{"data":{"groups":"not a list"}}}""")]
    [InlineData("""{"status":"SUCCESS","command":{"data":{"groups":[{"name":"Gemini Models","buckets":[]}]}}}""")]
    [InlineData("[]")]
    public void NoUsableBucketIsTheShapeChangeNotACrash(string reply)
    {
        var usage = GeminiUsage.Parse(reply, Now);

        Assert.Equal("unavailable", usage.State);
        Assert.Equal(ShapeChanged, usage.Message);
        Assert.Empty(usage.Meters);
    }

    // test 4
    [Fact]
    public void AStatusOtherThanSuccessShowsTheScrubbedResponse()
    {
        var node = FixtureNode();
        node["status"] = "ERROR";
        node["response"] = "sign-in failed: Bearer abcDEF123456789xyz expired";

        var usage = GeminiUsage.Interpret(node.ToJsonString(), 0, Now);

        Assert.Equal("unavailable", usage.State);
        Assert.Equal(DidNotReport, usage.Message);
        Assert.Equal("sign-in failed: [redacted] expired", usage.Detail);
        Assert.Empty(usage.Meters);
    }

    [Fact]
    public void ANonZeroExitShowsTheResponseEvenWithData()
    {
        var usage = GeminiUsage.Interpret(Fixture(), 1, Now);

        Assert.Equal(DidNotReport, usage.Message);
        // response's tabs and newlines collapse to spaces
        Assert.StartsWith("Gemini Models Weekly Limit Remaining 100% 2026-10-25T21:23:16Z Gemini Models", usage.Detail);
        Assert.Empty(usage.Meters);
    }

    [Fact]
    public void StdoutThatIsNotJsonShowsItsScrubbedStart()
    {
        var plain = GeminiUsage.Interpret("agy: not signed in", 1, Now);
        Assert.Equal(DidNotReport, plain.Message);
        Assert.Equal("agy: not signed in", plain.Detail);

        var secret = GeminiUsage.Interpret("login needed, token=SYNTHsecret99 " + new string('.', 400), 0, Now);
        Assert.Equal(DidNotReport, secret.Message);
        Assert.DoesNotContain("SYNTHsecret99", secret.Detail);
        Assert.Contains("[redacted]", secret.Detail);
        Assert.True(secret.Detail!.Length <= 200, $"{secret.Detail.Length} chars");

        var empty = GeminiUsage.Interpret("", 0, Now);
        Assert.Equal(DidNotReport, empty.Message);
        Assert.Null(empty.Detail);
    }

    // test 5
    [Fact]
    public async Task AStandInPrintingTheFixtureReadsOk()
    {
        var launch = PowerShell($"[Console]::Out.Write([IO.File]::ReadAllText('{FixturePath}')); exit 0");

        var outcome = await GeminiUsage.ReadOutcomeAsync(launch, TimeSpan.FromSeconds(15), CancellationToken.None);

        Assert.Equal("ok", outcome.Usage.State);
        Assert.Equal(5, outcome.Usage.Meters.Count);
        Assert.Equal("gemini-argon-wtu-weekly", outcome.Usage.Meters[4].Key);
        Assert.Equal(1.0, outcome.Usage.Meters[4].UsedPercent);
        Assert.Equal(ArgonReset, outcome.Usage.Meters[4].ResetsAt);
        Assert.Null(outcome.Cooldown);
        Assert.False(IsAlive(outcome.ProcessId!.Value, "powershell"));
    }

    // test 6
    [Fact]
    public async Task AStandInPastTheCapTimesOutAndDies()
    {
        var launch = PowerShell("Start-Sleep 60");
        var clock = Stopwatch.StartNew();

        var outcome = await GeminiUsage.ReadOutcomeAsync(launch, TimeSpan.FromSeconds(2), CancellationToken.None);

        clock.Stop();
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"took {clock.Elapsed.TotalSeconds:0.0} s");
        Assert.Equal("unavailable", outcome.Usage.State);
        Assert.Equal("Antigravity CLI timed out after 2 s", outcome.Usage.Message);
        // soft backoff, 10 min or 60 by the run of failures before it
        Assert.Contains(outcome.Cooldown, new TimeSpan?[] { TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(60) });
        Assert.Equal(CooldownKind.Soft, outcome.CooldownKind);
        Assert.NotNull(outcome.ProcessId);
        Assert.False(IsAlive(outcome.ProcessId.Value, "powershell"));
    }

    // test 21, and the same envelope without the marker for contrast
    [Theory]
    [InlineData(true, "Antigravity CLI timed out after 20 s")]
    [InlineData(false, ShapeChanged)]
    public async Task ThePrintTimeoutMarkerIsATimeoutNotAShapeChange(bool marker, string message)
    {
        const string envelope = """{"conversation_id":"","status":"SUCCESS","response":"","duration_seconds":20,"num_turns":1}""";
        var stderr = marker ? "[Console]::Error.WriteLine('[agy] print timeout after 20s with turn in progress; returning partial output'); " : "";
        var launch = PowerShell($"{stderr}[Console]::Out.Write('{envelope}'); exit 0");

        var outcome = await GeminiUsage.ReadOutcomeAsync(launch, TimeSpan.FromSeconds(15), CancellationToken.None);

        Assert.Equal("unavailable", outcome.Usage.State);
        Assert.Equal(message, outcome.Usage.Message);
        Assert.Null(outcome.Usage.Detail);
        Assert.NotNull(outcome.Cooldown);
    }

    // test 12 through the driver: cmd waits 2 s so the job is assigned first, starts a powershell that
    // inherits stdout and writes its pid to a file, then exits at once. stdout never ends, so the cap fires
    [Fact]
    public async Task AGrandchildHoldingStdoutDiesWithTheJob()
    {
        var folder = Path.Combine(Path.GetTempPath(), "InOutButton.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var pidFile = Path.Combine(folder, "pid.txt");
        int? grandchild = null;
        try
        {
            var line = $"ping -n 3 127.0.0.1 >nul & start /b powershell -NoProfile -Command $PID^|Out-File -Encoding ascii {pidFile};Start-Sleep 60 & echo started";
            var launch = new AgyLaunch("cmd.exe", ["/c", line]);

            var outcome = await GeminiUsage.ReadOutcomeAsync(launch, TimeSpan.FromSeconds(8), CancellationToken.None);

            Assert.Equal("Antigravity CLI timed out after 8 s", outcome.Usage.Message);
            Assert.True(File.Exists(pidFile), "the grandchild never wrote its pid");
            grandchild = int.Parse(File.ReadAllText(pidFile).Trim(), CultureInfo.InvariantCulture);

            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (IsAlive(grandchild.Value, "powershell") && DateTime.UtcNow < deadline)
            {
                await Task.Delay(50);
            }

            Assert.False(IsAlive(grandchild.Value, "powershell"));
        }
        finally
        {
            if (grandchild is { } pid && IsAlive(pid, "powershell"))
            {
                using var orphan = Process.GetProcessById(pid);
                orphan.Kill();
            }

            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void BackoffIsTenMinutesThenAnHourFromTheThirdFailure()
    {
        Assert.Equal(TimeSpan.FromMinutes(10), GeminiUsage.Backoff(1));
        Assert.Equal(TimeSpan.FromMinutes(10), GeminiUsage.Backoff(2));
        Assert.Equal(TimeSpan.FromMinutes(60), GeminiUsage.Backoff(3));
        Assert.Equal(TimeSpan.FromMinutes(60), GeminiUsage.Backoff(9));
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
