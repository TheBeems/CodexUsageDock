using System.Diagnostics;
using System.Text.Json;

namespace CodexUsageDock;

internal sealed record RateLimitWindow(double UsedPercent, int WindowMinutes, DateTimeOffset ResetsAt)
{
    public double RemainingPercent => Math.Clamp(100 - UsedPercent, 0, 100);
}

internal sealed record CreditBalance(bool HasCredits, bool Unlimited, string? Balance);

internal sealed record RateLimitResetCredit(string? Title, string? Status, DateTimeOffset? ExpiresAt);

internal sealed record RateLimitResetCredits(int AvailableCount, IReadOnlyList<RateLimitResetCredit>? Credits);

internal sealed record UsageHistoryEntry(DateTimeOffset RecordedAt, double RemainingPercent);

internal sealed record CodexUsageSnapshot(
    RateLimitWindow? Primary,
    RateLimitWindow? Secondary,
    string? PlanType,
    CreditBalance? Credits,
    RateLimitResetCredits? ResetCredits,
    DateTimeOffset UpdatedAt,
    string Source,
    string? Error)
{
    public static CodexUsageSnapshot Loading { get; } = new(
        null,
        null,
        null,
        null,
        null,
        DateTimeOffset.Now,
        "initialiseren",
        "Wachten op Codex");
}

