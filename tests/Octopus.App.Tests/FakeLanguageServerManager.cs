using Octopus.App.Files.Lsp;
using TKCode.Arc;

namespace Octopus.App.Tests;

/// <summary>Test double for <see cref="ILanguageServerManager"/> so headless tests never spawn real external LSP processes.</summary>
internal sealed class FakeLanguageServerManager : ILanguageServerManager
{
    public List<DetectedLanguageServer> Installed { get; } = [];
    public List<(string FilePath, string LanguageId, string? WorkspaceRoot)> AttachCalls { get; } = [];
    public int StopAllCount { get; private set; }
    public int DetachedCount { get; private set; }

    public Task<IReadOnlyList<DetectedLanguageServer>> DetectInstalledAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<DetectedLanguageServer>>(Installed.ToList());

    public Task<IDisposable?> AttachAsync(
        CodeEditor editor,
        string filePath,
        string languageId,
        string? workspaceRoot,
        CancellationToken cancellationToken = default)
    {
        AttachCalls.Add((filePath, languageId, workspaceRoot));
        return Task.FromResult<IDisposable?>(new DetachHandle(this));
    }

    public void StopAll() => StopAllCount++;
    public void Dispose() => StopAll();

    private sealed class DetachHandle(FakeLanguageServerManager owner) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            owner.DetachedCount++;
        }
    }
}
