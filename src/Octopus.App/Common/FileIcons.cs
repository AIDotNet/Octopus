using TKCode.Arc.Icons.Lucide;

namespace Octopus.App.Common;

/// <summary>Picks the Lucide icon of a file by its extension: a few families, everything else is a plain page.</summary>
internal static class FileIcons
{
    public static LucideIconKind For(string name, bool isDirectory, bool isExpanded = false)
    {
        if (isDirectory)
        {
            return isExpanded ? LucideIconKind.FolderOpen : LucideIconKind.Folder;
        }
        return Path.GetExtension(name).ToLowerInvariant() switch
        {
            ".cs" or ".csx" or ".fs" or ".vb" or ".c" or ".h" or ".cpp" or ".hpp" or ".cc" or ".java" or ".kt" or ".go" or ".rs"
                or ".py" or ".rb" or ".php" or ".swift" or ".js" or ".jsx" or ".ts" or ".tsx" or ".mjs" or ".cjs" or ".vue"
                or ".svelte" or ".html" or ".htm" or ".css" or ".scss" or ".less" or ".sql" or ".lua" or ".dart" => LucideIconKind.FileCode,
            ".sh" or ".bash" or ".zsh" or ".fish" or ".ps1" or ".bat" or ".cmd" => LucideIconKind.FileTerminal,
            ".md" or ".markdown" or ".txt" or ".rst" or ".log" => LucideIconKind.FileText,
            ".json" or ".jsonc" or ".json5" => LucideIconKind.FileJson,
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".svg" or ".ico" or ".bmp" or ".icns" or ".avif" => LucideIconKind.FileImage,
            ".mp3" or ".wav" or ".flac" or ".ogg" or ".m4a" => LucideIconKind.FileAudio,
            ".mp4" or ".mov" or ".mkv" or ".webm" or ".avi" => LucideIconKind.FileVideo,
            ".zip" or ".tar" or ".gz" or ".tgz" or ".7z" or ".rar" or ".nupkg" => LucideIconKind.FileArchive,
            ".csv" or ".tsv" or ".xlsx" or ".xls" => LucideIconKind.FileSpreadsheet,
            ".yml" or ".yaml" or ".toml" or ".ini" or ".xml" or ".axaml" or ".xaml" or ".csproj" or ".props" or ".targets" or ".sln"
                or ".slnx" or ".xlf" or ".config" or ".env" or ".editorconfig" or ".gitignore" or ".gitattributes" => LucideIconKind.FileCog,
            ".lock" => LucideIconKind.FileLock,
            _ => LucideIconKind.File,
        };
    }
}
