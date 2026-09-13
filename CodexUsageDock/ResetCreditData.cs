using System.Text.Json;

namespace CodexUsageDock;

internal enum ResetCreditOutcome
{
    Reset,
    AlreadyRedeemed,
    NothingToReset,
    NoCredit,
    Unsupported,
    AccountMismatch,
    // The consume request was not started; no credit could have been consumed.
    Unavailable,
    // A retry must use the original idempotency key.
    Ambiguous,
}

internal sealed record ResetCreditResult(ResetCreditOutcome Outcome, DateTimeOffset AttemptedAt);

internal static class ResetCreditParser
{
    internal static bool IsValidIdempotencyKey(string? value) => value is { Length: > 0 and <= 128 }
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    internal static ResetCreditResult Parse(JsonElement response, DateTimeOffset attemptedAt)
    {
        if (response.ValueKind == JsonValueKind.Object && response.TryGetProperty("error", out _)
            && response.TryGetProperty("result", out _)) return new(ResetCreditOutcome.Ambiguous, attemptedAt);
        if (CodexAppServerReader.IsMethodNotFound(response)) return new(ResetCreditOutcome.Unsupported, attemptedAt);
        if (response.ValueKind != JsonValueKind.Object
            || response.TryGetProperty("error", out _)
            || !response.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("outcome", out var value) || value.ValueKind != JsonValueKind.String)
        {
            return new(ResetCreditOutcome.Ambiguous, attemptedAt);
        }

        var outcome = value.GetString() switch
        {
            "reset" => ResetCreditOutcome.Reset,
            "alreadyRedeemed" => ResetCreditOutcome.AlreadyRedeemed,
            "nothingToReset" => ResetCreditOutcome.NothingToReset,
            "noCredit" => ResetCreditOutcome.NoCredit,
            _ => ResetCreditOutcome.Ambiguous,
        };
        return new(outcome, attemptedAt);
    }
}