internal sealed class CodexUsageService : IDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(1);
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly System.Timers.Timer _timer = new(RefreshInterval.TotalMilliseconds) { AutoReset = true };
    private readonly object _historyLock = new();
    private readonly List<UsageHistoryEntry> _primaryHistory = [];
    private bool _disposed;

    public CodexUsageSnapshot Current { get; private set; } = CodexUsageSnapshot.Loading;

    public IReadOnlyList<UsageHistoryEntry> PrimaryHistory
    {
        get
        {
            lock (_historyLock)
            {
                return _primaryHistory.ToArray();
            }
        }
    }

    public event EventHandler? Updated;

    public void Start()
    {
        _timer.Elapsed += OnTimer;
        _timer.Start();
        _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        if (_disposed || !await _refreshLock.WaitAsync(0).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            try
            {
                Current = await ReadFromAppServerAsync().ConfigureAwait(false);
            }
            catch (Exception appServerError)
            {
                var fallback = await Task.Run(ReadLatestRollout).ConfigureAwait(false);
                Current = fallback with { Error = $"App-server: {ShortError(appServerError)}" };
            }

            RecordHistory(Current);

            Updated?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    internal void RecordHistory(CodexUsageSnapshot snapshot, DateTimeOffset? recordedAt = null)
    {
        if (snapshot.Primary is null)
        {
            return;
        }

        lock (_historyLock)
        {
            var now = recordedAt ?? DateTimeOffset.Now;
            var cutoff = now - TimeSpan.FromHours(5);
            _primaryHistory.RemoveAll(entry => entry.RecordedAt < cutoff);
            if (snapshot.UpdatedAt < cutoff)
            {
                return;
            }

            if (_primaryHistory.Count > 0
                && _primaryHistory[^1].RecordedAt == snapshot.UpdatedAt
                && _primaryHistory[^1].RemainingPercent == snapshot.Primary.RemainingPercent)
            {
                return;
            }

            _primaryHistory.Add(new UsageHistoryEntry(snapshot.UpdatedAt, snapshot.Primary.RemainingPercent));
        }
    }

    private static async Task<CodexUsageSnapshot> ReadFromAppServerAsync()
    {
        using var process = StartAppServer();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var cancellationToken = timeout.Token;

        try
        {
            await SendAsync(process, new
            {
                method = "initialize",
                id = 1,
                @params = new
                {
                    clientInfo = new { name = "codex_usage_dock", title = "Codex Usage Dock", version = "0.1.0" },
                },
            }, cancellationToken).ConfigureAwait(false);
            _ = await ReadResponseAsync(process, 1, cancellationToken).ConfigureAwait(false);

            await SendAsync(process, new { method = "initialized" }, cancellationToken).ConfigureAwait(false);
            await SendAsync(process, new { method = "account/rateLimits/read", id = 2 }, cancellationToken).ConfigureAwait(false);
            await SendAsync(process, new { method = "account/read", id = 3, @params = new { refreshToken = false } }, cancellationToken).ConfigureAwait(false);

            using var rateResponse = await ReadResponseAsync(process, 2, cancellationToken).ConfigureAwait(false);
            using var accountResponse = await ReadResponseAsync(process, 3, cancellationToken).ConfigureAwait(false);

            ThrowIfError(rateResponse.RootElement);
            var rateResult = rateResponse.RootElement.GetProperty("result");
            var limits = rateResult.GetProperty("rateLimits");
            var primary = TryParseWindow(limits, "primary");
            var secondary = TryParseWindow(limits, "secondary");
            var credits = ParseCredits(rateResult, limits);
            var resetCredits = ParseResetCredits(rateResult);
            var plan = ParsePlan(accountResponse.RootElement);

            return new CodexUsageSnapshot(
                primary,
                secondary,
                plan,
                credits,
                resetCredits,
                DateTimeOffset.Now,
                "Codex app-server",
                null);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }

    private static Process StartAppServer()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = FindCodexExecutable(),
            Arguments = "app-server --stdio",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Codex app-server kon niet starten.");
        _ = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is not null)
            {
            }
        });
        return process;
    }

    private static string FindCodexExecutable()
    {
        var configured = Environment.GetEnvironmentVariable("CODEX_USAGE_DOCK_CODEX_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
        }

        var alias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "codex.exe");
        return File.Exists(alias) ? alias : "codex.exe";
    }

    private static async Task SendAsync(Process process, object message, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(message);
        await process.StandardInput.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
        await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<JsonDocument> ReadResponseAsync(Process process, int expectedId, CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                throw new InvalidOperationException("Codex app-server stopte onverwacht.");
            }

            var document = JsonDocument.Parse(line);
            if (document.RootElement.TryGetProperty("id", out var id) && id.TryGetInt32(out var value) && value == expectedId)
            {
                return document;
            }

            document.Dispose();
        }
    }

    private static void ThrowIfError(JsonElement response)
    {
        if (response.TryGetProperty("error", out var error))
        {
            throw new InvalidOperationException(error.GetRawText());
        }
    }

    private static RateLimitWindow? TryParseWindow(JsonElement limits, string propertyName)
    {
        if (!limits.TryGetProperty(propertyName, out var window) || window.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return new RateLimitWindow(
            window.GetProperty("usedPercent").GetDouble(),
            window.GetProperty("windowDurationMins").GetInt32(),
            DateTimeOffset.FromUnixTimeSeconds(window.GetProperty("resetsAt").GetInt64()));
    }

    private static string? ParsePlan(JsonElement accountResponse)
    {
        if (!accountResponse.TryGetProperty("result", out var result)
            || !result.TryGetProperty("account", out var account)
            || account.ValueKind == JsonValueKind.Null
            || !account.TryGetProperty("planType", out var plan))
        {
            return null;
        }

        return plan.GetString();
    }

    internal static CreditBalance? ParseCredits(JsonElement result, JsonElement limits)
    {
        var credits = default(JsonElement);
        if (limits.TryGetProperty("credits", out var nested) && nested.ValueKind == JsonValueKind.Object)
        {
            credits = nested;
        }
        else if (!result.TryGetProperty("credits", out credits) || credits.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var hasCredits = credits.TryGetProperty("hasCredits", out var hasCreditsValue)
            && hasCreditsValue.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? hasCreditsValue.GetBoolean()
            : credits.TryGetProperty("balance", out var existingBalance) && existingBalance.ValueKind != JsonValueKind.Null;
        var unlimited = credits.TryGetProperty("unlimited", out var unlimitedValue)
            && unlimitedValue.ValueKind is JsonValueKind.True or JsonValueKind.False
            && unlimitedValue.GetBoolean();
        var balance = credits.TryGetProperty("balance", out var balanceValue)
            && balanceValue.ValueKind != JsonValueKind.Null
            ? balanceValue.ToString()
            : null;
        return new CreditBalance(hasCredits, unlimited, balance);
    }

    internal static RateLimitResetCredits? ParseResetCredits(JsonElement result)
    {
        if (!result.TryGetProperty("rateLimitResetCredits", out var resets)
            || resets.ValueKind != JsonValueKind.Object
            || !resets.TryGetProperty("availableCount", out var count)
            || !count.TryGetInt32(out var availableCount))
        {
            return null;
        }

        if (!resets.TryGetProperty("credits", out var creditDetails) || creditDetails.ValueKind == JsonValueKind.Null)
        {
            return new RateLimitResetCredits(availableCount, null);
        }

        if (creditDetails.ValueKind != JsonValueKind.Array)
        {
            return new RateLimitResetCredits(availableCount, null);
        }

        var parsed = new List<RateLimitResetCredit>();
        foreach (var credit in creditDetails.EnumerateArray())
        {
            var title = credit.TryGetProperty("title", out var titleValue) ? titleValue.GetString() : null;
            var status = credit.TryGetProperty("status", out var statusValue) ? statusValue.GetString() : null;
            DateTimeOffset? expiresAt = null;
            if (credit.TryGetProperty("expiresAt", out var expiresValue) && expiresValue.TryGetInt64(out var seconds))
            {
                expiresAt = DateTimeOffset.FromUnixTimeSeconds(seconds);
            }

            parsed.Add(new RateLimitResetCredit(title, status, expiresAt));
        }

        return new RateLimitResetCredits(availableCount, parsed);
    }

    private static CodexUsageSnapshot ReadLatestRollout()
    {
        var sessions = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "sessions");
        var files = Directory.EnumerateFiles(sessions, "rollout-*.jsonl", SearchOption.AllDirectories)
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Take(12);

        foreach (var file in files)
        {
            RateLimitWindow? primary = null;
            RateLimitWindow? secondary = null;
            string? plan = null;
            using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                if (!line.Contains("\"rate_limits\"", StringComparison.Ordinal))
                {
                    continue;
                }

                try
                {
                    using var document = JsonDocument.Parse(line);
                    var payload = document.RootElement.GetProperty("payload");
                    if (!payload.TryGetProperty("rate_limits", out var rateLimits) || rateLimits.ValueKind == JsonValueKind.Null)
                    {
                        continue;
                    }

                    primary = TryParseRolloutWindow(rateLimits, "primary") ?? primary;
                    secondary = TryParseRolloutWindow(rateLimits, "secondary") ?? secondary;
                    if (rateLimits.TryGetProperty("plan_type", out var planType))
                    {
                        plan = planType.GetString() ?? plan;
                    }
                }
                catch (JsonException)
                {
                }
            }

            if (primary is not null || secondary is not null)
            {
                return new CodexUsageSnapshot(primary, secondary, plan, null, null, file.LastWriteTime, "lokale Codex-sessie", null);
            }
        }

        throw new InvalidOperationException("Geen recente Codex-gebruiksgegevens gevonden.");
    }

    private static RateLimitWindow? TryParseRolloutWindow(JsonElement limits, string propertyName)
    {
        if (!limits.TryGetProperty(propertyName, out var window) || window.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return new RateLimitWindow(
            window.GetProperty("used_percent").GetDouble(),
            window.GetProperty("window_minutes").GetInt32(),
            DateTimeOffset.FromUnixTimeSeconds(window.GetProperty("resets_at").GetInt64()));
    }

    private static string ShortError(Exception error)
    {
        var message = error.GetBaseException().Message.Replace(Environment.NewLine, " ", StringComparison.Ordinal);
        return message.Length <= 120 ? message : message[..117] + "...";
    }

    private void OnTimer(object? sender, System.Timers.ElapsedEventArgs e) => _ = RefreshAsync();

    public void Dispose()
    {
        _disposed = true;
        _timer.Stop();
        _timer.Elapsed -= OnTimer;
        _timer.Dispose();
        _refreshLock.Dispose();
    }
}
