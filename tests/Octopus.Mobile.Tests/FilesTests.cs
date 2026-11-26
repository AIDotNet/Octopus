using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Octopus.Mobile.Common;
using Octopus.Mobile.Files;
using Octopus.Mobile.Sessions;
using Octopus.Mobile.Terminals;
using Octopus.Mobile.Workspace;
using Octopus.Remote.Tests;
using Xunit;

namespace Octopus.Mobile.Tests;

/// <summary>
/// Route 2 (design/ui/移动端功能扩展方案.html, ⑤): browsing a project's files, read-only file viewing, git status and
/// unified diffs, both on the demo workspace and through the real <see cref="Octopus.Remote.Client.RemoteHostClient"/>.
/// </summary>
public class FilesTests
{
    [AvaloniaFact]
    public async Task Browsing_files_walks_folders_uses_breadcrumbs_and_opens_a_text_file()
    {
        using var phone = new Phone();
        var project = phone.Workspace.Hosts[0].Projects[0];
        // A session in a branch worktree opens that worktree's folder, not the default project folder.
        var worktree = await phone.Workspace.CreateWorkspaceAsync(project, new CreateBranchWorkspaceRequest(CreateBranch: true, BranchName: "feat/auth-split", BaseRef: null, IsRemoteBranch: false));
        var worktreeSession = await phone.Workspace.CreateSessionAsync(project, new NewSessionRequest("claude", null, null, null, worktree.Id));
        var expectedWorktreePath = RemoteFolders.WorkspacePathOf(worktreeSession);
        Assert.Equal(worktree.Path, expectedWorktreePath);

        phone.Shell.ShowSessionDetails(worktreeSession);
        Phone.Pump();
        var detailPage = phone.Top<SessionDetailPage>();
        var details = (SessionDetailViewModel)detailPage.DataContext!;
        Assert.True(details.CanBrowseFiles);
        Assert.True(details.CanViewChanges);
        Assert.True(detailPage.FindControl<Control>("BrowseFilesRow")!.IsVisible);
        Assert.True(detailPage.FindControl<Control>("ViewChangesRow")!.IsVisible);

        details.BrowseFilesCommand.Execute(null);
        var filesPage = phone.Top<FilesPage>();
        var files = (FilesViewModel)filesPage.DataContext!;
        Assert.True(Phone.Until(() => !files.IsLoading && files.Entries.Count > 0));
        Assert.Equal(expectedWorktreePath, files.Folder);
        // Folders come before files.
        Assert.True(files.Entries[0].IsDirectory);
        Assert.Contains(files.Entries, entry => entry.Name == "src" && entry.IsDirectory);
        Assert.Contains(files.Entries, entry => entry.Name == "README.md" && !entry.IsDirectory);

        // Walk into src -> Auth -> open TokenIssuer.cs.
        files.Entries.Single(entry => entry.Name == "src").OpenCommand.Execute(null);
        Assert.True(Phone.Until(() => !files.IsLoading && files.Crumbs.Count == 2));
        Assert.Equal(["src"], files.Crumbs.Skip(1).Select(crumb => crumb.Name));

        files.Entries.Single(entry => entry.Name == "Auth").OpenCommand.Execute(null);
        Assert.True(Phone.Until(() => !files.IsLoading && files.Crumbs.Count == 3));

        files.Entries.Single(entry => entry.Name == "TokenIssuer.cs").OpenCommand.Execute(null);
        var viewerPage = phone.Top<FileViewerPage>();
        var viewer = (FileViewerViewModel)viewerPage.DataContext!;
        Assert.True(Phone.Until(() => !viewer.IsLoading && viewer.Lines.Count > 0));
        Assert.Equal("TokenIssuer.cs", viewer.Title);
        Assert.Contains("src/Auth", viewer.Subtitle);
        Assert.Contains("只读", viewer.Subtitle);
        Assert.Contains($"{viewer.LineCount} 行", viewer.Subtitle);
        Assert.Equal("1", viewer.Lines[0].Number);
        Assert.Contains("namespace Octopus.Auth;", viewer.Lines[0].Text);
        Assert.False(viewer.HasMessage);

        // Wrap toggle flips wrapping and horizontal scroll mode.
        Assert.False(viewer.IsWrapped);
        viewer.ToggleWrapCommand.Execute(null);
        Assert.True(viewer.IsWrapped);

        // Back to Auth, then tap the "src" crumb to jump back up.
        viewer.BackCommand.Execute(null);
        Assert.True(Phone.Until(() => phone.Shell.Top is FilesPage));
        files.Crumbs[1].OpenCommand.Execute(null);
        Assert.True(Phone.Until(() => !files.IsLoading && files.Crumbs.Count == 2));
        Assert.Contains(files.Entries, entry => entry.Name == "Startup.cs");
    }

