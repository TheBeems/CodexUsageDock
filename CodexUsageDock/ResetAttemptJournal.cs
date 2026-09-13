using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexUsageDock;

// Persist before sending a mutation so a lost response or restart cannot create a second redemption.
internal sealed class ResetAttemptJournal(string path)
{
    internal bool TryRead(string accountKey, out string? key)
    {
        key = null;
        try
        {
            var scoped = LocalStorage.ContextPath(path, accountKey);
            if (!File.Exists(scoped)) return true;
            if (new FileInfo(scoped).Length > 4096) return false;
            using var document = JsonDocument.Parse(File.ReadAllText(scoped), new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("schemaVersion", out var version) || !version.TryGetInt32(out var value) || value != 1
                || !root.TryGetProperty("pendingKey", out var pending)) return false;
            if (pending.ValueKind == JsonValueKind.Null) return true;
            if (pending.ValueKind != JsonValueKind.String || !Guid.TryParseExact(pending.GetString(), "N", out _)) return false;
            key = pending.GetString();
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            LocalStorage.TraceFailure("read pending reset", error);
            return false;
        }
    }

    internal bool Save(string accountKey, string? key) => LocalStorage.TryWrite(
        LocalStorage.ContextPath(path, accountKey), new JsonObject { ["schemaVersion"] = 1, ["pendingKey"] = key }.ToJsonString());
}
