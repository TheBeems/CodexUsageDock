using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexUsageDock;

internal sealed record CodexProfile(
    Guid Id,
    string DisplayName,
    CodexSourceOptions SourceOptions);

internal sealed class CodexProfileStore
{
    internal const int SchemaVersion = 1;
    internal const int MaximumProfiles = 8;
    internal const int MaximumDisplayNameLength = 40;
    internal const int MaximumPathLength = 1024;
    internal const int MaximumDocumentBytes = 512 * 1024;

    private const string LoadErrorMessage = "Saved Codex profiles could not be read. No profiles were loaded.";
    private const string SaveErrorMessage = "The Codex profile could not be saved. Try again.";
    private const string InvalidNameMessage = "Profile names must contain 1 to 40 characters without control characters.";
    private const string InvalidSourceMessage = "The Codex executable or home path is invalid or unavailable.";
    private const string MaximumProfilesMessage = "You can save up to eight Codex profiles.";
    private const string ProfileNotFoundMessage = "The Codex profile was not found.";
    private const string InvalidProfileIdMessage = "The Codex profile identifier is invalid.";
    private readonly object _gate = new();
    private readonly string _path;
    private List<CodexProfile> _profiles;
    private string? _storageError;

    internal CodexProfileStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        _profiles = Load();
    }

    internal static CodexProfileStore CreateDefault() =>
        new(LocalStorage.GetPath("profiles.json"));

    internal IReadOnlyList<CodexProfile> Profiles
    {
        get
        {
            lock (_gate)
            {
                return _profiles.ToArray();
            }
        }
    }

    internal string? StorageError
    {
        get
        {
            lock (_gate)
            {
                return _storageError;
            }
        }
    }

    internal bool TryGet(Guid id, out CodexProfile? profile)
    {
        lock (_gate)
        {
            profile = id != Guid.Empty
                ? _profiles.FirstOrDefault(candidate => candidate.Id == id)
                : null;
            return profile is not null;
        }
    }

    internal bool TryUpsert(
        string? displayName,
        string? executablePath,
        string? homePath,
        out CodexProfile? profile,
        out string? error)
    {
        profile = null;
        error = null;
        if (!TryNormalizeDisplayName(displayName, out var normalizedName))
        {
            error = InvalidNameMessage;
            return false;
        }

        if (!CodexSourceOptions.TryCreate(executablePath, homePath, out var options, out _))
        {
            error = InvalidSourceMessage;
            return false;
        }

        lock (_gate)
        {
            var existingIndex = _profiles.FindIndex(candidate =>
                string.Equals(candidate.DisplayName, normalizedName, StringComparison.OrdinalIgnoreCase));
            if (existingIndex < 0 && _profiles.Count >= MaximumProfiles)
            {
                error = MaximumProfilesMessage;
                return false;
            }

            var candidate = new CodexProfile(
                existingIndex >= 0 ? _profiles[existingIndex].Id : Guid.NewGuid(),
                normalizedName,
                options);
            var updated = _profiles.ToList();
            if (existingIndex >= 0)
            {
                updated[existingIndex] = candidate;
            }
            else
            {
                updated.Add(candidate);
            }

            if (!TrySave(updated, out error))
            {
                return false;
            }

            _profiles = updated;
            profile = candidate;
            return true;
        }
    }

    internal bool TryRemove(Guid id, out string? error)
    {
        error = null;
        if (id == Guid.Empty)
        {
            error = InvalidProfileIdMessage;
            return false;
        }

        lock (_gate)
        {
            var existingIndex = _profiles.FindIndex(candidate => candidate.Id == id);
            if (existingIndex < 0)
            {
                error = ProfileNotFoundMessage;
                return false;
            }

            var updated = _profiles.ToList();
            updated.RemoveAt(existingIndex);
            if (!TrySave(updated, out error))
            {
                return false;
            }

            _profiles = updated;
            return true;
        }
    }

    private List<CodexProfile> Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return [];
            }

            var fileInfo = new FileInfo(_path);
            if (fileInfo.Length > MaximumDocumentBytes)
            {
                SetStorageError(LoadErrorMessage);
                return [];
            }

            var document = JsonSerializer.Deserialize(
                File.ReadAllText(_path),
                CodexProfileStoreJsonContext.Default.CodexProfileDocument);
            if (document is null || document.SchemaVersion != SchemaVersion || document.Profiles is null)
            {
                SetStorageError(LoadErrorMessage);
                return [];
            }

            var profiles = new List<CodexProfile>(Math.Min(document.Profiles.Length, MaximumProfiles));
            var ids = new HashSet<Guid>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in document.Profiles)
            {
                if (entry is null
                    || !Guid.TryParseExact(entry.Id, "N", out var id)
                    || id == Guid.Empty
                    || !TryNormalizeDisplayName(entry.DisplayName, out var displayName)
                    || !TryNormalizePathShape(entry.ExecutablePath, executable: true, out var executablePath)
                    || !TryNormalizePathShape(entry.HomePath, executable: false, out var homePath)
                    || !ids.Add(id)
                    || !names.Add(displayName))
                {
                    continue;
                }

                profiles.Add(new CodexProfile(id, displayName, new(executablePath, homePath)));
                if (profiles.Count == MaximumProfiles)
                {
                    break;
                }
            }

            return profiles;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or JsonException
            or NotSupportedException
            or InvalidOperationException
            or ArgumentException)
        {
            LocalStorage.TraceFailure("load Codex profiles", exception);
            SetStorageError(LoadErrorMessage);
            return [];
        }
    }

    private bool TrySave(IReadOnlyList<CodexProfile> profiles, out string? error)
    {
        error = null;
        try
        {
            var document = new CodexProfileDocument(
                SchemaVersion,
                profiles.Select(profile => new CodexProfileEntry(
                    profile.Id.ToString("N"),
                    profile.DisplayName,
                    profile.SourceOptions.ExecutablePath,
                    profile.SourceOptions.HomePath)).ToArray());
            var content = JsonSerializer.Serialize(
                document,
                CodexProfileStoreJsonContext.Default.CodexProfileDocument);
            if (!LocalStorage.TryWrite(_path, content))
            {
                error = SaveErrorMessage;
                SetStorageError(error);
                return false;
            }

            SetStorageError(null);
            return true;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or JsonException
            or NotSupportedException
            or InvalidOperationException
            or ArgumentException)
        {
            LocalStorage.TraceFailure("save Codex profiles", exception);
            error = SaveErrorMessage;
            SetStorageError(error);
            return false;
        }
    }

    private static bool TryNormalizeDisplayName(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (value is null || value.Any(char.IsControl))
        {
            return false;
        }

        normalized = value.Trim();
        return normalized.Length is >= 1 and <= MaximumDisplayNameLength;
    }

    // Loading deliberately checks only path shape. A temporarily unavailable
    // WSL or network directory remains selectable until use-time validation.
    private static bool TryNormalizePathShape(string? value, bool executable, out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var trimmed = value.Trim();
        if (trimmed.Length > MaximumPathLength
            || trimmed.Any(char.IsControl)
            || !Path.IsPathFullyQualified(trimmed))
        {
            return false;
        }

        try
        {
            normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(trimmed));
            if (normalized.Length == 0)
            {
                return false;
            }

            if (executable)
            {
                var fileName = Path.GetFileName(normalized);
                if (!(fileName.Equals("codex.exe", StringComparison.OrdinalIgnoreCase)
                    || fileName.Equals("codex.cmd", StringComparison.OrdinalIgnoreCase))
                    || CodexAppServerReader.IsWindowsAppsPath(normalized))
                {
                    normalized = null;
                    return false;
                }
            }

            return true;
        }
        catch (Exception exception) when (exception is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            normalized = null;
            return false;
        }
    }

    private void SetStorageError(string? error)
    {
        lock (_gate)
        {
            _storageError = error;
        }
    }
}

internal sealed record CodexProfileDocument(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("profiles")] CodexProfileEntry?[]? Profiles);

internal sealed record CodexProfileEntry(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("displayName")] string? DisplayName,
    [property: JsonPropertyName("executablePath")] string? ExecutablePath,
    [property: JsonPropertyName("homePath")] string? HomePath);

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(CodexProfileDocument))]
[JsonSerializable(typeof(CodexProfileEntry))]
internal sealed partial class CodexProfileStoreJsonContext : JsonSerializerContext
{
}
