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

    internal static WeeklyUsageHistoryStore CreateDefault() => new(LocalStorage.GetPath(FileName));

    internal string? StorageError { get; private set; }

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
            LocalStorage.TraceFailure("load weekly history", error);
            StorageError = "Saved weekly history could not be read. A new history will be collected.";
            return [];
        }
    }

    internal bool Save(IReadOnlyList<UsageHistoryEntry> entries)
    {
        var saved = LocalStorage.TryWrite(_path, JsonSerializer.Serialize(entries, typeof(IReadOnlyList<UsageHistoryEntry>), UsageHistoryJsonContext.Default));
        StorageError = saved ? null : "Weekly usage history could not be saved. It may be lost after restarting.";
        return saved;
    }
    private static UsageHistoryEntry[] Normalize(List<UsageHistoryEntry>? entries, DateTimeOffset now)
    {
        if (entries is null)
        {
            return [];
        }

        var cutoff = now - TimeSpan.FromDays(7);
        return entries
            .Where(entry => entry is not null && entry.RecordedAt >= cutoff
                && entry.RecordedAt <= now
                && double.IsFinite(entry.RemainingPercent)
                && entry.RemainingPercent is >= 0 and <= 100)
            .Select(entry => entry.ResetsAt is { } reset && entry.WindowMinutes == 10080 && reset >= DateTimeOffset.MinValue.AddDays(7)
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
