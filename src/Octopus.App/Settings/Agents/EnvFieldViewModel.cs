using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.App.Common;
using Octopus.Core.Agents;

namespace Octopus.App.Settings.Agents;

/// <summary>The environment variables of the agent: an editable <c>NAME=value</c> list plus the definition's common variables.</summary>
public sealed partial class EnvFieldViewModel : AgentFieldViewModel
{
    private readonly EnvCapability _capability;
    private readonly Debouncer _debounce = new();
    private bool _loading;

    public EnvFieldViewModel(AgentRowViewModel row, string label, EnvCapability capability)
        : base(row, "env", label)
    {
        _capability = capability;
        _loading = true;
        foreach (var (name, value) in row.Profile.Env)
        {
            Entries.Add(new EnvEntryViewModel(this, name, value));
        }
        _loading = false;
        Entries.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEmpty));
        RefreshKnown();
    }

    public ObservableCollection<EnvEntryViewModel> Entries { get; } = [];

    /// <summary>The common variables that are not in the list yet.</summary>
    public ObservableCollection<KnownEnvChoice> AvailableKnown { get; } = [];

    public bool IsEmpty => Entries.Count == 0;

    public bool HasKnown => AvailableKnown.Count > 0;

    public bool AllowCustom => _capability.AllowCustom;

    public bool IsSecret(string name) => _capability.Known.Any(k => k.Secret && k.Name == name);

    public string? HintOf(string name)
    {
        var known = _capability.Known.FirstOrDefault(k => k.Name == name);
        if (known is null)
        {
            return null;
        }
        var title = known.TitleFor(AgentLanguage.Current);
        return known.DescriptionFor(AgentLanguage.Current) is { Length: > 0 } description ? $"{title} · {description}" : title;
    }

    [RelayCommand]
    private void AddCustom() => Entries.Add(new EnvEntryViewModel(this, "", ""));

    public void AddKnown(KnownEnvVar variable)
    {
        Entries.Add(new EnvEntryViewModel(this, variable.Name, ""));
        RefreshKnown();
    }

    public void Remove(EnvEntryViewModel entry)
    {
        Entries.Remove(entry);
        RefreshKnown();
        Commit();
    }

    /// <summary>A name or value was edited: save once typing pauses.</summary>
    public void EntryChanged()
    {
        if (_loading)
        {
            return;
        }
        RefreshKnown();
        _debounce.Schedule(Commit);
    }

    public override void Flush() => _debounce.Flush();

    private void Commit()
    {
        _debounce.Cancel();
        var env = new Dictionary<string, string>();
        foreach (var entry in Entries)
        {
            var name = entry.Name.Trim();
            if (name.Length > 0 && entry.Value.Length > 0)
            {
                env[name] = entry.Value;
            }
        }
        Apply(p => p.Env = env);
    }

    private void RefreshKnown()
    {
        var used = Entries.Select(e => e.Name.Trim()).ToHashSet();
        AvailableKnown.Clear();
        foreach (var known in _capability.Known.Where(k => !used.Contains(k.Name)))
        {
            AvailableKnown.Add(new KnownEnvChoice(known, AddKnown));
        }
        OnPropertyChanged(nameof(HasKnown));
    }
}
