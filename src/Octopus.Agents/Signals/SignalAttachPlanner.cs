using System.Security.Cryptography;
using System.Text;
using Octopus.Core.Agents;
using Octopus.Platform;

namespace Octopus.Agents.Signals;

/// <summary>
/// Turns a definition's <c>signals</c> block into the extra arguments and environment variables of one launch
/// (design/plugins/11 §1.2–1.4). Files it needs (Claude settings, plugin directories, the OpenCode plugin) are
/// generated under Octopus' cache directory, named by a hash of their content so concurrent launches share them and
/// a changed forwarder path gets new ones. The user's own configuration is only ever read, to chain the command in
/// a single slot.
/// </summary>
public sealed class SignalAttachPlanner : ISignalAttachPlanner
{
    public const string EndpointVariable = "OCTOPUS_SIGNAL_ENDPOINT";
    public const string TokenVariable = "OCTOPUS_SIGNAL_TOKEN";
    public const string ItemVariable = "OCTOPUS_ITEM_ID";
    public const string AccountVariable = "OCTOPUS_SIGNAL_ACCOUNT_VAR";
    public const string ChainVariable = "OCTOPUS_SIGNAL_CHAIN";

    /// <summary>A status line fires several times a second while a turn runs (research/claude §6); 2 s is plenty.</summary>
    public const int StatusLineMinIntervalMs = 2000;

    /// <summary>The forwarder returns in milliseconds; this only bounds the damage if something hangs.</summary>
    public const int HookTimeoutSeconds = 10;

    internal const string AttachDirectoryName = "signal-attach";

    private readonly IAppPaths _paths;

    public SignalAttachPlanner(IAppPaths paths) => _paths = paths;

    public SignalAttachment Plan(SignalAttachRequest request)
    {
        var signals = request.Definition.Signals;
        if (!request.Enabled
            || signals is null
            || signals.Attach.Count == 0
            || request.Endpoint is not { Address.Length: > 0 } endpoint
            || string.IsNullOrWhiteSpace(request.ForwarderPath)
            || !AgentVersions.IsAtLeast(request.InstalledVersion, signals.MinVersion))
        {
            return SignalAttachment.None;
        }

        var context = new Context(this, request);
        var arguments = new List<string>();
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var attach in signals.Attach)
        {
            if (context.TryExpand(attach, out var args, out var env))
            {
                arguments.AddRange(args);
                foreach (var (key, value) in env)
                {
                    environment[key] = value;
                }
            }
        }
        // Nothing attached means nothing will call the forwarder: keep the token out of the agent's environment.
        if (arguments.Count == 0 && environment.Count == 0)
        {
            return SignalAttachment.None;
        }

