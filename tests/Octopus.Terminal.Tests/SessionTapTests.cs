using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Octopus.Core.Terminals;
using Octopus.Terminal;
using Octopus.Terminal.Avalonia;
using TKCode.Terminal.Core;
using Xunit;

namespace Octopus.Terminal.Tests;

/// <summary>
/// A session without a view, a second reader of its output, and the registry that keeps sessions apart from views:
/// what a view on another device is built on (design/12-远程操作.md §4).
/// </summary>
[Collection(TerminalSessionCollection.Name)]
public class SessionTapTests
{
    /// <summary>A connection whose output the test writes and whose input it reads.</summary>
    private sealed class Wire : ITerminalConnection, ITerminalConnectionFactory
    {
        private readonly Channel<byte[]> _output = Channel.CreateUnbounded<byte[]>();
        private readonly TaskCompletionSource<TerminalExit> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly StringBuilder _written = new();
        public int Starts { get; private set; }
        public bool Disposed { get; private set; }
        public int ProcessId => 1;
        public Task<TerminalExit> Exited => _exit.Task;
        public string Written { get { lock (_written) return _written.ToString(); } }

        public ITerminalConnection Start(LaunchSpec spec, int columns, int rows)
        {
            Starts++;
            return this;
        }

        public void Feed(string text) => _output.Writer.TryWrite(Encoding.UTF8.GetBytes(text));

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadOutputAsync([EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var chunk in _output.Reader.ReadAllAsync(ct).ConfigureAwait(false)) yield return chunk;
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> input, CancellationToken ct = default)
        {
            lock (_written) _written.Append(Encoding.UTF8.GetString(input.Span));
            return ValueTask.CompletedTask;
        }

        public ValueTask ResizeAsync(int columns, int rows, int pixelWidth, int pixelHeight) => ValueTask.CompletedTask;
        public ValueTask SignalAsync(TerminalSignal signal) => ValueTask.CompletedTask;
        public ValueTask<string?> GetForegroundProcessNameAsync() => ValueTask.FromResult<string?>(null);

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            _output.Writer.TryComplete();
            _exit.TrySetResult(new TerminalExit(0, null));
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Applies what a tap is told to a terminal of its own, as a view on another device would.</summary>
    private sealed class Mirror : ITerminalTap, IDisposable
    {
        private readonly object _gate = new();
        private readonly List<(ulong Position, string Kind)> _events = [];
        private TerminalCore? _core;
        private ulong _next;

        public void Begin(TerminalTapStart start)
        {
            lock (_gate)
            {
                var options = new TerminalCoreOptions(start.Size) { ScrollbackMaxLines = 1000 };
                if (start.Format == TerminalSnapshotFormat.Native)
                {
                    _core = TerminalCore.Restore(start.Snapshot, options);
                }
                else
                {
                    _core = TerminalCore.Create(options);
                    _core.Write(start.Snapshot);
                }
                _next = start.Position;
            }
        }

        public IReadOnlyList<(ulong Position, string Kind)> Events { get { lock (_gate) return [.. _events]; } }
        public string Text { get { lock (_gate) return _core?.FormatPlainText() ?? ""; } }
        public InputModes Modes { get { lock (_gate) return _core!.ReadInputModes(); } }
        public TerminalSize Size { get { lock (_gate) return _core!.Size; } }

        public void OnOutput(ulong position, ReadOnlySpan<byte> bytes)
        {
            lock (_gate)
            {
                Assert.Equal(_next, position);
                _events.Add((position, "output"));
                _core?.Write(bytes);
                _next = position + (ulong)bytes.Length;
            }
        }

        public void OnResize(ulong position, TerminalSize size)
        {
            lock (_gate)
            {
                Assert.Equal(_next, position);
                _events.Add((position, "resize"));
                _core?.Resize(size);
                _next = position + 1;
            }
        }

        public void OnReset(ulong position)
        {
            lock (_gate)
            {
                Assert.Equal(_next, position);
                _events.Add((position, "reset"));
                _core?.Reset();
                _next = position + 1;
            }
        }

        public void Dispose() { lock (_gate) { _core?.Dispose(); _core = null; } }
    }

    private static readonly LaunchSpec Spec = new("zsh", [], null, new Dictionary<string, string>());

    private static TerminalSession Session(Wire wire, bool mirror = false) =>
        new(Spec, wire, new TerminalSize(60, 12, 0, 0), TerminalPalettes.Dark, ColorScheme.Dark, 1000, () => false) { IsMirror = mirror };

    private static async Task Until(Func<bool> done)
    {
        for (var i = 0; i < 200 && !done(); i++) await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.True(done());
    }

