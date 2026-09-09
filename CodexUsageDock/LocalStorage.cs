using System.Diagnostics;

namespace CodexUsageDock;

internal static class LocalStorage
{
    internal static string GetPath(string fileName) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexUsageDock", fileName);

    internal static string GetCodexHome() => Path.GetFullPath(
        Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } configured
            ? configured
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"));

    internal static bool TryWrite(string path, string content)
    {
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(temporaryPath, content);
            File.Move(temporaryPath, path, overwrite: true);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            TraceFailure("save local data", error);
            return false;
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                TraceFailure("remove temporary local data", error);
            }
        }
    }

    internal static void TraceFailure(string operation, Exception error) =>
        Trace.TraceWarning("Codex Usage Dock: {0} failed ({1}, HRESULT 0x{2:X8}); sensitive details omitted.",
            operation, error.GetType().Name, error.HResult);
}
