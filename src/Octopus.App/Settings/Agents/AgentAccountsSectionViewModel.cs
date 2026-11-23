using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.Agents.Accounts;
using Octopus.App.Common;
using Octopus.App.Localization;
using Octopus.Core.Agents;
using Octopus.Core.Projects;

namespace Octopus.App.Settings.Agents;

/// <summary>One subscription account row inside <see cref="AgentAccountsSectionViewModel"/>.</summary>
public sealed partial class AgentAccountItemViewModel(
    AgentAccountsSectionViewModel owner,
    AgentAccountRecord record,
    bool isDefault,
    bool isActiveGlobal,
    bool isEnvDir) : ObservableObject
{
    public AgentAccountRecord Record { get; } = record;
    public string Id => Record.Id;
    public string DisplayLabel => Record.IsDefaultOrigin && Record.Label == "Default"
        ? AppLangResources.Get(AppLangResourceKind.AccountDefaultName)
        : Record.Label;

    public string? Subtitle
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Record.Identity))
            {
                return !string.IsNullOrWhiteSpace(Record.PlanTier)
                    ? $"{Record.Identity} · {Record.PlanTier}"
                    : Record.Identity;
            }
            if (!string.IsNullOrWhiteSpace(Record.Directory))
            {
                return ShortenHome(Record.Directory!);
            }
            return null;
        }
    }

    public bool HasSubtitle => !string.IsNullOrEmpty(Subtitle);
    public bool IsDefault { get; } = isDefault;
    public bool IsActiveGlobal { get; } = isActiveGlobal;
    public bool IsExternal => Record.Origin == AccountOrigin.External;
    public bool IsEnvDir { get; } = isEnvDir;
    public bool IsGlobalKeyring => !IsEnvDir;

    public bool CanSetDefault => !IsDefault;
    public bool CanSwitchGlobal => !IsEnvDir && !IsActiveGlobal;
    public bool CanUpdateSnapshot => !IsEnvDir;
    public bool CanLogin => IsEnvDir;
    public bool CanRevealFolder => IsEnvDir && !string.IsNullOrWhiteSpace(Record.Directory);
    public bool CanRemove => !Record.IsDefaultOrigin;

    [RelayCommand] private Task LoginAsync() => owner.LoginAccountAsync(this);
    [RelayCommand] private void SetDefault() => owner.SetDefaultAccount(this);
    [RelayCommand] private Task SwitchGlobalAsync() => owner.SwitchGlobalAccountAsync(this);
    [RelayCommand] private Task UpdateSnapshotAsync() => owner.UpdateGlobalSnapshotAsync(this);
    [RelayCommand] private Task RenameAsync() => owner.RenameAccountAsync(this);
    [RelayCommand] private void RevealFolder() => owner.RevealAccountFolder(this);
    [RelayCommand] private Task RemoveAsync() => owner.RemoveAccountAsync(this);

    private static string ShortenHome(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (!string.IsNullOrEmpty(home) && path.StartsWith(home, comparison))
        {
            var rest = path[home.Length..];
            if (rest.Length == 0) return "~";
            if (rest[0] is '/' or '\\') return "~" + rest;
        }
        return path;
    }
}

/// <summary>Account management section inside an expanded agent row in Settings → Agents.</summary>
public sealed partial class AgentAccountsSectionViewModel : ObservableObject, IDisposable
{
    private readonly AgentRowViewModel _row;
    private readonly AgentServices _services;
    private readonly IAgentAccountService _accounts;
    private bool _disposed;

    public AgentAccountsSectionViewModel(AgentRowViewModel row, AgentServices services, IAgentAccountService accounts)
    {
        _row = row;
        _services = services;
        _accounts = accounts;
        _accounts.Changed += OnAccountsChanged;
        Rebuild();
    }

    public ObservableCollection<AgentAccountItemViewModel> Items { get; } = [];

