using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Octopus.App.Plugins.Rendering;
using Octopus.App.Services;
using Octopus.Core.Agents;
using Octopus.Core.Projects;
using Octopus.Plugins.Runtime;
using Octopus.Plugins.Runtime.Modules;
using Octopus.Storage;
using TKCode.Arc;

namespace Octopus.App.Plugins;

/// <summary>
/// The parts of the plugin API that need the app's window: views (design/plugins/05-界面能力.md, 06-标签与渲染.md),
/// dialogs, toasts, progress, pickers, the workspace, the clipboard, notifications, opening links, indicators and
/// the read-only app state. Requests arrive on the plugin's thread and are run on the UI thread.
/// </summary>
public sealed class WorkbenchModule(IServiceProvider services) : IPluginApiModule
{
    private readonly ConcurrentDictionary<(string Plugin, string Id), string> _progress = new();
    private readonly ConcurrentDictionary<(string Plugin, string Id), Dialog> _dialogs = new();
    private volatile JsonObject _appState = new();

    private PluginWorkbench Workbench => services.GetRequiredService<PluginWorkbench>();
    private IProjectService Projects => services.GetRequiredService<IProjectService>();
    private PluginIndicators Indicators => services.GetRequiredService<PluginIndicators>();
    private PluginThemes Themes => services.GetRequiredService<PluginThemes>();

    private TopLevel Window => services.GetRequiredService<IWindowAccessor>().Window
        ?? throw new PluginApiException("Unavailable", "The main window is not open.");