    [AvaloniaFact]
    public void Binary_capped_and_unsupported_files_show_clear_messages()
    {
        using var phone = new Phone();
        var project = phone.Workspace.Hosts[0].Projects[0];

        // Image file renders an in-app preview with zoom controls and disposes its bitmap on back.
        phone.Shell.ShowFile(project, project.Path + "/assets/logo.png", "assets");
        var imagePage = phone.Top<FileViewerPage>();
        var imageViewer = (FileViewerViewModel)imagePage.DataContext!;
        Assert.True(Phone.Until(() => !imageViewer.IsLoading && imageViewer.ShowImage));
        Assert.True(imageViewer.IsImage);
        Assert.NotNull(imageViewer.ImageBitmap);
        Assert.Equal(1, imageViewer.ImageWidth);
        Assert.Equal(1, imageViewer.ImageHeight);
        Assert.Equal("PNG", imageViewer.ImageFormatName);
        Assert.Contains("1×1", imageViewer.Subtitle);
        Assert.True(imageViewer.IsZoomFit);
        Assert.True(imageViewer.CanZoomIn);
        Assert.False(imageViewer.CanZoomOut);

        imageViewer.ZoomInCommand.Execute(null);
        Assert.False(imageViewer.IsZoomFit);
        Assert.Equal(1.5, imageViewer.ZoomScale);
        Assert.True(imageViewer.CanZoomOut);

        imageViewer.ResetZoomCommand.Execute(null);
        Assert.True(imageViewer.IsZoomFit);
        Assert.Equal(1.0, imageViewer.ZoomScale);

        phone.Shell.Back();
        Assert.Null(imageViewer.ImageBitmap);

        // Non-image binary file shows a clear non-text notice.
        phone.Shell.ShowFile(project, project.Path + "/assets/data.bin", "assets");
        var binaryViewer = (FileViewerViewModel)phone.Top<FileViewerPage>().DataContext!;
        Assert.True(Phone.Until(() => !binaryViewer.IsLoading && binaryViewer.HasMessage));
        Assert.Contains("文本文件", binaryViewer.Message);
        Assert.Empty(binaryViewer.Lines);
        phone.Shell.Back();

        // Multi-thousand-line log is capped at MaxLines so the page stays responsive.
        phone.Shell.ShowFile(project, project.Path + "/logs/build.log", "logs");
        var logPage = phone.Top<FileViewerPage>();
        var logViewer = (FileViewerViewModel)logPage.DataContext!;
        Assert.True(Phone.Until(() => !logViewer.IsLoading && logViewer.Lines.Count == FileViewerViewModel.MaxLines));
        Assert.True(logViewer.IsCapped);
        Assert.Equal(6000, logViewer.LineCount);
        Assert.Contains("5000", logViewer.CappedText);
        Assert.True(logPage.FindControl<Control>("CappedBanner")!.IsVisible);
        phone.Shell.Back();

        // When the host lacks files/git capabilities, entries are hidden and direct navigation reports an error.
        phone.Workspace.FilesSupported = false;
        var session = project.Sessions[2];
        phone.Shell.ShowSession(session);
        Assert.True(Phone.Until(() => phone.Top<TerminalPage>().ReadScreenText().Trim().Length > 0));
        var terminalPage = phone.Top<TerminalPage>();
        var terminal = (TerminalViewModel)terminalPage.DataContext!;
        var overlay = terminalPage.FindControl<SessionWorkspaceOverlay>("WorkspaceOverlay")!;
        terminal.OpenMenuCommand.Execute(null);
        Phone.Pump();
        Assert.True(overlay.IsPopoverOpen);
        Assert.DoesNotContain("项目文件管理", terminal.WorkspaceMenu.VisibleMenuLabels());
        Assert.DoesNotContain("查看改动", terminal.WorkspaceMenu.VisibleMenuLabels());

        phone.Shell.ShowFiles(project);
        var files = (FilesViewModel)phone.Top<FilesPage>().DataContext!;
        Assert.True(Phone.Until(() => !files.IsLoading && files.HasError));
        Assert.Contains("更新", files.ErrorText);
    }

