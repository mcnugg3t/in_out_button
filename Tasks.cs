using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace InOutButton;

public enum TaskBucket { Projects, Admin, Misc }

public enum TaskProgress { NotStarted, InProgress, Complete }

public enum TaskPriority { Normal, Important, Urgent }

public sealed class ChecklistItem
{
    public string Text { get; set; } = "";
    public bool Done { get; set; }
}

/// <summary>
/// one task card. TaskStore owns Created, Updated and Completed and ignores what the page sends;
/// Rev comes back from the page and must match the file
/// </summary>
public sealed class TaskItem
{
    public string Id { get; set; } = "";
    public int Rev { get; set; }
    public string Title { get; set; } = "";
    public TaskBucket Bucket { get; set; }
    public TaskProgress Progress { get; set; }
    public DateOnly? Due { get; set; }          // local date, no time
    public TaskPriority Priority { get; set; }
    public string? Repo { get; set; }           // a scanned repo name or free text
    public List<ChecklistItem> Checklist { get; set; } = [];
    public string? Notes { get; set; }
    public DateTimeOffset Created { get; set; }
    public DateTimeOffset Updated { get; set; }
    public DateTimeOffset? Completed { get; set; }

    // fields this app doesn't know, kept from the file; the page's copy is never used
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class TaskDocument
{
    public int Version { get; set; } = 1;
    public List<TaskItem> Tasks { get; set; } = [];

    // top-level fields this app doesn't know, written back unchanged
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>
/// Rejected: a stale edit refused, with the fixed message. Ok false otherwise: an error, raw
/// exception text in Message for the caller to scrub. Document is the file after the call - empty if unreadable, never save that
/// </summary>
public sealed record TaskStoreResult(bool Ok, bool Rejected, string? Message, TaskDocument Document);

/// <summary>
/// reads and writes the tasks json file. synchronous - Form1 calls it off the ui thread, one call at a time.
/// every write reloads the file first and checks the task's rev, so a stale page can't overwrite newer work
/// </summary>
public sealed class TaskStore
{
    // spec 2.4
    private const string StaleMessage = "This task changed on disk. Your edit was not saved; reopen it to see the current version.";

    private const int LockWaitMilliseconds = 3000;
    private const int LockRetryMilliseconds = 50;

    private readonly object _gate = new();
    private FileStamp? _seen;

    public TaskStore(string path)
    {
        Path = path;

        // one lock per file, whatever case the path is typed in
        var full = System.IO.Path.GetFullPath(path).ToLowerInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full)), 0, 8).ToLowerInvariant();
        LockPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"InOutButton-{hash}.lock");
    }

    public string Path { get; }

    /// <summary>
    /// held for each whole read-check-write, so a second app instance can't pass the same rev check
    /// </summary>
    public string LockPath { get; }

    // shared with Form1 so the page gets the same shape as the file
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Converters =
        {
            // no integers, so a hand-typed "bucket": 5 is an error, not a nameless bucket
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false),
            new UtcInstantConverter(),
        },
    };

    /// <summary>
    /// reads and validates the file; a missing file is an empty document. on failure the document is
    /// empty and must not be saved - a save would overwrite the file nobody could read
    /// </summary>
    public TaskStoreResult Load()
    {
        return Exclusive(Read);
    }

    /// <summary>
    /// adds a task (Rev 0, id unknown) or replaces one whose Rev matches the file; anything else is
    /// rejected and the file is untouched
    /// </summary>
    public TaskStoreResult Upsert(TaskItem incoming, DateTimeOffset now)
    {
        return Exclusive(() =>
        {
            // always reload - the file is small and its last-write time doesn't catch every change
            var loaded = Read();
            if (!loaded.Ok)
            {
                return loaded;
            }

            var onDisk = string.IsNullOrEmpty(incoming.Id) ? null : loaded.Document.Tasks.Find(task => task.Id == incoming.Id);
            var stored = CopyEditable(incoming);
            var tasks = new List<TaskItem>(loaded.Document.Tasks);

            if (onDisk is null && incoming.Rev <= 0)
            {
                stored.Id = string.IsNullOrEmpty(incoming.Id) ? Guid.NewGuid().ToString("N")[..16] : incoming.Id;
                stored.Rev = 1;
                stored.Created = now;
                stored.Completed = stored.Progress == TaskProgress.Complete ? now : null;
                stored.Extra = null;
                tasks.Add(stored);
            }
            else if (onDisk is not null && incoming.Rev == onDisk.Rev)
            {
                stored.Id = onDisk.Id;
                stored.Rev = onDisk.Rev + 1;
                stored.Created = onDisk.Created;
                stored.Extra = onDisk.Extra;
                stored.Completed = stored.Progress != TaskProgress.Complete ? null
                    : onDisk.Progress == TaskProgress.Complete ? onDisk.Completed ?? now
                    : now;
                tasks[tasks.IndexOf(onDisk)] = stored;
            }
            else
            {
                // rev > 0 with an unknown id was deleted on disk - refusing keeps it deleted
                return new TaskStoreResult(false, true, StaleMessage, loaded.Document);
            }

            stored.Updated = now;
            return Write(new TaskDocument { Tasks = tasks, Extra = loaded.Document.Extra }, loaded.Document);
        });
    }

    /// <summary>
    /// removes a task whose Rev matches the file. a stale rev or an id already gone is rejected
    /// like a stale edit, and the page gets fresh state
    /// </summary>
    public TaskStoreResult Delete(string id, int rev)
    {
        return Exclusive(() =>
        {
            var loaded = Read();
            if (!loaded.Ok)
            {
                return loaded;
            }

            var onDisk = loaded.Document.Tasks.Find(task => task.Id == id);
            if (onDisk is null || onDisk.Rev != rev)
            {
                return new TaskStoreResult(false, true, StaleMessage, loaded.Document);
            }

            var tasks = loaded.Document.Tasks.Where(task => !ReferenceEquals(task, onDisk)).ToList();
            return Write(new TaskDocument { Tasks = tasks, Extra = loaded.Document.Extra }, loaded.Document);
        });
    }

    /// <summary>
    /// last-write time or length differs from the last load or write. a hint for when to reload;
    /// writes never rely on it
    /// </summary>
    public bool ChangedSinceLoad()
    {
        lock (_gate)
        {
            return _seen is null || Stamp() != _seen;
        }
    }

    // _gate for this instance, the lock file for other processes
    private TaskStoreResult Exclusive(Func<TaskStoreResult> operation)
    {
        lock (_gate)
        {
            var held = OpenLockFile();
            if (held is null)
            {
                return Failure($"{FileName} is in use by another process; try again in a moment.");
            }

            try
            {
                return operation();
            }
            finally
            {
                held.Dispose();
            }
        }
    }

    // null after LockWaitMilliseconds of sharing violations
    private FileStream? OpenLockFile()
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (clock.ElapsedMilliseconds >= LockWaitMilliseconds)
                {
                    return null;
                }

                Thread.Sleep(LockRetryMilliseconds);
            }
        }
    }

    private TaskStoreResult Read()
    {
        // stamp before reading: a change in between shows as changed rather than as seen
        _seen = Stamp();
        string text;
        try
        {
            text = File.ReadAllText(Path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new TaskStoreResult(true, false, null, new TaskDocument());
        }
        catch (Exception ex)
        {
            _seen = null;
            return Failure($"Could not read {FileName}: {ex.Message}");
        }

        return Parse(text);
    }

    private TaskStoreResult Parse(string text)
    {
        TaskDocument? document;
        try
        {
            using var json = JsonDocument.Parse(text);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Failure($"{FileName} is not a JSON object.");
            }

            // required, so some other json object can't load as an empty board
            int? version = null;
            foreach (var property in root.EnumerateObject())
            {
                if (string.Equals(property.Name, "version", StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out var number))
                {
                    version = number;
                }
            }

            if (version != 1)
            {
                return Failure($"{FileName} has version {version?.ToString() ?? "(missing)"}; this app reads version 1.");
            }

            document = root.Deserialize<TaskDocument>(JsonOptions);
        }
        catch (Exception ex)
        {
            return Failure($"{FileName} is not valid: {ex.Message}");
        }

        if (document is null)
        {
            return Failure($"{FileName} is empty.");
        }

        // explicit nulls in the json override the initializers
        document.Tasks ??= [];
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var task in document.Tasks)
        {
            if (task is null || string.IsNullOrEmpty(task.Id))
            {
                return Failure($"{FileName} has a task with no id.");
            }

            if (!ids.Add(task.Id))
            {
                return Failure($"{FileName} has two tasks with id {task.Id}.");
            }

            task.Title ??= "";
            task.Checklist = (task.Checklist ?? []).Where(item => item is not null).ToList();
            task.Checklist.ForEach(item => item.Text ??= "");
        }

        return new TaskStoreResult(true, false, null, document);
    }

    // temp file beside the target, then a rename - a failed write leaves the old file whole
    private TaskStoreResult Write(TaskDocument updated, TaskDocument onDisk)
    {
        // stable order keeps git diffs small
        updated.Tasks = updated.Tasks.OrderBy(task => task.Created).ThenBy(task => task.Id, StringComparer.Ordinal).ToList();

        string? temp = null;
        try
        {
            var full = System.IO.Path.GetFullPath(Path);
            var folder = System.IO.Path.GetDirectoryName(full)!;
            Directory.CreateDirectory(folder);
            temp = System.IO.Path.Combine(folder, $"{System.IO.Path.GetFileName(full)}.{Guid.NewGuid():N}.tmp");

            var bytes = JsonSerializer.SerializeToUtf8Bytes(updated, JsonOptions);
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Write("\n"u8);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, full, overwrite: true);
            _seen = Stamp();
            return new TaskStoreResult(true, false, null, updated);
        }
        catch (Exception ex)
        {
            if (temp is not null)
            {
                try { File.Delete(temp); } catch { }
            }

            return new TaskStoreResult(false, false, $"Could not save {FileName}: {ex.Message}", onDisk);
        }
    }

    // the page's fields only; the caller sets Id, Rev, the timestamps and Extra
    private static TaskItem CopyEditable(TaskItem incoming) => new()
    {
        Title = incoming.Title ?? "",
        Bucket = incoming.Bucket,
        Progress = incoming.Progress,
        Due = incoming.Due,
        Priority = incoming.Priority,
        Repo = string.IsNullOrWhiteSpace(incoming.Repo) ? null : incoming.Repo.Trim(),
        Checklist = (incoming.Checklist ?? [])
            .Where(item => item is not null)
            .Select(item => new ChecklistItem { Text = item.Text ?? "", Done = item.Done })
            .ToList(),
        Notes = incoming.Notes,
    };

    private string FileName => System.IO.Path.GetFileName(Path);

    private static TaskStoreResult Failure(string message) => new(false, false, message, new TaskDocument());

    private FileStamp? Stamp()
    {
        try
        {
            var info = new FileInfo(Path);
            return info.Exists ? new FileStamp(true, info.LastWriteTimeUtc, info.Length) : new FileStamp(false, default, 0);
        }
        catch
        {
            return null;
        }
    }

    private readonly record struct FileStamp(bool Exists, DateTime WriteUtc, long Length);

    // utc with a trailing Z, the shape in the spec's example, whatever offset the caller passed
    private sealed class UtcInstantConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => reader.GetDateTimeOffset();

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.UtcDateTime);
    }
}

