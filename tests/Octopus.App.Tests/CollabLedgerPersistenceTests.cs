using System.Reflection;
using System.Text.Json.Nodes;
using Octopus.App.Collab;
using Octopus.App.Collab.Ledger;
using Octopus.Core.Agents;
using Octopus.Platform;
using Xunit;

namespace Octopus.App.Tests;

public sealed class CollabLedgerPersistenceTests
{
    /// <summary>
    /// Portable test <see cref="IAppPaths"/>: obstructing <c>collab/state.json</c> with a directory forces an ordinary
    /// IO/access failure in <see cref="CollabStateStore.Save"/> on Windows, macOS, and Linux (including root) while
    /// preserving the real snapshot file untouched and exposing deterministic save-attempt barriers.
    /// </summary>
    private sealed class ControlledAppPaths : IAppPaths, IDisposable
    {
        private readonly object _sync = new();
        private readonly List<(int Target, TaskCompletionSource Tcs)> _waiters = [];
        private int _failWrites;
        private int _saveAttempts;
        private int _pauseAttempt;
        private TaskCompletionSource? _pauseEntered;
        private ManualResetEventSlim? _pauseRelease;

        public ControlledAppPaths()
        {
            Root = Directory.CreateTempSubdirectory("octopus-ledger-persist-").FullName;
            RealDataDirectory = Path.Combine(Root, "data");
            RealCollabDirectory = Path.Combine(RealDataDirectory, "collab");
            RealStateFilePath = Path.Combine(RealCollabDirectory, "state.json");
            BlockedDataDirectory = Path.Combine(Root, "blocked");
            BlockedCollabDirectory = Path.Combine(BlockedDataDirectory, "collab");
            Directory.CreateDirectory(Path.Combine(BlockedCollabDirectory, "state.json"));
        }

        public string Root { get; }
        public string RealDataDirectory { get; }
        public string RealCollabDirectory { get; }
        public string RealStateFilePath { get; }
        public string BlockedDataDirectory { get; }
        public string BlockedCollabDirectory { get; }
        public string CacheDirectory => Path.Combine(Root, "cache");

        public bool FailWrites
        {
            get => Volatile.Read(ref _failWrites) != 0;
            set => Volatile.Write(ref _failWrites, value ? 1 : 0);
        }

        public int SaveAttempts => Volatile.Read(ref _saveAttempts);

        public string? ReadSavedText() =>
            File.Exists(RealStateFilePath) ? File.ReadAllText(RealStateFilePath) : null;

        public Task<byte[]> ReadSavedBytesAsync(CancellationToken ct) =>
            File.ReadAllBytesAsync(RealStateFilePath, ct);

        public void ResetAttempts()
        {
            lock (_sync)
            {
                Interlocked.Exchange(ref _saveAttempts, 0);
                _pauseAttempt = 0;
                _pauseEntered = null;
                _pauseRelease?.Set();
                _pauseRelease?.Dispose();
                _pauseRelease = null;
            }
        }

        public Task WaitForAttemptAsync(int targetAttempt, CancellationToken ct)
        {
            TaskCompletionSource tcs;
            lock (_sync)
            {
                if (Volatile.Read(ref _saveAttempts) >= targetAttempt) return Task.CompletedTask;
                tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add((targetAttempt, tcs));
            }
            return tcs.Task.WaitAsync(ct);
        }

        public (Task Entered, Action Release) PauseOnAttempt(int targetAttempt)
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new ManualResetEventSlim(false);
            lock (_sync)
            {
                _pauseRelease?.Set();
                _pauseRelease?.Dispose();
                _pauseAttempt = targetAttempt;
                _pauseEntered = entered;
                _pauseRelease = release;
            }
            return (entered.Task, () => release.Set());
        }

