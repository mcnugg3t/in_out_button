using Xunit.Abstractions;

namespace InOutButton.Tests;

// gated on INOUT_LIVE_TOOLS=1: they need the real agy and claude on PATH. a few seconds each,
// no model call (/usage is a slash command, auth status calls none). unset, they return at once
public sealed class LiveToolsTests(ITestOutputHelper output)
{
    private static readonly string[] BucketIds = ["gemini-weekly", "gemini-5h", "3p-weekly", "3p-5h", "gemini-argon-wtu-weekly"];

    private static bool Live => Environment.GetEnvironmentVariable("INOUT_LIVE_TOOLS") == "1";

    [Fact]
    public async Task AgyReportsUsageThroughDiscovery()
    {
        if (!Live)
        {
            return;
        }

        // file times are coarse; a little slack
        var started = DateTime.UtcNow.AddSeconds(-2);

        var outcome = await GeminiUsage.ReadOutcomeAsync((string?)null, CancellationToken.None);

        output.WriteLine($"agy: {outcome.Usage.State} {outcome.Usage.Message}".TrimEnd());
        foreach (var meter in outcome.Usage.Meters)
        {
            output.WriteLine($"  {meter.Key} {meter.UsedPercent}% used, resets {meter.ResetsAt:u}");
        }

        Assert.Equal("ok", outcome.Usage.State);
        var now = DateTimeOffset.UtcNow;
        Assert.Contains(outcome.Usage.Meters, meter => BucketIds.Contains(meter.Key) && meter.ResetsAt > now);
        // agy overwrites its --log-file on every run, so a fresh write time is this run's
        Assert.StartsWith(SettingsStore.SettingsDirectory, GeminiUsage.LogPath, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(GeminiUsage.LogPath), "no agy log at " + GeminiUsage.LogPath);
        Assert.True(File.GetLastWriteTimeUtc(GeminiUsage.LogPath) >= started, "the agy log predates this run");
    }

    // step 1 only: step 2 is a haiku call
    [Fact]
    public async Task ClaudeAuthStatusExitsCleanAndLeavesTheLoginAlone()
    {
        if (!Live)
        {
            return;
        }

        var path = AnthropicUsage.CredentialsPath;
        var before = File.ReadAllBytes(path);

        output.WriteLine($"claude: {await ClaudeCli.FindAsync(null, CancellationToken.None)}");
        var result = await ClaudeCli.RunStepAsync(ClaudeStep.AuthStatus, null, CancellationToken.None);

        var after = File.ReadAllBytes(path);
        output.WriteLine(ClaudeCli.Describe(ClaudeStep.AuthStatus, result));
        Assert.Equal(StepStatus.Exited, result.Status);
        Assert.Equal(0, result.ExitCode);
        // not Assert.Equal: on a failure it would print the bytes, token and all
        Assert.True(before.AsSpan().SequenceEqual(after), "claude auth status changed the credentials file");
    }
}
