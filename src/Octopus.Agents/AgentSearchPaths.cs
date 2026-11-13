namespace Octopus.Agents;

/// <summary>
/// Directories where agent CLIs usually land, searched after the login shell's PATH. An app started from the Dock or
/// the Start menu sees only the PATH its login shell exports, so an installer that edits a file the shell does not
/// read (or that was run after Octopus started) would otherwise go unnoticed.
/// </summary>
public static class AgentSearchPaths
{
    /// <summary>
    /// The existing directories to search, most specific first, without duplicates. <paramref name="isWindows"/>
    /// selects the layout; the other parameters exist so that a test can run without touching the disk.
    /// </summary>
    public static IReadOnlyList<string> Compute(
        string home,
        bool isWindows,
        Func<string, string?> getEnv,
        Func<string, bool> directoryExists,
        Func<string, IReadOnlyList<string>> subdirectories)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(isWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        void Add(string? directory)
        {
            if (string.IsNullOrWhiteSpace(directory)) return;
            if (seen.Contains(directory)) return;
            bool exists;
            try { exists = directoryExists(directory); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }
            if (!exists) return;
            seen.Add(directory);
            result.Add(directory);
        }

        // A version manager keeps one directory per installed Node; the newest first, as a shell would after `nvm use`.
        void AddVersioned(string root, string suffix)
        {
            IReadOnlyList<string> versions;
            try { versions = subdirectories(root); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }
            foreach (var version in versions.OrderByDescending(VersionKey, StringComparer.Ordinal))
            {
                Add(suffix.Length == 0 ? version : Path.Combine(version, suffix));
            }
        }

        if (string.IsNullOrEmpty(home)) return result;

        // A user's own settings win over the defaults.
        AddEnvDirectory(Add, getEnv("npm_config_prefix"), isWindows ? "" : "bin");
        AddEnvDirectory(Add, getEnv("NPM_CONFIG_PREFIX"), isWindows ? "" : "bin");
        AddEnvDirectory(Add, getEnv("PNPM_HOME"), "");
        AddEnvDirectory(Add, getEnv("BUN_INSTALL"), "bin");
        AddEnvDirectory(Add, getEnv("VOLTA_HOME"), "bin");
        AddEnvDirectory(Add, getEnv("CARGO_HOME"), "bin");
        AddEnvDirectory(Add, getEnv("GOBIN"), "");
        AddEnvDirectory(Add, getEnv("GOPATH"), "bin");

        if (isWindows)
        {
            var appData = getEnv("APPDATA") ?? Path.Combine(home, "AppData", "Roaming");
            var localAppData = getEnv("LOCALAPPDATA") ?? Path.Combine(home, "AppData", "Local");
            Add(Path.Combine(appData, "npm"));
            Add(Path.Combine(localAppData, "pnpm"));
            Add(Path.Combine(localAppData, "Microsoft", "WinGet", "Links"));
            Add(Path.Combine(localAppData, "Programs"));
            Add(Path.Combine(home, ".local", "bin"));
            Add(Path.Combine(home, ".bun", "bin"));
            Add(Path.Combine(home, ".cargo", "bin"));
            Add(Path.Combine(home, ".volta", "bin"));
            Add(Path.Combine(home, ".claude", "local"));
            Add(Path.Combine(home, "scoop", "shims"));
            Add(Path.Combine(home, "go", "bin"));
            var chocolatey = getEnv("ChocolateyInstall");
            if (!string.IsNullOrEmpty(chocolatey)) Add(Path.Combine(chocolatey, "bin"));
            var programData = getEnv("ProgramData");
            if (!string.IsNullOrEmpty(programData)) Add(Path.Combine(programData, "chocolatey", "bin"));
            var nvmSymlink = getEnv("NVM_SYMLINK");
            if (!string.IsNullOrEmpty(nvmSymlink)) Add(nvmSymlink);
            var nvmHome = getEnv("NVM_HOME");
            if (!string.IsNullOrEmpty(nvmHome)) AddVersioned(nvmHome, "");
            return result;
        }

        Add(Path.Combine(home, ".local", "bin"));
        Add(Path.Combine(home, ".npm-global", "bin"));
        Add(Path.Combine(home, ".claude", "local"));
        Add(Path.Combine(home, ".bun", "bin"));
        Add(Path.Combine(home, ".volta", "bin"));
        Add(Path.Combine(home, ".cargo", "bin"));
        Add(Path.Combine(home, "go", "bin"));
        Add(Path.Combine(home, ".deno", "bin"));
        Add(Path.Combine(home, ".yarn", "bin"));
        Add(Path.Combine(home, ".config", "yarn", "global", "node_modules", ".bin"));
        Add(Path.Combine(home, ".local", "share", "pnpm"));
        Add(Path.Combine(home, "Library", "pnpm"));
        Add(Path.Combine(home, ".local", "share", "mise", "shims"));
        Add(Path.Combine(home, ".asdf", "shims"));
        var nvmDir = getEnv("NVM_DIR") ?? Path.Combine(home, ".nvm");
        AddVersioned(Path.Combine(nvmDir, "versions", "node"), "bin");
        AddVersioned(Path.Combine(home, ".local", "share", "fnm", "node-versions"), Path.Combine("installation", "bin"));
        AddVersioned(Path.Combine(home, "Library", "Application Support", "fnm", "node-versions"), Path.Combine("installation", "bin"));
        Add("/opt/homebrew/bin");
        Add("/home/linuxbrew/.linuxbrew/bin");
        Add("/usr/local/bin");
        Add("/snap/bin");
        return result;
    }

