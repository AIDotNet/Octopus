using CommunityToolkit.Mvvm.ComponentModel;
using Octopus.App.Common;
using TKCode.Arc;

namespace Octopus.App.Settings.Agents;

/// <summary>
/// A value picked from a list: model, effort, permission and <c>enum</c> features. The first option (id "") is the
/// "not set" choice. With <see cref="AllowCustom"/> a last choice lets the person type any id.
/// </summary>
public sealed partial class ChoiceFieldViewModel : AgentFieldViewModel
{
    /// <summary>The id of the "Custom…" choice; never written anywhere.</summary>
    public const string CustomId = "\u0001custom";

    private readonly Func<string?> _read;
    private readonly Action<string?> _write;
    private readonly Func<string>? _defaultLabel;
    private readonly Func<string?>? _defaultEffectiveId;
    private readonly Debouncer _debounce = new();
    private readonly HashSet<string> _dangerIds;
    private readonly string? _customLabel;
    private readonly string? _defaultOptionLabel;
    private List<(string Id, string Label)> _items;

    [ObservableProperty]
    private IReadOnlyList<SelectOption> _options = [];

    [ObservableProperty]
    private bool _isCustom;

    [ObservableProperty]
    private string _customText = "";

    public ChoiceFieldViewModel(
        AgentRowViewModel row,
        string key,
        string label,
        string defaultOptionLabel,
        IEnumerable<(string Id, string Label)> items,
        Func<string?> read,
        Action<string?> write,
        bool allowCustom = false,
        string? customLabel = null,
        string? customPlaceholder = null,
        IEnumerable<string>? dangerIds = null,
        Func<string>? defaultLabel = null,
        Func<string?>? defaultEffectiveId = null,
        string? description = null)
        : base(row, key, label, description)
    {
        _read = read;
        _write = write;
        _defaultOptionLabel = defaultOptionLabel;
        _defaultLabel = defaultLabel;
        _defaultEffectiveId = defaultEffectiveId;
        _customLabel = allowCustom ? customLabel : null;
        _dangerIds = [.. dangerIds ?? []];
        CustomPlaceholder = customPlaceholder;
        AllowCustom = allowCustom;
        _items = [.. items];
        var current = _read();
        _isCustom = allowCustom && !string.IsNullOrEmpty(current) && _items.All(i => i.Id != current);
        _customText = _isCustom ? current! : "";
        RebuildOptions();
    }

    public bool AllowCustom { get; }

    public string? CustomPlaceholder { get; }

    /// <summary>The id of the chosen option: "" for "not set", <see cref="CustomId"/> while typing a custom value.</summary>
    public string SelectedId
    {
        get => IsCustom ? CustomId : CurrentOrEmpty();
        set
        {
            value ??= "";
            if (value == SelectedId)
            {
                return;
            }
            if (value == CustomId)
            {
                IsCustom = true;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsDangerSelected));
                return;
            }
            IsCustom = false;
            CustomText = "";
            _debounce.Cancel();
            _write(value.Length == 0 ? null : value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsDangerSelected));
        }
    }

    /// <summary>True while a dangerous option (full access) is chosen: the view shows a warning shield.</summary>
    public bool IsDangerSelected
    {
        get
        {
            if (IsCustom)
            {
                return false;
            }
            var current = CurrentOrEmpty();
            var effective = current.Length == 0 ? _defaultEffectiveId?.Invoke() : current;
            return effective is not null && _dangerIds.Contains(effective);
        }
    }

    /// <summary>The text of the chosen option.</summary>
    public string SelectedLabel => Options.FirstOrDefault(o => Equals(o.Value, SelectedId))?.Label ?? "";

    public IReadOnlyList<string> OptionIds => [.. Options.Select(o => (string)o.Value!)];

    partial void OnCustomTextChanged(string value)
    {
        if (!IsCustom)
        {
            return;
        }
        _debounce.Schedule(() => _write(string.IsNullOrWhiteSpace(value) ? null : value.Trim()));
    }

    /// <summary>Replaces the choices (the effort levels of another model, discovered models).</summary>
    public void SetItems(IEnumerable<(string Id, string Label)> items)
    {
        _items = [.. items];
        var current = _read();
        if (AllowCustom && !string.IsNullOrEmpty(current) && _items.Any(i => i.Id == current) && IsCustom)
        {
            IsCustom = false;
            CustomText = "";
        }
        RebuildOptions();
        // A value the new list does not have goes back to "not set" (custom ids stay where they are allowed).
        if (!AllowCustom && !string.IsNullOrEmpty(current) && _items.All(i => i.Id != current))
        {
            _write(null);
        }
        OnPropertyChanged(nameof(SelectedId));
        OnPropertyChanged(nameof(SelectedLabel));
        OnPropertyChanged(nameof(IsDangerSelected));
    }

    public override void OnSettingsChanged()
    {
        if (_defaultLabel is null)
        {
            return;
        }
        RebuildOptions();
        OnPropertyChanged(nameof(SelectedId));
        OnPropertyChanged(nameof(SelectedLabel));
        OnPropertyChanged(nameof(IsDangerSelected));
    }

    public override void Flush() => _debounce.Flush();

    private string CurrentOrEmpty() => _read() ?? "";

    private void RebuildOptions()
    {
        var list = new List<SelectOption>
        {
            new("", _defaultLabel?.Invoke() ?? _defaultOptionLabel ?? "")
        };
        list.AddRange(_items.Select(i => new SelectOption(i.Id, i.Label)));
        if (_customLabel is not null)
        {
            list.Add(new SelectOption(CustomId, _customLabel));
        }
        // Replacing identical options makes the select lose its selection, so leave them alone when nothing changed.
        if (Options.Count == list.Count && Options.Zip(list).All(p => Equals(p.First.Value, p.Second.Value) && p.First.Label == p.Second.Label))
        {
            return;
        }
        Options = list;
    }
}