        environment[EndpointVariable] = endpoint.Address;
        environment[TokenVariable] = endpoint.Token;
        environment[ItemVariable] = request.ItemId;
        if (signals.Account is { Env.Length: > 0 } account)
        {
            environment[AccountVariable] = account.Env;
        }
        if (context.Chain is not null)
        {
            environment[ChainVariable] = context.Chain;
        }
        return new SignalAttachment(arguments, environment);
    }

    /// <summary>Writes <paramref name="content"/> unless the file already holds exactly that; null when it cannot.</summary>
    private string? EnsureFile(string relativePath, string content)
    {
        try
        {
            var root = AttachDirectory();
            var path = Path.Combine(root, relativePath);
            if (File.Exists(path) && File.ReadAllText(path) == content)
            {
                return path;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temp, path, overwrite: true);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// <c>&lt;cache&gt;/signal-attach</c>, private to the user: the agent runs the commands in these files, so nobody
    /// else may be able to change them.
    /// </summary>
    private string AttachDirectory()
    {
        var directory = Path.Combine(_paths.CacheDirectory, AttachDirectoryName);
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(directory);
        }
        else
        {
            const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            Directory.CreateDirectory(directory, Private);
            File.SetUnixFileMode(directory, Private);
        }
        return directory;
    }

    private static string ContentHash(params string[] parts)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\0', parts)));
        return Convert.ToHexStringLower(hash)[..16];
    }

    /// <summary>One launch's placeholders, each produced at most once and only when an attach entry uses it.</summary>
    private sealed class Context(SignalAttachPlanner planner, SignalAttachRequest request)
    {
        private static readonly string[] Placeholders =
            ["{signalSettings}", "{signalPluginDir}", "{signalConfig}", "{signalNotify}", "{forwarder}"];

        private readonly AgentDefinition _definition = request.Definition;
        private readonly AgentSignals _signals = request.Definition.Signals!;
        private readonly Dictionary<string, string?> _values = new(StringComparer.Ordinal);
        /// <summary>Placeholder to the user's command in the slot it takes over; adopted once its attach entry is used.</summary>
        private readonly Dictionary<string, string> _chains = new(StringComparer.Ordinal);
        private readonly HashSet<string> _used = new(StringComparer.Ordinal);
        private readonly bool _windows = OperatingSystem.IsWindows();
        private string? _accountDirectory;
        private string? _producing;

        /// <summary>The user's chained command, set when the slot it occupies is taken over.</summary>
        public string? Chain { get; private set; }

        /// <summary>Records the user's command for the slot the placeholder being produced takes over.</summary>
        private void OffersChain(UserSlotCommand? user)
        {
            if (user is not null && _producing is not null)
            {
                _chains[_producing] = user.ToChainJson();
            }
        }

        private string Dialect => string.IsNullOrEmpty(_signals.Dialect) ? _definition.Id : _signals.Dialect!;

        private string AccountDirectory => _accountDirectory ??= SignalAccounts.ResolveDirectory(
            _definition,
            _signals.Account is { Env.Length: > 0 } account && request.AgentEnvironment?.TryGetValue(account.Env, out var value) == true ? value : null,
            SignalAccounts.HomeOf(request.AgentEnvironment));

        public bool TryExpand(SignalAttachSpec attach, out List<string> args, out Dictionary<string, string> env)
        {
            _used.Clear();
            if (!TryExpandCore(attach, out args, out env))
            {
                return false;
            }
            foreach (var placeholder in _used)
            {
                if (_chains.TryGetValue(placeholder, out var chain))
                {
                    Chain = chain;
                }
            }
            return true;
        }

        private bool TryExpandCore(SignalAttachSpec attach, out List<string> args, out Dictionary<string, string> env)
        {
            args = [];
            env = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var template in attach.Args ?? [])
            {
                if (Substitute(template) is not { } arg)
                {
                    return false;
                }
                args.Add(arg);
            }
            foreach (var (name, template) in attach.Env ?? [])
            {
                if (Substitute(template) is not { } value)
                {
                    return false;
                }
                // The user may already set the same overlay variable; merge rather than silently replace theirs.
                if (request.AgentEnvironment?.TryGetValue(name, out var existing) == true && !string.IsNullOrWhiteSpace(existing))
                {
                    if (!template.Contains("{signalConfig}", StringComparison.Ordinal)
                        || SignalAttachContent.MergeJsonObjects(existing, value) is not { } merged)
                    {
                        return false;
                    }
                    value = merged;
                }
                env[name] = value;
            }
            return args.Count > 0 || env.Count > 0;
        }

        /// <summary>The template with every placeholder filled; null when one of them is not available.</summary>
        private string? Substitute(string template)
        {
            var result = template;
            foreach (var placeholder in Placeholders)
            {
                if (!result.Contains(placeholder, StringComparison.Ordinal))
                {
                    continue;
                }
                if (!_values.TryGetValue(placeholder, out var value))
                {
                    _producing = placeholder;
                    value = Produce(placeholder);
                    _producing = null;
                    _values[placeholder] = value;
                }
                if (value is null)
                {
                    return null;
                }
                _used.Add(placeholder);
                result = result.Replace(placeholder, value, StringComparison.Ordinal);
            }
            return result;
        }

        private string? Produce(string placeholder) => placeholder switch
        {
            "{forwarder}" => request.ForwarderPath,
            "{signalSettings}" => ClaudeSettings(),
            "{signalPluginDir}" => PluginDirectory(),
            "{signalConfig}" => Dialect switch
            {
                "grok" => GrokOverlay(),
                "opencode" => OpenCodeConfig(),
                _ => null,
            },
            "{signalNotify}" => Notify(),
            _ => null,
        };

        private KeyValuePair<string, SignalChannelSpec>? FirstChannel(string via) =>
            _signals.Channels.Where(c => c.Value.Via == via).Select(c => (KeyValuePair<string, SignalChannelSpec>?)c).FirstOrDefault();

        private List<GeneratedHook> Hooks(string? reply)
        {
            var hooks = new List<GeneratedHook>();
            foreach (var (name, channel) in _signals.Channels.OrderBy(c => c.Key, StringComparer.Ordinal))
            {
                if (channel.Via != SignalVia.Hook || string.IsNullOrEmpty(channel.Event))
                {
                    continue;
                }
                List<string> args = ["hook", "--agent", _definition.Id, "--channel", name];
                if (reply is not null)
                {
                    args.AddRange(["--reply", reply]);
                }
                hooks.Add(new GeneratedHook(channel.Event, ForwarderCommand(args), string.IsNullOrEmpty(channel.Matcher) ? null : channel.Matcher));
            }
            return hooks;
        }

        private string StatusLineCommand(string channel) => ForwarderCommand(
            ["statusline", "--agent", _definition.Id, "--channel", channel, "--min-interval", StatusLineMinIntervalMs.ToString(System.Globalization.CultureInfo.InvariantCulture)]);

        /// <summary>
        /// Claude Code runs hooks and the status line with Git Bash on Windows as well, so its commands always get
        /// POSIX quoting; other agents use the platform's.
        /// </summary>
        private string ForwarderCommand(IEnumerable<string> args) =>
            SignalAttachContent.CommandLine(request.ForwarderPath, args, _windows && Dialect != "claude");

        private static int? SmallerInterval(int? ours, int? theirs) =>
            (ours, theirs) switch
            {
                ({ } a, { } b) => Math.Min(a, b),
                ({ } a, null) => a,
                (null, { } b) => b,
                _ => null,
            };

        private string? ClaudeSettings()
        {
            if (Dialect != "claude" || FirstChannel(SignalVia.StatusLine) is not { } statusLine)
            {
                return null;
            }
            var user = statusLine.Value.ChainUserCommand ? UserSlotCommands.ClaudeStatusLine(AccountDirectory, request.WorkingDirectory) : null;
            var content = SignalAttachContent.ClaudeSettings(
                StatusLineCommand(statusLine.Key),
                SmallerInterval(statusLine.Value.RefreshInterval, user?.RefreshInterval),
                user);
            var path = planner.EnsureFile($"{Dialect}-settings-{ContentHash(content)}.json", content);
            if (path is not null)
            {
                OffersChain(user);
            }
            return path;
        }

        private string? PluginDirectory()
        {
            var cursor = Dialect == "cursor";
            if (Dialect != "claude" && !cursor)
            {
                return null;
            }
            // Cursor blocks a prompt only on "continue": false; "{}" is a valid no-op reply for every event.
            var hooks = Hooks(cursor ? "{}" : null);
            if (hooks.Count == 0)
            {
                return null;
            }
            var manifest = SignalAttachContent.PluginManifest();
            var hooksJson = cursor
                ? SignalAttachContent.CursorHooks(hooks, HookTimeoutSeconds)
                : SignalAttachContent.ClaudeHooks(hooks, HookTimeoutSeconds);
            var name = $"{Dialect}-plugin-{ContentHash(manifest, hooksJson)}";
            var manifestDirectory = cursor ? ".cursor-plugin" : ".claude-plugin";
            if (planner.EnsureFile(Path.Combine(name, manifestDirectory, "plugin.json"), manifest) is null
                || planner.EnsureFile(Path.Combine(name, "hooks", "hooks.json"), hooksJson) is not { } hooksPath)
            {
                return null;
            }
            return Path.GetDirectoryName(Path.GetDirectoryName(hooksPath));
        }

        private string? GrokOverlay()
        {
            var statusLine = FirstChannel(SignalVia.StatusLine);
            var hooks = Hooks(null);
            if (statusLine is null && hooks.Count == 0)
            {
                return null;
            }
            UserSlotCommand? user = null;
            if (statusLine is { Value.ChainUserCommand: true } && AccountDirectory.Length > 0)
            {
                user = UserSlotCommands.GrokStatusLine(AccountDirectory);
            }
            var overlay = SignalAttachContent.GrokOverlay(
                statusLine is { } s ? StatusLineCommand(s.Key) : null,
                statusLine?.Value.RefreshInterval,
                user?.Padding,
                hooks,
                HookTimeoutSeconds);
            OffersChain(user);
            return overlay;
        }

        private string? OpenCodeConfig()
        {
            var channels = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (name, channel) in _signals.Channels)
            {
                if (channel.Via == SignalVia.Plugin && !string.IsNullOrEmpty(channel.Event))
                {
                    channels[channel.Event] = name;
                }
            }
            if (channels.Count == 0)
            {
                return null;
            }
            var script = SignalAttachContent.OpenCodePlugin(_definition.Id, channels);
            var path = planner.EnsureFile($"{Dialect}-signal-{ContentHash(script)}.js", script);
            return path is null ? null : SignalAttachContent.OpenCodeConfig(new Uri(path).AbsoluteUri);
        }

        private string? Notify()
        {
            if (FirstChannel(SignalVia.Notify) is not { } notify)
            {
                return null;
            }
            if (notify.Value.ChainUserCommand && AccountDirectory.Length > 0)
            {
                OffersChain(UserSlotCommands.CodexNotify(AccountDirectory));
            }
            return SignalAttachContent.TomlArray([request.ForwarderPath, "notify", "--agent", _definition.Id, "--channel", notify.Key]);
        }
    }
}
