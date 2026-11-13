using Octopus.Core.Agents;
using Octopus.Platform;

namespace Octopus.Agents.Tests;

public class ScannerTests
{
    private static AgentDefinition Def(string id, string[] executables, string? fingerprint = null, string? regex = @"(\d+\.\d+\.\d+)") =>
        new()
        {
            Id = id,
            Name = id,
            Detect = new AgentDetect
            {
                Executables = [.. executables],
                Fingerprint = fingerprint,
                VersionRegex = regex,
                ProbeEnv = new() { ["PROBE"] = "1" }
            }
        };

    private static AgentScanner Make(TempDir dir, IAgentCatalog catalog, FakeLocator locator, FakeRunner runner) =>
        new(catalog, locator, runner, new FakeLoginEnvironment($"/x{Path.PathSeparator}/y"), new TestPaths(dir.Path), () => []);

    [Fact]
    public async Task First_candidate_broken_second_good()
    {
        using var dir = new TempDir();
        var runner = new FakeRunner((file, _, _) => file == "/usr/local/bin/claude"
            ? new ProcessResult(1, "", "Error: native binary not installed", false)
            : new ProcessResult(0, "2.1.322 (Claude Code)\n", "", false));
        var scanner = Make(dir, new FixedCatalog(Def("claude", ["claude"], "Claude Code")),
            new FakeLocator(new() { ["claude"] = ["/usr/local/bin/claude", "/home/x/bin/claude"] }), runner);

        var result = await scanner.ScanAsync();

        var install = Assert.Single(result.Installations);
        Assert.Equal("/home/x/bin/claude", install.Path);
        Assert.Equal("2.1.322", install.Version);
        Assert.Same(install, scanner.Find("claude"));
        Assert.Equal("1", runner.Calls[0].Env!["PROBE"]);
        // The login PATH, then the directory of the candidate (a script may start with `env node`).
        Assert.Equal(string.Join(Path.PathSeparator, "/x", "/y", Path.GetDirectoryName("/usr/local/bin/claude")), runner.Calls[0].Env!["PATH"]);
    }

    [Fact]
    public async Task Fingerprint_mismatch_is_skipped_and_missing_version_is_not_a_failure()
    {
        using var dir = new TempDir();
        var runner = new FakeRunner((file, _, _) => file switch
        {
            "/a/agent" => new ProcessResult(0, "grok 1.0.62", "", false),
            "/b/agent" => new ProcessResult(0, "cursor mystery build", "", false),
            _ => new ProcessResult(0, "", "", true)
        });
        var scanner = Make(dir, new FixedCatalog(Def("cursor", ["agent", "nope"], "cursor")),
            new FakeLocator(new() { ["agent"] = ["/a/agent", "/b/agent"] }), runner);

        var install = Assert.Single((await scanner.ScanAsync()).Installations);
        Assert.Equal("/b/agent", install.Path);
        Assert.Null(install.Version);
    }

    [Fact]
    public async Task Not_found_and_timeouts_give_no_installation()
    {
        using var dir = new TempDir();
        var runner = new FakeRunner((_, _, _) => new ProcessResult(0, "", "", true));
        var scanner = Make(dir, new FixedCatalog(Def("a", ["a"]), Def("b", ["b"])),
            new FakeLocator(new() { ["a"] = ["/bin/a"] }), runner);

        Assert.Empty((await scanner.ScanAsync()).Installations);
    }

    [Fact]
    public async Task Cache_is_written_atomically_and_loaded()
    {
        using var dir = new TempDir();
        var runner = new FakeRunner((_, _, _) => new ProcessResult(0, "1.2.3", "", false));
        var binary = Path.Combine(dir.Path, "a");
        File.WriteAllText(binary, "");
        var catalog = new FixedCatalog(Def("a", ["a"]));
        var locator = new FakeLocator(new() { ["a"] = [binary] });
        var scanner = Make(dir, catalog, locator, runner);
        var raised = 0;
        scanner.Changed += (_, _) => Interlocked.Increment(ref raised);

        await scanner.ScanAsync();

        Assert.True(raised >= 2);
        Assert.False(scanner.IsScanning);
        var cacheDir = Path.Combine(dir.Path, "cache");
        Assert.Equal(["agent-scan.json"], Directory.GetFiles(cacheDir).Select(Path.GetFileName));

        var fresh = Make(dir, catalog, locator, runner);
        Assert.Empty(fresh.Current.Installations);
        await fresh.LoadCacheAsync();
        Assert.Equal(binary, fresh.Find("a")!.Path);
        Assert.Equal("1.2.3", fresh.Find("a")!.Version);

        // An agent removed since the last run is not shown as installed.
        File.Delete(binary);
        var later = Make(dir, catalog, locator, runner);
        await later.LoadCacheAsync();
        Assert.Null(later.Find("a"));
    }

