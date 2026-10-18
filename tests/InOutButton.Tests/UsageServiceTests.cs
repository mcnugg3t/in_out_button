using System.Text.Json.Nodes;

namespace InOutButton.Tests;

// spec tests 10, 11 and 17 to 20 (docs/2026-10-18-burn-pace-gemini-token-spec.md, section 9),
// with fake readers in place of the three providers
public sealed class UsageServiceTests : IDisposable
{
    private static readonly UsageOptions Options = new(null, null, null, ClaudeRefreshAuto: false, ForceClaudeRefresh: false);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "InOutButton.Tests", Guid.NewGuid().ToString("N"));
    private readonly List<string> _log = [];
    // whole seconds, so the store's window identity matches exactly
    private readonly DateTimeOffset _reset = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.AddDays(3).ToUnixTimeSeconds());

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private string StorePath => Path.Combine(_folder, "usage-store.json");

    private UsageStore NewStore()
    {
        var store = new UsageStore(StorePath);
        store.Load();
        return store;
    }

    private UsageService NewService(UsageStore store, FakeReader? anthropic = null, FakeReader? openai = null, FakeReader? google = null,
                                    Func<DateTimeOffset>? clock = null)
        => new(store, Log, new UsageReaders(
            (anthropic ?? FakeReader.Failing("Anthropic")).ReadAsync,
            (openai ?? FakeReader.Failing("OpenAI")).ReadAsync,
            (google ?? FakeReader.Failing("Google")).ReadAsync), clock);

    private void Log(string line)
    {
        lock (_log)
        {
            _log.Add(line);
        }
    }

    private ProviderUsage Ok(string provider, double used, string? plan = "Claude Max 20x", DateTimeOffset? at = null)
        => new(provider, plan, "ok", null, null,
            [new UsageMeter("weekly", "Weekly, all models", 10080, used, _reset, "normal", null)],
            "Extra usage off", [], at ?? DateTimeOffset.UtcNow);

    private static ProviderUsage Failed(string provider, string message, string? plan = null)
        => new(provider, plan, "unavailable", message, "detail text", [], null, [new ProviderAction("claude-refresh", "Refresh via Claude Code")], DateTimeOffset.UtcNow);

    private static ProviderUsage Provider(UsageService service, string name) => service.Last!.Providers.Single(p => p.Provider == name);

    // test 10: a cache hit is the same observation, not a new point
    [Fact]
    public async Task ACacheHitAddsNoPoint()
    {
        var store = NewStore();
        var anthropic = new FakeReader(() => new ReaderOutcome(Ok("Anthropic", 10)));
        var service = NewService(store, anthropic);

        await service.RefreshAsync(false, Options, CancellationToken.None);
        await service.RefreshAsync(false, Options, CancellationToken.None);

        Assert.Equal(1, anthropic.Calls);
        var history = Provider(service, "Anthropic").Meters[0].History;
        Assert.NotNull(history);
        Assert.Single(history);
        // the saved series too, not only the window the post shows
        var saved = JsonNode.Parse(File.ReadAllText(StorePath))!["history"]!["Anthropic/weekly"]!.AsArray();
        Assert.Single(saved);
    }

    // test 11
    [Fact]
    public async Task AFailureWithAnHourOldReadingIsStale()
    {
        var store = NewStore();
        var readAt = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds());
        store.RecordOk(Ok("Anthropic", 43, at: readAt));
        var anthropic = new FakeReader(() => new ReaderOutcome(Failed("Anthropic", "Claude Code token expired; the refresh ladder did not replace it")));
        var service = NewService(store, anthropic);

        await service.RefreshAsync(false, Options, CancellationToken.None);

        var stale = Provider(service, "Anthropic");
        Assert.Equal("stale", stale.State);
        Assert.Equal("Claude Code token expired; the refresh ladder did not replace it", stale.Message);
        Assert.Equal("detail text", stale.Detail);
        Assert.Equal(readAt, stale.UpdatedAt);
        Assert.Equal("Extra usage off", stale.Footnote);
        // the failure had no plan, so the stored one
        Assert.Equal("Claude Max 20x", stale.Plan);
        Assert.Equal(["claude-refresh"], stale.Actions.Select(a => a.Id));
        var meter = Assert.Single(stale.Meters);
        Assert.Equal(43, meter.UsedPercent);
        Assert.Equal(_reset, meter.ResetsAt);
        var point = Assert.Single(meter.History!);
        Assert.Equal(readAt, point.At);
        Assert.Contains(_log, line => line.StartsWith("usage: Anthropic stale, ", StringComparison.Ordinal));
        Assert.DoesNotContain(_log, line => line.StartsWith("usage: Anthropic unavailable", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFailureWithAnEightDayOldReadingIsUnavailable()
    {
        var store = NewStore();
        store.RecordOk(Ok("Anthropic", 43, at: DateTimeOffset.UtcNow.AddDays(-8)));
        var service = NewService(store);

        await service.RefreshAsync(false, Options, CancellationToken.None);

        var usage = Provider(service, "Anthropic");
        Assert.Equal("unavailable", usage.State);
        Assert.Empty(usage.Meters);
        Assert.Contains(_log, line => line.StartsWith("usage: Anthropic unavailable", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFailurePlanWinsOverTheStoredOne()
    {
        var store = NewStore();
        store.RecordOk(Ok("Anthropic", 43, plan: "Claude Pro", at: DateTimeOffset.UtcNow.AddHours(-1)));
        var anthropic = new FakeReader(() => new ReaderOutcome(Failed("Anthropic", "Claude Code token expired", plan: "Claude Max 20x")));
        var service = NewService(store, anthropic);

        await service.RefreshAsync(false, Options, CancellationToken.None);

        Assert.Equal("Claude Max 20x", Provider(service, "Anthropic").Plan);
    }

    // test 17
    [Fact]
    public async Task TwoConcurrentCallersShareOneRead()
    {
        var store = NewStore();
        var gate = new TaskCompletionSource();
        var anthropic = new FakeReader(async () =>
        {
            await gate.Task;
            return new ReaderOutcome(Ok("Anthropic", 10));
        });
        var service = NewService(store, anthropic);

        var first = service.RefreshAsync(false, Options, CancellationToken.None);
        // fresh skips the cache, not a read already in flight
        var second = service.RefreshAsync(true, Options, CancellationToken.None);
        gate.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(1, anthropic.Calls);
        Assert.Single(Provider(service, "Anthropic").Meters[0].History!);
    }

    // the store is sealed and swallows its own io failures, so a null provider is what makes RecordOk
    // throw here (it keys a dictionary on it). the refresh completes with the reader's own reading
    [Fact]
    public async Task AStoreFaultIsLoggedAndTheReadingStillPosts()
    {
        var store = NewStore();
        var anthropic = new FakeReader(() => new ReaderOutcome(Ok("Anthropic", 10) with { Provider = null! }));
        var service = NewService(store, anthropic);

        await service.RefreshAsync(true, Options, CancellationToken.None);

        var usage = Provider(service, "Anthropic");
        Assert.Equal("ok", usage.State);
        Assert.Equal(10, usage.Meters[0].UsedPercent);
        Assert.NotNull(usage.Meters[0].History);
        Assert.Contains(_log, line => line.StartsWith("usage: Anthropic store failed: ", StringComparison.Ordinal));
        Assert.False(File.Exists(StorePath));
    }

    // a reading with no meters list can't be posted either: unavailable, and the next refresh still reads
    [Fact]
    public async Task AnUnusableReadingIsUnavailableAndTheNextRefreshReads()
    {
        var store = NewStore();
        var calls = 0;
        var anthropic = new FakeReader(() => Interlocked.Increment(ref calls) == 1
            ? new ReaderOutcome(Ok("Anthropic", 10) with { Meters = null! })
            : new ReaderOutcome(Ok("Anthropic", 11)));
        var service = NewService(store, anthropic);

        await service.RefreshAsync(true, Options, CancellationToken.None);

        var first = Provider(service, "Anthropic");
        Assert.Equal("unavailable", first.State);
        Assert.StartsWith("usage reading unusable: ", first.Message);
        Assert.Empty(first.Meters);

        await service.RefreshAsync(true, Options, CancellationToken.None);

        Assert.Equal(2, anthropic.Calls);
        Assert.Equal("ok", Provider(service, "Anthropic").State);
        Assert.Equal(11, Provider(service, "Anthropic").Meters[0].UsedPercent);
    }

    // a cancelled read ends the refresh quietly and must not leave the slot pending
    [Fact]
    public async Task ACancelledReadDoesNotLeaveTheSlotPending()
    {
        var store = NewStore();
        var calls = 0;
        var anthropic = new FakeReader(() => Interlocked.Increment(ref calls) == 1
            ? throw new OperationCanceledException()
            : new ReaderOutcome(Ok("Anthropic", 11)));
        var service = NewService(store, anthropic);

        await service.RefreshAsync(true, Options, CancellationToken.None);
        await service.RefreshAsync(true, Options, CancellationToken.None);

        Assert.Equal(2, anthropic.Calls);
        Assert.Equal("ok", Provider(service, "Anthropic").State);
    }

    // test 18
    [Fact]
    public async Task FreshSkipsASoftCooldownButNotAHardOne()
    {
        var store = NewStore();
        var soft = new FakeReader(() => new ReaderOutcome(Failed("Google", "Antigravity CLI timed out after 25 s"), TimeSpan.FromMinutes(10)));
        var hard = new FakeReader(() => new ReaderOutcome(
            Failed("Anthropic", "rate limited by the usage endpoint; next try in 600 s"), TimeSpan.FromMinutes(10), "HTTP 429", CooldownKind: CooldownKind.Hard));
        // no cooldown, so it reads again once the 60 s cache is past
        var plain = FakeReader.Failing("OpenAI");
        var now = DateTimeOffset.UtcNow;
        var service = NewService(store, anthropic: hard, openai: plain, google: soft, clock: () => now);

        await service.RefreshAsync(false, Options, CancellationToken.None);
        Assert.Equal(1, soft.Calls);
        Assert.Equal(1, hard.Calls);

        // past the cache, inside both cooldowns: only the cooldowns hold the readers back
        now = now.AddMinutes(2);
        await service.RefreshAsync(false, Options, CancellationToken.None);
        Assert.Equal(2, plain.Calls);
        Assert.Equal(1, soft.Calls);
        Assert.Equal(1, hard.Calls);

        await service.RefreshAsync(true, Options, CancellationToken.None);
        Assert.Equal(2, soft.Calls);
        Assert.Equal(1, hard.Calls);
        Assert.Contains(_log, line => line == "usage: Anthropic cooling down 600 s after HTTP 429");
    }

    // test 19
    [Fact]
    public async Task AnOkReadingWithANullPlanKeepsTheStoredPlan()
    {
        var store = NewStore();
        store.RecordOk(Ok("Anthropic", 40, at: DateTimeOffset.UtcNow.AddMinutes(-10)));
        var anthropic = new FakeReader(() => new ReaderOutcome(Ok("Anthropic", 41, plan: null)));
        var service = NewService(store, anthropic);

        await service.RefreshAsync(false, Options, CancellationToken.None);

        var usage = Provider(service, "Anthropic");
        Assert.Equal("ok", usage.State);
        Assert.Equal("Claude Max 20x", usage.Plan);
        // and the store keeps it for the next null
        Assert.Equal("Claude Max 20x", store.LastOk("Anthropic", TimeSpan.FromDays(7), DateTimeOffset.UtcNow)!.Plan);
    }

    // test 20
    [Fact]
    public void StartupSeedsStoredReadingsAsStaleInProviderOrder()
    {
        var store = NewStore();
        var googleAt = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.AddHours(-2).ToUnixTimeSeconds());
        store.RecordOk(Ok("Google", 5, plan: "Antigravity CLI", at: googleAt));
        store.RecordOk(Ok("Anthropic", 43, at: DateTimeOffset.UtcNow.AddHours(-9)));
        store.RecordOk(Ok("OpenAI", 6, plan: "prolite", at: DateTimeOffset.UtcNow.AddDays(-8)));

        var service = NewService(store);

        Assert.NotNull(service.Last);
        // OpenAI's reading is past the 7 days
        Assert.Equal(["Anthropic", "Google"], service.Last.Providers.Select(p => p.Provider));
        Assert.All(service.Last.Providers, provider =>
        {
            Assert.Equal("stale", provider.State);
            Assert.Equal("not read yet since the app started", provider.Message);
            Assert.Null(provider.Detail);
            Assert.Empty(provider.Actions);
            Assert.NotNull(provider.Meters[0].History);
        });
        // dated by the newest reading, not by the clock
        Assert.Equal(googleAt, service.Last.UpdatedAt);
    }

    [Fact]
    public void AnEmptyStoreSeedsNothing()
    {
        Assert.Null(NewService(NewStore()).Last);
    }

    // every posted provider has a list, ok and unavailable alike
    [Fact]
    public async Task HistoryIsNeverNullOnAPost()
    {
        var store = NewStore();
        var service = NewService(store, new FakeReader(() => new ReaderOutcome(Ok("Anthropic", 10))));
        UsageSnapshot? posted = null;
        service.Updated += snapshot => posted = snapshot;

        await service.RefreshAsync(false, Options, CancellationToken.None);

        Assert.NotNull(posted);
        Assert.All(posted.Providers.SelectMany(p => p.Meters), meter => Assert.NotNull(meter.History));
        Assert.Equal(["Anthropic", "OpenAI", "Google"], posted.Providers.Select(p => p.Provider));
    }

    private sealed class FakeReader(Func<Task<ReaderOutcome>> read)
    {
        private int _calls;

        public FakeReader(Func<ReaderOutcome> outcome)
            : this(() => Task.FromResult(outcome()))
        {
        }

        public int Calls => Volatile.Read(ref _calls);

        public static FakeReader Failing(string provider)
            => new(() => new ReaderOutcome(ProviderUsage.Unavailable(provider, null, "not configured in this test", DateTimeOffset.UtcNow)));

        public Task<ReaderOutcome> ReadAsync(UsageOptions options, Action<string> log, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return read();
        }
    }
}
