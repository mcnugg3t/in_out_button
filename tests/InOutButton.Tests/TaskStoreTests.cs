namespace InOutButton.Tests;

// spec tests 7 to 13 (docs/2026-10-17-usage-and-tasks-tabs-spec.md, section 3)
public sealed class TaskStoreTests : IDisposable
{
    // the fixed text from spec 2.4, not read from the store
    private const string StaleMessage = "This task changed on disk. Your edit was not saved; reopen it to see the current version.";

    private static readonly DateTimeOffset T0 = new(2026, 10, 17, 22, 10, 0, TimeSpan.Zero);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "InOutButton.Tests", Guid.NewGuid().ToString("N"));

    // tasks\ doesn't exist until the first save creates it
    private string TasksFolder => Path.Combine(_folder, "tasks");
    private string FilePath => Path.Combine(TasksFolder, "tasks.json");

    public void Dispose()
    {
        if (!Directory.Exists(_folder))
        {
            return;
        }

        // test 13 leaves the file read-only
        foreach (var file in Directory.EnumerateFiles(_folder, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_folder, recursive: true);
    }

    // test 7
    [Fact]
    public void Upsert_ThenLoad_RoundTripsEveryField_AndOmitsNulls()
    {
        var store = new TaskStore(FilePath);
        var created = store.Upsert(new TaskItem
        {
            Title = "Refit buffer pH GAM with September data",
            Bucket = TaskBucket.Projects,
            Progress = TaskProgress.InProgress,
            Due = new DateOnly(2026, 10, 24),
            Priority = TaskPriority.Important,
            Repo = "rrl_sikora",
            Checklist = [new() { Text = "Pull September rows", Done = true }, new() { Text = "Refit", Done = false }],
            Notes = "compare against the August fit",
        }, T0);

        Assert.True(created.Ok, created.Message);
        var id = Assert.Single(created.Document.Tasks).Id;
        Assert.Matches("^[0-9a-f]{16}$", id);

        var first = Assert.Single(new TaskStore(FilePath).Load().Document.Tasks);
        Assert.Equal(id, first.Id);
        Assert.Equal(1, first.Rev);
        Assert.Equal("Refit buffer pH GAM with September data", first.Title);
        Assert.Equal(TaskBucket.Projects, first.Bucket);
        Assert.Equal(TaskProgress.InProgress, first.Progress);
        Assert.Equal(new DateOnly(2026, 10, 24), first.Due);
        Assert.Equal(TaskPriority.Important, first.Priority);
        Assert.Equal("rrl_sikora", first.Repo);
        Assert.Collection(first.Checklist,
            item => { Assert.Equal("Pull September rows", item.Text); Assert.True(item.Done); },
            item => { Assert.Equal("Refit", item.Text); Assert.False(item.Done); });
        Assert.Equal("compare against the August fit", first.Notes);
        Assert.Equal(T0, first.Created);
        Assert.Equal(T0, first.Updated);
        Assert.Null(first.Completed);

        var later = T0.AddHours(2);
        first.Progress = TaskProgress.Complete;
        var edited = store.Upsert(first, later);
        Assert.True(edited.Ok, edited.Message);

        var second = Assert.Single(new TaskStore(FilePath).Load().Document.Tasks);
        Assert.Equal(2, second.Rev);
        Assert.Equal(TaskProgress.Complete, second.Progress);
        Assert.Equal(T0, second.Created);
        Assert.Equal(later, second.Updated);
        Assert.Equal(later, second.Completed);

        // every optional field unset
        Assert.True(store.Upsert(new TaskItem { Title = "Renew lab cert", Bucket = TaskBucket.Admin }, later).Ok);

        var json = File.ReadAllText(FilePath);
        Assert.Contains("\"version\": 1", json);
        Assert.Contains("\"bucket\": \"projects\"", json);
        Assert.Contains("\"progress\": \"notStarted\"", json);
        Assert.Contains("\"due\": \"2026-10-24\"", json);
        Assert.Contains("\"created\": \"2026-10-17T22:10:00Z\"", json);
        Assert.DoesNotContain("\"repo\": null", json);
        Assert.DoesNotContain("null", json);
    }

    // test 8
    [Fact]
    public void Upsert_KeepsATaskWrittenExternallyAfterTheLoad()
    {
        var store = new TaskStore(FilePath);
        Assert.True(store.Upsert(new TaskItem { Title = "Seed" }, T0).Ok);
        Assert.True(store.Load().Ok);

        // task B lands in the file between the load and the next save, as another machine's pull would
        const string taskB = """

                {
                  "id": "bbbbbbbbbbbbbbbb",
                  "rev": 4,
                  "title": "Task B from elsewhere",
                  "bucket": "misc",
                  "progress": "inProgress",
                  "priority": "urgent",
                  "checklist": [],
                  "created": "2026-10-16T08:00:00Z",
                  "updated": "2026-10-17T09:30:00Z"
                },
            """;
        var text = File.ReadAllText(FilePath);
        Assert.Contains("\"tasks\": [", text);
        File.WriteAllText(FilePath, text.Replace("\"tasks\": [", "\"tasks\": [" + taskB));

        var result = store.Upsert(new TaskItem { Title = "Task A", Bucket = TaskBucket.Projects }, T0.AddMinutes(5));

        Assert.True(result.Ok, result.Message);
        var tasks = new TaskStore(FilePath).Load().Document.Tasks;
        Assert.Equal(["Task B from elsewhere", "Seed", "Task A"], tasks.Select(task => task.Title));
        var b = tasks.Single(task => task.Id == "bbbbbbbbbbbbbbbb");
        Assert.Equal(4, b.Rev);
        Assert.Equal(TaskBucket.Misc, b.Bucket);
        Assert.Equal(TaskProgress.InProgress, b.Progress);
        Assert.Equal(TaskPriority.Urgent, b.Priority);
        Assert.Equal(new DateTimeOffset(2026, 10, 17, 9, 30, 0, TimeSpan.Zero), b.Updated);
    }

    // test 9
    [Fact]
    public void Upsert_WithAStaleRev_IsRejected_AndLeavesTheFileUnchanged()
    {
        var store = new TaskStore(FilePath);
        var id = Assert.Single(store.Upsert(new TaskItem { Title = "Original" }, T0).Document.Tasks).Id;

        // a second page moves the task to rev 2
        Assert.True(new TaskStore(FilePath).Upsert(new TaskItem { Id = id, Rev = 1, Title = "Newer" }, T0.AddMinutes(1)).Ok);
        var before = File.ReadAllBytes(FilePath);

        var stale = store.Upsert(new TaskItem { Id = id, Rev = 1, Title = "Stale draft" }, T0.AddMinutes(2));

        Assert.False(stale.Ok);
        Assert.True(stale.Rejected);
        Assert.Equal(StaleMessage, stale.Message);
        Assert.Equal(before, File.ReadAllBytes(FilePath));
        Assert.Equal("Newer", Assert.Single(stale.Document.Tasks).Title);
    }

    // test 10
    [Fact]
    public void Upsert_OfATaskDeletedOnDisk_IsRejected_NotResurrected()
    {
        var store = new TaskStore(FilePath);
        var id = Assert.Single(store.Upsert(new TaskItem { Title = "Doomed" }, T0).Document.Tasks).Id;
        Assert.True(new TaskStore(FilePath).Delete(id, 1).Ok);
        var before = File.ReadAllBytes(FilePath);

        var result = store.Upsert(new TaskItem { Id = id, Rev = 1, Title = "Doomed, edited" }, T0.AddMinutes(1));

        Assert.False(result.Ok);
        Assert.True(result.Rejected);
        Assert.Equal(StaleMessage, result.Message);
        Assert.Equal(before, File.ReadAllBytes(FilePath));
        Assert.Empty(new TaskStore(FilePath).Load().Document.Tasks);
    }

    // test 11
    [Fact]
    public void Delete_RemovesOnlyThatId_RejectsAStaleRev_AndLeavesNoTempFile()
    {
        var store = new TaskStore(FilePath);
        var a = store.Upsert(new TaskItem { Title = "A" }, T0).Document.Tasks.Single(task => task.Title == "A").Id;
        var b = store.Upsert(new TaskItem { Title = "B" }, T0.AddMinutes(1)).Document.Tasks.Single(task => task.Title == "B").Id;
        var c = store.Upsert(new TaskItem { Title = "C" }, T0.AddMinutes(2)).Document.Tasks.Single(task => task.Title == "C").Id;
        Assert.True(store.Upsert(new TaskItem { Id = b, Rev = 1, Title = "B edited" }, T0.AddMinutes(3)).Ok);
        var before = File.ReadAllBytes(FilePath);

        var stale = store.Delete(b, 1);

        Assert.False(stale.Ok);
        Assert.True(stale.Rejected);
        Assert.Equal(StaleMessage, stale.Message);
        Assert.Equal(before, File.ReadAllBytes(FilePath));

        var deleted = store.Delete(b, 2);

        Assert.True(deleted.Ok, deleted.Message);
        Assert.Equal([a, c], new TaskStore(FilePath).Load().Document.Tasks.Select(task => task.Id));
        Assert.Equal([FilePath], Directory.GetFiles(TasksFolder));
    }

    // test 12
    [Theory]
    [InlineData("{ \"version\": 1, \"tasks\": [ ")]           // truncated
    [InlineData("[]")]                                        // root is not an object
    [InlineData("{ \"version\": 2, \"tasks\": [] }")]        // unsupported version
    [InlineData("{ \"tasks\": [] }")]                        // no version
    public void Load_OfABadFile_IsAnError_AndUpsertLeavesItAlone(string content)
    {
        Directory.CreateDirectory(TasksFolder);
        File.WriteAllText(FilePath, content);
        var store = new TaskStore(FilePath);

        var loaded = store.Load();

        Assert.False(loaded.Ok);
        Assert.False(loaded.Rejected);
        Assert.False(string.IsNullOrEmpty(loaded.Message));

        var upsert = store.Upsert(new TaskItem { Title = "Would overwrite" }, T0);

        Assert.False(upsert.Ok);
        Assert.Equal(content, File.ReadAllText(FilePath));
    }

    [Fact]
    public void Load_OfAMissingFile_IsAnEmptyDocument()
    {
        var loaded = new TaskStore(FilePath).Load();

        Assert.True(loaded.Ok, loaded.Message);
        Assert.Empty(loaded.Document.Tasks);
        Assert.False(Directory.Exists(TasksFolder));
    }

    // test 13
    [Fact]
    public void Upsert_WhenTheWriteFails_KeepsThePreviousFile_AndReturnsAnError()
    {
        var store = new TaskStore(FilePath);
        var id = Assert.Single(store.Upsert(new TaskItem { Title = "Original" }, T0).Document.Tasks).Id;
        var before = File.ReadAllBytes(FilePath);
        File.SetAttributes(FilePath, FileAttributes.ReadOnly);

        var result = store.Upsert(new TaskItem { Id = id, Rev = 1, Title = "Edited" }, T0.AddMinutes(1));

        Assert.False(result.Ok);
        Assert.False(result.Rejected);
        Assert.False(string.IsNullOrEmpty(result.Message));
        Assert.Equal(before, File.ReadAllBytes(FilePath));
        Assert.Equal([FilePath], Directory.GetFiles(TasksFolder));
    }

    [Fact]
    public void ChangedSinceLoad_SeesAnExternalEdit_ButNotTheStoresOwnWrite()
    {
        var store = new TaskStore(FilePath);
        Assert.True(store.Upsert(new TaskItem { Title = "A" }, T0).Ok);
        Assert.False(store.ChangedSinceLoad());

        // the length changes even when the timestamp tick doesn't
        File.AppendAllText(FilePath, " ");
        Assert.True(store.ChangedSinceLoad());

        Assert.True(store.Load().Ok);
        Assert.False(store.ChangedSinceLoad());
    }

    [Fact]
    public void Resolve_PrefersTheOverride_ThenTheScannedPersistentMemoryClone()
    {
        var clone = Path.Combine(_folder, "persistent-memory");
        Directory.CreateDirectory(clone);
        var repos = new[] { new RepoRow(Path.Combine(_folder, "other")), new RepoRow(clone) };

        Assert.Equal(@"D:\elsewhere\tasks.json", TaskPaths.Resolve(@"D:\elsewhere\tasks.json", repos, out var overridden));
        Assert.False(overridden);
        Assert.Equal(Path.Combine(clone, "tasks", "tasks.json"), TaskPaths.Resolve(null, repos, out var fallback));
        Assert.False(fallback);
    }
}
