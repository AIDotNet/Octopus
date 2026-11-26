using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Octopus.Acp.Dialects;
using Octopus.Acp.Protocol;
using Octopus.App.AgentGui;
using Octopus.App.Composition;
using Octopus.App.Services;
using Octopus.App.Workspace;
using Octopus.Core.Agents;
using Octopus.Core.Terminals;
using Octopus.Storage;
using Octopus.Terminal;
using Octopus.Terminal.Avalonia;
using TKCode.Arc.Icons.Lucide;
using Xunit;

namespace Octopus.App.Tests;

public class FileDropTests
{
    [AvaloniaFact]
    public async Task TerminalControl_accepts_external_file_drops_and_writes_quoted_absolute_paths()
    {
        var tempDir = Directory.CreateTempSubdirectory("octopus-term-drop-");
        var fileA = Path.Combine(tempDir.FullName, "notes.txt");
        var fileB = Path.Combine(tempDir.FullName, "my screenshot (1).png");
        var folderC = Path.Combine(tempDir.FullName, "sub dir");
        File.WriteAllText(fileA, "hello");
        File.WriteAllText(fileB, "png");
        Directory.CreateDirectory(folderC);

        var factory = new RecordingFactory();
        var terminal = new TerminalControl();
        var window = new Window { Width = 720, Height = 320, Content = terminal };
        window.Show();
        try
        {
            Assert.True(DragDrop.GetAllowDrop(terminal));

            // Before a process starts, file drops are rejected.
            var filesPayload = await BuildFilesTransferAsync(window.StorageProvider, fileA, fileB, folderC);
            var idleOver = RaiseDrag(terminal, DragDrop.DragOverEvent, filesPayload);
            Assert.Equal(DragDropEffects.None, idleOver.DragEffects);
            Assert.True(idleOver.Handled);

            terminal.Start(new LaunchSpec("test-shell", [], tempDir.FullName, new Dictionary<string, string>()), factory);
            Assert.True(Frames.Until(() => terminal.IsRunning));

            // Non-file drag is rejected.
            var textPayload = new DataTransfer();
            textPayload.Add(DataTransferItem.CreateText("just text"));
            var textOver = RaiseDrag(terminal, DragDrop.DragEnterEvent, textPayload);
            Assert.Equal(DragDropEffects.None, textOver.DragEffects);

            // File drag is accepted on both DragEnter and DragOver.
            var enterArgs = RaiseDrag(terminal, DragDrop.DragEnterEvent, filesPayload);
            Assert.Equal(DragDropEffects.Copy, enterArgs.DragEffects);
            var overArgs = RaiseDrag(terminal, DragDrop.DragOverEvent, filesPayload);
            Assert.Equal(DragDropEffects.Copy, overArgs.DragEffects);

            // Drop inserts formatted absolute paths into the PTY.
            var dropArgs = RaiseDrag(terminal, DragDrop.DropEvent, filesPayload);
            Assert.Equal(DragDropEffects.Copy, dropArgs.DragEffects);
            Assert.True(dropArgs.Handled);

            var expected = TerminalFileDrop.FormatPaths([fileA, fileB, folderC]);
            Assert.True(Frames.Until(() => string.Concat(factory.Connection.Writes).Contains(expected, StringComparison.Ordinal)));
            Assert.Equal(expected, string.Concat(factory.Connection.Writes));
        }
        finally
        {
            await terminal.CloseAsync();
            window.Close();
            Dispatcher.UIThread.RunJobs();
            try { tempDir.Delete(recursive: true); } catch (IOException) { }
        }
    }

