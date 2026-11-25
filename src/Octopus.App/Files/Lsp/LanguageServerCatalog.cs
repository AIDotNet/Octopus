namespace Octopus.App.Files.Lsp;

/// <summary>How to launch one stdio Language Server Protocol executable for a set of Monaco language ids.</summary>
public sealed record LanguageServerSpec(
    string Id,
    string DisplayName,
    IReadOnlyList<string> LanguageIds,
    string Executable,
    IReadOnlyList<string> Arguments);

/// <summary>Built-in stdio language servers Octopus can attach to <c>CodeEditor</c> when installed on PATH.</summary>
public static class LanguageServerCatalog
{
    public static IReadOnlyList<LanguageServerSpec> All { get; } =
    [
        new("csharp-ls", "C# (csharp-ls)", ["csharp"], "csharp-ls", []),
        new("typescript-language-server", "TypeScript / JavaScript", ["typescript", "javascript"], "typescript-language-server", ["--stdio"]),
        new("pyright", "Python (pyright)", ["python"], "pyright-langserver", ["--stdio"]),
        new("pylsp", "Python (pylsp)", ["python"], "pylsp", []),
        new("rust-analyzer", "Rust (rust-analyzer)", ["rust"], "rust-analyzer", []),
        new("gopls", "Go (gopls)", ["go"], "gopls", []),
        new("clangd", "C / C++ (clangd)", ["c", "cpp", "objective-c"], "clangd", []),
        new("vscode-json-language-server", "JSON", ["json"], "vscode-json-language-server", ["--stdio"]),
        new("vscode-css-language-server", "CSS / SCSS / Less", ["css", "scss", "less"], "vscode-css-language-server", ["--stdio"]),
        new("vscode-html-language-server", "HTML", ["html"], "vscode-html-language-server", ["--stdio"]),
        new("yaml-language-server", "YAML", ["yaml"], "yaml-language-server", ["--stdio"]),
        new("bash-language-server", "Shell (bash-language-server)", ["shell"], "bash-language-server", ["start"]),
    ];

    public static IEnumerable<LanguageServerSpec> ForLanguage(string languageId) =>
        All.Where(s => s.LanguageIds.Contains(languageId, StringComparer.OrdinalIgnoreCase));
}
