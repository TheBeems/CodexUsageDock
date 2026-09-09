using System.Globalization;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace CodexUsageDock;

/// <summary>
/// Presents a bounded, privacy-preserving snapshot of the data used by the extension.
/// </summary>
internal sealed partial class CodexUsageDiagnosticsPage : ContentPage, IDisposable
{
    private const int MaximumBucketDetails = 16;
    private readonly object _presentationLock = new();
    private readonly CodexUsageService _service;
    private MarkdownContent _content = new(string.Empty);
    private bool _disposed;

    public CodexUsageDiagnosticsPage(CodexUsageService service)
    {
        _service = service;
        Name = "Open";
        Title = "Codex Usage diagnostics";
        Icon = new IconInfo("\uE946");
        _service.Updated += OnUpdated;
        UpdatePresentation();
    }

    public override IContent[] GetContent()
    {
        lock (_presentationLock)
        {
            return [_content];
        }
    }

    internal static string FormatDiagnostics(
        CodexUsageSnapshot snapshot,
        TimeSpan refreshInterval,
        DateTimeOffset now,
        string? historyStorageError = null,
        string? version = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var safeVersion = UsageText.SanitizeExternal(version ?? CodexUsageDockMetadata.Version, 32) ?? "unknown";
        var source = FormatSource(snapshot.Source);
        var sourceConfigured = FormatSourceConfiguration(snapshot.Source);
        var measurementTime = FormatTimestamp(snapshot.Source is UsageDataSource.Unavailable or UsageDataSource.Initializing
            ? null
            : snapshot.UpdatedAt);
        var lastAttemptTime = FormatTimestamp(snapshot.LastAttemptAt);
        var freshness = FormatFreshness(snapshot, refreshInterval, now);
        var resetDiagnostics = FormatResetDiagnostics(snapshot.ResetCredits);
        var bucketDiagnostics = FormatBucketDiagnostics(snapshot.Buckets);
        var accountContext = FormatAccountContext(snapshot);
        var historyContext = FormatHistoryContext(snapshot);
        var storageStatus = string.IsNullOrWhiteSpace(historyStorageError)
            ? "no error reported"
            : "error recorded (details withheld)";
        var ordinaryUsage = snapshot.OrdinaryUsageAllowed switch
        {
            true => "allowed",
            false => "not allowed",
            _ => "unknown (field missing)",
        };

        return $"""
        # Codex Usage diagnostics

        These details are bounded and omit account identifiers, tokens, file paths, and raw error messages.

        ## Application and refresh

        - **Application version (source build):** {UsageText.EscapeMarkdown(safeVersion)}
        - **Source:** {source}
        - **Source configured:** {sourceConfigured}
        - **Measurement time (UTC):** {measurementTime}
        - **Last attempt time (UTC):** {lastAttemptTime}
        - **Refresh interval:** {FormatDuration(refreshInterval)}
        - **Freshness:** {freshness}

        ## Quota and account context

        {bucketDiagnostics}
        {resetDiagnostics}
        - **Ordinary usage:** {ordinaryUsage}
        {accountContext}
        {historyContext}

        ## Local persistence

        - **History persistence:** {storageStatus}
        """.Trim();
    }

    private static string FormatSource(UsageDataSource source) => source switch
    {
        UsageDataSource.AppServer => "AppServer — standalone Codex CLI app-server",
        UsageDataSource.LocalSession => "LocalSession — local Codex session metadata",
        UsageDataSource.LastConfirmed => "LastConfirmed — last confirmed Codex usage",
        UsageDataSource.Unavailable => "Unavailable — no usable usage data",
        _ => "Initializing — waiting for a usage measurement",
    };

    private static string FormatSourceConfiguration(UsageDataSource source) => source switch
    {
        UsageDataSource.AppServer => "live app-server source selected",
        UsageDataSource.LocalSession => "local session fallback selected",
        UsageDataSource.LastConfirmed => "last confirmed measurement retained; live source unavailable",
        UsageDataSource.Unavailable => "no usable source",
        _ => "not determined (initializing)",
    };

    private static string FormatFreshness(CodexUsageSnapshot snapshot, TimeSpan refreshInterval, DateTimeOffset now)
    {
        DateTimeOffset? timestamp = snapshot.Source is UsageDataSource.Unavailable or UsageDataSource.Initializing
            ? null
            : snapshot.UpdatedAt;
        var state = UsageFreshness.Classify(
            timestamp,
            now,
            refreshInterval,
            snapshot.Source == UsageDataSource.LastConfirmed);

        return state switch
        {
            UsageFreshnessState.Fresh => $"fresh (age {FormatAge(snapshot.UpdatedAt, now)}; threshold {FormatDuration(UsageFreshness.MaximumAge(refreshInterval))})",
            UsageFreshnessState.Stale => $"stale (age {FormatAge(snapshot.UpdatedAt, now)}; threshold {FormatDuration(UsageFreshness.MaximumAge(refreshInterval))})",
            UsageFreshnessState.Future => "future timestamp (clock skew suspected)",
            UsageFreshnessState.LastConfirmed => $"last confirmed (age {FormatAge(snapshot.UpdatedAt, now)})",
            _ => "unknown (measurement time unavailable)",
        };
    }