    public void Register(PluginApiRouter router)
    {
        // views
        router.Notify("view.update", call => Workbench.Post(call.Plugin.Id, call.RequiredString("instanceId"), s => s.Update(Take(call, "tree"))));
        router.Notify("view.chrome", call =>
        {
            if (call.Args is JsonObject change) Workbench.Post(call.Plugin.Id, call.RequiredString("instanceId"), s => s.Chrome(change));
        });
        router.Notify("view.error", call => Workbench.Post(call.Plugin.Id, call.RequiredString("instanceId"), s => s.Error(call.String("message") ?? "")));
        router.Notify("view.saveState", call => Workbench.Post(call.Plugin.Id, call.RequiredString("instanceId"), s => s.SaveState(Take(call, "state"))));

        // ui
        router.Notify("ui.toast", Toast);
        router.Request("ui.confirm", (call, _) => OnUi(() => ConfirmAsync(call)));
        router.Request("ui.prompt", (call, _) => OnUi(() => PromptAsync(call)));
        router.Request("ui.pick", (call, _) => OnUi(() => PickAsync(call)));
        router.Request("ui.pickFile", (call, _) => OnUi(() => PickStorageAsync(call, folder: false, save: false)));
        router.Request("ui.pickFolder", (call, _) => OnUi(() => PickStorageAsync(call, folder: true, save: false)));
        router.Request("ui.pickSaveLocation", (call, _) => OnUi(() => PickStorageAsync(call, folder: false, save: true)));
        router.Request("fs.requestAccess", (call, _) => OnUi(() => RequestAccessAsync(call)));
        router.Request("ui.dialog.open", (call, _) => OnUi(() => ShowDialogAsync(call)));
        router.Notify("ui.dialog.close", call => Dispatcher.UIThread.Post(() =>
        {
            if (_dialogs.TryRemove((call.Plugin.Id, call.RequiredString("instanceId")), out var dialog)) dialog.Close(null);
        }));
        router.Notify("ui.progress.start", ProgressStart);
        router.Notify("ui.progress.report", ProgressReport);
        router.Notify("ui.progress.end", ProgressEnd);

        // workspace
        router.Request("workspace.items", (call, _) => OnUi(() => Task.FromResult(WorkspaceItems(call))));
        router.Sync("workspace.activeItem", _ => (_appState["activeItem"]?.ToJsonString()) ?? "null");
        router.Request("workspace.openView", (call, _) => OnUi(() => Task.FromResult(OpenView(call))));
        router.Request("workspace.openFile", (call, _) => OnUi(() => Task.FromResult(OpenFile(call))));
        router.Request("workspace.activate", (call, _) => OnUi(() => Task.FromResult(Activate(call))));
        router.Request("workspace.close", (call, _) => OnUi(() => CloseAsync(call)));

        // clipboard, notifications, links
        router.Request("clipboard.readText", (call, _) =>
        {
            call.Plugin.Grants.Require("clipboard.read");
            return OnUi(async () => OfNullable(await ClipboardOrThrow().TryGetTextAsync()));
        });
        router.Request("clipboard.writeText", (call, _) =>
        {
            call.Plugin.Grants.Require("clipboard.write");
            var text = call.Text.Length > 0 ? call.Text : call.String("text") ?? "";
            return OnUi(async () =>
            {
                await ClipboardOrThrow().SetTextAsync(text);
                return PluginReply.Empty;
            });
        });
        router.Request("notifications.show", (call, _) =>
        {
            call.Plugin.Grants.Require("notifications");
            return OnUi(() =>
            {
                // No system notification service yet: shown as a toast with the plugin's name (design 08 §2).
                ShowToast(call.Plugin, call.String("title") ?? PluginWorkbench.NameOf(call.Plugin), call.String("body"), ToastKind.Info, null);
                return Task.FromResult(PluginReply.Empty);
            });
        });
        router.Request("shell.open", (call, _) => OnUi(() => OpenExternalAsync(call)));

        // app
        router.Sync("app.state", _ => _appState.ToJsonString());
        router.Sync("theme.current", _ => new JsonObject { ["appearance"] = _appState["appearance"]?.DeepClone() ?? "light" }.ToJsonString());
        router.Sync("settings.get", call =>
        {
            call.Plugin.Grants.Require("settings");
            return SettingsValue(call.RequiredString("key"))?.ToJsonString() ?? "null";
        });

        // indicators
        router.Notify("indicators.create", call => Indicators.Create(call.Plugin, call.RequiredString("id")));
        router.Notify("indicators.update", call =>
        {
            if (Take(call, "state") is JsonObject state) Indicators.Update(call.Plugin, call.RequiredString("id"), state);
        });
        router.Notify("indicators.remove", call => Indicators.Remove(call.Plugin, call.RequiredString("id")));

        // themes (design/plugins/07-主题.md §6)
        router.Notify("theme.register", call =>
        {
            if (!call.Plugin.Grants.Has("theme")) { call.Plugin.Write(PluginLogLevel.Warn, "theme.register needs the \"theme\" permission."); return; }
            Themes.Register(call.Plugin.Id, call.RequiredString("id"), Take(call, "definition"), call.String("label"));
        });
        router.Notify("theme.unregister", call => Themes.Unregister(call.Plugin.Id, call.RequiredString("id")));
        router.Notify("theme.override", call =>
        {
            if (!call.Plugin.Grants.Has("theme")) { call.Plugin.Write(PluginLogLevel.Warn, "theme.setOverrides needs the \"theme\" permission."); return; }
            Themes.SetOverride(call.Plugin.Id, call.RequiredString("key"), Take(call, "definition"));
        });
        router.Notify("theme.override.remove", call => Themes.RemoveOverride(call.Plugin.Id, call.RequiredString("key")));

        // agents
        router.Request("agents.launch", (call, _) =>
        {
            call.Plugin.Grants.Require("agents.launch");
            return OnUi(() => Task.FromResult(LaunchAgent(call)));
        });
    }

    public void Reset(PluginHost plugin)
    {
        Indicators.RemoveAll(plugin);
        Themes.Forget(plugin.Id);
        foreach (var key in _progress.Keys.Where(k => k.Plugin == plugin.Id).ToList())
        {
            if (_progress.TryRemove(key, out var toast)) Dispatcher.UIThread.Post(() => DismissToast(toast));
        }
        foreach (var key in _dialogs.Keys.Where(k => k.Plugin == plugin.Id).ToList())
        {
            if (_dialogs.TryRemove(key, out var dialog)) Dispatcher.UIThread.Post(() => dialog.Close(null));
        }
    }

