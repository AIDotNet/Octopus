using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using Octopus.Agents;
using Octopus.App.Localization;
using Octopus.Core.Agents;

namespace Octopus.App.Settings.Agents;

/// <summary>
/// What the agent editor holds: the common parts of a definition as form fields (name, id, executables, arguments,
/// model, prompt, resume, environment) and the whole definition as JSON for everything else. The form edits a copy of
/// the definition, so the blocks it has no field for (permission modes, effort, features, signals...) are kept as they
/// were. Switching to JSON writes the form into it; switching back reads it.
/// </summary>
public sealed partial class AgentEditorModel : ObservableObject
{
    public const string FormMode = "form";
    public const string JsonMode = "json";

    public const string PromptPositional = "positional";
    public const string PromptFlag = "flag";
    public const string PromptNone = "none";

    private readonly IAgentDefinitionStore _store;
    private readonly IReadOnlyCollection<string> _takenIds;
    private AgentDefinition _definition;

    /// <param name="existing">The agent to change; null for a new one.</param>
    /// <param name="takenIds">Ids a new agent may not use.</param>
    public AgentEditorModel(IAgentDefinitionStore store, AgentDefinition? existing, IReadOnlyCollection<string> takenIds)
    {
        _store = store;
        _takenIds = takenIds;
        IsNew = existing is null;
        _definition = existing is null
            ? new AgentDefinition
            {
                Capabilities = new AgentCapabilities { Prompt = new PromptCapability { Mode = PromptPositional } },
            }
            : store.Clone(existing);
        IsBuiltIn = existing is not null && store.IsBuiltIn(existing.Id);
        ReadForm();
    }

    public bool IsNew { get; }

    /// <summary>Editing an agent Octopus ships: saving writes the user's own version of it, which replaces the built-in one.</summary>
    public bool IsBuiltIn { get; }

    public bool CanEditId => IsNew;

    private string _mode = FormMode;