        public string DataDirectory
        {
            get
            {
                var dir = FailWrites ? BlockedDataDirectory : RealDataDirectory;
                var attempt = Interlocked.Increment(ref _saveAttempts);
                TaskCompletionSource? pauseEntered = null;
                ManualResetEventSlim? pauseRelease = null;
                lock (_sync)
                {
                    if (_pauseAttempt == attempt)
                    {
                        pauseEntered = _pauseEntered;
                        pauseRelease = _pauseRelease;
                    }
                    for (var i = _waiters.Count - 1; i >= 0; i--)
                    {
                        if (attempt >= _waiters[i].Target)
                        {
                            _waiters[i].Tcs.TrySetResult();
                            _waiters.RemoveAt(i);
                        }
                    }
                }
                if (pauseEntered is not null && pauseRelease is not null)
                {
                    pauseEntered.TrySetResult();
                    if (!pauseRelease.Wait(TimeSpan.FromSeconds(10)))
                    {
                        throw new TimeoutException($"Timed out waiting to release save attempt {attempt}.");
                    }
                }
                return dir;
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                _pauseRelease?.Set();
                _pauseRelease?.Dispose();
                _pauseRelease = null;
            }
            try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
        }
    }

    private static CollabTaskRecord MakeTask(CollabLedger ledger, string title) => new()
    {
        TaskId = ledger.NextId("task"),
        CallerItemId = "caller-1",
        TargetItemId = "target-1",
        TargetAgentId = "codex",
        Title = title,
        Goal = $"Goal for {title}",
        Isolation = CollabIsolationMode.Shared,
        Status = CollabTaskStatus.Completed,
        CreatedAt = DateTimeOffset.UtcNow,
        CompletedAt = DateTimeOffset.UtcNow,
    };

    private static void WaitPersistIdle(CollabLedger ledger)
    {
        var persistGate = typeof(CollabLedger)
            .GetField("_persistGate", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(ledger)!;
        lock (persistGate) { }
    }

    [Fact]
    public async Task Transient_write_failure_preserves_existing_file_and_retries_without_further_mutation()
    {
        var ct = TestContext.Current.CancellationToken;
        using var paths = new ControlledAppPaths();
        var store = new CollabStateStore(paths);
        using var ledger = new CollabLedger(store);

        var first = MakeTask(ledger, "Initial persisted task");
        lock (ledger.Gate) { ledger.Add(first); }
        ledger.Flush();

        Assert.True(File.Exists(paths.RealStateFilePath));
        var beforeBytes = await paths.ReadSavedBytesAsync(ct);
        var beforeJson = JsonNode.Parse(beforeBytes)!.AsObject();
        Assert.Equal(CollabLedgerDocument.CurrentVersion, beforeJson["version"]!.GetValue<int>());
        Assert.Contains(first.TaskId, paths.ReadSavedText()!, StringComparison.Ordinal);

        paths.ResetAttempts();
        var second = MakeTask(ledger, "Transient failure task");
        paths.FailWrites = true;
        lock (ledger.Gate) { ledger.Add(second); }

        // Ordinary portable IO/access failure while target is obstructed: must not throw, must preserve existing file and clean up temp files.
        ledger.Flush();

        var duringFailureBytes = await paths.ReadSavedBytesAsync(ct);
        Assert.Equal(beforeBytes, duringFailureBytes);
        Assert.Empty(Directory.GetFiles(paths.RealCollabDirectory, "*.tmp"));
        Assert.Empty(Directory.GetFiles(paths.BlockedCollabDirectory, "*.tmp"));
        Assert.Equal(1, paths.SaveAttempts);

        // Clear the obstruction without further mutation or explicit Flush: the scheduled retry must persist automatically.
        paths.FailWrites = false;
        await paths.WaitForAttemptAsync(2, ct).WaitAsync(TimeSpan.FromSeconds(5), ct);
        WaitPersistIdle(ledger);

        var savedText = paths.ReadSavedText();
        Assert.NotNull(savedText);
        Assert.Contains(second.TaskId, savedText!, StringComparison.Ordinal);

        // Existing serialized format remains intact and loads cleanly into a fresh ledger.
        var afterJson = JsonNode.Parse(savedText!)!.AsObject();
        Assert.Equal(CollabLedgerDocument.CurrentVersion, afterJson["version"]!.GetValue<int>());
        Assert.True(afterJson["id_seq"]!.GetValue<int>() >= 2);
        Assert.True(afterJson["event_seq"]!.GetValue<long>() >= 2);
        Assert.NotNull(afterJson["tasks"]);
        Assert.NotNull(afterJson["threads"]);
        Assert.NotNull(afterJson["seen"]);

        using var restored = new CollabLedger(store);
        restored.Restore();
        Assert.Contains(first.TaskId, restored.Tasks.Keys);
        Assert.Contains(second.TaskId, restored.Tasks.Keys);
    }

    [Fact]
    public async Task Bounded_retries_stop_after_persistent_failure_but_keep_ledger_dirty_for_later_flush()
    {
        var ct = TestContext.Current.CancellationToken;
        using var paths = new ControlledAppPaths();
        var store = new CollabStateStore(paths);
        using var ledger = new CollabLedger(store);

        var first = MakeTask(ledger, "Baseline task");
        lock (ledger.Gate) { ledger.Add(first); }
        ledger.Flush();
        var baselineBytes = await paths.ReadSavedBytesAsync(ct);

        paths.ResetAttempts();
        paths.FailWrites = true;
        var second = MakeTask(ledger, "Task during prolonged IO failure");
        lock (ledger.Gate) { ledger.Add(second); }
        ledger.Flush();

        // Wait deterministically for the initial attempt + 3 bounded retries (4 total attempts: 0 + 400 + 800 + 1600 ms).
        await paths.WaitForAttemptAsync(4, ct).WaitAsync(TimeSpan.FromSeconds(10), ct);
        WaitPersistIdle(ledger);
        Assert.Equal(4, paths.SaveAttempts);
        Assert.Equal(baselineBytes, await paths.ReadSavedBytesAsync(ct));

        paths.FailWrites = false;

        // Retries must have stopped once the retry budget was exhausted (no background write after recovery).
        var unexpectedFifth = paths.WaitForAttemptAsync(5, ct);
        Assert.NotEqual(unexpectedFifth, await Task.WhenAny(unexpectedFifth, Task.Delay(700, ct)));
        Assert.Equal(4, paths.SaveAttempts);
        Assert.DoesNotContain(second.TaskId, paths.ReadSavedText()!, StringComparison.Ordinal);

        // Even after bounded retries stop, the ledger remains dirty so a later explicit Flush succeeds without a new mutation.
        ledger.Flush();
        Assert.Equal(5, paths.SaveAttempts);
        Assert.Contains(second.TaskId, paths.ReadSavedText()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Overlapping_flush_calls_preserve_latest_snapshot_order()
    {
        var ct = TestContext.Current.CancellationToken;
        using var paths = new ControlledAppPaths();
        var store = new CollabStateStore(paths);
        using var ledger = new CollabLedger(store);

        // Deterministic overlap: hold an in-flight flush of an older snapshot while a newer task is added and flushed concurrently.
        var (firstEntered, releaseFirst) = paths.PauseOnAttempt(1);
        var older = MakeTask(ledger, "Older snapshot task");
        lock (ledger.Gate) { ledger.Add(older); }
        var flushOlder = Task.Run(() => ledger.Flush(), ct);
        await firstEntered.WaitAsync(TimeSpan.FromSeconds(5), ct);

        var newer = MakeTask(ledger, "Newer concurrent task");
        lock (ledger.Gate) { ledger.Add(newer); }
        var flushNewer = Task.Run(() => ledger.Flush(), ct);
        releaseFirst();
        await Task.WhenAll(flushOlder, flushNewer);

        var deterministicText = paths.ReadSavedText();
        Assert.NotNull(deterministicText);
        Assert.Contains(older.TaskId, deterministicText!, StringComparison.Ordinal);
        Assert.Contains(newer.TaskId, deterministicText!, StringComparison.Ordinal);

        // Seed enough entries so serialization and fsync overlap across concurrent Flush calls.
        lock (ledger.Gate)
        {
            for (var i = 0; i < 60; i++) ledger.Add(MakeTask(ledger, $"Seed task {i} " + new string('x', 200)));
        }
        ledger.Flush();

        long lastPersistedSeq = 0;
        for (var round = 0; round < 30; round++)
        {
            var tasks = new List<Task>(4);
            long expectedSeq = 0;
            CollabTaskRecord? newest = null;
            for (var step = 0; step < 4; step++)
            {
                var item = MakeTask(ledger, $"Round {round} step {step}");
                lock (ledger.Gate)
                {
                    ledger.Add(item);
                    expectedSeq = item.UpdatedSeq;
                }
                newest = item;
                tasks.Add(Task.Run(() => ledger.Flush(), ct));
            }

            await Task.WhenAll(tasks);

            var text = paths.ReadSavedText();
            Assert.NotNull(text);
            var doc = JsonNode.Parse(text!)!.AsObject();
            var persistedSeq = doc["event_seq"]!.GetValue<long>();
            Assert.True(persistedSeq >= lastPersistedSeq, $"Snapshot sequence moved backwards: {persistedSeq} < {lastPersistedSeq}.");
            Assert.Equal(expectedSeq, persistedSeq);
            Assert.Contains(newest!.TaskId, text, StringComparison.Ordinal);
            lastPersistedSeq = persistedSeq;
        }
    }

    [Fact]
    public async Task Dispose_flushes_pending_dirty_state_after_transient_failure_and_stops_retries()
    {
        var ct = TestContext.Current.CancellationToken;

        // Part 1: Dispose flushes unpersisted state that survived an earlier transient failure.
        using (var paths = new ControlledAppPaths())
        {
            var store = new CollabStateStore(paths);
            var ledger = new CollabLedger(store);

            var first = MakeTask(ledger, "Saved before failure");
            lock (ledger.Gate) { ledger.Add(first); }
            ledger.Flush();

            paths.FailWrites = true;
            var pending = MakeTask(ledger, "Must flush on dispose");
            lock (ledger.Gate) { ledger.Add(pending); }
            ledger.Flush();
            Assert.DoesNotContain(pending.TaskId, paths.ReadSavedText()!, StringComparison.Ordinal);

            paths.FailWrites = false;
            ledger.Dispose();
            Assert.Contains(pending.TaskId, paths.ReadSavedText()!, StringComparison.Ordinal);
        }

        // Part 2: Dispose while storage is still failing does not throw, stops retries, and tolerates repeated/racing calls.
        using (var paths = new ControlledAppPaths())
        {
            var store = new CollabStateStore(paths);
            var ledger = new CollabLedger(store);

            var baseline = MakeTask(ledger, "Baseline before dispose");
            lock (ledger.Gate) { ledger.Add(baseline); }
            ledger.Flush();
            var baselineBytes = await paths.ReadSavedBytesAsync(ct);

            paths.ResetAttempts();
            paths.FailWrites = true;
            var unsaved = MakeTask(ledger, "Unsaved at dispose");
            lock (ledger.Gate) { ledger.Add(unsaved); }
            ledger.SchedulePersist();
            ledger.Flush();

            var scheduleRace = Task.Run(() =>
            {
                for (var i = 0; i < 100; i++) ledger.SchedulePersist();
            }, ct);

            ledger.Dispose();
            ledger.Dispose();
            ledger.SchedulePersist();
            await scheduleRace;

            var attemptsAtDispose = paths.SaveAttempts;
            paths.FailWrites = false;

            // No stray timer retry may write after Dispose returned.
            var strayAttempt = paths.WaitForAttemptAsync(attemptsAtDispose + 1, ct);
            Assert.NotEqual(strayAttempt, await Task.WhenAny(strayAttempt, Task.Delay(700, ct)));
            Assert.Equal(attemptsAtDispose, paths.SaveAttempts);
            Assert.Equal(baselineBytes, await paths.ReadSavedBytesAsync(ct));
        }
    }

    [Fact]
    public async Task Mutation_during_final_failed_retry_gets_fresh_retry_budget_and_persists_after_recovery()
    {
        var ct = TestContext.Current.CancellationToken;

        // Case 1: New dirty generation added while final retry (attempt 4) writes older version,
        // and SchedulePersist runs after attempt 4 fails and storage recovers.
        using (var paths = new ControlledAppPaths())
        {
            var store = new CollabStateStore(paths);
            using var ledger = new CollabLedger(store);

            paths.FailWrites = true;
            var (fourthEntered, releaseFourth) = paths.PauseOnAttempt(4);

            var oldTask = MakeTask(ledger, "old-version");
            lock (ledger.Gate) { ledger.Add(oldTask); }
            ledger.Flush();

            await fourthEntered.WaitAsync(TimeSpan.FromSeconds(10), ct);

            var newTask = MakeTask(ledger, "new-version");
            lock (ledger.Gate) { ledger.Add(newTask); }
            releaseFourth();

            // Wait for attempt 4's failure bookkeeping to complete before clearing storage obstruction and scheduling.
            WaitPersistIdle(ledger);
            paths.FailWrites = false;
            ledger.SchedulePersist();

            var fifthAttempt = paths.WaitForAttemptAsync(5, ct);
            Assert.True(
                await Task.WhenAny(fifthAttempt, Task.Delay(3500, ct)) == fifthAttempt,
                "The recovered storage received zero writes for the newer dirty generation.");
            WaitPersistIdle(ledger);

            var saved = paths.ReadSavedText();
            Assert.NotNull(saved);
            Assert.Contains(oldTask.TaskId, saved!, StringComparison.Ordinal);
            Assert.Contains(newTask.TaskId, saved!, StringComparison.Ordinal);
        }

        // Case 2: Producer also calls SchedulePersist while attempt 4 is still in flight;
        // attempt 4 failing must not cancel or exhaust the retry budget of the newer generation.
        using (var paths = new ControlledAppPaths())
        {
            var store = new CollabStateStore(paths);
            using var ledger = new CollabLedger(store);

            paths.FailWrites = true;
            var (fourthEntered, releaseFourth) = paths.PauseOnAttempt(4);

            var oldTask = MakeTask(ledger, "old-version-in-flight");
            lock (ledger.Gate) { ledger.Add(oldTask); }
            ledger.Flush();

            await fourthEntered.WaitAsync(TimeSpan.FromSeconds(10), ct);

            var newTask = MakeTask(ledger, "new-version-in-flight");
            lock (ledger.Gate) { ledger.Add(newTask); }
            ledger.SchedulePersist();
            paths.FailWrites = false;
            releaseFourth();

            var fifthAttempt = paths.WaitForAttemptAsync(5, ct);
            Assert.True(
                await Task.WhenAny(fifthAttempt, Task.Delay(3500, ct)) == fifthAttempt,
                "In-flight final failure stranded the newer generation scheduled before it completed.");
            WaitPersistIdle(ledger);

            var saved = paths.ReadSavedText();
            Assert.NotNull(saved);
            Assert.Contains(oldTask.TaskId, saved!, StringComparison.Ordinal);
            Assert.Contains(newTask.TaskId, saved!, StringComparison.Ordinal);
        }
    }
}
