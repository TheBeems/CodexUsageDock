using System.Buffers;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexUsageDock;

internal static class CodexAppServerReader
{
    private const int MaximumBucketCount = 32;

    internal static Task<CodexUsageSnapshot> ReadAsync(CancellationToken cancellationToken = default) =>
        ReadAsync(CodexSourceOptions.Default, cancellationToken);

    internal static Task<CodexUsageSnapshot> ReadAsync(CodexSourceOptions options, CancellationToken cancellationToken) =>
        WithAppServerAsync(options, static async (process, token) =>
        {
            await SendAsync(process, "account/rateLimits/read", 2, null, token).ConfigureAwait(false);
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
                token).ConfigureAwait(false);

            var responses = await ReadResponsesAsync(process.StandardOutput, token, 2, 3).ConfigureAwait(false);
            using var rateResponse = responses[2];
            using var accountResponse = responses[3];
            ThrowIfError(rateResponse.RootElement);
            return ParseSnapshot(rateResponse.RootElement.GetProperty("result"), accountResponse.RootElement, DateTimeOffset.Now);
        }, cancellationToken);

    internal static async Task<AccountUsageSnapshot> ReadAccountUsageAsync(CodexSourceOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return await WithAppServerAsync(options, static (process, token) =>
                ReadAccountUsageSequenceAsync((method, id, requestToken) => RequestAsync(process, method, id, requestToken), token),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or JsonException
            or OperationCanceledException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            return AccountUsageSnapshot.Unavailable with { UpdatedAt = DateTimeOffset.Now };
        }
    }

    internal static async Task<AccountUsageSnapshot> ReadAccountUsageSequenceAsync(
        Func<string, int, CancellationToken, Task<JsonDocument>> request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var before = await request("account/rateLimits/read", 2, cancellationToken).ConfigureAwait(false);
        var beforeKey = GetResponseAccountKey(before.RootElement);
        if (beforeKey is null)
        {
            return AccountUsageSnapshot.Unavailable with { UpdatedAt = DateTimeOffset.Now };
        }

        using var usage = await request("account/usage/read", 3, cancellationToken).ConfigureAwait(false);
        if (IsMethodNotFound(usage.RootElement))
        {
            return new AccountUsageSnapshot(beforeKey, DateTimeOffset.Now, [], AccountUsageStatus.Unsupported);
        }

        if (HasError(usage.RootElement))
        {
            return AccountUsageSnapshot.Unavailable with { UpdatedAt = DateTimeOffset.Now };
        }

        using var after = await request("account/rateLimits/read", 4, cancellationToken).ConfigureAwait(false);
        var afterKey = GetResponseAccountKey(after.RootElement);
        if (!string.Equals(beforeKey, afterKey, StringComparison.Ordinal))
        {
            return AccountUsageSnapshot.Unavailable with { UpdatedAt = DateTimeOffset.Now };
        }

        return TryGetObject(usage.RootElement, "result", out var result)
            ? AccountUsageParser.Parse(result, beforeKey, DateTimeOffset.Now)
            : AccountUsageSnapshot.Unavailable with { UpdatedAt = DateTimeOffset.Now };
    }

    private static string? GetResponseAccountKey(JsonElement response) =>
        !HasError(response) && TryGetObject(response, "result", out var result) ? ParseAccountKey(result) : null;

    internal static bool IsMethodNotFound(JsonElement response) =>
        TryGetObject(response, "error", out var error)
        && error.TryGetProperty("code", out var code)
        && code.ValueKind == JsonValueKind.Number
        && code.TryGetInt32(out var number)
        && number == -32601;

    private static async Task<JsonDocument> RequestAsync(Process process, string method, int id, CancellationToken cancellationToken)
    {
        await SendAsync(process, method, id, method == "account/usage/read" ? static writer =>
        {
            writer.WritePropertyName("params");
            writer.WriteStartObject();
            writer.WriteEndObject();
        } : null, cancellationToken).ConfigureAwait(false);
        var responses = await ReadResponsesAsync(process.StandardOutput, cancellationToken, id).ConfigureAwait(false);
        return responses[id];
    }

    private static async Task<T> WithAppServerAsync<T>(
        CodexSourceOptions options,
        Func<Process, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var process = StartAppServer(options);
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
            return await operation(process, readCancellationToken).ConfigureAwait(false);
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

    internal static CodexUsageSnapshot ParseSnapshot(JsonElement rateResult, JsonElement accountResponse, DateTimeOffset now)
    {
        if (rateResult.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Codex app-server did not return a usage result.");
        }

        TryGetObject(rateResult, "rateLimits", out var legacyLimits);
        var defaultId = ReadLimitId(legacyLimits, "limitId") ?? "codex";
        var defaultLimits = legacyLimits;
        if (TryGetObject(rateResult, "rateLimitsByLimitId", out var limitsById)
            && TryGetObject(limitsById, defaultId, out var richDefault))
        {
            defaultLimits = richDefault;
        }

        var buckets = new List<RateLimitBucket>();
        var bucketIds = new HashSet<string>(StringComparer.Ordinal);
        RateLimitBucket? defaultBucket = null;
        if (defaultLimits.ValueKind == JsonValueKind.Object)
        {
            defaultBucket = ParseBucket(defaultId, defaultLimits);
            buckets.Add(defaultBucket);
            bucketIds.Add(defaultId);
        }

        if (limitsById.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in limitsById.EnumerateObject())
            {
                if (buckets.Count >= MaximumBucketCount)
                {
                    break;
                }

                if (entry.Value.ValueKind == JsonValueKind.Object
                    && IsValidLimitId(entry.Name)
                    && bucketIds.Add(entry.Name))
                {
                    buckets.Add(ParseBucket(entry.Name, entry.Value));
                }
            }
        }

        var windows = RateLimitWindowParser.Classify(defaultBucket?.Primary, defaultBucket?.Secondary);
        var credits = ParseCredits(rateResult, defaultLimits);
        var resetCredits = ParseResetCredits(rateResult);
        var ordinaryUsageAllowed = rateResult.TryGetProperty("ordinaryUsageAllowed", out var allowed)
            && allowed.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? allowed.GetBoolean()
            : (bool?)null;
        if (buckets.Count == 0 && credits is null && resetCredits is null && ordinaryUsageAllowed is null)
        {
            throw new InvalidOperationException("Codex app-server did not return usable usage information.");
        }

        return new CodexUsageSnapshot(
            windows.FiveHour,
            windows.Weekly,
            ParsePlan(accountResponse) ?? ReadLabel(defaultLimits, "planType", 32),
            credits,
            resetCredits,
            now,
            UsageDataSource.AppServer,
            null,
            buckets.ToArray(),
            ParseAccountKey(rateResult),
            ordinaryUsageAllowed,
            now,
            defaultBucket?.Id);
    }

    private static RateLimitBucket ParseBucket(string id, JsonElement limits) => new(
        id,
        ReadLabel(limits, "limitName", 80),
        RateLimitWindowParser.TryParse(limits, "primary", "usedPercent", "windowDurationMins", "resetsAt"),
        RateLimitWindowParser.TryParse(limits, "secondary", "usedPercent", "windowDurationMins", "resetsAt"));

    private static string? ParseAccountKey(JsonElement result)
    {
        if (!result.TryGetProperty("accountId", out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var accountId = value.GetString();
        if (string.IsNullOrWhiteSpace(accountId) || accountId.Length > 512
            || accountId.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)))
        {
            return null;
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("CodexUsageDock:account:v1:" + accountId));
        return Convert.ToHexString(hash);
    }

    private static string? ReadLimitId(JsonElement limits, string propertyName)
    {
        if (limits.ValueKind != JsonValueKind.Object
            || !limits.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var id = value.GetString();
        return id is not null && IsValidLimitId(id) ? id : null;
    }

    private static bool IsValidLimitId(string id) => id.Length is > 0 and <= 128
        && id.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':' or '/');

    private static string? ReadLabel(JsonElement element, string propertyName, int maxLength) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.String
            ? UsageText.SanitizeExternal(value.GetString(), maxLength)
            : null;

    private static bool TryGetObject(JsonElement element, string propertyName, out JsonElement value)
    {
        value = default;
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out value)
            && value.ValueKind == JsonValueKind.Object;
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

                if (line.Length > 1_048_576)
                {
                    throw new InvalidOperationException("Codex app-server returned an oversized response.");
                }

                var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 32 });
                if (document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("id", out var id)
                    && id.ValueKind == JsonValueKind.Number
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
        if (TryGetObject(limits, "credits", out var nested))
        {
            credits = nested;
        }
        else if (!TryGetObject(result, "credits", out credits))
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
        if (result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("rateLimitResetCredits", out var resets)
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

    private static Process StartAppServer(CodexSourceOptions options) =>
        Process.Start(CreateStartInfo(options)) ?? throw new InvalidOperationException("Codex app-server could not be started.");

    internal static ProcessStartInfo CreateStartInfo(CodexSourceOptions options)
    {
        var executable = options.ExecutablePath ?? FindCodexExecutable();
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = GetSafeWorkingDirectory(executable),
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("app-server");
        startInfo.ArgumentList.Add("--stdio");
        if (options.HomePath is not null)
        {
            startInfo.Environment["CODEX_HOME"] = options.HomePath;
        }
        return startInfo;
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
        if (HasError(response))
        {
            throw new InvalidOperationException("Codex app-server returned an error.");
        }
    }

    private static bool HasError(JsonElement response) => response.ValueKind == JsonValueKind.Object
        && response.TryGetProperty("error", out _);

    private static string? ParsePlan(JsonElement accountResponse)
    {
        if (!TryGetObject(accountResponse, "result", out var result)
            || !TryGetObject(result, "account", out var account))
        {
            return null;
        }

        return ReadLabel(account, "planType", 32);
    }
}