    /// <summary>
    /// <see cref="FormMode"/> or <see cref="JsonMode"/>. Switching to JSON writes the form into it; switching back reads
    /// it, and JSON that does not parse keeps the JSON view, with the reason in <see cref="Error"/>.
    /// </summary>
    public string Mode
    {
        get => _mode;
        set
        {
            if (value == _mode) return;
            if (value == JsonMode)
            {
                WriteForm();
                Json = _store.ToJson(_definition);
                Error = null;
            }
            else if (_mode == JsonMode && !ReadJson())
            {
                // Tell the switch it did not move.
                Avalonia.Threading.Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(Mode)));
                return;
            }
            _mode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsForm));
            OnPropertyChanged(nameof(IsJson));
        }
    }

    [ObservableProperty] public partial string Name { get; set; } = "";
    [ObservableProperty] public partial string Id { get; set; } = "";
    [ObservableProperty] public partial string Executables { get; set; } = "";
    [ObservableProperty] public partial string VersionArgs { get; set; } = "";
    [ObservableProperty] public partial string Homepage { get; set; } = "";
    [ObservableProperty] public partial string BaseArgs { get; set; } = "";
    [ObservableProperty] public partial string ModelArgs { get; set; } = "";
    [ObservableProperty] public partial string ModelOptions { get; set; } = "";
    [ObservableProperty] public partial bool AllowCustomModel { get; set; } = true;
    [ObservableProperty] public partial string PromptMode { get; set; } = PromptPositional;
    [ObservableProperty] public partial string PromptArgs { get; set; } = "";
    [ObservableProperty] public partial string ResumeArgs { get; set; } = "";
    [ObservableProperty] public partial bool ExtraArgs { get; set; } = true;
    [ObservableProperty] public partial string Environment { get; set; } = "";
    [ObservableProperty] public partial string Json { get; set; } = "";

    /// <summary>Why the agent cannot be saved as it is; null when it can.</summary>
    [ObservableProperty] public partial string? Error { get; set; }

    public bool HasError => Error is not null;
    public bool IsForm => Mode == FormMode;
    public bool IsJson => Mode == JsonMode;
    public bool ShowPromptArgs => PromptMode == PromptFlag;

    /// <summary>A new agent's id follows its name until the id is typed into.</summary>
    private bool _idTyped;
    private bool _settingId;

    partial void OnNameChanged(string value)
    {
        if (!IsNew || _idTyped) return;
        _settingId = true;
        Id = _store.SuggestId(value);
        _settingId = false;
    }

    partial void OnIdChanged(string value)
    {
        if (!_settingId) _idTyped = true;
    }

    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(HasError));
    partial void OnPromptModeChanged(string value) => OnPropertyChanged(nameof(ShowPromptArgs));

    /// <summary>The definition to save; null with <see cref="Error"/> set when something is missing or wrong.</summary>
    public AgentDefinition? Build()
    {
        if (IsJson && !ReadJson()) return null;
        if (IsForm) WriteForm();
        var definition = _store.Clone(_definition);
        Error = Validate(definition);
        return Error is null ? definition : null;
    }

    private string? Validate(AgentDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(definition.Name)) return AppLangResources.Get(AppLangResourceKind.AgentEditorNeedName);
        if (!_store.IsValidId(definition.Id)) return AppLangResources.Get(AppLangResourceKind.AgentEditorBadId);
        if (IsNew && _takenIds.Contains(definition.Id, StringComparer.OrdinalIgnoreCase)) return AppLangResources.Format(AppLangResourceKind.AgentEditorIdTaken, definition.Id);
        if (!IsNew && !string.Equals(definition.Id, _originalId, StringComparison.OrdinalIgnoreCase)) return AppLangResources.Get(AppLangResourceKind.AgentEditorIdChanged);
        if (definition.Detect.Executables.Count == 0) return AppLangResources.Get(AppLangResourceKind.AgentEditorNeedExecutable);
        var model = definition.Capabilities.Model;
        if (model is not null && model.Args.Count > 0 && !model.Args.Any(a => a.Contains("{value}", StringComparison.Ordinal)))
        {
            return AppLangResources.Get(AppLangResourceKind.AgentEditorModelNeedsValue);
        }
        if (definition.Capabilities.Prompt is { Mode: PromptFlag } prompt && (prompt.Args is null || !prompt.Args.Any(a => a.Contains("{value}", StringComparison.Ordinal))))
        {
            return AppLangResources.Get(AppLangResourceKind.AgentEditorPromptNeedsValue);
        }
        return null;
    }

    private string _originalId = "";

    private void ReadForm()
    {
        var d = _definition;
        _originalId = d.Id;
        _settingId = true;
        Name = d.Name;
        Id = d.Id;
        _settingId = false;
        _idTyped = !IsNew || d.Id.Length > 0;
        Executables = string.Join(' ', d.Detect.Executables);
        VersionArgs = ArgText.Join(d.Detect.VersionArgs);
        Homepage = d.Homepage ?? "";
        BaseArgs = ArgText.Join(d.Capabilities.BaseArgs);
        ModelArgs = ArgText.Join(d.Capabilities.Model?.Args ?? []);
        ModelOptions = string.Join('\n', d.Capabilities.Model?.Options.Select(o => o.Id) ?? []);
        AllowCustomModel = d.Capabilities.Model?.AllowCustom ?? true;
        PromptMode = d.Capabilities.Prompt is null ? PromptNone : d.Capabilities.Prompt.Mode == PromptFlag ? PromptFlag : PromptPositional;
        PromptArgs = ArgText.Join(d.Capabilities.Prompt?.Args ?? []);
        ResumeArgs = ArgText.Join(d.Capabilities.Resume?.ContinueLatest ?? []);
        ExtraArgs = d.Capabilities.ExtraArgs;
        Environment = string.Join('\n', d.Capabilities.BaseEnv.Select(p => $"{p.Key}={p.Value}"));
    }

    /// <summary>Puts the form's fields into the definition; the blocks without a field stay as they are.</summary>
    private void WriteForm()
    {
        var d = _definition;
        d.Name = Name.Trim();
        d.Id = Id.Trim();
        d.Detect.Executables = Executables.Split([' ', ',', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var version = ArgText.Split(VersionArgs);
        d.Detect.VersionArgs = version.Count > 0 ? version : ["--version"];
        d.Homepage = string.IsNullOrWhiteSpace(Homepage) ? null : Homepage.Trim();
        d.Capabilities.BaseArgs = ArgText.Split(BaseArgs);

        var modelArgs = ArgText.Split(ModelArgs);
        var options = ModelOptions.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (modelArgs.Count == 0 && options.Length == 0)
        {
            d.Capabilities.Model = null;
        }
        else
        {
            var model = d.Capabilities.Model ??= new ModelCapability();
            model.Args = modelArgs;
            model.AllowCustom = AllowCustomModel;
            // Options that stay keep their labels and notes.
            var old = model.Options.GroupBy(o => o.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            model.Options = options.Distinct(StringComparer.Ordinal).Select(id => old.TryGetValue(id, out var o) ? o : new AgentOption { Id = id }).ToList();
        }

        d.Capabilities.Prompt = PromptMode switch
        {
            PromptNone => null,
            PromptFlag => new PromptCapability { Mode = PromptFlag, Args = ArgText.Split(PromptArgs) },
            _ => new PromptCapability { Mode = PromptPositional, Separator = d.Capabilities.Prompt?.Separator ?? false },
        };
        var resume = ArgText.Split(ResumeArgs);
        var byId = d.Capabilities.Resume?.ById;
        d.Capabilities.Resume = resume.Count == 0 && byId is null ? null : new ResumeCapability { ContinueLatest = resume.Count == 0 ? null : resume, ById = byId };
        d.Capabilities.ExtraArgs = ExtraArgs;
        d.Capabilities.BaseEnv = Environment.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split('=', 2))
            .Where(p => p[0].Trim().Length > 0)
            .GroupBy(p => p[0].Trim(), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Last().Length > 1 ? g.Last()[1] : "", StringComparer.Ordinal);
    }

    private bool ReadJson()
    {
        var parsed = _store.Parse(Json, out var error);
        if (parsed is null)
        {
            Error = AppLangResources.Format(AppLangResourceKind.AgentEditorBadJson, error ?? "");
            return false;
        }
        var originalId = _originalId;
        _definition = parsed;
        ReadForm();
        _originalId = originalId;
        Error = null;
        return true;
    }
}

/// <summary>
/// Arguments as one line of text and back: split on spaces, with double or single quotes around an argument that has
/// spaces of its own, and a backslash before a quote inside double quotes.
/// </summary>
public static class ArgText
{
    public static List<string> Split(string text)
    {
        var args = new List<string>();
        var current = new StringBuilder();
        var any = false;
        char? quote = null;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quote is { } q)
            {
                if (c == q) quote = null;
                else if (c == '\\' && q == '"' && i + 1 < text.Length && text[i + 1] is '"' or '\\') current.Append(text[++i]);
                else current.Append(c);
                continue;
            }
            if (c is '"' or '\'')
            {
                quote = c;
                any = true;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (any || current.Length > 0) args.Add(current.ToString());
                current.Clear();
                any = false;
            }
            else
            {
                current.Append(c);
            }
        }
        if (any || current.Length > 0) args.Add(current.ToString());
        return args;
    }

    public static string Join(IEnumerable<string> args) => string.Join(' ', args.Select(Quote));

    private static string Quote(string arg) =>
        arg.Length > 0 && !arg.Any(c => char.IsWhiteSpace(c) || c is '"' or '\'' or '\\')
            ? arg
            : "\"" + arg.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