/// <summary>
/// where the tasks file came from. sign-in and sign-out sync only scanned repos, so a DefaultClone
/// file never syncs and a LocalFallback file never leaves this machine
/// </summary>
public enum TaskPathSource { Override, ScannedRepo, DefaultClone, LocalFallback }

public static class TaskPaths
{
    /// <summary>
    /// the override when set, else persistent-memory's tasks\tasks.json - the scanned clone, else
    /// ~\Tools\persistent-memory. with neither on this machine, %APPDATA%\InOutButton\tasks.json
    /// </summary>
    public static string Resolve(string? tasksFileOverride, IEnumerable<RepoRow> repos, out TaskPathSource source)
    {
        if (!string.IsNullOrWhiteSpace(tasksFileOverride))
        {
            source = TaskPathSource.Override;
            return tasksFileOverride.Trim();
        }

        // same lookup as Form1.RunMachineSyncAsync
        var scanned = repos.FirstOrDefault(repo => string.Equals(repo.Name, "persistent-memory", StringComparison.OrdinalIgnoreCase))?.Path;
        var clone = scanned ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Tools", "persistent-memory");
        if (Directory.Exists(clone))
        {
            source = scanned is null ? TaskPathSource.DefaultClone : TaskPathSource.ScannedRepo;
            return Path.Combine(clone, "tasks", "tasks.json");
        }

        source = TaskPathSource.LocalFallback;
        return Path.Combine(SettingsStore.SettingsDirectory, "tasks.json");
    }
}
