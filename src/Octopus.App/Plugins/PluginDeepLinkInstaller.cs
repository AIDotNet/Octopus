using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Octopus.App.Plugins.Settings;
using Octopus.App.Services;
using Octopus.Core;
using Octopus.Core.Plugins;
using Octopus.Plugins.Catalog;
using Octopus.Plugins.Marketplace;
using Octopus.Plugins.Packages;
using Octopus.Plugins.Runtime;
using Octopus.Storage;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;

namespace Octopus.App.Plugins;

/// <summary>
/// Handles <c>octopus://plugin/install?id=...&amp;version=...&amp;server=...</c> deep links from the web marketplace:
/// fetches the plugin metadata, displays a confirmation modal with permissions &amp; risk details, and downloads,
/// SHA-256 verifies, installs, and enables the plugin only after explicit user confirmation.
/// </summary>
public sealed class PluginDeepLinkInstaller(
    PluginMarketplaceClient marketplace,
    PluginInstaller installer,
    IPluginCatalog catalog,
    IPluginStateStore state,
    PluginManager manager,
    IWindowAccessor windows,
    ISettingsStore settings)
{
    private readonly SemaphoreSlim _promptGate = new(1, 1);

    public async Task HandleInstallUriAsync(Uri uri)
    {
        if (!PluginInstallLink.TryParse(uri, out var request, out var parseError) || request is null)
        {
            await ShowErrorAsync(
                PluginText.Get("无法解析插件安装链接", "Invalid plugin install link"),
                parseError ?? uri.ToString());
            return;
        }

        if (!await _promptGate.WaitAsync(0))
        {
            return;
        }

        try
        {
            var serverUrl = request.ServerUrl ?? ConfiguredServerUrl();
            MarketplacePluginDetail? detail;
            try
            {
                detail = await marketplace.GetDetailAsync(serverUrl, request.PluginId);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException)
            {
                await ShowErrorAsync(
                    PluginText.Get("无法获取插件信息", "Could not load plugin details"),
                    PluginText.Get(
                        $"无法连接插件市场（{serverUrl}）：{ex.Message}",
                        $"Could not reach marketplace ({serverUrl}): {ex.Message}"));
                return;
            }

            if (detail is null || detail.Versions.Count == 0)
            {
                await ShowErrorAsync(
                    PluginText.Get("未找到插件", "Plugin not found"),
                    PluginText.Get(
                        $"插件市场（{serverUrl}）中未找到已上架的插件「{request.PluginId}」。",
                        $"No published version of \"{request.PluginId}\" was found on {serverUrl}."));
                return;
            }

            var targetVersion = (request.Version is not null
                ? detail.Versions.FirstOrDefault(v => string.Equals(v.Version, request.Version, StringComparison.OrdinalIgnoreCase))
                : null) ?? detail.Versions[0];

            await Dispatcher.UIThread.InvokeAsync(() => ShowInstallDialogAsync(serverUrl, detail, targetVersion));
        }
        finally
        {
            _promptGate.Release();
        }
    }

    private async Task ShowInstallDialogAsync(
        string serverUrl,
        MarketplacePluginDetail detail,
        MarketplacePluginVersion targetVersion)
    {
        if (windows.Window is not { } owner) return;

        var installedPkg = catalog.Packages
            .FirstOrDefault(p => p.Status != PluginStatus.Shadowed
                && p.Manifest is not null
                && string.Equals(p.Id, detail.Plugin.Id, StringComparison.OrdinalIgnoreCase));
        var installedVersion = installedPkg?.Manifest?.Version;
        var localIcon = installedPkg is null
            ? null
            : Rendering.UiIcons.TryLoadPackageBitmap(installedPkg.Directory, installedPkg.Manifest?.Icon ?? detail.Plugin.Icon);

        var row = new MarketplacePluginRowViewModel(
            new MarketplaceCatalogEntry(detail.Plugin, targetVersion),
            installedVersion,
            localIcon,
            url => OpenExternalUrlAsync(owner, url));

        var detailView = new MarketplacePluginDetailView { DataContext = row };
        var statusAlert = new Alert
        {
            Tone = AlertTone.Danger,
            Title = PluginStrings.MarketDetailMissingShaNote,
            IsVisible = !row.CanInstall,
        };
        var body = new StackPanel
        {
            Spacing = 12,
            Children = { detailView, statusAlert },
        };

        var isSameVersion = installedVersion is not null
            && string.Equals(installedVersion, targetVersion.Version, StringComparison.OrdinalIgnoreCase);
        var isUpgrade = installedVersion is not null && !isSameVersion;

        var installLabel = isSameVersion
            ? PluginText.Get($"重新安装 v{targetVersion.Version}", $"Reinstall v{targetVersion.Version}")
            : isUpgrade
                ? $"{PluginStrings.MarketUpgrade} v{targetVersion.Version}"
                : $"{PluginStrings.MarketInstall} v{targetVersion.Version}";

        var cancelBtn = new TKCode.Arc.Button
        {
            Content = PluginText.Get("取消", "Cancel"),
            Variant = ButtonVariant.Secondary,
            Size = ControlSize.Small,
        };
        var installBtn = new TKCode.Arc.Button
        {
            Content = installLabel,
            Variant = ButtonVariant.Primary,
            Size = ControlSize.Small,
            IsEnabled = row.CanInstall,
        };
        var installedBadge = new TKCode.Arc.Badge
        {
            Size = BadgeSize.Small,
            Tone = BadgeTone.Success,
            Content = PluginStrings.MarketInstalledBadge,
            IsVisible = false,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { cancelBtn, installedBadge, installBtn },
        };

        var sourceHost = Uri.TryCreate(serverUrl, UriKind.Absolute, out var parsedHost)
            ? parsedHost.Authority
            : serverUrl;
        var installedNote = installedVersion is null
            ? ""
            : isSameVersion
                ? PluginText.Get(" · 已安装此版本", " · Installed")
                : PluginText.Get($" · 当前已装 v{installedVersion}", $" · Installed v{installedVersion}");

        var dialog = new Dialog
        {
            Size = ControlSize.Medium,
            Title = row.Name,
            Description = $"{row.IdentitySummary} · {sourceHost}{installedNote}",
            Content = body,
            Actions = actions,
        };
        dialog.Resources[DialogSurfaceTokens.Key(DialogSurfaceTokenKind.PanelWidthMD)] = 540.0;

        cancelBtn.Click += (_, _) => dialog.Close(false);
        installBtn.Click += async (_, _) =>
        {
            installBtn.IsLoading = true;
            installBtn.IsEnabled = false;
            cancelBtn.IsEnabled = false;
            statusAlert.IsVisible = false;

            string? tempFile = null;
            try
            {
                tempFile = await marketplace.DownloadVerifiedAsync(serverUrl, targetVersion);
                var staged = await Task.Run(() => installer.Stage(tempFile));
                if (!staged.CanInstall || staged.Manifest is not { } manifest)
                {
                    installer.Discard(staged);
                    throw new PluginPackageException(string.Join("; ", staged.Result.Errors.Select(i => i.ToString())));
                }
                if (!string.Equals(manifest.Id, detail.Plugin.Id, StringComparison.OrdinalIgnoreCase))
                {
                    installer.Discard(staged);
                    throw new PluginPackageException(PluginText.Get(
                        $"安装包 ID（{manifest.Id}）与市场条目（{detail.Plugin.Id}）不一致。",
                        $"Package id ({manifest.Id}) does not match requested plugin ({detail.Plugin.Id})."));
                }

                var consent = PluginGrants.PlanInstall(manifest, state.Current.Find(manifest.Id), staged.IsUpgrade, staged.InstalledManifest);
                if (staged.IsUpgrade || manifest.Permissions.Requests.Count > 0)
                {
                    var accepted = await PermissionText.ConfirmInstallAsync(owner, consent, staged.IsUpgrade);
                    if (!accepted)
                    {
                        installer.Discard(staged);
                        installBtn.IsEnabled = row.CanInstall;
                        return;
                    }
                }

                await CommitStagedInstallAsync(manager, installer, state, staged, consent);
                row.UpdateInstalledState(manifest.Version);
                installBtn.IsVisible = false;
                installedBadge.IsVisible = true;
                cancelBtn.Content = PluginStrings.Close;
                statusAlert.Tone = AlertTone.Success;
                statusAlert.Title = PluginText.Get(
                    $"已安装并启用 {row.Name} v{manifest.Version}",
                    $"Installed and enabled {row.Name} v{manifest.Version}");
                statusAlert.IsVisible = true;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidDataException or InvalidOperationException or PluginPackageException or UnauthorizedAccessException)
            {
                statusAlert.Tone = AlertTone.Danger;
                statusAlert.Title = PluginText.Get($"安装失败：{ex.Message}", $"Install failed: {ex.Message}");
                statusAlert.IsVisible = true;
                installBtn.IsEnabled = true;
            }
            finally
            {
                installBtn.IsLoading = false;
                cancelBtn.IsEnabled = true;
                if (tempFile is not null)
                {
                    try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
                }
            }
        };

        await dialog.ShowAsync(owner);
    }

    internal static async Task<PluginManifest> CommitStagedInstallAsync(
        PluginManager manager,
        PluginInstaller installer,
        IPluginStateStore state,
        StagedPlugin staged,
        PluginInstallConsent? consent = null)
    {
        var manifest = staged.Manifest ?? throw new InvalidOperationException("Staged package has no manifest.");
        var resolvedConsent = consent ?? PluginGrants.PlanInstall(
            manifest,
            state.Current.Find(manifest.Id),
            staged.IsUpgrade,
            staged.InstalledManifest);
        await manager.SetEnabledAsync(manifest.Id, false);
        await Task.Run(() => installer.Install(staged));
        state.Update(d =>
        {
            var record = d.Find(manifest.Id) ?? (d.Plugins[manifest.Id] = new PluginRecord { InstalledAt = DateTimeOffset.Now });
            record.Source = PluginSource.File;
            record.Version = manifest.Version;
            PluginGrants.ApplyInstallGrants(record, resolvedConsent);
        });
        await manager.SetEnabledAsync(manifest.Id, true);
        return manifest;
    }

    private string ConfiguredServerUrl()
    {
        var configured = settings.Current.Remote.RelayUrl?.Trim().TrimEnd('/');
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        return ServiceEndpoints.OfficialServiceUrl ?? ServiceEndpoints.DefaultOfficialServiceUrl;
    }

    private async Task ShowErrorAsync(string title, string message)
    {
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            if (windows.Window is not { } owner) return;
            await Dialog.ConfirmAsync(owner, title, message, PluginStrings.Close, "", isDanger: false);
        });
    }

    private static async Task OpenExternalUrlAsync(TopLevel owner, string? url)
    {
        if (!MarketplaceEndpointPolicy.TryValidateExternalUrl(url, out var target) || target is null) return;
        await owner.Launcher.LaunchUriAsync(target);
    }
}