    [Theory]
    [InlineData(TerminalSnapshotFormat.Native)]
    [InlineData(TerminalSnapshotFormat.Vt)]
    public async Task A_tap_gets_the_screen_so_far_and_everything_after_it(TerminalSnapshotFormat format)
    {
        var wire = new Wire();
        await using var session = Session(wire);
        session.Start();
        wire.Feed("first line\r\nsecond \u001b[31mred\u001b[0m line\r\n");
        await Until(() => session.ReadText().Contains("second"));

        using var mirror = new Mirror();
        using (session.Tap(mirror, format, out var start))
        {
            mirror.Begin(start);
            // The snapshot alone already shows what was on screen.
            Assert.Equal(session.ReadText(), mirror.Text);
            Assert.Equal(new TerminalSize(60, 12, 0, 0), start.Size);

            wire.Feed("third line\r\n");
            wire.Feed("fourth 中文 line");
            await Until(() => session.ReadText().Contains("fourth"));
            Assert.Equal(session.ReadText(), mirror.Text);
        }

        // Disposed: the session goes on, the tap hears nothing more.
        wire.Feed("\r\nfifth line");
        await Until(() => session.ReadText().Contains("fifth"));
        Assert.DoesNotContain("fifth", mirror.Text);
    }

    [Theory]
    [InlineData(TerminalSnapshotFormat.Native)]
    [InlineData(TerminalSnapshotFormat.Vt)]
    public async Task A_tap_taken_while_output_pours_in_misses_and_repeats_nothing(TerminalSnapshotFormat format)
    {
        var wire = new Wire();
        await using var session = Session(wire);
        session.Start();
        var feeding = Task.Run(() =>
        {
            for (var i = 0; i < 400; i++) wire.Feed($"line {i:000} \u001b[3{i % 8}mcolored\u001b[0m text\r\n");
        }, TestContext.Current.CancellationToken);

        // Somewhere in the middle of the flood.
        await Until(() => session.ReadText().Contains("line 0"));
        using var mirror = new Mirror();
        using var tap = session.Tap(mirror, format, out var start);
        mirror.Begin(start);
        await feeding;
        await Until(() => session.ReadText().Contains("line 399"));

        // Positions were contiguous (the mirror asserts it) and both terminals show the same scrollback and screen.
        Assert.Equal(session.ReadText(), mirror.Text);
    }

    [Theory]
    [InlineData(TerminalSnapshotFormat.Native)]
    [InlineData(TerminalSnapshotFormat.Vt)]
    public async Task The_snapshot_carries_a_full_screen_program_and_its_input_modes(TerminalSnapshotFormat format)
    {
        var wire = new Wire();
        await using var session = Session(wire);
        session.Start();
        // What an editor does: the alternate screen, application cursor keys, bracketed paste, mouse reports, then its
        // own screen with the cursor parked somewhere.
        wire.Feed("shell line before\r\n\u001b[?1049h\u001b[?1h\u001b[?2004h\u001b[?1000h\u001b[?1006h"
            + "\u001b[2J\u001b[1;1H~ editor top\u001b[12;1H-- INSERT --\u001b[5;10Hcursor here");
        await Until(() => session.ReadText().Contains("cursor here"));

        using var mirror = new Mirror();
        using var tap = session.Tap(mirror, format, out var start);
        mirror.Begin(start);

        var modes = session.ReadInputModes();
        Assert.True(modes.AlternateScreen);
        Assert.Equal(modes, mirror.Modes);
        Assert.Equal(session.ReadText(), mirror.Text);

        // Typing goes on where the cursor was.
        wire.Feed("!");
        await Until(() => session.ReadText().Contains("cursor here!"));
        Assert.Equal(session.ReadText(), mirror.Text);

        // Leaving the editor shows the shell's screen again. Only the library's own snapshot carries that screen: the
        // VT text has the screen in use alone, so its mirror comes back to an empty one.
        wire.Feed("\u001b[?1049l");
        await Until(() => !session.ReadInputModes().AlternateScreen);
        Assert.False(mirror.Modes.AlternateScreen);
        Assert.Contains("shell line before", session.ReadText());
        if (format == TerminalSnapshotFormat.Native) Assert.Equal(session.ReadText(), mirror.Text);
        else Assert.DoesNotContain("editor top", mirror.Text);
    }

