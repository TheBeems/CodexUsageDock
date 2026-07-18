using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexUsageDock;

internal sealed class WeeklyUsageHistoryStore
{
    private const string FileName = "weekly-usage-history.json";
    private readonly string _path;

    internal WeeklyUsageHistoryStore(string path)
    {
        _path = path;
    }

    internal static WeeklyUsageHistoryStore CreateDefault()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexUsageDock");
        return new WeeklyUsageHistoryStore(Path.Combine(directory, FileName));
    }

    internal IReadOnlyList<UsageHistoryEntry> Load(DateTimeOffset now)
    {
        try
        {
            if (!File.Exists(_path))
            {
                return [];
            }

            var entries = JsonSerializer.Deserialize(File.ReadAllText(_path), typeof(List<UsageHistoryEntry>), UsageHistoryJsonContext.Default) as List<UsageHistoryEntry>;
            var normalized = Normalize(entries, now);
            if (entries is not null && normalized.Length != entries.Count)
            {
                Save(normalized);
            }

            return normalized;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return [];
        }
    }

    internal void Save(IReadOnlyList<UsageHistoryEntry> entries)
    {
        var temporaryPath = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (string.IsNullOrWhiteSpace(directory))
            {
                return;
            }

            Directory.CreateDirectory(directory);
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(entries, typeof(IReadOnlyList<UsageHistoryEntry>), UsageHistoryJsonContext.Default));
            File.Move(temporaryPath, _path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static UsageHistoryEntry[] Normalize(List<UsageHistoryEntry>? entries, DateTimeOffset now)
    {
        if (entries is null)
        {
            return [];
        }

        var cutoff = now - TimeSpan.FromDays(7);
        return entries
            .Where(entry => entry.RecordedAt >= cutoff
                && entry.RecordedAt <= now
                && double.IsFinite(entry.RemainingPercent)
                && entry.RemainingPercent is >= 0 and <= 100)
            .Select(entry => entry.ResetsAt is not null && entry.WindowMinutes is > 0
                ? entry
                : entry with { ResetsAt = null, WindowMinutes = null })
            .OrderBy(entry => entry.RecordedAt)
            .DistinctBy(entry => entry.RecordedAt)
            .ToArray();
    }
}

[JsonSerializable(typeof(List<UsageHistoryEntry>))]
[JsonSerializable(typeof(IReadOnlyList<UsageHistoryEntry>))]
internal sealed partial class UsageHistoryJsonContext : JsonSerializerContext
{
}