    /// <summary>Kept up to date by the app on the UI thread, read by plugins on theirs.</summary>
    public void SetAppState(JsonObject state) => _appState = state;

    // ---------------------------------------------------------------- helpers

    private static JsonNode? Take(PluginCall call, string name)
    {
        if (call.Args is JsonObject o && o.TryGetPropertyValue(name, out var value))
        {
            o.Remove(name);
            return value;
        }
        return null;
    }

    private static Task<PluginReply> OnUi(Func<Task<PluginReply>> action) => Dispatcher.UIThread.InvokeAsync(action);

    private static PluginReply OfNullable(string? value) => PluginReply.Of(value is null ? null : JsonValue.Create(value));

    private IClipboard ClipboardOrThrow() => Window.Clipboard ?? throw PluginApiException.Unsupported("The clipboard");

    // ---------------------------------------------------------------- toasts and progress

    private void Toast(PluginCall call)
    {
        var message = call.String("message") ?? "";
        var kind = call.String("kind") switch
        {
            "success" => ToastKind.Success,
            "warning" => ToastKind.Warning,
            "error" => ToastKind.Error,
            _ => ToastKind.Info,
        };
        ToastAction? action = null;
        if (call["actions"] is JsonArray { Count: > 0 } actions && actions[0] is JsonObject first)
        {
            var label = first["label"]?.GetValue<string>() ?? "OK";
            var command = first["command"]?.GetValue<string>();
            var handler = first["handler"]?.GetValue<string>();
            var plugin = call.Plugin;
            action = new ToastAction(label, () =>
            {
                if (handler is not null) _ = PluginWorkbench.InvokeQuietlyAsync(plugin, handler, []);
                else if (command is not null) Workbench.ExecuteCommand(plugin, command, []);
            });
        }
        Dispatcher.UIThread.Post(() => ShowToast(call.Plugin, message, null, kind, action));
    }

    private string? ShowToast(PluginHost plugin, string title, string? description, ToastKind kind, ToastAction? action, TimeSpan? duration = null, string? id = null)
    {
        if (services.GetRequiredService<IWindowAccessor>().Window is not { } window) return null;
        // The plugin's name goes under the message: a toast must always say who sent it (design 05 §6).
        var source = PluginWorkbench.NameOf(plugin);
        return TKCode.Arc.Toast.Show(window, title, kind, description is null ? source : $"{description}\n{source}", action!, duration, id!);
    }

    private void DismissToast(string id)
    {
        if (services.GetRequiredService<IWindowAccessor>().Window is { } window) TKCode.Arc.Toast.Dismiss(window, id);
    }

    private void ProgressStart(PluginCall call)
    {
        var id = call.RequiredString("id");
        var plugin = call.Plugin;
        var title = call.String("title") ?? PluginWorkbench.NameOf(plugin);
        ToastAction? cancel = call.Bool("cancellable")
            ? new ToastAction(PluginText.Get("取消", "Cancel"), () => plugin.Emit("ui.progress.cancel", null, a => a?["id"]?.GetValue<string>() == id))
            : null;
        var toastId = $"plugin-progress:{plugin.Id}:{id}";
        _progress[(plugin.Id, id)] = toastId;
        Dispatcher.UIThread.Post(() => ShowToast(plugin, title, null, ToastKind.Loading, cancel, TimeSpan.FromDays(1), toastId));
    }

