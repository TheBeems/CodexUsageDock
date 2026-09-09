namespace CodexUsageDock;

internal sealed record CodexSourceOptions(string? ExecutablePath = null, string? HomePath = null)
{
    internal static CodexSourceOptions Default { get; } = new();

    internal static bool TryCreate(string? executablePath, string? homePath, out CodexSourceOptions options, out string? error)
    {
        options = Default;
        error = null;
        try
        {
            var executable = Normalize(executablePath);
            var home = Normalize(homePath);
            if (executable is not null && (!File.Exists(executable)
                || !(Path.GetFileName(executable).Equals("codex.exe", StringComparison.OrdinalIgnoreCase)
                    || Path.GetFileName(executable).Equals("codex.cmd", StringComparison.OrdinalIgnoreCase))
                || CodexAppServerReader.IsWindowsAppsPath(executable)))
            {
                error = "Choose an existing standalone codex.exe or codex.cmd outside WindowsApps.";
                return false;
            }
            if (home is not null && !Directory.Exists(home))
            {
                error = "The selected Codex home directory is unavailable. Check the path and its access permissions.";
                return false;
            }
            options = new(executable, home);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            error = "Source paths must be complete, accessible Windows paths without arguments or control characters.";
            return false;
        }
    }

    private static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (value.Length > 1024 || value.Any(char.IsControl) || !Path.IsPathFullyQualified(value))
        {
            throw new ArgumentException("Invalid source path.");
        }
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(value.Trim()));
    }
}
