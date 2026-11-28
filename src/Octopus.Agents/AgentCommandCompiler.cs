using Octopus.Agents.Providers;
using Octopus.Core.Agents;
using Octopus.Platform;

namespace Octopus.Agents;

/// <summary>Compiles a definition, a profile and launch options into an executable plus an argument array. Pure.</summary>
public sealed class AgentCommandCompiler : IAgentCommandCompiler
{
    private const string FastSuffix = "-fast";
    private static readonly string[] KnownEffortSuffixes = ["-low", "-medium", "-high", "-xhigh", "-max"];

    private readonly IShellCommandBuilder _shell;

    public AgentCommandCompiler(IShellCommandBuilder shell) => _shell = shell;

    public AgentCommand Compile(AgentCompileRequest request)
    {
        var def = request.Definition;
        var profile = request.Profile;
        var launch = request.Launch;
        var caps = def.Capabilities;
        var args = new List<string>();
        var env = new Dictionary<string, string>(StringComparer.Ordinal);

        // Executable: the profile's command (may carry arguments), else the detected file name, else the first name.
        string executable;
        if (!string.IsNullOrWhiteSpace(profile.Command))
        {
            var parts = _shell.Split(profile.Command);
            if (parts.Count > 0)
            {
                executable = parts[0];
                args.AddRange(parts.Skip(1));
            }
            else
            {
                executable = DefaultExecutable(def, request.InstalledPath);
            }
        }
        else
        {
            executable = DefaultExecutable(def, request.InstalledPath);
        }

        args.AddRange(caps.BaseArgs);
        foreach (var (k, v) in caps.BaseEnv)
        {
            env[k] = v;
        }
        if (request.AccountEnvironment is { } accountEnv)
        {
            foreach (var (k, v) in accountEnv)
            {
                env[k] = v;
            }
        }
        if (request.Signals is { } signals)
        {
            args.AddRange(signals.Arguments);
        }

        var model = Normalize(launch?.Model ?? profile.Model);
        var effort = Normalize(launch?.Effort ?? profile.Effort);
        var fast = launch?.Fast ?? profile.Fast;

        // Effort only counts when the (possibly model-narrowed) options contain it.
        if (effort is not null && caps.Effort is not null
            && !EffortOptionsFor(def, model).Any(o => o.Id == effort))
        {
            effort = null;
        }
        if (caps.Effort is null)
        {
            effort = null;
        }

        // Model, with params that the CLI wants inside the model id.
        if (caps.Model is { } modelCap && model is not null)
        {
            var modelParams = new List<string>();
            if (!model.Contains('[', StringComparison.Ordinal))
            {
                if (effort is not null
                    && !string.IsNullOrEmpty(caps.Effort?.ModelParam)
                    && !ModelEncodesEffort(model, effort))
                {
                    modelParams.Add(Substitute(caps.Effort.ModelParam, effort));
                }
                if (fast == true
                    && !string.IsNullOrEmpty(caps.Fast?.ModelParam)
                    && !ModelEncodesFast(model))
                {
                    modelParams.Add(Substitute(caps.Fast.ModelParam, "true"));
                }
            }
            var value = modelParams.Count > 0 ? $"{model}[{string.Join(',', modelParams)}]" : model;
            if (!string.IsNullOrWhiteSpace(request.ModelPrefix)
                && !value.StartsWith(request.ModelPrefix, StringComparison.OrdinalIgnoreCase))
            {
                value = request.ModelPrefix + value;
            }
            var option = modelCap.Options.FirstOrDefault(o => o.Id == model);
            AddTemplate(args, option?.Args ?? modelCap.Args, value);
        }

        if (effort is not null && caps.Effort is { } effortCap)
        {
            var option = effortCap.Options.FirstOrDefault(o => o.Id == effort);
            AddTemplate(args, option?.Args ?? effortCap.Args, effort);
        }

        if (fast == true && caps.Fast is { } fastCap)
        {
            AddTemplate(args, fastCap.Args, "true");
        }

        var permission = ResolvePermission(def, profile, request.GlobalPermission);
        if (!string.IsNullOrEmpty(launch?.Permission) && caps.Permission?.Modes.ContainsKey(launch.Permission) == true)
        {
            permission = launch.Permission;
        }
        if (permission is not null && caps.Permission?.Modes.TryGetValue(permission, out var spec) == true)
        {
            args.AddRange(spec.Args);
            if (spec.Env is not null)
            {
                foreach (var (k, v) in spec.Env)
                {
                    env[k] = v;
                }
            }
        }

        foreach (var feature in caps.Features)
        {
            ApplyFeature(feature, profile, args, env);
        }

        var variadicAdditionalDirs = ApplyAdditionalDirs(caps.AdditionalDirs, request.AdditionalDirectories, args);

        if (caps.ExtraArgs)
        {
            args.AddRange(profile.ExtraArgs);
        }

        // A chosen session is continued by its id, also when a restored tab would otherwise continue the latest.
        var byId = !string.IsNullOrWhiteSpace(launch?.SessionId) && caps.Resume?.ById is { Count: > 0 };
        var resume = !byId && launch?.Resume == true && caps.Resume?.ContinueLatest is { Count: > 0 };
        if (byId)
        {
            foreach (var arg in caps.Resume!.ById!) args.Add(arg.Replace("{id}", launch!.SessionId, StringComparison.Ordinal));
        }
        else if (resume)
        {
            args.AddRange(caps.Resume!.ContinueLatest!);
        }
        else if (caps.Prompt is { } promptCap && !string.IsNullOrWhiteSpace(launch?.Prompt))
        {
            var prompt = launch!.Prompt!;
            if (string.Equals(promptCap.Mode, "flag", StringComparison.OrdinalIgnoreCase))
            {
                AddTemplate(args, promptCap.Args, prompt);
            }
            else
            {
                if (promptCap.Separator || variadicAdditionalDirs)
                {
                    args.Add("--");
                }
                args.Add(prompt);
            }
        }

        foreach (var (k, v) in profile.Env)
        {
            env[k] = v;
        }
        if (launch?.Env is { } own)
        {
            foreach (var (k, v) in own)
            {
                env[k] = v;
            }
        }

        // Last: an overlay variable the user also sets was already merged with theirs by the planner.
        if (request.Signals is { } attached)
        {
            foreach (var (k, v) in attached.Environment)
            {
                if (string.Equals(k, ProviderMaterializer.OpenCodeConfigContentEnv, StringComparison.Ordinal)
                    && env.TryGetValue(k, out var existingOpenCode)
                    && !string.IsNullOrWhiteSpace(existingOpenCode))
                {
                    env[k] = ProviderMaterializer.MergeOpenCodeConfigJson(existingOpenCode, v);
                }
                else
                {
                    env[k] = v;
                }
            }
        }

        IReadOnlyList<string>? unsets = null;
        if (request.UnsetEnvironment is { Count: > 0 } requestedUnsets)
        {
            var filtered = new List<string>();
            foreach (var name in requestedUnsets)
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                // Never unset a variable the user explicitly configured in Profile.Env, Launch.Env, or BaseEnv.
                if (env.ContainsKey(name)) continue;
                if (!filtered.Contains(name, StringComparer.Ordinal))
                {
                    filtered.Add(name);
                }
            }
            if (filtered.Count > 0)
            {
                unsets = filtered;
            }
        }

