using Octopus.App.Activity;
using Octopus.Terminal.Avalonia;

namespace Octopus.App.Collab.Sessions;

/// <summary>
/// A native terminal (a CLI agent such as Claude Code or Codex, or a shell) as a collaboration peer: prompts are pasted
/// into its PTY, its answer is the screen text that appeared after the prompt, Esc interrupts its turn. The PTY is
/// looked up on every call, so a restarted terminal keeps working.
/// </summary>
internal sealed class TerminalCollabSession(
    string itemId,
    string agentId,
    string title,
    string workingDirectory,
    TerminalSessionRegistry terminals,
    IItemActivityService? activity) : ICollabSessionBridge
{
    private const int MaxReadChars = 4000;
    private int _baseline = -1;

    public string ItemId => itemId;
    public string AgentId => agentId;
    public string DisplayTitle => title;
    public string WorkingDirectory => workingDirectory;
    public bool IsBusy => activity?.Of(itemId) == ItemActivity.Working;
    public bool IsConnected => terminals.Find(itemId)?.IsRunning == true;
    public string? CurrentStatusDetail => null;

    public async Task<bool> DeliverCollabPromptAsync(string promptText, string? threadId = null, string? taskId = null, CancellationToken cancellationToken = default)
    {
        if (terminals.Find(itemId) is not { IsRunning: true } term) return false;
        Volatile.Write(ref _baseline, term.ReadText().Length);

        // One clean submission: bracketed paste when the TUI supports it, otherwise newlines folded into spaces.
        var block = promptText.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        if (term.CachedInputModes.BracketedPaste)
        {
            term.Send($"\x1b[200~{block}\x1b[201~");
            await Task.Delay(60, cancellationToken).ConfigureAwait(false);
            term.Send("\r");
        }
        else
        {
            term.Send(block.Replace("\n", " ", StringComparison.Ordinal) + "\r");
        }
        return true;
    }

    // The CLI keeps its own input queue; nothing sent can be taken back.
    public int CancelQueuedCollabPrompts(string? threadId = null, string? taskId = null) => 0;

    public Task CancelActiveTurnAsync()
    {
        // Esc interrupts the running turn in Claude Code and Codex CLI without quitting the TUI.
        if (terminals.Find(itemId) is { IsRunning: true } term) term.Send("\x1b");
        return Task.CompletedTask;
    }

    public string ReadRecentSummary(int maxTurns = 5)
    {
        var text = terminals.Find(itemId)?.ReadText().Trim() ?? "";
        if (text.Length == 0) return "(Terminal buffer is currently empty.)";
        return text.Length > MaxReadChars ? "…\n" + text[^MaxReadChars..] : text;
    }

    /// <summary>
    /// What appeared on screen since the last prompt was pasted (its answer), cut to the last <paramref name="maxChars"/>;
    /// null when no prompt is outstanding. Taking it clears the mark.
    /// </summary>
    public string? TakeAnswer(int maxChars)
    {
        var baseline = Interlocked.Exchange(ref _baseline, -1);
        if (baseline < 0) return null;
        var text = terminals.Find(itemId)?.ReadText() ?? "";
        var delta = (baseline < text.Length ? text[baseline..] : text).Trim();
        if (delta.Length > maxChars) delta = "…\n" + delta[^maxChars..];
        return delta.Length == 0 ? "Terminal session reported turn completion." : delta;
    }
}
