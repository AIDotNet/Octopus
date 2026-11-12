using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.App.Common;

namespace Octopus.App.Settings.Agents;

/// <summary>The command that starts the agent. Empty means the detected one, shown as the placeholder.</summary>
public sealed partial class CommandFieldViewModel : AgentFieldViewModel
{
    private readonly Debouncer _debounce = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasValue))]
    [NotifyCanExecuteChangedFor(nameof(ResetCommand))]
    private string _text;

    public CommandFieldViewModel(AgentRowViewModel row, string label, string placeholder)
        : base(row, "command", label)
    {
        Placeholder = placeholder;
        _text = row.Profile.Command ?? "";
    }

    public string Placeholder { get; }

    public bool HasValue => !string.IsNullOrWhiteSpace(Text);

    partial void OnTextChanged(string value) =>
        _debounce.Schedule(() => Apply(p => p.Command = string.IsNullOrWhiteSpace(value) ? null : value.Trim()));

    [RelayCommand(CanExecute = nameof(HasValue))]
    private void Reset()
    {
        Text = "";
        Flush();
    }

    public override void Flush() => _debounce.Flush();
}