        return new AgentCommand(executable, args, env, unsets);
    }

    public string Preview(AgentCompileRequest request)
    {
        var command = Compile(request);
        return _shell.Build(command.Executable, command.Arguments, ShellKind.Posix);
    }

    public string? ResolvePermission(AgentDefinition definition, AgentProfile profile, string globalPermission)
    {
        var cap = definition.Capabilities.Permission;
        if (cap is null)
        {
            return null;
        }
        var wanted = string.IsNullOrEmpty(profile.Permission) ? globalPermission : profile.Permission;
        return !string.IsNullOrEmpty(wanted) && cap.Modes.ContainsKey(wanted) ? wanted : cap.Default;
    }

    public IReadOnlyList<AgentOption> EffortOptionsFor(AgentDefinition definition, string? model)
    {
        var effort = definition.Capabilities.Effort;
        if (effort is null)
        {
            return [];
        }
        if (effort.PerModel is null || effort.PerModel.Count == 0)
        {
            return effort.Options;
        }
        List<string>? allowed = null;
        if (!string.IsNullOrEmpty(model) && effort.PerModel.TryGetValue(model, out var exact))
        {
            allowed = exact;
        }
        else if (effort.PerModel.TryGetValue("*", out var wildcard))
        {
            allowed = wildcard;
        }
        return allowed is null ? effort.Options : effort.Options.Where(o => allowed.Contains(o.Id)).ToList();
    }

    private static string DefaultExecutable(AgentDefinition def, string? installedPath)
    {
        if (!string.IsNullOrWhiteSpace(installedPath))
        {
            var i = installedPath.LastIndexOfAny(['/', '\\']);
            var name = i >= 0 ? installedPath[(i + 1)..] : installedPath;
            if (name.Length > 0)
            {
                return name;
            }
        }
        return def.Detect.Executables.Count > 0 ? def.Detect.Executables[0] : def.Id;
    }

    private static void ApplyFeature(AgentFeature feature, AgentProfile profile, List<string> args, Dictionary<string, string> env)
    {
        if (!profile.Features.TryGetValue(feature.Id, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return;
        }

        switch (feature.Type)
        {
            case "bool":
                if (!string.Equals(raw.Trim(), "true", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
                AddTemplate(args, feature.Args, "true");
                if (!string.IsNullOrEmpty(feature.Env))
                {
                    env[feature.Env] = string.IsNullOrEmpty(feature.EnvValue) ? "true" : feature.EnvValue;
                }
                break;
            case "list":
                var items = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (items.Length == 0)
                {
                    return;
                }
                foreach (var item in items)
                {
                    AddTemplate(args, feature.Args, item);
                }
                if (!string.IsNullOrEmpty(feature.Env))
                {
                    env[feature.Env] = string.Join(',', items);
                }
                break;
            default:
                AddTemplate(args, feature.Args, raw);
                if (!string.IsNullOrEmpty(feature.Env))
                {
                    env[feature.Env] = raw;
                }
                break;
        }
    }

    private static bool ApplyAdditionalDirs(
        AdditionalDirsCapability? capability,
        IReadOnlyList<string>? directories,
        List<string> args)
    {
        if (capability is null || capability.Args.Count == 0 || directories is null || directories.Count == 0)
        {
            return false;
        }
        var items = directories
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Select(d => d.Trim())
            .ToList();
        if (capability.MaxCount is > 0 and var cap && items.Count > cap)
        {
            items.RemoveRange(cap, items.Count - cap);
        }
        if (items.Count == 0)
        {
            return false;
        }

        var isVariadic = false;
        if (string.Equals(capability.Mode, "variadic", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var part in capability.Args)
            {
                if (part.Contains("{value}", StringComparison.Ordinal))
                {
                    foreach (var item in items)
                    {
                        args.Add(Substitute(part, item));
                    }
                }
                else
                {
                    args.Add(part);
                }
            }
            isVariadic = true;
        }
        else if (string.Equals(capability.Mode, "comma", StringComparison.OrdinalIgnoreCase))
        {
            AddTemplate(args, capability.Args, string.Join(',', items));
        }
        else
        {
            foreach (var item in items)
            {
                AddTemplate(args, capability.Args, item);
            }
        }
        return isVariadic;
    }

    private static void AddTemplate(List<string> args, IEnumerable<string>? template, string value)
    {
        if (template is null)
        {
            return;
        }
        foreach (var part in template)
        {
            args.Add(Substitute(part, value));
        }
    }

    private static string Substitute(string template, string value) =>
        template.Replace("{value}", value, StringComparison.Ordinal);

    private static bool ModelEncodesEffort(string model, string effort)
    {
        var span = model.AsSpan();
        if (span.EndsWith(FastSuffix.AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            span = span[..^FastSuffix.Length];
        }
        foreach (var suffix in KnownEffortSuffixes)
        {
            if (span.EndsWith(suffix.AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return span.Length > effort.Length
            && span[^(effort.Length + 1)] == '-'
            && span[^effort.Length..].Equals(effort.AsSpan(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool ModelEncodesFast(string model) =>
        model.EndsWith(FastSuffix, StringComparison.OrdinalIgnoreCase);

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