    [AvaloniaFact]
    public void Changes_list_and_diff_viewer_show_modifications_binary_and_empty_states()
    {
        using var phone = new Phone();
        var octopus = phone.Workspace.Hosts[0].Projects[0];
        var other = phone.Workspace.Hosts[0].Projects[1];

        // Open changes on Octopus: 6 changes with badges and line counts.
        phone.Shell.ShowChanges(octopus);
        var changesPage = phone.Top<ChangesPage>();
        var changes = (ChangesViewModel)changesPage.DataContext!;
        Assert.True(Phone.Until(() => !changes.IsLoading && changes.Rows.Count == 6));
        Assert.Equal("Octopus · main", changes.Subtitle);
        Assert.Contains("6", changes.CountText);

        var modified = changes.Rows.Single(row => row.Name == "TokenService.cs");
        Assert.Equal("M", modified.Badge);
        Assert.True(modified.IsModified);
        Assert.True(modified.HasCounts);
        Assert.NotNull(modified.AddedText);
        Assert.NotNull(modified.RemovedText);

        var added = changes.Rows.Single(row => row.Name == "TokenValidator.cs");
        Assert.Equal("A", added.Badge);
        Assert.True(added.IsAdded);
        Assert.Contains("已暂存", added.Subtitle);

        var deleted = changes.Rows.Single(row => row.Name == "old-notes.md");
        Assert.Equal("D", deleted.Badge);
        Assert.True(deleted.IsRemoved);

        // Tap TokenService.cs -> DiffPage with hunk, added, and removed lines.
        modified.OpenCommand.Execute(null);
        var diffPage = phone.Top<DiffPage>();
        var diff = (DiffViewModel)diffPage.DataContext!;
        Assert.True(Phone.Until(() => !diff.IsLoading && diff.Lines.Count > 0));
        Assert.Equal("TokenService.cs", diff.Title);
        Assert.Equal("src/Auth", diff.Subtitle);
        Assert.Contains(diff.Lines, line => line.IsHunk);
        Assert.Contains(diff.Lines, line => line.IsAdded && line.Marker == "+");
        Assert.Contains(diff.Lines, line => line.IsRemoved && line.Marker == "-");
        Assert.True(diff.HasAdded);
        Assert.True(diff.HasRemoved);
        Assert.Contains("src/Auth/TokenService.cs", phone.Workspace.DiffsRead);
        phone.Shell.Back();

        // Tap binary change -> binary diff message.
        changes.Rows.Single(row => row.Name == "banner.png").OpenCommand.Execute(null);
        var binaryDiff = (DiffViewModel)phone.Top<DiffPage>().DataContext!;
        Assert.True(Phone.Until(() => !binaryDiff.IsLoading && binaryDiff.HasMessage));
        Assert.Contains("二进制", binaryDiff.Message);
        phone.Shell.Back();

        // Refresh button reloads cleanly.
        changes.RefreshCommand.Execute(null);
        Assert.True(Phone.Until(() => !changes.IsLoading && !changes.IsRefreshing && changes.Rows.Count == 6));
        phone.Shell.Back();

        // Project with no uncommitted changes shows the empty state.
        phone.Shell.ShowChanges(other);
        var emptyPage = phone.Top<ChangesPage>();
        var empty = (ChangesViewModel)emptyPage.DataContext!;
        Assert.True(Phone.Until(() => !empty.IsLoading && empty.IsEmpty));
        Assert.True(emptyPage.FindControl<Control>("EmptyText")!.IsVisible);
        Assert.False(empty.HasRows);
    }

    [AvaloniaFact]
    public async Task Remote_files_and_git_work_through_the_protocol_including_too_large_and_not_repo()
    {
        await using var relay = new FakeRelay();
        await using var computer = await Computer.StartAsync(relay);
        var repo = computer.Workspace.AddProject("App", "/repo/app", isGitRepository: true, defaultBranch: "main");
        var plain = computer.Workspace.AddProject("Notes", "/repo/notes", isGitRepository: false);

        // Seed files: UTF-8 with BOM, nested folder, hidden .git folder (must be filtered out), binary, valid PNG, and >1MB too-large file.
        computer.Workspace.Files["/repo/app/.git/HEAD"] = Encoding.UTF8.GetBytes("ref: refs/heads/main\n");
        computer.Workspace.Files["/repo/app/src/Main.cs"] = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("line 1\r\nline 2\n")];
        computer.Workspace.Files["/repo/app/img.bin"] = [0x89, 0x50, 0x4E, 0x47, 0x00, 0x01];
        computer.Workspace.Files["/repo/app/shot.png"] = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
        computer.Workspace.Files["/repo/app/huge.log"] = new byte[FileViewerViewModel.MaxBytes + 64];

