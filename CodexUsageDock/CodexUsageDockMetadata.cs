using System.Reflection;

namespace CodexUsageDock;

internal static class CodexUsageDockMetadata
{
    public static string Version { get; } = GetVersion();

    private static string GetVersion()
    {
        var informationalVersion = typeof(CodexUsageDockMetadata).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        return string.IsNullOrWhiteSpace(informationalVersion)
            ? "unknown"
            : informationalVersion.Split('+', 2)[0];
    }
}
