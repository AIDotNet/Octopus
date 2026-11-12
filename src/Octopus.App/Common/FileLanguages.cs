namespace Octopus.App.Common;

/// <summary>
/// Maps a file name to the language identifier the code view highlights with (the TextMate grammars Arc's code block
/// uses). Names without a grammar come back as <see cref="PlainText"/>, so words are not colored by accident.
/// </summary>
public static class FileLanguages
{
    public const string PlainText = "txt";

    public static string For(string fileName)
    {
        var name = Path.GetFileName(fileName);
        if (name.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase)) return "dockerfile";
        if (name.Equals("Makefile", StringComparison.OrdinalIgnoreCase) || name.Equals("GNUmakefile", StringComparison.OrdinalIgnoreCase)) return "make";
        return Path.GetExtension(name).ToLowerInvariant() switch
        {
            ".cs" or ".csx" => "cs",
            ".fs" or ".fsx" => "fs",
            ".vb" => "vb",
            ".ts" or ".mts" or ".cts" => "ts",
            ".tsx" => "tsx",
            ".js" or ".mjs" or ".cjs" => "js",
            ".jsx" => "jsx",
            ".json" or ".jsonc" or ".json5" => "json",
            ".axaml" or ".xaml" => "axaml",
            ".xml" or ".csproj" or ".fsproj" or ".vbproj" or ".props" or ".targets" or ".xlf" or ".svg" or ".plist" or ".config" or ".slnx" => "xml",
            ".html" or ".htm" => "html",
            ".css" => "css",
            ".scss" => "scss",
            ".less" => "less",
            ".vue" => "vue",
            ".svelte" => "svelte",
            ".astro" => "astro",
            ".py" or ".pyi" => "py",
            ".rs" => "rust",
            ".go" => "go",
            ".java" => "java",
            ".swift" => "swift",
            ".rb" => "rb",
            ".php" => "php",
            ".lua" => "lua",
            ".dart" => "dart",
            ".r" => "r",
            ".pl" or ".pm" => "perl",
            ".c" or ".h" => "c",
            ".cc" or ".cpp" or ".cxx" or ".hpp" or ".hh" or ".hxx" => "cpp",
            ".sh" or ".bash" or ".zsh" => "sh",
            ".ps1" or ".psm1" or ".psd1" => "ps1",
            ".bat" or ".cmd" => "bat",
            ".yaml" or ".yml" => "yaml",
            ".toml" => "toml",
            ".ini" or ".cfg" or ".conf" or ".editorconfig" => "ini",
            ".properties" => "properties",
            ".gradle" => "gradle",
            ".groovy" => "groovy",
            ".sql" => "sql",
            ".md" or ".markdown" => "md",
            ".mk" => "make",
            _ => PlainText,
        };
    }

    /// <summary>The name of a language identifier as people write it ("cs" is "C#"); the identifier in capitals when there is no better name.</summary>
    public static string DisplayName(string language) => language switch
    {
        "cs" => "C#", "fs" => "F#", "vb" => "Visual Basic", "ts" => "TypeScript", "tsx" => "TSX", "js" => "JavaScript", "jsx" => "JSX",
        "json" => "JSON", "axaml" => "AXAML", "xml" => "XML", "html" => "HTML", "css" => "CSS", "scss" => "SCSS", "less" => "Less",
        "vue" => "Vue", "svelte" => "Svelte", "astro" => "Astro", "py" => "Python", "rust" => "Rust", "go" => "Go", "java" => "Java",
        "swift" => "Swift", "rb" => "Ruby", "php" => "PHP", "lua" => "Lua", "dart" => "Dart", "r" => "R", "perl" => "Perl",
        "c" => "C", "cpp" => "C++", "sh" => "Shell", "ps1" => "PowerShell", "bat" => "Batch", "yaml" => "YAML", "toml" => "TOML",
        "ini" => "INI", "properties" => "Properties", "gradle" => "Gradle", "groovy" => "Groovy", "sql" => "SQL", "md" => "Markdown",
        "make" => "Makefile", "dockerfile" => "Dockerfile",
        _ => language.ToUpperInvariant(),
    };

    /// <summary>Markdown files get a rendered preview besides their source.</summary>
    public static bool IsMarkdown(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        return extension.Equals(".md", StringComparison.OrdinalIgnoreCase) || extension.Equals(".markdown", StringComparison.OrdinalIgnoreCase);
    }
}