        // Respond to git/call using the exact camelCase JSON schema of Octopus.App's RemoteGitDispatcher.
        computer.Workspace.GitHandler = (method, arguments) =>
        {
            using var doc = JsonDocument.Parse(arguments);
            var root = doc.RootElement.GetProperty("root").GetString();
            return method switch
            {
                "FindRepositoryRootAsync" => root == "/repo/app" ? """{"text":"/repo/app"}""" : "{}",
                "GetStatusAsync" => """
                    {
                      "status": {
                        "branch": "feat/mobile",
                        "changes": [
                          { "path": "src/Main.cs", "fullPath": "/repo/app/src/Main.cs", "kind": 0, "isStaged": false, "isDirectory": false },
                          { "path": "old.txt", "fullPath": "/repo/app/old.txt", "kind": 2, "isStaged": true, "isDirectory": false }
                        ]
                      }
                    }
                    """,
                "GetFileDiffAsync" => doc.RootElement.GetProperty("path").GetString() == "src/Main.cs"
                    ? """
                      {
                        "diff": {
                          "kind": 0,
                          "totalLines": 3,
                          "added": 1,
                          "removed": 1,
                          "lines": [
                            { "kind": 0, "text": "@@ -1,2 +1,2 @@" },
                            { "kind": 3, "oldNumber": 1, "text": "old line" },
                            { "kind": 2, "newNumber": 1, "text": "new line" }
                          ]
                        }
                      }
                      """
                    : """{"diff":{"kind":3,"lines":[],"totalLines":0,"added":0,"removed":0}}""",
                _ => throw new InvalidOperationException("Unexpected git method: " + method),
            };
        };

        using var phone = new RealPhone(relay);
        phone.SignIn(relay);
        phone.Pair(computer);

        var host = phone.Remote.Workspace.Hosts.Single(h => h.Id == computer.Identity.Id);
        Assert.True(Phone.Until(() => host.Projects.Count == 2));
        var remoteRepo = host.Projects.Single(p => p.Name == "App");
        var remotePlain = host.Projects.Single(p => p.Name == "Notes");
        Assert.True(phone.Remote.Workspace.SupportsFiles(host));
        Assert.True(phone.Remote.Workspace.SupportsGit(host));

        // 1. List files: .git is hidden, folders come first.
        phone.Shell.ShowFiles(remoteRepo);
        var files = phone.Model<FilesViewModel>();
        Assert.True(Phone.Until(() => !files.IsLoading && files.Entries.Count == 4));
        Assert.Equal(["src", "huge.log", "img.bin", "shot.png"], files.Entries.Select(entry => entry.Name));

        // 2. Read UTF-8 BOM text file through fs/read.
        files.Entries[0].OpenCommand.Execute(null);
        Assert.True(Phone.Until(() => !files.IsLoading && files.Entries.Count == 1));
        files.Entries[0].OpenCommand.Execute(null);
        var textViewer = phone.Model<FileViewerViewModel>();
        Assert.True(Phone.Until(() => !textViewer.IsLoading && textViewer.Lines.Count == 2));
        Assert.Equal("line 1", textViewer.Lines[0].Text);
        Assert.Equal("line 2", textViewer.Lines[1].Text);
        phone.Shell.Back();
        phone.Shell.Back();

        // 3. Too-large file stops at the first fs/read piece and reports its size.
        phone.Shell.ShowFile(remoteRepo, "/repo/app/huge.log");
        var hugeViewer = phone.Model<FileViewerViewModel>();
        Assert.True(Phone.Until(() => !hugeViewer.IsLoading && hugeViewer.HasMessage));
        Assert.Contains("1 MB", hugeViewer.Message);
        phone.Shell.Back();