    [AvaloniaFact]
    public async Task AgentGuiTabView_accepts_external_files_folders_and_images_and_preserves_full_paths_in_prompts()
    {
        var workspaceDir = Directory.CreateTempSubdirectory("octopus-gui-ws-");
        var externalDir = Directory.CreateTempSubdirectory("octopus-gui-ext-");
        var siblingDir = Directory.CreateDirectory(workspaceDir.FullName + "-sibling");
        try
        {
            var wsSubDir = Directory.CreateDirectory(Path.Combine(workspaceDir.FullName, "src"));
            var wsFile = Path.Combine(wsSubDir.FullName, "Program.cs");
            var extFile = Path.Combine(externalDir.FullName, "spec notes.md");
            var extFolder = Directory.CreateDirectory(Path.Combine(externalDir.FullName, "logs")).FullName;
            var extImage = Path.Combine(externalDir.FullName, "shot.png");
            var siblingFile = Path.Combine(siblingDir.FullName, "other.txt");
            File.WriteAllText(wsFile, "// code");
            File.WriteAllText(extFile, "# spec");
            File.WriteAllBytes(extImage, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
            File.WriteAllText(siblingFile, "sibling");

            var fakeFactory = AppHost.Get<FakeAcpSessionFactory>();
            var settings = AppHost.Get<ISettingsStore>();
            settings.Update(s => s.Agents.DefaultSurface = AgentSurface.Gui);

            var projects = AppHost.Get<IProjectService>();
            var project = projects.AddProject(workspaceDir.FullName, "gui-drop-test");
            var opener = AppHost.Get<IItemOpener>();
            var registry = AppHost.Get<ITabContentRegistry>();

            fakeFactory.ConfigureNext = session =>
            {
                session.Initialized = new AcpInitializeResult
                {
                    ProtocolVersion = 1,
                    AgentCapabilities = new() { PromptCapabilities = new() { Image = true } },
                };
            };

            var item = opener.OpenAgent("opencode")!;
            var view = Assert.IsType<AgentGuiTabView>(registry.GetOrCreate(project, item));
            var vm = Assert.IsType<AgentGuiTabViewModel>(view.DataContext);
            var window = new Window { Width = 900, Height = 700, Content = view };
            window.Show();
            try
            {
                Assert.True(Frames.Until(() => vm.Composer.SupportsImageAttachments));

                var dropOverlay = view.FindControl<Border>("DropOverlay")!;
                Assert.False(dropOverlay.IsVisible);

                // Non-file drag is rejected and keeps overlay hidden.
                var textPayload = new DataTransfer();
                textPayload.Add(DataTransferItem.CreateText("hello"));
                var textEnter = RaiseDrag(view, DragDrop.DragEnterEvent, textPayload);
                Assert.Equal(DragDropEffects.None, textEnter.DragEffects);
                Assert.False(dropOverlay.IsVisible);

                // File drag highlights overlay on DragEnter/DragOver and clears on DragLeave.
                var filesPayload = await BuildFilesTransferAsync(window.StorageProvider, wsFile, extFile, extFolder, extImage, siblingFile);
                var enterArgs = RaiseDrag(view, DragDrop.DragEnterEvent, filesPayload);
                Assert.Equal(DragDropEffects.Copy, enterArgs.DragEffects);
                Assert.True(dropOverlay.IsVisible);

                RaiseDrag(view, DragDrop.DragLeaveEvent, filesPayload);
                Assert.False(dropOverlay.IsVisible);

                // Drop attaches all 5 items cleanly and hides overlay.
                var dropArgs = RaiseDrag(view, DragDrop.DropEvent, filesPayload);
                Assert.Equal(DragDropEffects.Copy, dropArgs.DragEffects);
                Assert.True(dropArgs.Handled);
                Assert.False(dropOverlay.IsVisible);

                Assert.True(Frames.Until(() => vm.Attachments.Count == 5));

                // Workspace file keeps relative DisplayName.
                var wsChip = Assert.Single(vm.Attachments, c => c.Attachment.Path == wsFile);
                Assert.Equal("file", wsChip.Attachment.Kind);
                Assert.Equal("src/Program.cs", wsChip.Attachment.DisplayName);
                Assert.Equal("src/Program.cs", wsChip.Label);
                Assert.Equal(wsFile, wsChip.Tooltip);

                // External file preserves full path in DisplayName while chip shows compact filename + full-path tooltip.
                var extFileChip = Assert.Single(vm.Attachments, c => c.Attachment.Path == extFile);
                Assert.Equal("file", extFileChip.Attachment.Kind);
                Assert.Equal(extFile, extFileChip.Attachment.DisplayName);
                Assert.Equal("spec notes.md", extFileChip.Label);
                Assert.Equal(extFile, extFileChip.Tooltip);

                // External folder is supported with folder kind and Folder icon.
                var folderChip = Assert.Single(vm.Attachments, c => c.Attachment.Path == extFolder);
                Assert.Equal("folder", folderChip.Attachment.Kind);
                Assert.Equal(LucideIconKind.Folder, folderChip.Icon);
                Assert.Equal("logs", folderChip.Label);
                Assert.Equal(extFolder, folderChip.Tooltip);

                // Sibling directory sharing workspace prefix is treated as external (no "../" false positive).
                var siblingChip = Assert.Single(vm.Attachments, c => c.Attachment.Path == siblingFile);
                Assert.Equal(siblingFile, siblingChip.Attachment.DisplayName);
                Assert.Equal("other.txt", siblingChip.Label);

                // Image file attaches as base64 image when agent supports images.
                var imgChip = Assert.Single(vm.Attachments, c => c.Attachment.Path == extImage);
                Assert.Equal("image", imgChip.Attachment.Kind);
                Assert.Equal("image/png", imgChip.Attachment.MimeType);
                Assert.False(string.IsNullOrEmpty(imgChip.Attachment.Base64Data));

                // Re-dropping the same file does not add duplicates.
                RaiseDrag(view, DragDrop.DropEvent, await BuildFilesTransferAsync(window.StorageProvider, extFile));
                Frames.Pump();
                Assert.Equal(5, vm.Attachments.Count);

                // Verify ACP prompt block generation for both dialect modes:
                var attachedList = vm.Attachments.Select(c => c.Attachment).ToList();

                // 1) Plain-text reference dialect (e.g. Claude / Codex / Generic: LinksFileMentions == false)
                var plainBlocks = AcpProtocolParser.BuildPromptBlocks("check these", attachedList, new GenericAcpDialect(), agentSupportsImages: true);
                var promptText = plainBlocks[0]!["text"]!.GetValue<string>();
                Assert.Contains("@src/Program.cs", promptText);
                Assert.Contains($"\"{extFile}\"", promptText);
                Assert.Contains(extFolder, promptText);
                Assert.Contains(siblingFile, promptText);
                Assert.Contains(plainBlocks, b => b?["type"]?.GetValue<string>() == "image");

                // 2) Resource-link dialect (e.g. OpenCode / Gemini: LinksFileMentions == true)
                var linkedBlocks = AcpProtocolParser.BuildPromptBlocks("check these", attachedList, new OpenCodeAcpDialect(), agentSupportsImages: true);
                var links = linkedBlocks.Where(b => b?["type"]?.GetValue<string>() == "resource_link").ToList();
                Assert.Equal(4, links.Count);
                Assert.Contains(links, l => l?["name"]?.GetValue<string>() == "spec notes.md" && l?["uri"]?.GetValue<string>() == new Uri(extFile).AbsoluteUri);
                Assert.Contains(links, l => l?["name"]?.GetValue<string>() == "logs" && l?["uri"]?.GetValue<string>() == new Uri(extFolder).AbsoluteUri);
            }
            finally
            {
                fakeFactory.ConfigureNext = null;
                await view.CloseAsync();
                vm.Dispose();
                window.Close();
                Dispatcher.UIThread.RunJobs();
            }
        }
        finally
        {
            try { workspaceDir.Delete(recursive: true); } catch (IOException) { }
            try { externalDir.Delete(recursive: true); } catch (IOException) { }
            try { siblingDir.Delete(recursive: true); } catch (IOException) { }
        }
    }

    private static async Task<DataTransfer> BuildFilesTransferAsync(IStorageProvider storage, params string[] paths)
    {
        var data = new DataTransfer();
        foreach (var path in paths)
        {
            IStorageItem? item = Directory.Exists(path)
                ? await storage.TryGetFolderFromPathAsync(path)
                : await storage.TryGetFileFromPathAsync(path);
            Assert.NotNull(item);
            data.Add(DataTransferItem.CreateFile(item!));
        }
        return data;
    }

    private static DragEventArgs RaiseDrag(Control target, Avalonia.Interactivity.RoutedEvent<DragEventArgs> routedEvent, IDataTransfer data)
    {
        var args = new DragEventArgs(routedEvent, data, target, new Point(20, 20), KeyModifiers.None)
        {
            DragEffects = DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link,
        };
        target.RaiseEvent(args);
        return args;
    }

    private sealed class RecordingFactory : ITerminalConnectionFactory
    {
        public RecordingConnection Connection { get; } = new();
        public ITerminalConnection Start(LaunchSpec spec, int columns, int rows) => Connection;
    }

    private sealed class RecordingConnection : ITerminalConnection
    {
        private readonly TaskCompletionSource<TerminalExit> _exit = new();
        public Channel<byte[]> Output { get; } = Channel.CreateUnbounded<byte[]>();
        public ConcurrentQueue<string> Writes { get; } = new();
        public int ProcessId => 1;
        public Task<TerminalExit> Exited => _exit.Task;
        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadOutputAsync([EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var bytes in Output.Reader.ReadAllAsync(ct)) yield return bytes;
        }
        public ValueTask WriteAsync(ReadOnlyMemory<byte> input, CancellationToken ct = default)
        {
            Writes.Enqueue(Encoding.UTF8.GetString(input.Span));
            return ValueTask.CompletedTask;
        }
        public ValueTask ResizeAsync(int columns, int rows, int pixelWidth, int pixelHeight) => ValueTask.CompletedTask;
        public ValueTask SignalAsync(TerminalSignal signal) => ValueTask.CompletedTask;
        public ValueTask<string?> GetForegroundProcessNameAsync() => ValueTask.FromResult<string?>(null);
        public ValueTask DisposeAsync()
        {
            Output.Writer.TryComplete();
            _exit.TrySetResult(new TerminalExit(0, null));
            return ValueTask.CompletedTask;
        }
    }
}
