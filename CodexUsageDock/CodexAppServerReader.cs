using System.Buffers;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace CodexUsageDock;

internal static class CodexAppServerReader
{
    internal static async Task<CodexUsageSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var process = StartAppServer();
        var standardErrorDrain = DrainAsync(process.StandardError);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var readCancellationToken = timeout.Token;

        try
        {
            await SendAsync(
                process,
                "initialize",
                1,
                static writer =>
                {
                    writer.WritePropertyName("params");
                    writer.WriteStartObject();
                    writer.WritePropertyName("clientInfo");
                    writer.WriteStartObject();
                    writer.WriteString("name", "codex_usage_dock");
                    writer.WriteString("title", "Codex Usage Dock");
                    writer.WriteString("version", CodexUsageDockMetadata.Version);
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                },
                readCancellationToken).ConfigureAwait(false);
            var initializationResponses = await ReadResponsesAsync(process.StandardOutput, readCancellationToken, 1).ConfigureAwait(false);
            using var initializationResponse = initializationResponses[1];
            ThrowIfError(initializationResponse.RootElement);

            await SendAsync(process, "initialized", null, null, readCancellationToken).ConfigureAwait(false);
            await SendAsync(process, "account/rateLimits/read", 2, null, readCancellationToken).ConfigureAwait(false);
            await SendAsync(
                process,
                "account/read",
                3,
                static writer =>
                {
                    writer.WritePropertyName("params");
                    writer.WriteStartObject();
                    writer.WriteBoolean("refreshToken", false);
                    writer.WriteEndObject();
                },
                readCancellationToken).ConfigureAwait(false);

            var responses = await ReadResponsesAsync(process.StandardOutput, readCancellationToken, 2, 3).ConfigureAwait(false);
            using var rateResponse = responses[2];
            using var accountResponse = responses[3];

            ThrowIfError(rateResponse.RootElement);
            var rateResult = rateResponse.RootElement.GetProperty("result");
            var limits = rateResult.GetProperty("rateLimits");
            var windows = RateLimitWindowParser.Classify(
                RateLimitWindowParser.TryParse(limits, "primary", "usedPercent", "windowDurationMins", "resetsAt"),
                RateLimitWindowParser.TryParse(limits, "secondary", "usedPercent", "windowDurationMins", "resetsAt"));
            RateLimitWindowParser.ThrowIfNoKnownWindow(windows);
            var credits = ParseCredits(rateResult, limits);
            var resetCredits = ParseResetCredits(rateResult);
            var plan = ParsePlan(accountResponse.RootElement);

            return new CodexUsageSnapshot(
                windows.FiveHour,
                windows.Weekly,
                plan,
                credits,
                resetCredits,
                DateTimeOffset.Now,
                UsageDataSource.AppServer,
                null);
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
            }

