using Xunit;

namespace Octopus.Terminal.Tests;

/// <summary>
/// The test classes that run a <see cref="Octopus.Terminal.Avalonia.TerminalSession"/>, one after another. A session posts
/// to Avalonia's UI dispatcher, and with no app running that dispatcher is set up by whichever thread reaches it first:
/// classes running in parallel reached it at once and one of them got a half-made dispatcher (a NullReferenceException
/// inside Dispatcher.Post on CI).
/// </summary>
[CollectionDefinition(Name)]
public sealed class TerminalSessionCollection
{
    public const string Name = "TerminalSession";
}