    private static string FormatBucketDiagnostics(IReadOnlyList<RateLimitBucket>? buckets)
    {
        if (buckets is null)
        {
            return "- **Quota buckets:** unknown (field missing)";
        }

        var lines = new List<string>
        {
            $"- **Quota buckets:** available ({buckets.Count.ToString(CultureInfo.InvariantCulture)})",
        };
        var detailCount = Math.Min(buckets.Count, MaximumBucketDetails);
        for (var index = 0; index < detailCount; index++)
        {
            var bucket = buckets[index];
            if (bucket is null)
            {
                lines.Add($"  - Bucket {index + 1}: no usable details");
                continue;
            }

            var windows = new List<string>(2);
            AddWindowDescription(windows, "primary", bucket.Primary);
            AddWindowDescription(windows, "secondary", bucket.Secondary);
            var detail = windows.Count == 0 ? "no recognized windows" : string.Join(", ", windows);
            lines.Add($"  - Bucket {index + 1}: {detail}");
        }

        if (buckets.Count > detailCount)
        {
            lines.Add($"  - {buckets.Count - detailCount} additional bucket(s) omitted from this bounded report.");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static void AddWindowDescription(List<string> descriptions, string role, RateLimitWindow? window)
    {
        if (window is null)
        {
            return;
        }

        descriptions.Add($"{role} {FormatWindowDuration(window.WindowMinutes)}");
    }

    private static string FormatResetDiagnostics(RateLimitResetCredits? resets)
    {
        if (resets is null)
        {
            return "- **Reset credits:** unknown (field missing)\n- **Reset credit details:** unknown (field missing)";
        }

        var available = Math.Max(0, resets.AvailableCount).ToString(CultureInfo.InvariantCulture);
        var details = resets.Credits is null
            ? "unknown (field missing)"
            : $"{resets.Credits.Count.ToString(CultureInfo.InvariantCulture)} provided";
        return $"- **Reset credits:** {available} available\n- **Reset credit details:** {details}";
    }

    private static string FormatAccountContext(CodexUsageSnapshot snapshot) =>
        $"- **Account context:** {(string.IsNullOrWhiteSpace(snapshot.AccountKey) ? "unavailable (account identity not provided)" : "available (hashed identifier present; value withheld)")}";

    private static string FormatHistoryContext(CodexUsageSnapshot snapshot)
    {
        var accountVerified = !string.IsNullOrWhiteSpace(snapshot.AccountKey);
        var bucketVerified = !string.IsNullOrWhiteSpace(snapshot.DefaultBucketId);
        return accountVerified && bucketVerified
            ? "- **Adaptive history context:** verified account and quota bucket; scoped learning can be persisted"
            : "- **Adaptive history context:** unverified account or quota bucket; adaptive learning is paused (in-memory only)";
    }

    private static string FormatTimestamp(DateTimeOffset? timestamp)
    {
        if (timestamp is null || timestamp == DateTimeOffset.MinValue || timestamp == DateTimeOffset.MaxValue)
        {
            return "unknown";
        }

        try
        {
            return timestamp.Value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);
        }
        catch (ArgumentOutOfRangeException)
        {
            return "unknown";
        }
    }

    private static string FormatAge(DateTimeOffset timestamp, DateTimeOffset now)
    {
        try
        {
            var age = now - timestamp;
            return age < TimeSpan.Zero ? "future" : FormatDuration(age);
        }
        catch (ArgumentOutOfRangeException)
        {
            return "unknown";
        }
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            return "future";
        }

        if (duration.TotalDays >= 1)
        {
            return $"{(int)duration.TotalDays}d";
        }

        if (duration.TotalHours >= 1)
        {
            return $"{(int)duration.TotalHours}h";
        }

        if (duration.TotalMinutes >= 1)
        {
            return $"{Math.Max(1, (int)duration.TotalMinutes)}m";
        }

        return $"{Math.Max(1, (int)duration.TotalSeconds)}s";
    }

    private static string FormatWindowDuration(int minutes)
    {
        if (minutes <= 0)
        {
            return "unknown duration";
        }

        if (minutes % (24 * 60) == 0)
        {
            return $"{minutes / (24 * 60)}d";
        }

        if (minutes % 60 == 0)
        {
            return $"{minutes / 60}h";
        }

        return $"{minutes}m";
    }

    private void UpdatePresentation()
    {
        var body = FormatDiagnostics(
            _service.Current,
            _service.RefreshInterval,
            DateTimeOffset.Now,
            _service.HistoryStorageError);

        lock (_presentationLock)
        {
            if (_disposed)
            {
                return;
            }

            _content = new MarkdownContent(body);
            Commands =
            [
                new CommandContextItem(new RefreshUsageCommand(_service))
                {
                    Title = "Refresh now",
                },
                new CommandContextItem(new CopyTextCommand(body))
                {
                    Title = "Copy safe diagnostics",
                },
            ];
        }
    }

    private void OnUpdated(object? sender, EventArgs e)
    {
        UpdatePresentation();
        lock (_presentationLock)
        {
            if (_disposed)
            {
                return;
            }
        }

        RaiseItemsChanged(0);
    }

    public void Dispose()
    {
        lock (_presentationLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _service.Updated -= OnUpdated;
        }

        GC.SuppressFinalize(this);
    }
}
