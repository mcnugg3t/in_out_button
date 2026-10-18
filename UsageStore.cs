using System.Text.Json;
using System.Text.Json.Serialization;

namespace InOutButton;

/// <summary>
/// the last ok reading per provider and every meter's observed percents, kept across restarts
/// (docs/2026-10-18-burn-pace-gemini-token-spec.md, section 4). one lock around every call;
/// load and save never throw
/// </summary>
public sealed class UsageStore
{
    public const int Version = 1;

    // seven days at five minutes is 2,016
    private const int MaxPoints = 3000;
    private static readonly long RetentionMs = (long)TimeSpan.FromDays(8).TotalMilliseconds;
    // the last whole second a DateTimeOffset holds, so a rounded reset still reads back
    private const long MaxSecondMs = 253_402_300_799_000;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly IComparer<Point> ByTime = Comparer<Point>.Create((a, b) => a.AtMs.CompareTo(b.AtMs));
    private static readonly TimeSpan ReadRetryDelay = TimeSpan.FromMilliseconds(500);

    private readonly object _gate = new();
    private readonly string _path;
    private readonly Dictionary<string, ProviderUsage> _last = new(StringComparer.Ordinal);
    // key is "<provider>/<meter key>"; each series oldest first
    private readonly Dictionary<string, List<Point>> _history = new(StringComparer.Ordinal);
    private bool _saveFailureLogged;
    // a file we could not read or move aside is never overwritten - it may be a newer build's, or whole
    private bool _saveBlocked;

    public UsageStore(string path)
    {
        _path = path;
    }

    public static string DefaultPath { get; } = Path.Combine(SettingsStore.SettingsDirectory, "usage-store.json");

    // runs under the store lock, maybe on a pool thread - hand it a queue, not the form's AppendLog
    public Action<string>? Log { get; set; }

    private string BakPath => Path.ChangeExtension(_path, ".bak.json");

    /// <summary>
    /// once at startup. a missing file is an empty store; one that does not parse or carries
    /// an unknown version moves to usage-store.bak.json and the store starts empty. one it can't
    /// read stays put, and nothing saves this run
    /// </summary>
    public void Load()
    {
        lock (_gate)
        {
            _last.Clear();
            _history.Clear();
            _saveBlocked = false;

            string text;
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    if (!File.Exists(_path))
                    {
                        return;
                    }

                    text = File.ReadAllText(_path);
                    break;
                }
                catch (Exception ex) when (attempt == 1 && ex is IOException or UnauthorizedAccessException)
                {
                    // a lock is often brief: one more try
                    Thread.Sleep(ReadRetryDelay);
                }
                catch (Exception ex)
                {
                    // a lock is not a parse failure: the file may be whole, so the next save must not replace it
                    _saveBlocked = true;
                    Log?.Invoke($"usage store: could not read {_path} ({Scrub.Message(ex.Message)}); not saving this run");
                    return;
                }
            }

            StoreFile? file;
            try
            {
                file = JsonSerializer.Deserialize<StoreFile>(text, Json);
            }
            catch (Exception)
            {
                file = null;
            }

            var problem = file is null ? "does not parse"
                : file.Version != Version ? $"has version {file.Version?.ToString() ?? "none"}"
                : !IsSound(file) ? "has a malformed entry"
                : null;
            if (problem is not null)
            {
                SetAside(problem);
                return;
            }

            foreach (var (provider, usage) in file!.Last ?? [])
            {
                _last[provider] = Strip(usage);
            }