    public bool IsEnvDir => string.Equals(_row.Definition.Accounts?.Mode, AccountModes.EnvDir, StringComparison.OrdinalIgnoreCase);
    public bool IsGlobalKeyring => string.Equals(_row.Definition.Accounts?.Mode, AccountModes.GlobalKeyring, StringComparison.OrdinalIgnoreCase);

    public string SectionHint => IsEnvDir
        ? AppLangResources.Format(AppLangResourceKind.AccountSectionEnvHint, _row.Definition.Accounts?.Env ?? "")
        : "";

    public string GlobalWarningTitle => AppLangResources.Get(AppLangResourceKind.AccountSectionGlobalWarningTitle);
    public string GlobalWarningBody => AppLangResources.Format(AppLangResourceKind.AccountSectionGlobalWarningBody, _row.Name);

    public void Rebuild()
    {
        Items.Clear();
        var records = _accounts.GetAccounts(_row.Definition);
        var profile = _row.Profile;
        var activeGlobalId = IsGlobalKeyring ? _accounts.GetActiveGlobalAccountId(_row.Id) : null;
        var defaultId = profile.DefaultAccountId;
        if (string.IsNullOrWhiteSpace(defaultId) || records.All(r => r.Id != defaultId))
        {
            defaultId = IsEnvDir ? AgentAccountRecord.DefaultId : activeGlobalId;
        }

        foreach (var record in records)
        {
            var isDefault = record.Id == defaultId;
            var isActive = IsGlobalKeyring && record.Id == activeGlobalId;
            Items.Add(new AgentAccountItemViewModel(this, record, isDefault, isActive, IsEnvDir));
        }
    }

    [RelayCommand]
    private async Task AddManagedAsync()
    {
        if (!IsEnvDir || _services.Dialogs is null) return;
        var suggested = AppLangResources.Format(AppLangResourceKind.AccountAddDefaultLabel, Items.Count);
        var name = await _services.Dialogs.AskNameAsync(
            AppLangResources.Format(AppLangResourceKind.AccountAddDialogTitle, _row.Name),
            AppLangResources.Get(AppLangResourceKind.AccountLabelField),
            suggested,
            AppLangResources.Get(AppLangResourceKind.AccountCreateAndLogin));
        if (string.IsNullOrWhiteSpace(name)) return;

        var record = _accounts.CreateManaged(_row.Definition, name);
        Rebuild();
        await RunInteractiveLoginAsync(record);
    }

    [RelayCommand]
    private async Task ImportFolderAsync()
    {
        if (!IsEnvDir) return;
        var picked = await _services.PathPicker.PickAsync(
            folder: true,
            AppLangResources.Get(AppLangResourceKind.AccountImportFolder));
        if (string.IsNullOrWhiteSpace(picked) || !Directory.Exists(picked)) return;
        _accounts.ImportDirectory(_row.Definition, picked);
        Rebuild();
    }

    [RelayCommand]
    private async Task SaveCurrentLoginAsync()
    {
        if (!IsGlobalKeyring || _services.Dialogs is null) return;
        if (!await _accounts.HasLiveKeyringCredentialAsync(_row.Definition))
        {
            await _services.Dialogs.ConfirmAsync(
                AppLangResources.Get(AppLangResourceKind.AccountNoKeyringCredentialTitle),
                AppLangResources.Format(AppLangResourceKind.AccountNoKeyringCredentialBody, _row.Name),
                AppLangResources.Get(AppLangResourceKind.RemoteOk));
            return;
        }

        var suggested = AppLangResources.Format(AppLangResourceKind.AccountAddDefaultLabel, Items.Count + 1);
        var name = await _services.Dialogs.AskNameAsync(
            AppLangResources.Format(AppLangResourceKind.AccountAddDialogTitle, _row.Name),
            AppLangResources.Get(AppLangResourceKind.AccountLabelField),
            suggested,
            AppLangResources.Get(AppLangResourceKind.Save));
        if (string.IsNullOrWhiteSpace(name)) return;

        var record = await _accounts.CaptureLiveKeyringAsync(_row.Definition, name);
        if (string.IsNullOrEmpty(_row.Profile.DefaultAccountId))
        {
            _row.Mutate(p => p.DefaultAccountId = record.Id);
        }
        Rebuild();
    }