            await ObserveProcessExitAsync(process, standardErrorDrain).ConfigureAwait(false);
        }
    }

    internal static string GetSafeWorkingDirectory(string executable)
    {
        if (Path.IsPathRooted(executable) && Path.GetDirectoryName(executable) is { Length: > 0 } directory)
        {
            return directory;
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Directory.Exists(profile) ? profile : Environment.CurrentDirectory;
    }

    internal static async Task<Dictionary<int, JsonDocument>> ReadResponsesAsync(TextReader standardOutput, CancellationToken cancellationToken, params int[] expectedIds)
    {
        var pendingIds = new HashSet<int>(expectedIds);
        if (pendingIds.Count != expectedIds.Length)
        {
            throw new ArgumentException("Expected response identifiers must be unique.", nameof(expectedIds));
        }

        var responses = new Dictionary<int, JsonDocument>();
        try
        {
            while (pendingIds.Count > 0)
            {
                var line = await standardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    throw new InvalidOperationException("Codex app-server stopped unexpectedly.");
                }

                var document = JsonDocument.Parse(line);
                if (document.RootElement.TryGetProperty("id", out var id)
                    && id.TryGetInt32(out var value)
                    && pendingIds.Remove(value))
                {
                    responses.Add(value, document);
                }
                else
                {
                    document.Dispose();
                }
            }

            return responses;
        }
        catch
        {
            foreach (var response in responses.Values)
            {
                response.Dispose();
            }

            throw;
        }
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

        var unlimited = credits.TryGetProperty("unlimited", out var unlimitedValue)
            && unlimitedValue.ValueKind is JsonValueKind.True or JsonValueKind.False
            && unlimitedValue.GetBoolean();
        string? balance = null;
        if (credits.TryGetProperty("balance", out var balanceValue))
        {
            balance = balanceValue.ValueKind switch
            {
                JsonValueKind.String => UsageText.SanitizeExternal(balanceValue.GetString(), 64),
                JsonValueKind.Number => UsageText.SanitizeExternal(balanceValue.GetRawText(), 64),
                _ => null,
            };
        }

        var hasCredits = credits.TryGetProperty("hasCredits", out var hasCreditsValue)
            && hasCreditsValue.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? hasCreditsValue.GetBoolean()
            : balance is not null;
        return new CreditBalance(hasCredits, unlimited, balance);
    }

    internal static RateLimitResetCredits? ParseResetCredits(JsonElement result)
    {
        if (!result.TryGetProperty("rateLimitResetCredits", out var resets)
            || resets.ValueKind != JsonValueKind.Object
            || !resets.TryGetProperty("availableCount", out var count)
            || count.ValueKind != JsonValueKind.Number
            || !count.TryGetInt32(out var availableCount)
            || availableCount < 0)
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
            if (credit.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var title = credit.TryGetProperty("title", out var titleValue) && titleValue.ValueKind == JsonValueKind.String
                ? UsageText.SanitizeExternal(titleValue.GetString())
                : null;
            var status = credit.TryGetProperty("status", out var statusValue) && statusValue.ValueKind == JsonValueKind.String
                ? UsageText.SanitizeExternal(statusValue.GetString(), 32)
                : null;
            DateTimeOffset? expiresAt = null;
            if (credit.TryGetProperty("expiresAt", out var expiresValue)
                && expiresValue.ValueKind == JsonValueKind.Number
                && expiresValue.TryGetInt64(out var seconds))
            {
                try
                {
                    expiresAt = DateTimeOffset.FromUnixTimeSeconds(seconds);
                }
                catch (ArgumentOutOfRangeException)
                {
                }
            }

            parsed.Add(new RateLimitResetCredit(title, status, expiresAt));
        }

        return new RateLimitResetCredits(availableCount, parsed);
    }

    private static Process StartAppServer()
    {
        var executable = FindCodexExecutable();
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = "app-server --stdio",
            WorkingDirectory = GetSafeWorkingDirectory(executable),
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        return Process.Start(startInfo) ?? throw new InvalidOperationException("Codex app-server could not be started.");
    }

    private static string FindCodexExecutable()
    {
        var candidates = new List<string>();
        var configured = Environment.GetEnvironmentVariable("CODEX_USAGE_DOCK_CODEX_PATH");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            candidates.Add(configured);
        }

        var path = Environment.GetEnvironmentVariable("PATH");
        foreach (var directory in (path ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            candidates.Add(Path.Combine(directory, "codex.exe"));
            candidates.Add(Path.Combine(directory, "codex.cmd"));
        }

        return SelectLaunchableCliPath(candidates)
            ?? throw new InvalidOperationException(
                "No launchable Codex CLI was found. Install a standalone Codex CLI or set CODEX_USAGE_DOCK_CODEX_PATH to its executable path.");
    }

    internal static string? SelectLaunchableCliPath(IEnumerable<string> candidates)
    {
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate) && !IsWindowsAppsPath(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    internal static bool IsWindowsAppsPath(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            return IsUnderDirectory(fullPath, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps"))
                || IsUnderDirectory(fullPath, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps"));
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return true;
        }
    }

    private static bool IsUnderDirectory(string path, string directory)
    {
        var fullDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar;
        return path.StartsWith(fullDirectory, StringComparison.OrdinalIgnoreCase);
    }

    internal static string CreateRequestJson(string method, int? id, Action<Utf8JsonWriter>? writeParameters = null)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("method", method);
            if (id.HasValue)
            {
                writer.WriteNumber("id", id.Value);
            }

            writeParameters?.Invoke(writer);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static async Task SendAsync(
        Process process,
        string method,
        int? id,
        Action<Utf8JsonWriter>? writeParameters,
        CancellationToken cancellationToken)
    {
        var json = CreateRequestJson(method, id, writeParameters);
        await process.StandardInput.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
        await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task DrainAsync(TextReader standardError)
    {
        try
        {
            while (await standardError.ReadLineAsync().ConfigureAwait(false) is not null)
            {
            }
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static async Task ObserveProcessExitAsync(Process process, Task standardErrorDrain)
    {
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
        }
        catch (TimeoutException)
        {
        }

        try
        {
            await standardErrorDrain.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }
    }

    private static void ThrowIfError(JsonElement response)
    {
        if (response.TryGetProperty("error", out var error))
        {
            throw new InvalidOperationException(error.GetRawText());
        }
    }

    private static string? ParsePlan(JsonElement accountResponse)
    {
        if (!accountResponse.TryGetProperty("result", out var result)
            || !result.TryGetProperty("account", out var account)
            || account.ValueKind == JsonValueKind.Null
            || !account.TryGetProperty("planType", out var plan)
            || plan.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return UsageText.SanitizeExternal(plan.GetString(), 32);
    }
}