        // 4. Binary file reports non-text; PNG file decodes and renders as image preview over the remote protocol.
        phone.Shell.ShowFile(remoteRepo, "/repo/app/img.bin");
        var binViewer = phone.Model<FileViewerViewModel>();
        Assert.True(Phone.Until(() => !binViewer.IsLoading && binViewer.HasMessage));
        Assert.Contains("文本文件", binViewer.Message);
        phone.Shell.Back();

        phone.Shell.ShowFile(remoteRepo, "/repo/app/shot.png");
        var imgViewer = phone.Model<FileViewerViewModel>();
        Assert.True(Phone.Until(() => !imgViewer.IsLoading && imgViewer.ShowImage));
        Assert.Equal((1, 1, "PNG"), (imgViewer.ImageWidth, imgViewer.ImageHeight, imgViewer.ImageFormatName));
        phone.Shell.Back();

        // 5. Git status + async diff-based line count backfill + DiffPage.
        phone.Shell.ShowChanges(remoteRepo);
        var changes = phone.Model<ChangesViewModel>();
        Assert.True(Phone.Until(() => !changes.IsLoading && changes.Rows.Count == 2 && changes.Rows[0].HasCounts));
        Assert.Equal("App · feat/mobile", changes.Subtitle);
        Assert.Equal("+1", changes.Rows[0].AddedText);
        Assert.Equal("−1", changes.Rows[0].RemovedText);

        changes.Rows[0].OpenCommand.Execute(null);
        var diff = phone.Model<DiffViewModel>();
        Assert.True(Phone.Until(() => !diff.IsLoading && diff.Lines.Count == 3));
        Assert.True(diff.Lines[0].IsHunk);
        Assert.True(diff.Lines[1].IsRemoved);
        Assert.True(diff.Lines[2].IsAdded);
        phone.Shell.Back();
        phone.Shell.Back();

