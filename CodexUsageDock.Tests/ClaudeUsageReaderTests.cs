using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Xunit;

namespace CodexUsageDock.Tests;

public sealed class ClaudeUsageReaderTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(1);

    [Fact]
    public void ReaderParsesDocumentedWindowsAndIgnoresUnrelatedFields()
    {
        var snapshot = ReadJson(
            CaptureJson(
                Now,
                WindowJson(23.5, Now.AddHours(1)),
                WindowJson(41.25, Now.AddDays(3)),
                extra: "\"private\":{\"account\":\"secret\",\"model\":\"private-model\"}"));

        Assert.Equal(ClaudeUsageReadStatus.Available, snapshot.Status);
        Assert.True(snapshot.IsAvailable);
        Assert.Equal(23.5, snapshot.Primary!.UsedPercent);
        Assert.Equal(300, snapshot.Primary!.WindowMinutes);
        Assert.Equal(76.5, snapshot.Primary!.RemainingPercent);
        Assert.Equal(Now.AddHours(1), snapshot.Primary!.ResetsAt);
        Assert.Equal(41.25, snapshot.Weekly!.UsedPercent);
        Assert.Equal(10080, snapshot.Weekly!.WindowMinutes);
        Assert.Equal(Now, snapshot.ObservedAt);
    }

    [Fact]
    public void ReaderRequiresTheBridgeSchemaAndDoesNotAcceptUnspecifiedAliases()
    {
        var aliases = """
            {
              "schemaVersion": 1,
              "provider": "claude",
              "observedAtUTC": "2026-09-09T12:00:00.0000000Z",
              "fiveHour": { "usedPercentage": 20, "resetsAt": 1788958800 },
              "sevenDay": { "usedPercentage": 30, "resetsAt": 1789214400 }
            }
            """;

        var snapshot = ReadJson(aliases);

        Assert.Equal(ClaudeUsageReadStatus.Unavailable, snapshot.Status);
        Assert.Null(snapshot.Primary);
        Assert.Null(snapshot.Weekly);
    }

    [Fact]
    public void ReaderKeepsWindowsIndependentWhenOneIsMissingOrInvalid()
    {
        var missingWeekly = ReadJson(CaptureJson(Now, WindowJson(20, Now.AddHours(1)), null));
        var invalidPrimary = ReadJson(CaptureJson(Now, WindowJson(-1, Now.AddHours(1)), WindowJson(30, Now.AddDays(3))));

        Assert.Equal(ClaudeUsageReadStatus.Partial, missingWeekly.Status);
        Assert.NotNull(missingWeekly.Primary);
        Assert.Null(missingWeekly.Weekly);
        Assert.Equal(ClaudeUsageReadStatus.Partial, invalidPrimary.Status);
        Assert.Null(invalidPrimary.Primary);
        Assert.NotNull(invalidPrimary.Weekly);
    }

    [Fact]
    public void ReaderMarksFutureAndStaleObservationsWithoutCallingThemAvailable()
    {
        var future = ReadJson(CaptureJson(Now.AddSeconds(1), WindowJson(20, Now.AddHours(1)), WindowJson(30, Now.AddDays(3))));
        var stale = ReadJson(CaptureJson(Now.AddMinutes(-6), WindowJson(20, Now.AddHours(1)), WindowJson(30, Now.AddDays(3))));

        Assert.Equal(ClaudeUsageReadStatus.Future, future.Status);
        Assert.False(future.IsAvailable);
        Assert.NotNull(future.Primary);
        Assert.Equal(ClaudeUsageReadStatus.Stale, stale.Status);
        Assert.False(stale.IsAvailable);
        Assert.NotNull(stale.Weekly);
    }

    [Fact]
    public void ReaderRejectsZoneLessObservationTimes()
    {
        var zoneLess = """
            {
              "schemaVersion": 1,
              "provider": "claude",
              "observedAtUTC": "2026-09-09T12:00:00",
              "rate_limits": {
                "five_hour": { "used_percentage": 20, "resets_at": 1788958800 },
                "seven_day": { "used_percentage": 30, "resets_at": 1789214400 }
              }
            }
            """;

        Assert.Equal(ClaudeUsageReadStatus.Unavailable, ReadJson(zoneLess).Status);
    }

    [Fact]
    public void ReaderRejectsExpiredAndOutOfRangeWindowValues()
    {
        var expiredPrimary = ReadJson(CaptureJson(Now, WindowJson(20, Now.AddSeconds(-1)), WindowJson(30, Now.AddDays(3))));
        var expiredBoth = ReadJson(CaptureJson(Now, WindowJson(20, Now.AddSeconds(-1)), WindowJson(30, Now.AddSeconds(-1))));
        var outOfRange = ReadJson(CaptureJson(Now, WindowJson(101, Now.AddHours(1)), WindowJson(30, Now.AddDays(3))));

        Assert.Equal(ClaudeUsageReadStatus.Partial, expiredPrimary.Status);
        Assert.Null(expiredPrimary.Primary);
        Assert.NotNull(expiredPrimary.Weekly);
        Assert.Equal(ClaudeUsageReadStatus.Unavailable, expiredBoth.Status);
        Assert.Equal(ClaudeUsageReadStatus.Partial, outOfRange.Status);
        Assert.Null(outOfRange.Primary);
    }

    [Fact]
    public void ReaderRejectsMissingMalformedAndOversizedFilesSafely()
    {
        Assert.Equal(
            ClaudeUsageReadStatus.Unavailable,
            ClaudeUsageReader.Read(
                Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.json"),
                Now,
                RefreshInterval).Status);
        Assert.Equal(ClaudeUsageReadStatus.Unavailable, ReadJson("not-json").Status);

        var path = Path.Combine(Path.GetTempPath(), $"claude-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllBytes(path, new byte[ClaudeUsageReader.MaximumFileBytes + 1]);
            var snapshot = ClaudeUsageReader.Read(path, Now, RefreshInterval);
            Assert.Equal(ClaudeUsageReadStatus.Unavailable, snapshot.Status);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReaderRequiresAQualifiedCapturePath()
    {
        var snapshot = ClaudeUsageReader.Read("relative-claude-usage.json", Now, RefreshInterval);

        Assert.Equal(ClaudeUsageReadStatus.Unavailable, snapshot.Status);
        Assert.Contains("fully qualified", snapshot.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ScriptPassesThroughStatuslineAndWritesOnlyAggregateWindows()
    {
        var input = "{\"model\":\"private-model\",\"api_key\":\"do-not-copy\",\"rate_limits\":{\"five_hour\":{\"used_percentage\":23.5,\"resets_at\":4102444800},\"seven_day\":{\"used_percentage\":41,\"resets_at\":4102444800}},\"workspace\":{\"path\":\"private\"}}";
        var result = RunCaptureScript(input);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(input, result.StandardOutput);
        Assert.DoesNotContain("do-not-copy", result.SnapshotJson, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(result.SnapshotJson);
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("claude", root.GetProperty("provider").GetString());
        Assert.EndsWith("+00:00", root.GetProperty("observedAtUTC").GetString()!, StringComparison.Ordinal);
        var limits = root.GetProperty("rate_limits");
        Assert.Equal(23.5, limits.GetProperty("five_hour").GetProperty("used_percentage").GetDouble());
        Assert.Equal(41, limits.GetProperty("seven_day").GetProperty("used_percentage").GetDouble());
        Assert.DoesNotContain("model", result.SnapshotJson, StringComparison.Ordinal);
        Assert.DoesNotContain("workspace", result.SnapshotJson, StringComparison.Ordinal);
    }

    [Fact]
    public void ScriptStandaloneModeSuppressesRawStatuslineMetadata()
    {
        const string input = "{\"model\":\"private-model\",\"api_key\":\"do-not-copy\",\"rate_limits\":{\"five_hour\":{\"used_percentage\":23.5,\"resets_at\":4102444800},\"seven_day\":{\"used_percentage\":41,\"resets_at\":4102444800}}}";
        var result = RunCaptureScript(input, standalone: true);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Claude 5h 76.5% / week 59%", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("private-model", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("do-not-copy", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void ScriptDrainsAndPassesThroughInputWhenDestinationIsRelative()
    {
        const string input = "{\"rate_limits\":{},\"private\":\"unchanged\"}";
        var result = RunCaptureScript(input, "relative-claude-capture.json");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(input, result.StandardOutput);
        Assert.Contains("could not write the snapshot", result.StandardError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ScriptWritesUnavailableSnapshotForMissingOrInvalidFields()
    {
        var input = "{\"rate_limits\":{\"five_hour\":{\"used_percentage\":\"75\",\"resets_at\":0},\"seven_day\":{\"used_percentage\":101,\"resets_at\":0}},\"secret\":\"keep-out\"}";
        var result = RunCaptureScript(input);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(input, result.StandardOutput);
        using var document = JsonDocument.Parse(result.SnapshotJson);
        var root = document.RootElement;
        Assert.Equal("unavailable", root.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("rate_limits").GetProperty("five_hour").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("rate_limits").GetProperty("seven_day").ValueKind);
        Assert.DoesNotContain("keep-out", result.SnapshotJson, StringComparison.Ordinal);
    }

    [Fact]
    public void ScriptBoundsCapturedInputButStillPassesThroughOversizedStatuslineData()
    {
        var input = new string('x', 256 * 1024 + 1);
        var result = RunCaptureScript(input);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(input, result.StandardOutput);
        using var document = JsonDocument.Parse(result.SnapshotJson);
        Assert.Equal("unavailable", document.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public void ScriptRequiresAnAbsoluteDestinationAndReportsWriteFailuresGenerically()
    {
        var script = FindScript();
        var destinationDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(destinationDirectory);
        try
        {
            var result = RunCaptureScript("{}", destinationDirectory);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("could not write the snapshot", result.StandardError, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("{}", result.StandardError, StringComparison.Ordinal);
            Assert.NotEmpty(script);
        }
        finally
        {
            Directory.Delete(destinationDirectory, recursive: true);
        }
    }

    private static ClaudeUsageSnapshot ReadJson(
        string json,
        DateTimeOffset? now = null,
        TimeSpan? refreshInterval = null)
    {
        var path = Path.Combine(Path.GetTempPath(), $"claude-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return ClaudeUsageReader.Read(path, now ?? Now, refreshInterval ?? RefreshInterval);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string CaptureJson(
        DateTimeOffset observedAt,
        string? primary,
        string? weekly,
        string? extra = null)
    {
        var primaryText = primary ?? "null";
        var weeklyText = weekly ?? "null";
        var suffix = string.IsNullOrWhiteSpace(extra) ? string.Empty : $",{extra}";
        return "{\"schemaVersion\":1,\"provider\":\"claude\",\"observedAtUTC\":\""
            + observedAt.ToString("O", CultureInfo.InvariantCulture)
            + "\",\"rate_limits\":{\"five_hour\":"
            + primaryText
            + ",\"seven_day\":"
            + weeklyText
            + "}"
            + suffix
            + "}";
    }

    private static string WindowJson(double usedPercent, DateTimeOffset resetsAt) =>
        "{\"used_percentage\":"
        + usedPercent.ToString(CultureInfo.InvariantCulture)
        + ",\"resets_at\":"
        + Unix(resetsAt)
        + "}";

    private static long Unix(DateTimeOffset timestamp) => timestamp.ToUnixTimeSeconds();

    private static ScriptResult RunCaptureScript(string input, string? outputPath = null, bool standalone = false)
    {
        var destination = outputPath ?? Path.Combine(Path.GetTempPath(), $"claude-capture-{Guid.NewGuid():N}.json");
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = FindPowerShell(),
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            };
            startInfo.ArgumentList.Add("-NoLogo");
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(FindScript());
            startInfo.ArgumentList.Add("-OutputPath");
            startInfo.ArgumentList.Add(destination);
            if (standalone)
            {
                startInfo.ArgumentList.Add("-Standalone");
            }

            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("PowerShell did not start.");
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            process.StandardInput.Write(input);
            process.StandardInput.Close();
            if (!process.WaitForExit(30_000))
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException("The capture fixture did not finish.");
            }

            var stdout = stdoutTask.GetAwaiter().GetResult();
            var stderr = stderrTask.GetAwaiter().GetResult();
            var snapshot = File.Exists(destination) ? File.ReadAllText(destination, Encoding.UTF8) : string.Empty;
            return new ScriptResult(process.ExitCode, stdout, stderr, snapshot);
        }
        finally
        {
            if (File.Exists(destination))
            {
                File.Delete(destination);
            }
        }
    }

    private static string FindScript()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "scripts", "capture-claude-usage.ps1");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("The Claude capture fixture was not found.");
    }

    private static string FindPowerShell()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var candidate = Path.Combine(windows, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        return File.Exists(candidate) ? candidate : "pwsh";
    }

    private sealed record ScriptResult(int ExitCode, string StandardOutput, string StandardError, string SnapshotJson);
}
