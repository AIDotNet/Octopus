using Octopus.App.Services;
using Xunit;

namespace Octopus.App.Tests;

public sealed class CrashLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "octopus-crashlog-tests-" + Guid.NewGuid().ToString("N"));

    public CrashLogTests() => CrashLog.ResetDeduplicationForTests();

    public void Dispose()
    {
        CrashLog.ResetDeduplicationForTests();
        if (Directory.Exists(_dir))
        {
            try { Directory.Delete(_dir, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public void WriteCrash_WritesStructuredReportWithExceptionDetailsAndHResult()
    {
        Exception captured;
        try
        {
            throw new InvalidOperationException("broken UI state", new ArgumentException("bad arg"));
        }
        catch (Exception ex)
        {
            captured = ex;
        }

        var path = CrashLog.WriteCrash(
            "Dispatcher.UIThread",
            captured,
            isTerminating: true,
            directory: _dir,
            now: new DateTimeOffset(2026, 11, 24, 19, 44, 51, 678, TimeSpan.FromHours(8)));

        Assert.NotNull(path);
        Assert.True(File.Exists(path));
        Assert.StartsWith("crash-20261124-194451-678-", Path.GetFileName(path));

        var content = File.ReadAllText(path);
        Assert.Contains("Source: Dispatcher.UIThread", content);
        Assert.Contains("IsTerminating: True", content);
        Assert.Contains("ExceptionType: System.InvalidOperationException", content);
        Assert.Contains("HResult: 0x80131509", content);
        Assert.Contains("Message: broken UI state", content);
        Assert.Contains("System.ArgumentException: bad arg", content);
        Assert.Contains(nameof(WriteCrash_WritesStructuredReportWithExceptionDetailsAndHResult), content);
    }

    [Fact]
    public void WriteCrash_DeduplicatesSameExceptionInstance()
    {
        var ex = new InvalidOperationException("once");

        var first = CrashLog.WriteCrash("Dispatcher.UIThread", ex, isTerminating: true, directory: _dir);
        var second = CrashLog.WriteCrash("Main", ex, isTerminating: true, directory: _dir);
        var third = CrashLog.WriteCrash("AppDomain.UnhandledException", ex, isTerminating: true, directory: _dir);

        Assert.NotNull(first);
        Assert.Null(second);
        Assert.Null(third);
        Assert.Single(Directory.GetFiles(_dir, "crash-*.log"));
    }

    [Fact]
    public void WriteCrash_PrunesOldCrashFilesToLimit()
    {
        var start = new DateTimeOffset(2026, 11, 24, 10, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < 25; i++)
        {
            var path = CrashLog.WriteCrash("Main", new InvalidOperationException($"err-{i}"), true, _dir, start.AddSeconds(i));
            Assert.NotNull(path);
        }

        var remaining = Directory.GetFiles(_dir, "crash-*.log");
        Assert.Equal(20, remaining.Length);
        Array.Sort(remaining, StringComparer.Ordinal);
        Assert.Contains("err-5", File.ReadAllText(remaining[0]));
        Assert.Contains("err-24", File.ReadAllText(remaining[^1]));
    }

    [Fact]
    public void WriteUnobserved_AppendsToUnobservedLog()
    {
        var p1 = CrashLog.WriteUnobserved(new InvalidOperationException("bg-1"), _dir);
        var p2 = CrashLog.WriteUnobserved(new IOException("bg-2"), _dir);

        Assert.NotNull(p1);
        Assert.Equal(p1, p2);
        var content = File.ReadAllText(p1);
        Assert.Contains("TaskScheduler.UnobservedTaskException", content);
        Assert.Contains("bg-1", content);
        Assert.Contains("bg-2", content);
    }

    [Fact]
    public void ResolveLogDirectory_DefaultsToDotOctopusLogsUnderHome()
    {
        var dir = CrashLog.ResolveLogDirectory(dataDirOverride: "", homeOverride: _dir);
        Assert.Equal(Path.Combine(_dir, ".octopus", "logs"), dir);
    }

    [Fact]
    public void ResolveLogDirectory_RespectsIsolatedDataDirOverride()
    {
        var dir = CrashLog.ResolveLogDirectory(_dir);
        Assert.Equal(Path.Combine(Path.GetFullPath(_dir), "logs"), dir);
    }

    [Fact]
    public void ShouldRecover_SwallowsOrdinaryUiExceptions()
    {
        Assert.True(CrashLog.ShouldRecover(new InvalidOperationException("handler bug"), nowMs: 1_000));
        Assert.True(CrashLog.ShouldRecover(new NullReferenceException(), nowMs: 1_001));
    }

    [Fact]
    public void ShouldRecover_LetsCorruptingExceptionsTerminate()
    {
        Assert.False(CrashLog.ShouldRecover(new OutOfMemoryException(), nowMs: 1_000));
        Assert.False(CrashLog.ShouldRecover(new AccessViolationException(), nowMs: 1_000));
    }

    [Fact]
    public void ShouldRecover_StopsRecoveringWhenFailuresRepeatInABurst_ThenAllowsAgainLater()
    {
        for (var i = 0; i < 20; i++)
        {
            Assert.True(CrashLog.ShouldRecover(new InvalidOperationException(), nowMs: 1_000 + i));
        }
        Assert.False(CrashLog.ShouldRecover(new InvalidOperationException(), nowMs: 1_100));
        Assert.True(CrashLog.ShouldRecover(new InvalidOperationException(), nowMs: 20_000));
    }
}