    [Fact]
    public async Task A_resize_and_a_restart_reach_the_tap_in_order_each_at_its_own_position()
    {
        var wire = new Wire();
        await using var session = Session(wire);
        session.Start();
        using var mirror = new Mirror();
        using var tap = session.Tap(mirror, TerminalSnapshotFormat.Native, out var start);
        mirror.Begin(start);
        Assert.Equal(0UL, start.Position);

        wire.Feed("abc");
        await Until(() => session.ReadText().Contains("abc"));
        session.Resize(new TerminalSize(40, 10, 0, 0));
        Assert.Equal(new TerminalSize(40, 10, 0, 0), mirror.Size);
        wire.Feed("de");
        await Until(() => session.ReadText().Contains("abcde"));

        Assert.Equal([(0UL, "output"), (3UL, "resize"), (4UL, "output")], mirror.Events);
        Assert.Equal(session.ReadText(), mirror.Text);
    }

    [Fact]
    public async Task A_mirror_does_not_answer_the_programs_queries_but_still_sends_typed_text()
    {
        // "Who are you" (device attributes) and "where is the cursor": a terminal answers both.
        const string queries = "\u001b[c\u001b[6n";

        var owner = new Wire();
        await using var session = Session(owner);
        session.Start();
        owner.Feed(queries + "x");
        await Until(() => session.ReadText().Contains('x'));
        await Until(() => owner.Written.Length > 0);
        Assert.StartsWith("\u001b[", owner.Written);

        var other = new Wire();
        await using var mirror = Session(other, mirror: true);
        mirror.Start();
        other.Feed(queries + "x");
        await Until(() => mirror.ReadText().Contains('x'));
        mirror.Send("typed");
        await Until(() => other.Written.Length > 0);
        Assert.Equal("typed", other.Written);
    }

    [Fact]
    public async Task The_registry_starts_a_session_once_and_closing_it_ends_the_process()
    {
        var wire = new Wire();
        var registry = new TerminalSessionRegistry();
        var changes = new List<string>();
        registry.Changed += changes.Add;
        var size = new TerminalSize(80, 24, 0, 0);

        var first = registry.GetOrStart("item", Spec, wire, size, TerminalPalettes.Dark, ColorScheme.Dark, 1000, out var created);
        Assert.True(created);
        Assert.True(first.IsRunning);
        var again = registry.GetOrStart("item", Spec, wire, size, TerminalPalettes.Dark, ColorScheme.Dark, 1000, out created);
        Assert.False(created);
        Assert.Same(first, again);
        Assert.Equal(1, wire.Starts);
        Assert.Same(first, registry.Find("item"));
        Assert.Equal(["item"], registry.Keys);

        // It runs with no view: output is parsed all the same.
        wire.Feed("still parsed");
        await Until(() => first.ReadText().Contains("still parsed"));

        // Closing on behalf of a session that is not the current one leaves the current one alone.
        await using (var stranger = Session(new Wire()))
        {
            await registry.CloseAsync("item", stranger);
        }
        Assert.Same(first, registry.Find("item"));

        await registry.CloseAsync("item");
        Assert.Null(registry.Find("item"));
        Assert.True(wire.Disposed);
        Assert.Equal(["item", "item"], changes);
        await registry.CloseAsync("item"); // nothing there: nothing happens
    }

    [Fact]
    public async Task A_session_remembers_its_title_and_how_it_ended_for_a_view_that_comes_later()
    {
        var wire = new Wire();
        await using var session = Session(wire);
        session.Start();
        wire.Feed("\u001b]2;build watcher\u0007ready");
        await Until(() => session.ReadText().Contains("ready"));
        // Both are known without anything running the UI thread's queue: a session with no view has nobody to do that.
        Assert.Equal("build watcher", session.Title);
        Assert.Null(session.LastExit);

        await wire.DisposeAsync();
        await Until(() => session.LastExit is not null);
        Assert.Equal(0, session.LastExit!.Value.ExitCode);
    }
}

/// <summary>One terminal, several views: who decides its size, and how a mirror follows the terminal it shows (design/12-远程操作.md §6.5, §4.4).</summary>
[Collection(TerminalSessionCollection.Name)]
public class SessionSizeAndMirrorTests
{
    private sealed class Wire : ITerminalMirrorConnection, ITerminalConnectionFactory
    {
        private readonly System.Threading.Channels.Channel<byte[]> _output = System.Threading.Channels.Channel.CreateUnbounded<byte[]>();
        private readonly Queue<TerminalMirrorEvent> _events = new();
        private readonly TaskCompletionSource<TerminalExit> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<(int Columns, int Rows)> Resizes { get; } = [];
        public int ProcessId => 1;
        public Task<TerminalExit> Exited => _exit.Task;
        public ITerminalConnection Start(LaunchSpec spec, int columns, int rows) => this;
        public void Feed(string text) => _output.Writer.TryWrite(System.Text.Encoding.UTF8.GetBytes(text));
        /// <summary>An event at this place in the output: queued, then announced with an empty chunk.</summary>
        public void Announce(TerminalMirrorEvent change)
        {
            lock (_events) _events.Enqueue(change);
            _output.Writer.TryWrite([]);
        }
        public TerminalMirrorEvent? TakeEvent()
        {
            lock (_events) return _events.Count > 0 ? _events.Dequeue() : null;
        }
        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadOutputAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var chunk in _output.Reader.ReadAllAsync(ct).ConfigureAwait(false)) yield return chunk;
        }
        public ValueTask WriteAsync(ReadOnlyMemory<byte> input, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask ResizeAsync(int columns, int rows, int pixelWidth, int pixelHeight)
        {
            lock (Resizes) Resizes.Add((columns, rows));
            return ValueTask.CompletedTask;
        }
        public ValueTask SignalAsync(TerminalSignal signal) => ValueTask.CompletedTask;
        public ValueTask<string?> GetForegroundProcessNameAsync() => ValueTask.FromResult<string?>(null);
        public ValueTask DisposeAsync()
        {
            _output.Writer.TryComplete();
            _exit.TrySetResult(new TerminalExit(0, null));
            return ValueTask.CompletedTask;
        }
    }