    private void ProgressReport(PluginCall call)
    {
        if (!_progress.TryGetValue((call.Plugin.Id, call.RequiredString("id")), out var toastId)) return;
        var value = call["value"] is JsonValue v && v.TryGetValue<double>(out var d) ? d : (double?)null;
        var message = call.String("message");
        var text = (value, message) switch
        {
            ({ } p, { } m) => $"{m} · {Math.Clamp(p, 0, 1):P0}",
            ({ } p, null) => $"{Math.Clamp(p, 0, 1):P0}",
            (null, { } m) => m,
            _ => null,
        };
        if (text is null) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (services.GetRequiredService<IWindowAccessor>().Window is { } window)
            {
                TKCode.Arc.Toast.Update(window, toastId, new ToastUpdate { Description = $"{text}\n{PluginWorkbench.NameOf(call.Plugin)}" });
            }
        });
    }

    private void ProgressEnd(PluginCall call)
    {
        if (_progress.TryRemove((call.Plugin.Id, call.RequiredString("id")), out var toastId)) Dispatcher.UIThread.Post(() => DismissToast(toastId));
    }

    // ---------------------------------------------------------------- dialogs

    private async Task<PluginReply> ConfirmAsync(PluginCall call)
    {
        var title = call.String("title") ?? call.String("message") ?? "";
        var description = call.String("description") ?? call.String("detail") ?? (call.String("title") is null ? null : call.String("message")) ?? "";
        var confirm = call.String("confirmLabel") ?? call.String("confirmText") ?? PluginText.Get("确定", "OK");
        var cancel = call.String("cancelLabel") ?? call.String("cancelText") ?? PluginText.Get("取消", "Cancel");
        var danger = call.Bool("danger") || call.Bool("isDanger");
        var accepted = await Dialog.ConfirmAsync(Window, title, Signed(description, call.Plugin), confirm, cancel, danger);
        return PluginReply.Of(accepted);
    }

    /// <summary>Dialog descriptions name the plugin, so a plugin cannot pass for Octopus itself.</summary>
    private static string Signed(string description, PluginHost plugin) =>
        description.Length == 0 ? $"— {PluginWorkbench.NameOf(plugin)}" : $"{description}\n\n— {PluginWorkbench.NameOf(plugin)}";

    private async Task<PluginReply> PromptAsync(PluginCall call)
    {
        FieldTextBox field = call.Bool("password") ? new PasswordField() : new Input();
        field.Text = call.String("value") ?? "";
        field.Placeholder = call.String("placeholder");
        field.Label = call.String("label");
        field.IsLabelVisible = call.String("label") is not null;
        field.MinWidth = 360;
        var ok = new DialogAction(call.String("confirmLabel") ?? PluginText.Get("确定", "OK"), true, ButtonVariant.Primary);
        var cancel = new DialogAction(PluginText.Get("取消", "Cancel"), false, ButtonVariant.Secondary);
        field.AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() => field.Focus(), DispatcherPriority.Input);
        var dialog = new Dialog
        {
            Title = call.String("title") ?? "",
            Description = Signed(call.String("description") ?? "", call.Plugin),
            Content = field,
        };
        dialog.Actions = Dialog.BuildActions([cancel, ok]);
        field.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) dialog.Close(true);
        };
        var result = await dialog.ShowAsync(Window);
        return result is true ? PluginReply.Of(field.Text ?? "") : PluginReply.Of((JsonNode?)null);
    }

    private sealed record PickRow(string Id, string Label, string? Description);

    private async Task<PluginReply> PickAsync(PluginCall call)
    {
        var rows = (call["items"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(i => new PickRow(i["id"]?.ToString() ?? "", i["label"]?.ToString() ?? i["id"]?.ToString() ?? "", i["description"]?.ToString()))
            .Take(5000).ToList();
        var search = new SearchField { Placeholder = call.String("placeholder") ?? PluginText.Get("搜索", "Search") };
        var list = new ListBox
        {
            Height = 320,
            MinWidth = 420,
            ItemsSource = rows,
            ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<PickRow>((row, _) => row is null ? new Panel() : new StackPanel
            {
                Spacing = 1,
                Margin = new Thickness(2, 3),
                Children =
                {
                    new TextBlock { Text = row.Label },
                    new TextBlock { Text = row.Description, IsVisible = row.Description is not null, FontSize = 11, Opacity = 0.7 },
                },
            }, supportsRecycling: true),
        };
        var dialog = new Dialog
        {
            Title = call.String("title") ?? "",
            Description = Signed("", call.Plugin),
            Content = new StackPanel { Spacing = 8, Children = { search, list } },
        };
        dialog.Actions = Dialog.BuildActions([new DialogAction(PluginText.Get("取消", "Cancel"), null, ButtonVariant.Secondary)]);
        search.TextChanged += (_, _) =>
        {
            var q = search.Text?.Trim() ?? "";
            list.ItemsSource = q.Length == 0 ? rows : rows.Where(r => r.Label.Contains(q, StringComparison.OrdinalIgnoreCase) || (r.Description?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();
            list.SelectedIndex = 0;
        };
        search.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && list.SelectedItem is PickRow row) dialog.Close(row.Id);
            else if (e.Key == Key.Down) list.SelectedIndex = Math.Min(list.SelectedIndex + 1, list.ItemCount - 1);
            else if (e.Key == Key.Up) list.SelectedIndex = Math.Max(list.SelectedIndex - 1, 0);
        };
        list.DoubleTapped += (_, _) =>
        {
            if (list.SelectedItem is PickRow row) dialog.Close(row.Id);
        };
        search.AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() => search.Focus(), DispatcherPriority.Input);
        var result = await dialog.ShowAsync(Window);
        return PluginReply.Of(result as string is { } id ? JsonValue.Create(id) : null);
    }

    /// <summary>
    /// The system pickers. What the user picks is something they chose to hand over: the path becomes readable (and
    /// for a save location writable) by the plugin from then on (design/plugins/04-权限与文件系统.md §3.5).
    /// </summary>
    private async Task<PluginReply> PickStorageAsync(PluginCall call, bool folder, bool save)
    {
        var storage = Window.StorageProvider;
        var title = call.String("title");
        string? path;
        if (save)
        {
            var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions { Title = title, SuggestedFileName = call.String("defaultName") });
            path = file?.TryGetLocalPath();
        }
        else if (folder)
        {
            var picked = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
            path = picked.FirstOrDefault()?.TryGetLocalPath();
        }
        else
        {
            var filters = (call["filters"] as JsonArray ?? []).OfType<JsonObject>()
                .Select(f => new FilePickerFileType(f["name"]?.ToString() ?? "") { Patterns = (f["extensions"] as JsonArray ?? []).Select(e => "*." + e?.ToString().TrimStart('.')).ToList() })
                .ToList();
            var picked = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = false, FileTypeFilter = filters.Count > 0 ? filters : null });
            path = picked.FirstOrDefault()?.TryGetLocalPath();
        }
        if (path is null) return PluginReply.Of((JsonNode?)null);
        services.GetService<IPickedPathGrants>()?.Grant(call.Plugin, path, folder, writable: save);
        return PluginReply.Of(path);
    }

    /// <summary>
    /// <c>fs.requestAccess</c>: the plugin says why it wants a file or folder; the person is told (with the plugin's
    /// name and whether it would also write), then picks one, or not. The pick is the grant, as for the pickers.
    /// </summary>
    private async Task<PluginReply> RequestAccessAsync(PluginCall call)
    {
        var folder = call.RequiredString("kind") switch
        {
            "folder" => true,
            "file" => false,
            _ => throw PluginApiException.Invalid("\"kind\" is \"file\" or \"folder\"."),
        };
        var writable = call.String("access") switch
        {
            null or "read" => false,
            "readwrite" => true,
            _ => throw PluginApiException.Invalid("\"access\" is \"read\" or \"readwrite\"."),
        };
        var reason = call.RequiredString("reason");
        var name = PluginWorkbench.NameOf(call.Plugin);
        var title = folder
            ? PluginText.Get($"{name} 想访问一个文件夹", $"{name} asks for a folder")
            : PluginText.Get($"{name} 想访问一个文件", $"{name} asks for a file");
        var what = writable
            ? PluginText.Get("它将可以读取和修改你选的内容。", "It will be able to read and change what you pick.")
            : PluginText.Get("它将可以读取你选的内容。", "It will be able to read what you pick.");
        var accepted = await Dialog.ConfirmAsync(Window, title, Signed($"{reason}\n\n{what}", call.Plugin),
            PluginText.Get("选择…", "Choose…"), PluginText.Get("取消", "Cancel"), writable);
        if (!accepted) return PluginReply.Of((JsonNode?)null);

        var storage = Window.StorageProvider;
        string? path = folder
            ? (await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false })).FirstOrDefault()?.TryGetLocalPath()
            : (await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = false })).FirstOrDefault()?.TryGetLocalPath();
        if (path is null) return PluginReply.Of((JsonNode?)null);
        services.GetService<IPickedPathGrants>()?.Grant(call.Plugin, path, folder, writable);
        return PluginReply.Of(new JsonObject
        {
            ["kind"] = "picked",
            ["root"] = path,
            ["access"] = writable ? "readwrite" : "read",
        });
    }

    private async Task<PluginReply> ShowDialogAsync(PluginCall call)
    {
        var plugin = call.Plugin;
        var instanceId = call.RequiredString("instanceId");
        var renderer = new UiRenderer(Workbench.SinkFor(plugin, instanceId));
        var host = new Border { MinWidth = call.String("size") switch { "sm" => 320, "lg" => 720, _ => 480 }, MaxHeight = 640 };
        var surface = new DialogSurface(renderer, host);
        surface.Update(Take(call, "tree"));
        var dialog = new Dialog
        {
            Title = call.String("title") ?? "",
            Description = Signed("", plugin),
            Content = host,
            Size = call.String("size") switch { "sm" => ControlSize.Small, "lg" => ControlSize.Large, _ => ControlSize.Medium },
        };
        _dialogs[(plugin.Id, instanceId)] = dialog;
        Workbench.Attach(plugin.Id, instanceId, surface);
        try
        {
            await dialog.ShowAsync(Window);
        }
        finally
        {
            _dialogs.TryRemove((plugin.Id, instanceId), out _);
            Workbench.Detach(plugin.Id, instanceId, surface);
        }
        return PluginReply.Of((JsonNode?)null);
    }

    private sealed class DialogSurface(UiRenderer renderer, Border host) : IPluginSurface
    {
        private Control? _rendered;

        public void Update(JsonNode? tree)
        {
            _rendered = renderer.Render(tree, _rendered);
            if (!ReferenceEquals(host.Child, _rendered)) host.Child = _rendered;
        }
    }

    // ---------------------------------------------------------------- workspace

    private static JsonObject Describe(ProjectItem item, Project project) => new()
    {
        ["id"] = item.Id,
        ["projectId"] = project.Id,
        ["kind"] = item.Kind.ToString().ToLowerInvariant(),
        ["title"] = item.DisplayTitle,
        ["agentId"] = item.AgentId,
        ["viewId"] = item.ViewId,
        ["path"] = item.FilePath,
        ["active"] = project.ActiveItemId == item.Id,
    };

    public static JsonObject? DescribeActive(Project? project) =>
        project?.Items.FirstOrDefault(i => i.Id == project.ActiveItemId) is { } item ? Describe(item, project) : null;

    private PluginReply WorkspaceItems(PluginCall call)
    {
        call.Plugin.Grants.Require("projects");
        var id = call.String("projectId");
        var project = id is null ? Projects.Current : Projects.Projects.FirstOrDefault(p => p.Id == id);
        var list = new JsonArray();
        foreach (var item in project?.Items ?? []) list.Add((JsonNode)Describe(item, project!));
        return PluginReply.Of(list);
    }

    private PluginReply OpenView(PluginCall call)
    {
        var viewId = call.RequiredString("viewId");
        var view = call.Plugin.Manifest.Contributes.Views.FirstOrDefault(v => v.Id == viewId)
            ?? throw PluginApiException.Invalid($"\"{viewId}\" is not a view of {call.Plugin.Id}.");
        var item = Workbench.OpenView(call.Plugin, view, call["state"]?.DeepClone(), call.String("title"));
        return OfNullable(item?.Id);
    }

    private PluginReply OpenFile(PluginCall call)
    {
        call.Plugin.Grants.Require("workspace");
        var path = call.RequiredString("path");
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path)) throw new PluginApiException("NotFound", $"{path} does not exist.") { Path = path };
        var item = services.GetRequiredService<IItemOpener>().OpenFile(path);
        return OfNullable(item?.Id);
    }

    private PluginReply Activate(PluginCall call)
    {
        call.Plugin.Grants.Require("workspace");
        var item = Projects.FindItem(call.RequiredString("itemId")) ?? throw new PluginApiException("NotFound", "No such tab.");
        Projects.ActivateItem(item.Id);
        return PluginReply.Empty;
    }

    /// <summary>A plugin closes only its own view tabs: a terminal or an agent is the user's to end.</summary>
    private async Task<PluginReply> CloseAsync(PluginCall call)
    {
        var item = Projects.FindItem(call.RequiredString("itemId")) ?? throw new PluginApiException("NotFound", "No such tab.");
        if (item.Kind != ProjectItemKind.Plugin || !call.Plugin.Manifest.Contributes.Views.Any(v => v.Id == item.ViewId))
        {
            throw PluginApiException.Denied("workspace", canRequest: false);
        }
        await services.GetRequiredService<Workspace.IWorkspaceCommands>().CloseItemAsync(item.Id);
        return PluginReply.Empty;
    }

    private PluginReply LaunchAgent(PluginCall call)
    {
        var options = new AgentLaunchOptions
        {
            Model = call.String("model"),
            Effort = call.String("effort"),
            Prompt = call.String("prompt"),
        };
        var item = services.GetRequiredService<IItemOpener>().OpenAgent(call.RequiredString("agentId"), options)
            ?? throw new PluginApiException("NotFound", "The agent is not available or no project is open.");
        return PluginReply.Of(item.Id);
    }

    // ---------------------------------------------------------------- links

    /// <summary>https links to the hosts the manifest lists under <c>shell.open</c>, or any https link when it lists "https".</summary>
    private async Task<PluginReply> OpenExternalAsync(PluginCall call)
    {
        var grants = call.Plugin.Grants;
        grants.Require("shell.open");
        var text = call.String("target") ?? call.RequiredString("url");
        if (!Uri.TryCreate(text, UriKind.Absolute, out var url) || url.Scheme is not ("https" or "http" or "mailto"))
        {
            throw PluginApiException.Invalid("Only http, https and mailto links can be opened.");
        }
        var allowed = grants.Details("shell.open");
        var ok = allowed.Contains(url.Scheme, StringComparer.OrdinalIgnoreCase) || (url.Scheme != "mailto" && NetModule.HostAllowed(url.Host, allowed));
        if (!ok) throw PluginApiException.Denied($"shell.open:{url.Host}", canRequest: false);
        var launched = await Window.Launcher.LaunchUriAsync(url);
        return PluginReply.Of(launched);
    }

    // ---------------------------------------------------------------- settings

    /// <summary>The few settings a plugin may read (design 09 §settings): appearance and language, nothing personal.</summary>
    private JsonNode? SettingsValue(string key)
    {
        var settings = services.GetRequiredService<ISettingsStore>().Current;
        return key switch
        {
            "appearance.theme" => settings.Appearance.Theme,
            "appearance.language" => PluginText.Language,
            "terminal.fontFamily" => settings.Terminal.FontFamily,
            "terminal.fontSize" => settings.Terminal.FontSize,
            _ => throw PluginApiException.Invalid($"\"{key}\" is not a setting plugins can read."),
        };
    }
}

/// <summary>Turns a path the user picked for a plugin into a grant (implemented with the file access policy).</summary>
public interface IPickedPathGrants
{
    void Grant(PluginHost plugin, string path, bool folder, bool writable);
}
