namespace Octopus.App.Files.Lsp;

/// <summary>Which operating systems an <see cref="LanguageServerInstallRecipe"/> applies to.</summary>
[Flags]
public enum InstallPlatforms
{
    MacOS = 1,
    Linux = 2,
    Windows = 4,
    Unix = MacOS | Linux,
    All = MacOS | Linux | Windows,
}

/// <summary>
/// One way to install a language server without elevation: run <see cref="Tool"/> (looked up on the login PATH) with
/// <see cref="Arguments"/>. Recipes are tried in order; the first whose tool exists on this machine is offered.
/// </summary>
public sealed record LanguageServerInstallRecipe(string Tool, IReadOnlyList<string> Arguments, InstallPlatforms Platforms = InstallPlatforms.All)
{
    public string CommandLine => Arguments.Count == 0 ? Tool : $"{Tool} {string.Join(' ', Arguments)}";

    /// <summary>The same command in the shape agent install hints use, so one installer runs both.</summary>
    public Octopus.Core.Agents.AgentInstallHint ToHint() => new() { Kind = Tool, Command = CommandLine };

    public bool AppliesToCurrentPlatform =>
        (OperatingSystem.IsMacOS() && Platforms.HasFlag(InstallPlatforms.MacOS))
        || (OperatingSystem.IsLinux() && Platforms.HasFlag(InstallPlatforms.Linux))
        || (OperatingSystem.IsWindows() && Platforms.HasFlag(InstallPlatforms.Windows));
}

/// <summary>How to launch one stdio Language Server Protocol executable for a set of Monaco language ids.</summary>
public sealed record LanguageServerSpec(
    string Id,
    string DisplayName,
    IReadOnlyList<string> LanguageIds,
    string Executable,
    IReadOnlyList<string> Arguments)
{
    /// <summary>Install recipes in order of preference; empty when Octopus cannot install the server itself.</summary>
    public IReadOnlyList<LanguageServerInstallRecipe> Install { get; init; } = [];
}

/// <summary>Built-in stdio language servers Octopus can attach to <c>CodeEditor</c> when installed on PATH.</summary>
public static class LanguageServerCatalog
{
    private static LanguageServerInstallRecipe Npm(params string[] packages) => new("npm", ["install", "-g", .. packages]);

    private static LanguageServerInstallRecipe Brew(string formula) => new("brew", ["install", formula], InstallPlatforms.Unix);

    public static IReadOnlyList<LanguageServerSpec> All { get; } =
    [
        new("csharp-ls", "C# (csharp-ls)", ["csharp"], "csharp-ls", [])
        {
            Install = [new("dotnet", ["tool", "install", "--global", "csharp-ls"])],
        },
        new("typescript-language-server", "TypeScript / JavaScript", ["typescript", "javascript"], "typescript-language-server", ["--stdio"])
        {
            Install = [Npm("typescript-language-server", "typescript"), Brew("typescript-language-server")],
        },
        new("pyright", "Python (pyright)", ["python"], "pyright-langserver", ["--stdio"])
        {
            Install = [Npm("pyright"), new("pipx", ["install", "pyright"]), Brew("pyright")],
        },
        new("pylsp", "Python (pylsp)", ["python"], "pylsp", [])
        {
            Install = [new("pipx", ["install", "python-lsp-server"]), Brew("python-lsp-server")],
        },
        new("rust-analyzer", "Rust (rust-analyzer)", ["rust"], "rust-analyzer", [])
        {
            Install = [new("rustup", ["component", "add", "rust-analyzer"]), Brew("rust-analyzer")],
        },
        new("gopls", "Go (gopls)", ["go"], "gopls", [])
        {
            Install = [new("go", ["install", "golang.org/x/tools/gopls@latest"]), Brew("gopls")],
        },
        new("clangd", "C / C++ (clangd)", ["c", "cpp", "objective-c"], "clangd", [])
        {
            Install =
            [
                // macOS ships /usr/bin/clangd with the command line tools; Linux needs the distro package manager (sudo).
                new("winget", ["install", "--id", "LLVM.LLVM", "-e", "--silent", "--accept-source-agreements", "--accept-package-agreements"], InstallPlatforms.Windows),
            ],
        },
        new("vscode-json-language-server", "JSON", ["json"], "vscode-json-language-server", ["--stdio"])
        {
            Install = [Npm("vscode-langservers-extracted"), Brew("vscode-langservers-extracted")],
        },
        new("vscode-css-language-server", "CSS / SCSS / Less", ["css", "scss", "less"], "vscode-css-language-server", ["--stdio"])
        {
            Install = [Npm("vscode-langservers-extracted"), Brew("vscode-langservers-extracted")],
        },
        new("vscode-html-language-server", "HTML", ["html"], "vscode-html-language-server", ["--stdio"])
        {
            Install = [Npm("vscode-langservers-extracted"), Brew("vscode-langservers-extracted")],
        },
        new("yaml-language-server", "YAML", ["yaml"], "yaml-language-server", ["--stdio"])
        {
            Install = [Npm("yaml-language-server"), Brew("yaml-language-server")],
        },
        new("bash-language-server", "Shell (bash-language-server)", ["shell"], "bash-language-server", ["start"])
        {
            Install = [Npm("bash-language-server"), Brew("bash-language-server")],
        },
    ];

    public static IEnumerable<LanguageServerSpec> ForLanguage(string languageId) =>
        All.Where(s => s.LanguageIds.Contains(languageId, StringComparer.OrdinalIgnoreCase));
}