    [RelayCommand]
    private async Task LoginNewGlobalAsync()
    {
        if (!IsGlobalKeyring || _services.Dialogs is null || _services.LaunchPlanner is null) return;
        var suggested = AppLangResources.Format(AppLangResourceKind.AccountAddDefaultLabel, Items.Count + 1);
        var name = await _services.Dialogs.AskNameAsync(
            AppLangResources.Format(AppLangResourceKind.AccountAddDialogTitle, _row.Name),
            AppLangResources.Get(AppLangResourceKind.AccountLabelField),
            suggested,
            AppLangResources.Get(AppLangResourceKind.AccountCreateAndLogin));
        if (string.IsNullOrWhiteSpace(name)) return;

        if (!await ConfirmGlobalSwitchIfRunningAsync(name)) return;

        var prep = await _accounts.PrepareNewKeyringLoginAsync(_row.Definition);
        try
        {
            var cmd = _accounts.BuildLoginCommand(_row.Definition, _row.Profile, _row.Installation?.Path);
            var spec = _services.LaunchPlanner.PlanAgent(cmd, null, _services.Settings.Current.Terminal.Shell);
            await _services.Dialogs.RunAccountLoginAsync(
                AppLangResources.Format(AppLangResourceKind.AccountLoginDialogTitle, _row.Name, name),
                AppLangResources.Get(AppLangResourceKind.AccountLoginAgyDescription),
                spec,
                autoCloseOnSuccess: false);
        }
        finally
        {
            var created = await _accounts.CompleteNewKeyringLoginAsync(_row.Definition, name, prep);
            if (created is not null && string.IsNullOrEmpty(_row.Profile.DefaultAccountId))
            {
                _row.Mutate(p => p.DefaultAccountId = created.Id);
            }
            Rebuild();
        }
    }

    internal async Task LoginAccountAsync(AgentAccountItemViewModel item)
    {
        await RunInteractiveLoginAsync(item.Record);
    }

    private async Task RunInteractiveLoginAsync(AgentAccountRecord record)
    {
        if (_services.Dialogs is null || _services.LaunchPlanner is null) return;
        var cmd = _accounts.BuildLoginCommand(_row.Definition, _row.Profile, _row.Installation?.Path, record);
        var spec = _services.LaunchPlanner.PlanAgent(cmd, record.Directory, _services.Settings.Current.Terminal.Shell);
        await _services.Dialogs.RunAccountLoginAsync(
            AppLangResources.Format(AppLangResourceKind.AccountLoginDialogTitle, _row.Name, record.Label),
            AppLangResources.Get(AppLangResourceKind.AccountLoginDialogDescription),
            spec,
            autoCloseOnSuccess: true);
        await _accounts.RefreshIdentityAsync(_row.Definition, record.Id);
        Rebuild();
    }

    internal void SetDefaultAccount(AgentAccountItemViewModel item)
    {
        var newDefault = item.Record.IsDefaultOrigin ? null : item.Id;
        _row.Mutate(p => p.DefaultAccountId = newDefault);
        Rebuild();
    }

    internal async Task SwitchGlobalAccountAsync(AgentAccountItemViewModel item)
    {
        if (!await ConfirmGlobalSwitchIfRunningAsync(item.DisplayLabel)) return;
        await _accounts.SwitchGlobalKeyringAsync(_row.Definition, item.Id);
        _row.Mutate(p => p.DefaultAccountId = item.Id);
        Rebuild();
    }