            foreach (var (key, series) in file.History ?? [])
            {
                series.Sort(ByTime);
                _history[key] = series;
            }
        }
    }

    /// <summary>
    /// the stored ok reading with History null, or null when there is none or it is older than maxAge
    /// </summary>
    public ProviderUsage? LastOk(string provider, TimeSpan maxAge, DateTimeOffset now)
    {
        lock (_gate)
        {
            return _last.TryGetValue(provider, out var stored)
                && stored.State == UsageState.Ok
                && stored.UpdatedAt is { } at
                && now - at <= maxAge
                ? stored
                : null;
        }
    }

    /// <summary>
    /// keeps an ok reading as the provider's last and adds a point per meter at its UpdatedAt,
    /// then prunes, caps, and saves. a reading already stored at that time adds nothing
    /// </summary>
    public void RecordOk(ProviderUsage reading)
    {
        // json can't hold NaN; the readers already drop non-finite values
        if (reading.State != UsageState.Ok
            || reading.UpdatedAt is not { } at
            || reading.Meters.Any(meter => !double.IsFinite(meter.UsedPercent)))
        {
            return;
        }

        lock (_gate)
        {
            var atMs = at.ToUnixTimeMilliseconds();
            var changed = false;

            // an older reading arriving late keeps the newer last
            if (!_last.TryGetValue(reading.Provider, out var stored) || stored.UpdatedAt < at)
            {
                _last[reading.Provider] = Strip(reading);
                changed = true;
            }

            foreach (var meter in reading.Meters)
            {
                var key = $"{reading.Provider}/{meter.Key}";
                if (!_history.TryGetValue(key, out var series))
                {
                    _history[key] = series = [];
                }

                // the reset time is the window identity: a point taken a second before the reset
                // carries the old window's reset and stays out of the new one
                var point = new Point(atMs, meter.UsedPercent, WholeSecondMs(meter.ResetsAt));
                var index = series.BinarySearch(point, ByTime);
                // same instant is a cache hit, not a new observation. same value at a new time is kept
                if (index < 0)
                {
                    series.Insert(~index, point);
                    changed = true;
                }
            }

            // every series, so a meter a provider stops reporting ages out too
            var cutoff = atMs - RetentionMs;
            foreach (var key in _history.Keys.ToList())
            {
                var series = _history[key];
                changed |= series.RemoveAll(point => point.AtMs < cutoff) > 0;
                if (series.Count > MaxPoints)
                {
                    series.RemoveRange(0, series.Count - MaxPoints);
                    changed = true;
                }

                if (series.Count == 0)
                {
                    _history.Remove(key);
                }
            }

            if (changed)
            {
                Save();
            }
        }
    }

    /// <summary>
    /// a copy whose meters carry the stored points from their current window, oldest first;
    /// an empty list when there are none, never null
    /// </summary>
    public ProviderUsage WithHistory(ProviderUsage usage)
    {
        lock (_gate)
        {
            var meters = usage.Meters.Select(meter => meter with { History = PointsFor(usage.Provider, meter) }).ToList();
            return usage with { Meters = meters };
        }
    }

    private List<UsagePoint> PointsFor(string provider, UsageMeter meter)
    {
        if (!_history.TryGetValue($"{provider}/{meter.Key}", out var series))
        {
            return [];
        }

        // exact to the second, no tolerance: an idle agy bucket's reset moves with each read, and two
        // idle reads a minute apart must not merge (spec 4, G4). older files may hold unrounded ms
        var resetsAtMs = WholeSecondMs(meter.ResetsAt);
        return series
            .Where(point => WholeSecondMs(point.ResetsAtMs) == resetsAtMs)
            .Select(point => new UsagePoint(DateTimeOffset.FromUnixTimeMilliseconds(point.AtMs), point.UsedPercent))
            .ToList();
    }

    private static long WholeSecondMs(DateTimeOffset instant) => WholeSecondMs(instant.ToUnixTimeMilliseconds());

    private static long WholeSecondMs(long ms) => Math.Min((long)Math.Round(ms / 1000.0, MidpointRounding.AwayFromZero) * 1000, MaxSecondMs);

    // temp file beside the target, then a rename - a failed write leaves the old file whole
    private void Save()
    {
        if (_saveBlocked)
        {
            return;
        }

        var temp = _path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new StoreFile { Version = Version, Last = _last, History = _history }, Json);
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            try { File.Delete(temp); } catch { }

            // once per run; the in-memory store keeps going
            if (!_saveFailureLogged)
            {
                _saveFailureLogged = true;
                Log?.Invoke($"usage store: could not save {_path}: {Scrub.Message(ex.Message)}");
            }
        }
    }

    // overwrites an older .bak, so a newer build's file survives one downgrade, not two
    private void SetAside(string problem)
    {
        var name = Path.GetFileName(_path);
        try
        {
            File.Move(_path, BakPath, overwrite: true);
            Log?.Invoke($"usage store: {name} {problem}; moved to {Path.GetFileName(BakPath)}, starting empty");
        }
        catch (Exception ex)
        {
            _saveBlocked = true;
            Log?.Invoke($"usage store: {name} {problem} and could not be moved aside ({Scrub.Message(ex.Message)}); starting empty, not saving this run");
        }
    }

    private static ProviderUsage Strip(ProviderUsage usage)
        => usage with { Meters = usage.Meters.Select(meter => meter with { History = null }).ToList() };

    // the serializer leaves a missing member null whatever its declared type, so check what callers dereference
    private static bool IsSound(StoreFile file)
        => (file.Last is null || file.Last.All(pair => IsSoundReading(pair.Key, pair.Value)))
            && (file.History is null || file.History.Values.All(series => series is not null));

    private static bool IsSoundReading(string provider, ProviderUsage? usage)
        => usage is { Provider: not null, State: not null, Meters: not null, Actions: not null, UpdatedAt: not null }
            && usage.Provider == provider
            && usage.Meters.All(meter => meter is { Key: not null, Name: not null, Severity: not null })
            && usage.Actions.All(action => action is { Id: not null, Label: not null });

    private sealed class StoreFile
    {
        public int? Version { get; set; }

        public Dictionary<string, ProviderUsage>? Last { get; set; }

        public Dictionary<string, List<Point>>? History { get; set; }
    }

    [JsonConverter(typeof(PointConverter))]
    private readonly record struct Point(long AtMs, double UsedPercent, long ResetsAtMs);

    // [epochMs, usedPercent, resetsAtEpochMs]; anything else makes the file malformed
    private sealed class PointConverter : JsonConverter<Point>
    {
        // DateTimeOffset.MaxValue; FromUnixTimeMilliseconds throws past it
        private const long MaxMs = 253_402_300_799_999;

        public override Point Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartArray)
            {
                throw new JsonException("point is not an array");
            }

            var at = NextMs(ref reader);
            reader.Read();
            if (reader.TokenType != JsonTokenType.Number || !reader.TryGetDouble(out var used) || !double.IsFinite(used))
            {
                throw new JsonException("point percent is not a number");
            }

            var resetsAt = NextMs(ref reader);
            reader.Read();
            if (reader.TokenType != JsonTokenType.EndArray)
            {
                throw new JsonException("point has more than three values");
            }

            return new Point(at, used, resetsAt);
        }

        public override void Write(Utf8JsonWriter writer, Point value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(value.AtMs);
            writer.WriteNumberValue(value.UsedPercent);
            writer.WriteNumberValue(value.ResetsAtMs);
            writer.WriteEndArray();
        }

        private static long NextMs(ref Utf8JsonReader reader)
        {
            reader.Read();
            return reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var ms) && ms is >= 0 and <= MaxMs
                ? ms
                : throw new JsonException("point time is not epoch milliseconds");
        }
    }
}