    /// <summary>The same for this machine.</summary>
    public static IReadOnlyList<string> ForThisMachine() =>
        Compute(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            OperatingSystem.IsWindows(),
            Environment.GetEnvironmentVariable,
            Directory.Exists,
            directory => Directory.Exists(directory) ? Directory.GetDirectories(directory) : []);

    /// <summary>
    /// Expands <c>~</c>, <c>$VAR</c> and <c>%VAR%</c> in a directory a definition lists. A variable that is not set
    /// leaves the text as it is, which then matches no directory.
    /// </summary>
    public static string Expand(string directory, string home, Func<string, string?> getEnv)
    {
        var text = directory.Trim();
        if (text == "~") return home;
        if (text.StartsWith("~/", StringComparison.Ordinal) || text.StartsWith("~\\", StringComparison.Ordinal))
        {
            // Split on both separators so the result uses the host's separator only.
            var parts = new List<string> { home };
            parts.AddRange(text[2..].Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries));
            text = Path.Combine(parts.ToArray());
        }
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '%')
            {
                var end = text.IndexOf('%', i + 1);
                if (end > i + 1 && getEnv(text[(i + 1)..end]) is { Length: > 0 } value)
                {
                    sb.Append(value);
                    i = end;
                    continue;
                }
            }
            else if (c == '$')
            {
                var j = i + 1;
                while (j < text.Length && (char.IsLetterOrDigit(text[j]) || text[j] == '_')) j++;
                if (j > i + 1 && getEnv(text[(i + 1)..j]) is { Length: > 0 } value)
                {
                    sb.Append(value);
                    i = j - 1;
                    continue;
                }
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static void AddEnvDirectory(Action<string?> add, string? root, string subdirectory)
    {
        if (string.IsNullOrWhiteSpace(root)) return;
        add(subdirectory.Length == 0 ? root : Path.Combine(root, subdirectory));
    }

    /// <summary>Sort key that puts v22.10.0 after v22.9.0: each number is zero padded.</summary>
    private static string VersionKey(string directory)
    {
        var name = Path.GetFileName(directory.TrimEnd('/', '\\'));
        var sb = new System.Text.StringBuilder();
        var digits = 0;
        foreach (var c in name)
        {
            if (char.IsDigit(c))
            {
                digits++;
                sb.Append(c);
                continue;
            }
            if (digits > 0) sb.Insert(sb.Length - digits, new string('0', Math.Max(0, 8 - digits)));
            digits = 0;
            sb.Append(c);
        }
        if (digits > 0) sb.Insert(sb.Length - digits, new string('0', Math.Max(0, 8 - digits)));
        return sb.ToString();
    }
}