    internal async Task UpdateGlobalSnapshotAsync(AgentAccountItemViewModel item)
    {
        if (_services.Dialogs is null) return;
        if (!await _accounts.HasLiveKeyringCredentialAsync(_row.Definition))
        {
            await _services.Dialogs.ConfirmAsync(
                AppLangResources.Get(AppLangResourceKind.AccountNoKeyringCredentialTitle),
                AppLangResources.Format(AppLangResourceKind.AccountNoKeyringCredentialBody, _row.Name),
                AppLangResources.Get(AppLangResourceKind.RemoteOk));
            return;
        }
        await _accounts.CaptureLiveKeyringAsync(_row.Definition, item.Record.Label, overwriteAccountId: item.Id);
        Rebuild();
    }

    internal async Task RenameAccountAsync(AgentAccountItemViewModel item)
    {
        if (_services.Dialogs is null) return;
        var renamed = await _services.Dialogs.AskNameAsync(
            AppLangResources.Get(AppLangResourceKind.AccountRenameTitle),
            AppLangResources.Get(AppLangResourceKind.AccountLabelField),
            item.DisplayLabel,
            AppLangResources.Get(AppLangResourceKind.Save));
        if (string.IsNullOrWhiteSpace(renamed)) return;
        _accounts.Rename(_row.Id, item.Id, renamed);
        Rebuild();
    }

    internal void RevealAccountFolder(AgentAccountItemViewModel item)
    {
        if (string.IsNullOrWhiteSpace(item.Record.Directory)) return;
        if (item.Record.Origin == AccountOrigin.Managed)
        {
            _accounts.EnsurePrepared(_row.Definition, item.Record);
        }
        if (Directory.Exists(item.Record.Directory))
        {
            _services.Opener.Reveal(item.Record.Directory!);
        }
    }

    internal async Task RemoveAccountAsync(AgentAccountItemViewModel item)
    {
        if (!item.CanRemove) return;
        if (_services.Dialogs is not null)
        {
            var isExternal = item.IsExternal;
            var confirmed = await _services.Dialogs.ConfirmAsync(
                AppLangResources.Format(isExternal ? AppLangResourceKind.AccountRemoveExternalTitle : AppLangResourceKind.AccountRemoveManagedTitle, item.DisplayLabel),
                AppLangResources.Get(isExternal ? AppLangResourceKind.AccountRemoveExternalBody : AppLangResourceKind.AccountRemoveManagedBody),
                AppLangResources.Get(AppLangResourceKind.AccountRemove),
                isDanger: !isExternal);
            if (!confirmed) return;
        }

        await _accounts.RemoveAsync(_row.Definition, item.Id, deleteManagedFiles: !item.IsExternal);
        if (_row.Profile.DefaultAccountId == item.Id)
        {
            _row.Mutate(p => p.DefaultAccountId = null);
        }
        Rebuild();
    }

    private async Task<bool> ConfirmGlobalSwitchIfRunningAsync(string targetLabel)
    {
        if (_services.Dialogs is null || _services.Projects is null || _services.Sessions is null)
        {
            return true;
        }
        var hasRunning = _services.Projects.Projects
            .SelectMany(p => p.Items)
            .Any(i => i.Kind == ProjectItemKind.Agent
                      && i.AgentId == _row.Id
                      && _services.Sessions.Find(i.Id)?.IsRunning == true);
        if (!hasRunning) return true;

        return await _services.Dialogs.ConfirmAsync(
            AppLangResources.Format(AppLangResourceKind.AccountGlobalSwitchConfirmTitle, _row.Name, targetLabel),
            AppLangResources.Format(AppLangResourceKind.AccountGlobalSwitchConfirmBody, _row.Name),
            AppLangResources.Get(AppLangResourceKind.AccountGlobalSwitchConfirmAction),
            isDanger: true);
    }

    private void OnAccountsChanged(object? sender, EventArgs e) => UiContext.Run(() =>
    {
        if (!_disposed) Rebuild();
    });

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _accounts.Changed -= OnAccountsChanged;
    }
}