    private static readonly LaunchSpec Spec = new("zsh", [], null, new Dictionary<string, string>());

    private static TerminalSession Session(Wire wire, bool mirror = false) =>
        new(Spec, wire, new TerminalSize(80, 24, 0, 0), TerminalPalettes.Dark, ColorScheme.Dark, 1000, () => false) { IsMirror = mirror };

    private static async Task Until(Func<bool> done)
    {
        for (var i = 0; i < 200 && !done(); i++) await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.True(done());
    }

    [Fact]
    public async Task Only_the_view_that_has_the_say_changes_the_size()
    {
        var wire = new Wire();
        await using var session = Session(wire);
        session.Start();
        var phone = new object();
        var changes = 0;
        session.SizeOwnerChanged += () => changes++;

        // Nobody claimed it: this app's own view decides.
        Assert.Null(session.SizeOwner);
        Assert.True(session.Resize(new TerminalSize(100, 30, 0, 0)));
        Assert.False(session.Resize(new TerminalSize(40, 20, 0, 0), phone));
        Assert.Equal(100, session.Size.Columns);

        // The phone takes it: the terminal has the phone's size, and the window's own resizes do nothing.
        session.ClaimSize(phone, new TerminalSize(40, 20, 0, 0));
        Assert.Same(phone, session.SizeOwner);
        Assert.Equal((40, 20), (session.Size.Columns, session.Size.Rows));
        Assert.False(session.Resize(new TerminalSize(120, 40, 0, 0)));
        Assert.True(session.Resize(new TerminalSize(42, 21, 0, 0), phone));
        Assert.Equal(42, session.Size.Columns);

        // Somebody else letting go changes nothing; the phone letting go gives it back to the window.
        session.ReleaseSize(new object());
        Assert.Same(phone, session.SizeOwner);
        session.ReleaseSize(phone);
        Assert.Null(session.SizeOwner);
        Assert.True(session.Resize(new TerminalSize(120, 40, 0, 0)));
        Assert.Equal(2, changes);

        // The window takes it back while the phone has it.
        session.ClaimSize(phone, new TerminalSize(40, 20, 0, 0));
        session.ClaimSize(null, new TerminalSize(110, 35, 0, 0));
        Assert.Null(session.SizeOwner);
        Assert.Equal(110, session.Size.Columns);
        await Until(() => { lock (wire.Resizes) return wire.Resizes.Contains((110, 35)); });
    }

    [Fact]
    public async Task A_mirror_asks_for_a_size_and_takes_the_one_it_is_told()
    {
        var wire = new Wire();
        await using var mirror = Session(wire, mirror: true);
        mirror.Start();

        // Its view wants another size: that is only asked for, the grid stays.
        Assert.True(mirror.Resize(new TerminalSize(50, 15, 0, 0)));
        await Until(() => { lock (wire.Resizes) return wire.Resizes.Contains((50, 15)); });
        Assert.Equal(80, mirror.Size.Columns);

        // The owning side says what the size now is, at a place in the output: the text after it wraps at that width.
        wire.Feed("before ");
        wire.Announce(new TerminalMirrorEvent(TerminalMirrorEventKind.Resize, 10, 5));
        wire.Feed("0123456789abc");
        await Until(() => mirror.ReadText().Contains("abc"));
        Assert.Equal((10, 5), (mirror.Size.Columns, mirror.Size.Rows));
        Assert.Equal((10, 5), mirror.Read(core => (core.Size.Columns, core.Size.Rows)));

        // A restart on the owning side clears the mirror too.
        wire.Announce(new TerminalMirrorEvent(TerminalMirrorEventKind.Reset));
        wire.Feed("fresh");
        await Until(() => mirror.ReadText().Contains("fresh"));
        Assert.DoesNotContain("before", mirror.ReadText());
    }

