using System.Text;
using System.Text.Json.Nodes;

namespace InOutButton.Tests;

// spec tests 7 to 10 (docs/2026-10-18-burn-pace-gemini-token-spec.md, section 9).
// expected values are written out by hand, never read back from the store
public sealed class UsageStoreTests : IDisposable
{
    private const string Provider = "Anthropic";
    private static readonly DateTimeOffset T0 = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
    // the weekly reset that closes T0's window
    private static readonly DateTimeOffset Reset = new(2026, 10, 25, 0, 0, 0, TimeSpan.Zero);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "InOutButton.Tests", Guid.NewGuid().ToString("N"));

    private string StorePath => Path.Combine(_folder, "usage-store.json");
    private string BakPath => Path.Combine(_folder, "usage-store.bak.json");

    public void Dispose()
    {
        if (!Directory.Exists(_folder))
        {
            return;
        }

        // the read-only test leaves a read-only file behind
        foreach (var file in Directory.EnumerateFiles(_folder, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_folder, recursive: true);
    }

    private static UsageMeter Meter(double used, DateTimeOffset resetsAt, string key = "weekly")
        => new(key, "Weekly, all models", 10080, used, resetsAt, "normal", null);

    private static ProviderUsage Reading(DateTimeOffset at, params UsageMeter[] meters)
        => Reading(Provider, at, meters);

    private static ProviderUsage Reading(string provider, DateTimeOffset at, params UsageMeter[] meters)
        => new(provider, "Claude Max 20x", "ok", null, null, meters, "Extra usage off", [new ProviderAction("open", "Open")], at);

    private UsageStore NewStore(List<string>? log = null)
    {
        var store = new UsageStore(StorePath);
        if (log is not null)
        {
            store.Log = log.Add;
        }

        store.Load();
        return store;
    }

    private static IReadOnlyList<UsagePoint> HistoryOf(UsageStore store, DateTimeOffset resetsAt, string key = "weekly")
        => store.WithHistory(Reading(T0, Meter(0, resetsAt, key))).Meters[0].History!;

    // test 7
    [Fact]
    public void RecordedReadingsSurviveAReloadInOrder()
    {
        var store = NewStore();
        store.RecordOk(Reading(T0, Meter(10.0, Reset)));
        store.RecordOk(Reading(T0.AddHours(1), Meter(12.5, Reset)));

        var reloaded = NewStore();

        var last = reloaded.LastOk(Provider, TimeSpan.FromDays(7), T0.AddHours(2));
        Assert.NotNull(last);
        Assert.Equal(T0.AddHours(1), last.UpdatedAt);
        Assert.Equal("ok", last.State);
        Assert.Equal("Claude Max 20x", last.Plan);
        Assert.Equal("Extra usage off", last.Footnote);
        Assert.Equal([new ProviderAction("open", "Open")], last.Actions);
        var meter = Assert.Single(last.Meters);
        Assert.Equal(12.5, meter.UsedPercent);
        Assert.Equal(Reset, meter.ResetsAt);
        Assert.Null(meter.History);

        var filled = reloaded.WithHistory(last);
        Assert.Equal([new UsagePoint(T0, 10.0), new UsagePoint(T0.AddHours(1), 12.5)], filled.Meters[0].History!);
    }

    // section 4's layout: other tools may read this file
    [Fact]
    public void TheFileHoldsVersionLastAndTriples()
    {
        var store = NewStore();
        store.RecordOk(Reading(T0, Meter(43.0, Reset)));

        var root = JsonNode.Parse(File.ReadAllText(StorePath))!;
        Assert.Equal(1, root["version"]!.GetValue<int>());
        Assert.Equal("ok", root["last"]![Provider]!["state"]!.GetValue<string>());
        Assert.Null(root["last"]![Provider]!["meters"]![0]!["history"]);

        var point = root["history"]!["Anthropic/weekly"]![0]!.AsArray();
        Assert.Equal(3, point.Count);
        Assert.Equal(1_792_324_800_000L, point[0]!.GetValue<long>());   // 2026-10-18T12:00:00Z
        Assert.Equal(43.0, point[1]!.GetValue<double>());
        Assert.Equal(1_792_886_400_000L, point[2]!.GetValue<long>());   // 2026-10-25T00:00:00Z
        Assert.False(File.Exists(StorePath + ".tmp"));
    }

    // a meter never recorded gets an empty list, never null
    [Fact]
    public void AMeterWithNoPointsGetsAnEmptyHistory()
    {
        var store = NewStore();
        store.RecordOk(Reading(T0, Meter(10.0, Reset)));

        var filled = store.WithHistory(Reading(T0, Meter(10.0, Reset), Meter(3.0, T0.AddHours(5), "session")));

        Assert.Single(filled.Meters[0].History!);
        Assert.NotNull(filled.Meters[1].History);
        Assert.Empty(filled.Meters[1].History!);
    }

    // test 7, window identity
    [Fact]
    public void HistoryKeepsOnlyPointsFromTheMetersWindow()
    {
        var next = Reset.AddDays(7);
        var store = NewStore();
        store.RecordOk(Reading(Reset.AddSeconds(-1), Meter(40.0, Reset)));                     // old window, a second before its reset
        store.RecordOk(Reading(Reset.AddHours(1), Meter(1.0, next)));
        store.RecordOk(Reading(Reset.AddHours(2), Meter(2.0, next.AddSeconds(1))));            // one second off is another window (G4)
        store.RecordOk(Reading(Reset.AddHours(3), Meter(3.0, next)));                          // exact is this one
        store.RecordOk(Reading(Reset.AddHours(4), Meter(4.0, next.AddMilliseconds(400))));     // rounds to the same second

        Assert.Equal(
            [new UsagePoint(Reset.AddHours(1), 1.0), new UsagePoint(Reset.AddHours(3), 3.0), new UsagePoint(Reset.AddHours(4), 4.0)],
            HistoryOf(store, next));
        Assert.Equal([new UsagePoint(Reset.AddHours(2), 2.0)], HistoryOf(store, next.AddSeconds(1)));
        Assert.Equal([new UsagePoint(Reset.AddSeconds(-1), 40.0)], HistoryOf(store, Reset));
    }

    // test 8
    [Fact]
    public void PointsOlderThanEightDaysDrop()
    {
        var identity = T0.AddDays(30);
        var store = NewStore();
        store.RecordOk(Reading(T0, Meter(1.0, identity), Meter(9.0, identity, "weekly_fable")));
        store.RecordOk(Reading(T0.AddDays(1), Meter(2.0, identity)));
        store.RecordOk(Reading(T0.AddDays(8), Meter(3.0, identity)));

        // exactly eight days old is not older than eight days
        Assert.Equal(3, HistoryOf(store, identity).Count);

        store.RecordOk(Reading(T0.AddDays(8).AddMinutes(1), Meter(4.0, identity)));

        Assert.Equal(
            [new UsagePoint(T0.AddDays(1), 2.0), new UsagePoint(T0.AddDays(8), 3.0), new UsagePoint(T0.AddDays(8).AddMinutes(1), 4.0)],
            HistoryOf(store, identity));
        // a meter the provider stopped reporting ages out too
        Assert.Empty(HistoryOf(store, identity, "weekly_fable"));
    }

    // test 8, the cap; seeded through the file so the loop doesn't save 3,000 times
    [Fact]
    public void ASeriesKeepsItsNewest3000Points()
    {
        var file = new StringBuilder("{\"version\":1,\"last\":{},\"history\":{\"Anthropic/weekly\":[");
        var resetMs = Reset.ToUnixTimeMilliseconds();
        for (var i = 0; i < 3000; i++)
        {
            file.Append(i == 0 ? "" : ",").Append($"[{T0.AddMinutes(i).ToUnixTimeMilliseconds()},{i % 100},{resetMs}]");
        }

        Directory.CreateDirectory(_folder);
        File.WriteAllText(StorePath, file.Append("]}}").ToString());

        var store = NewStore();
        Assert.Equal(3000, HistoryOf(store, Reset).Count);

        store.RecordOk(Reading(T0.AddMinutes(3000), Meter(55.0, Reset)));

        var history = HistoryOf(store, Reset);
        Assert.Equal(3000, history.Count);
        Assert.Equal(new UsagePoint(T0.AddMinutes(1), 1.0), history[0]);                // minute 0 went
        Assert.Equal(new UsagePoint(T0.AddMinutes(3000), 55.0), history[^1]);
        Assert.Equal(3000, HistoryOf(NewStore(), Reset).Count);
    }

    // test 9
    [Fact]
    public void AMalformedFileLoadsEmptyAndMovesToBak()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(StorePath, "{\"version\":1,\"last\":{");
        var log = new List<string>();

        var store = NewStore(log);

        Assert.Null(store.LastOk(Provider, TimeSpan.FromDays(7), T0));
        Assert.Empty(HistoryOf(store, Reset));
        Assert.False(File.Exists(StorePath));
        Assert.Equal("{\"version\":1,\"last\":{", File.ReadAllText(BakPath));
        Assert.Single(log);

        // and the store works from empty
        store.RecordOk(Reading(T0, Meter(5.0, Reset)));
        Assert.Single(HistoryOf(NewStore(), Reset));
    }

    // test 9; a newer build's file must not be destroyed by this one
    [Fact]
    public void AnUnknownVersionLoadsEmptyAndReplacesAnOlderBak()
    {
        const string newer = "{\"version\":2,\"last\":{},\"history\":{\"Anthropic/weekly\":[[1792324800000,43,1792886400000]]}}";
        Directory.CreateDirectory(_folder);
        File.WriteAllText(BakPath, "older bak");
        File.WriteAllText(StorePath, newer);

        var store = NewStore();

        Assert.Empty(HistoryOf(store, Reset));
        Assert.False(File.Exists(StorePath));
        Assert.Equal(newer, File.ReadAllText(BakPath));
    }

    // a structurally bad entry is a malformed file, not a crash later in the page
    [Theory]
    [InlineData("{\"version\":1,\"history\":{\"Anthropic/weekly\":[[1792324800000,43]]}}")]
    [InlineData("{\"version\":1,\"history\":{\"Anthropic/weekly\":[[-5,43,1792886400000]]}}")]
    [InlineData("{\"version\":1,\"history\":{\"Anthropic/weekly\":null}}")]
    [InlineData("{\"version\":1,\"last\":{\"Anthropic\":{\"provider\":\"Anthropic\",\"state\":\"ok\",\"actions\":[],\"updatedAt\":\"2026-10-18T12:00:00+00:00\"}}}")]
    [InlineData("{\"last\":{},\"history\":{}}")]
    [InlineData("null")]
    public void AMalformedEntryLoadsEmpty(string text)
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(StorePath, text);

        var store = NewStore();

        Assert.Null(store.LastOk(Provider, TimeSpan.FromDays(7), T0));
        Assert.Empty(HistoryOf(store, Reset));
        Assert.True(File.Exists(BakPath));
    }

    // a lock is not a parse failure: the file is never moved aside or overwritten, even once it frees
    [Fact]
    public void ALockedFileIsNeitherMovedNorOverwritten()
    {
        Directory.CreateDirectory(_folder);
        const string original = "{\"version\":1,\"last\":{},\"history\":{\"Anthropic/weekly\":[[1792324800000,43,1792886400000]]}}";
        File.WriteAllText(StorePath, original);
        var before = File.ReadAllBytes(StorePath);
        var log = new List<string>();
        var store = new UsageStore(StorePath) { Log = log.Add };

        using (new FileStream(StorePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            store.Load();
        }

        // released before the save, so only the store's own rule keeps the file
        store.RecordOk(Reading(T0, Meter(10.0, Reset)));

        Assert.Equal(before, File.ReadAllBytes(StorePath));
        Assert.False(File.Exists(BakPath));
        Assert.False(File.Exists(StorePath + ".tmp"));
        var line = Assert.Single(log);
        Assert.StartsWith($"usage store: could not read {StorePath} (", line, StringComparison.Ordinal);
        Assert.EndsWith("); not saving this run", line, StringComparison.Ordinal);
        // the in-memory store keeps going
        Assert.Single(HistoryOf(store, Reset));
    }

    [Fact]
    public void AMissingFileIsAnEmptyStoreWithNoBak()
    {
        var store = NewStore();

        Assert.Null(store.LastOk(Provider, TimeSpan.FromDays(7), T0));
        Assert.False(File.Exists(BakPath));
    }

    [Fact]
    public void LastOkHonoursMaxAge()
    {
        var now = T0.AddDays(10);
        var store = NewStore();
        store.RecordOk(Reading(Provider, now.AddDays(-8), Meter(10.0, Reset)));
        store.RecordOk(Reading("OpenAI", now.AddHours(-1), Meter(20.0, Reset)));

        Assert.Null(store.LastOk(Provider, TimeSpan.FromDays(7), now));
        Assert.Equal(now.AddHours(-1), store.LastOk("OpenAI", TimeSpan.FromDays(7), now)?.UpdatedAt);
        Assert.Null(store.LastOk("Google", TimeSpan.FromDays(7), now));
    }

    // spec 3.1 keeps a reading "from the last 7 days" and drops one "older than that", so exactly
    // 7 days is in. spec 8's "under 7 days" reads the other way at this one instant
    [Fact]
    public void LastOkKeepsAReadingExactlySevenDaysOldAndDropsOneATickOlder()
    {
        Assert.Equal(TimeSpan.FromDays(7), UsageService.StaleWindow);
        var store = NewStore();
        store.RecordOk(Reading(T0, Meter(10.0, Reset)));

        Assert.Equal(T0, store.LastOk(Provider, UsageService.StaleWindow, T0.AddDays(7))?.UpdatedAt);
        Assert.Null(store.LastOk(Provider, UsageService.StaleWindow, T0.AddDays(7).AddTicks(1)));
    }

    // test 10 at the store: a cache hit hands back the same UpdatedAt
    [Fact]
    public void TheSameReadingTwiceIsOneObservation()
    {
        var store = NewStore();
        var reading = Reading(T0, Meter(10.0, Reset));
        store.RecordOk(reading);
        store.RecordOk(reading);

        Assert.Single(HistoryOf(store, Reset));

        // the same value at a new time is a real observation that the meter did not move
        store.RecordOk(Reading(T0.AddMinutes(5), Meter(10.0, Reset)));
        Assert.Equal([new UsagePoint(T0, 10.0), new UsagePoint(T0.AddMinutes(5), 10.0)], HistoryOf(store, Reset));
    }

    [Fact]
    public void AnUnavailableReadingIsNotRecorded()
    {
        var store = NewStore();
        store.RecordOk(ProviderUsage.Unavailable(Provider, null, "down", T0));

        Assert.Null(store.LastOk(Provider, TimeSpan.FromDays(7), T0));
        Assert.False(File.Exists(StorePath));
    }

    // a folder that can't be created: a file stands where it would go
    [Fact]
    public void ASaveThatCannotWriteIsLoggedOnceAndSwallowed()
    {
        Directory.CreateDirectory(_folder);
        var blocker = Path.Combine(_folder, "blocker");
        File.WriteAllText(blocker, "");
        var log = new List<string>();
        var store = new UsageStore(Path.Combine(blocker, "sub", "usage-store.json")) { Log = log.Add };
        store.Load();

        store.RecordOk(Reading(T0, Meter(10.0, Reset)));
        store.RecordOk(Reading(T0.AddMinutes(5), Meter(11.0, Reset)));

        Assert.Single(log);
        // the in-memory store keeps going
        Assert.Equal(2, store.WithHistory(Reading(T0, Meter(0, Reset))).Meters[0].History!.Count);
    }

    [Fact]
    public void ASaveOverAReadOnlyFileLeavesItAndNoTemp()
    {
        var store = NewStore();
        store.RecordOk(Reading(T0, Meter(10.0, Reset)));
        var before = File.ReadAllText(StorePath);
        File.SetAttributes(StorePath, FileAttributes.ReadOnly);

        store.RecordOk(Reading(T0.AddMinutes(5), Meter(11.0, Reset)));

        Assert.Equal(before, File.ReadAllText(StorePath));
        Assert.False(File.Exists(StorePath + ".tmp"));
        Assert.Equal(2, HistoryOf(store, Reset).Count);
    }
}