        // 6. Non-repository folder reports IsNotRepository.
        phone.Shell.ShowChanges(remotePlain);
        var notRepo = phone.Model<ChangesViewModel>();
        Assert.True(Phone.Until(() => !notRepo.IsLoading && notRepo.IsNotRepository));
        Assert.True(phone.Top<ChangesPage>().FindControl<Control>("NotRepositoryText")!.IsVisible);
    }

    [AvaloniaFact]
    public void Markdown_file_opens_in_rendered_preview_toggles_to_source_and_opens_relative_links()
    {
        using var phone = new Phone();
        var project = phone.Workspace.Hosts[0].Projects[0];

        phone.Shell.ShowFile(project, project.Path + "/README.md", project.Name);
        var page = phone.Top<FileViewerPage>();
        var viewer = (FileViewerViewModel)page.DataContext!;
        Assert.True(Phone.Until(() => !viewer.IsLoading && viewer.ShowMarkdownPreview));
        Assert.True(viewer.IsMarkdownFile);
        Assert.True(viewer.ShowPreviewToggle);
        Assert.False(viewer.ShowCodeLines);
        Assert.True(page.FindControl<MobileMarkdownView>("MarkdownPreview")!.IsVisible);

        // Toggle to raw source lines and back to preview.
        viewer.TogglePreviewCommand.Execute(null);
        Phone.Pump();
        Assert.False(viewer.ShowMarkdownPreview);
        Assert.True(viewer.ShowCodeLines);
        Assert.NotEmpty(viewer.Lines);

        viewer.TogglePreviewCommand.Execute(null);
        Phone.Pump();
        Assert.True(viewer.ShowMarkdownPreview);

        // Clicking a web link opens the browser; clicking a relative image link opens the in-app image preview.
        viewer.OpenLinkCommand.Execute("https://octopus.example/docs");
        Assert.Equal("https://octopus.example/docs", phone.Opened?.ToString());

        viewer.OpenLinkCommand.Execute("assets/logo.png");
        var imageViewer = (FileViewerViewModel)phone.Top<FileViewerPage>().DataContext!;
        Assert.True(Phone.Until(() => !imageViewer.IsLoading && imageViewer.ShowImage));
        Assert.Equal("logo.png", imageViewer.Title);
    }

    [AvaloniaFact]
    public async Task Older_host_without_files_or_git_capabilities_hides_entries_and_reports_unsupported()
    {
        await using var relay = new FakeRelay();
        await using var computer = await Computer.StartAsync(relay, advertiseFiles: false, advertiseGit: false);
        var project = computer.Workspace.AddProject("Legacy", "/repo/legacy");
        computer.Workspace.AddItem(project, "zsh");

        using var phone = new RealPhone(relay);
        phone.SignIn(relay);
        phone.Pair(computer);

        var host = phone.Remote.Workspace.Hosts.Single(h => h.Id == computer.Identity.Id);
        Assert.True(Phone.Until(() => host.Projects.Count == 1 && host.Projects[0].Sessions.Count == 1));
        Assert.False(phone.Remote.Workspace.SupportsFiles(host));
        Assert.False(phone.Remote.Workspace.SupportsGit(host));

        // Details page hides the browse & changes rows.
        phone.Shell.ShowSessionDetails(host.Projects[0].Sessions[0]);
        Phone.Pump();
        var details = phone.Model<SessionDetailViewModel>();
        Assert.False(details.CanBrowseFiles);
        Assert.False(details.CanViewChanges);

        // Direct navigation fails gracefully with the localized "update Octopus" message.
        phone.Shell.ShowFiles(host.Projects[0]);
        var files = phone.Model<FilesViewModel>();
        Assert.True(Phone.Until(() => !files.IsLoading && files.HasError));
        Assert.Contains("更新", files.ErrorText);
        phone.Shell.Back();

        phone.Shell.ShowChanges(host.Projects[0]);
        var changes = phone.Model<ChangesViewModel>();
        Assert.True(Phone.Until(() => !changes.IsLoading && changes.HasError));
        Assert.Contains("更新", changes.ErrorText);
    }

    [AvaloniaFact]
    public void Files_and_changes_pages_use_virtualized_lists_and_support_instant_filtering()
    {
        using var phone = new Phone();
        var octopus = phone.Workspace.Hosts[0].Projects[0];

        // FilesPage uses a ListBox with VirtualizingStackPanel and filters client-side.
        phone.Shell.ShowFiles(octopus);
        var filesPage = phone.Top<FilesPage>();
        var files = (FilesViewModel)filesPage.DataContext!;
        Assert.True(Phone.Until(() => !files.IsLoading && files.Entries.Count > 1));
        var filesList = filesPage.FindControl<ListBox>("Entries");
        Assert.NotNull(filesList);
        Assert.IsType<VirtualizingStackPanel>(filesList!.ItemsPanelRoot);

        var totalFiles = files.Entries.Count;
        files.SearchQuery = "README";
        Assert.Single(files.Entries);
        Assert.Equal("README.md", files.Entries[0].Name);
        files.SearchQuery = "";
        Assert.Equal(totalFiles, files.Entries.Count);
        phone.Shell.Back();

        // ChangesPage uses a ListBox with VirtualizingStackPanel and filters client-side.
        phone.Shell.ShowChanges(octopus);
        var changesPage = phone.Top<ChangesPage>();
        var changes = (ChangesViewModel)changesPage.DataContext!;
        Assert.True(Phone.Until(() => !changes.IsLoading && changes.Rows.Count == 6));
        var changeList = changesPage.FindControl<ListBox>("ChangeList");
        Assert.NotNull(changeList);
        Assert.IsType<VirtualizingStackPanel>(changeList!.ItemsPanelRoot);

        changes.SearchQuery = "TokenService";
        Assert.Single(changes.Rows);
        Assert.Equal("TokenService.cs", changes.Rows[0].Name);
        changes.SearchQuery = "";
        Assert.Equal(6, changes.Rows.Count);
    }

    [AvaloniaFact]
    public void Mobile_loading_view_animates_only_while_visible_attached_and_motion_enabled()
    {
        var view = new MobileLoadingView { Variant = MobileLoadingVariant.List, Label = "正在加载文件…" };
        var window = new Window { Width = 390, Height = 500, Content = view };
        window.Show();
        try
        {
            Phone.Pump();
            Assert.True(view.IsTimerRunning);

            // Hiding pauses the animation timer immediately (zero idle CPU/battery).
            view.IsVisible = false;
            Phone.Pump();
            Assert.False(view.IsTimerRunning);

            view.IsVisible = true;
            Phone.Pump();
            Assert.True(view.IsTimerRunning);

            // Exercise all layout variants to verify measure/render cleanly.
            foreach (var variant in Enum.GetValues<MobileLoadingVariant>())
            {
                view.Variant = variant;
                window.UpdateLayout();
            }
        }
        finally
        {
            window.Close();
            Phone.Pump();
        }
        Assert.False(view.IsTimerRunning);
    }
}