    [Fact]
    public async Task A_mirror_takes_over_a_whole_terminal_from_its_snapshot()
    {
        // The owning side: a shell line, then an editor on the alternate screen.
        var ownerWire = new Wire();
        await using var owner = Session(ownerWire);
        owner.Start();
        owner.Resize(new TerminalSize(60, 12, 0, 0));
        ownerWire.Feed("shell line before\r\n\u001b[?1049h\u001b[2J\u001b[1;1Heditor screen");
        await Until(() => owner.ReadText().Contains("editor screen"));
        var snapshot = owner.Read(core => core.EncodeSnapshot());

        var wire = new Wire();
        await using var mirror = Session(wire, mirror: true);
        mirror.Start();
        wire.Feed("stale text of an earlier attachment");
        wire.Announce(new TerminalMirrorEvent(TerminalMirrorEventKind.Snapshot, Snapshot: snapshot));
        wire.Feed(" + typed after");
        await Until(() => mirror.ReadText().Contains("typed after"));

        Assert.Contains("editor screen + typed after", mirror.ReadText());
        Assert.DoesNotContain("stale", mirror.ReadText());
        Assert.Equal((60, 12), (mirror.Size.Columns, mirror.Size.Rows));
        Assert.True(mirror.ReadInputModes().AlternateScreen);
        // Leaving the editor shows the shell's screen: the snapshot carried it.
        wire.Feed("\u001b[?1049l");
        await Until(() => !mirror.ReadInputModes().AlternateScreen);
        Assert.Contains("shell line before", mirror.ReadText());
    }
}

[Collection(TerminalSessionCollection.Name)]
public class SessionSizeHandoverTests
{
    private sealed class Quiet : ITerminalConnection, ITerminalConnectionFactory
    {
        private readonly TaskCompletionSource<TerminalExit> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ProcessId => 1;
        public Task<TerminalExit> Exited => _exit.Task;
        public ITerminalConnection Start(LaunchSpec spec, int columns, int rows) => this;
        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadOutputAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.WhenAny(_exit.Task, Task.Delay(Timeout.Infinite, ct)).ConfigureAwait(false);
            yield break;
        }
        public ValueTask WriteAsync(ReadOnlyMemory<byte> input, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask ResizeAsync(int columns, int rows, int pixelWidth, int pixelHeight) => ValueTask.CompletedTask;
        public ValueTask SignalAsync(TerminalSignal signal) => ValueTask.CompletedTask;
        public ValueTask<string?> GetForegroundProcessNameAsync() => ValueTask.FromResult<string?>(null);
        public ValueTask DisposeAsync()
        {
            _exit.TrySetResult(new TerminalExit(0, null));
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>The view that has the say leaves: it goes to the one that had it before, then to the window (design/12-远程操作.md §6.5).</summary>
    [Fact]
    public async Task When_the_view_with_the_say_leaves_it_goes_back_to_the_one_before()
    {
        await using var session = new TerminalSession(new LaunchSpec("zsh", [], null, new Dictionary<string, string>()), new Quiet(),
            new TerminalSize(80, 24, 0, 0), TerminalPalettes.Dark, ColorScheme.Dark, 100, () => false);
        session.Start();
        object laptop = new(), phone = new();

        session.ClaimSize(laptop, new TerminalSize(100, 30, 0, 0));
        session.ClaimSize(phone, new TerminalSize(40, 20, 0, 0));
        Assert.Same(phone, session.SizeOwner);

        session.ReleaseSize(phone);
        Assert.Same(laptop, session.SizeOwner);
        // The laptop's view applies its size again when told (here: by hand).
        Assert.True(session.Resize(new TerminalSize(100, 30, 0, 0), laptop));

        // A view that leaves without having the say changes nothing.
        session.ClaimSize(phone, new TerminalSize(40, 20, 0, 0));
        session.ReleaseSize(laptop);
        Assert.Same(phone, session.SizeOwner);
        session.ReleaseSize(phone);
        Assert.Null(session.SizeOwner);
    }
}