    [Fact]
    public async Task Search_directories_and_definition_directories_are_searched_after_PATH()
    {
        using var dir = new TempDir();
        var own = Path.Combine(dir.Path, "own");
        Directory.CreateDirectory(own);
        var definition = Def("x", ["x"]);
        definition.Detect.ExtraDirs = [own];
        string? seen = null;
        var locator = new PathRecordingLocator(path => seen = path, "/found/x");
        var scanner = new AgentScanner(new FixedCatalog(definition), locator,
            new FakeRunner((_, _, _) => new ProcessResult(0, "x 3.4.5", "", false)),
            new FakeLoginEnvironment($"/x{Path.PathSeparator}/y"), new TestPaths(dir.Path), () => ["/x", "/opt/tools"]);

        var install = Assert.Single((await scanner.ScanAsync()).Installations);

        Assert.Equal("/found/x", install.Path);
        // "/x" is already on PATH and is not listed twice.
        var ps = Path.PathSeparator;
        Assert.Equal($"/x{ps}/y{ps}/opt/tools{ps}{own}", seen);
    }

    [Fact]
    public async Task Two_names_of_one_program_are_started_once()
    {
        if (OperatingSystem.IsWindows()) return; // creating a link needs a privilege there
        using var dir = new TempDir();
        var real = Path.Combine(dir.Path, "qodercli");
        File.WriteAllText(real, "");
        var alias = Path.Combine(dir.Path, "qoder");
        File.CreateSymbolicLink(alias, real);
        var runner = new FakeRunner((_, _, _) => new ProcessResult(1, "", "", false));
        var scanner = Make(dir, new FixedCatalog(Def("qoder", ["qodercli", "qoder"])),
            new FakeLocator(new() { ["qodercli"] = [real], ["qoder"] = [alias] }), runner);

        Assert.Empty((await scanner.ScanAsync()).Installations);
        Assert.Single(runner.Calls);
    }

    [Theory]
    [InlineData(null, "anything", true)]
    [InlineData("vibe", "vibe 3.1.2", true)]
    [InlineData("vibe", "something else", false)]
    [InlineData("Claude Code|claude-code", "2.1.0 (claude-code)", true)]
    [InlineData("Claude Code|claude-code", "2.1.0", false)]
    public void Fingerprint_accepts_any_alternative_ignoring_case(string? fingerprint, string output, bool expected) =>
        Assert.Equal(expected, AgentScanner.MatchesFingerprint(fingerprint, output));

    [Fact]
    public void Colored_output_is_read_without_the_color_codes()
    {
        var text = AgentScanner.StripAnsi("\u001b[1mqoder\u001b[0m \u001b[32m1.1.88\u001b[0m\n");
        Assert.Equal("qoder 1.1.88\n", text);
        var version = AgentScanner.ExtractVersion(Def("qoder", ["qoder"], regex: null), text);
        Assert.Equal("1.1.88", version);
    }

    [Fact]
    public void A_version_regex_that_does_not_fit_falls_back_to_the_first_version_number()
    {
        var definition = Def("a", ["a"], regex: @"^a (\d+)$");
        Assert.Equal("0.37.1-rc.2", AgentScanner.ExtractVersion(definition, "Auggie v0.37.1-rc.2 (build 9)"));
        Assert.Null(AgentScanner.ExtractVersion(definition, "no digits here"));
        Assert.Equal("2.0.0", AgentScanner.ExtractVersion(Def("b", ["b"], regex: "(["), "2.0.0"));
    }

    [Fact]
    public async Task Corrupt_cache_is_ignored()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(Path.Combine(dir.Path, "cache"));
        File.WriteAllText(Path.Combine(dir.Path, "cache", "agent-scan.json"), "{{{");
        var scanner = Make(dir, new FixedCatalog(), new FakeLocator([]), new FakeRunner((_, _, _) => new ProcessResult(0, "", "", false)));
        await scanner.LoadCacheAsync();
        Assert.Empty(scanner.Current.Installations);
    }

    [Fact]
    public async Task Many_definitions_are_scanned()
    {
        using var dir = new TempDir();
        var defs = Enumerable.Range(0, 20).Select(i => Def("a" + i, ["a" + i])).ToArray();
        var map = defs.ToDictionary(d => d.Detect.Executables[0], d => new[] { "/bin/" + d.Id });
        var scanner = Make(dir, new FixedCatalog(defs), new FakeLocator(map), new FakeRunner((_, _, _) => new ProcessResult(0, "9.9.9", "", false)));
        Assert.Equal(20, (await scanner.ScanAsync()).Installations.Count);
    }
}
