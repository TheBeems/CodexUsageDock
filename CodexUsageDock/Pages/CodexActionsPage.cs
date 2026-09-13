using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Microsoft.CmdPal.Common.Commands;

namespace CodexUsageDock;

internal sealed partial class CodexActionsPage : ContentPage, IDisposable
{
    private readonly CodexUsageService _service;
    private readonly TaskUsageForm _form;
    private readonly object _gate = new();
    private MarkdownContent _content = new(string.Empty);
    private bool _disposed;

    internal CodexActionsPage(CodexUsageService service)
    {
        _service = service;
        _form = new TaskUsageForm(service);
        Id = "nl.mathijs.codexusage.actions";
        Name = "Open";
        Title = "Codex task usage and earned resets";
        Icon = new IconInfo("\uE945");
        service.Updated += OnUpdated;
        Refresh();
    }

    public override IContent[] GetContent() { lock (_gate) { return [_form, _content]; } }

    private void Refresh()
    {
        lock (_gate)
        {
            if (_disposed) return;
            var presentation = _service.GetActionPresentation();
            var body = new StringBuilder("# Task usage\n\n").Append(presentation.TaskStatus).Append("\n\n");
            if (presentation.Task is { } task) body.Append(FormatTask(task));
            body.Append("\n# Earned resets\n\nReported available: ")
                .Append(presentation.Usage.ResetCredits?.AvailableCount.ToString(CultureInfo.InvariantCulture) ?? "Unknown")
                .Append(".\n\n").Append(presentation.ResetStatus)
                .Append("\n\nUse the confirmed reset action only when you want to redeem one existing earned reset. ")
                .Append("After an unknown outcome, retrying uses the same saved request ID. The backend decides eligibility.\n");
            var expectedAccount = presentation.Usage.AccountKey;
            var commands = new List<ICommandContextItem>
            {
                new CommandContextItem(new RefreshUsageCommand(_service)) { Title = "Refresh usage" },
            };
            if (expectedAccount is not null)
            {
                commands.Add(new CommandContextItem(new ConfirmableCommand(
                    new AnonymousCommand(() => { _ = _service.ConsumeEarnedResetAsync(expectedAccount); }) { Result = CommandResult.KeepOpen() },
                    "Use or retry an earned reset?",
                    "Redeem one existing earned reset for the currently identified Codex account. A retry of an uncertain request reuses its saved ID. No reset is purchased.",
                    () => true) { Name = "Use or retry an earned reset" }) { Title = "Use or retry an earned reset" });
            }
            _content = new MarkdownContent(body.ToString());
            Commands = commands.ToArray();
        }
        RaiseItemsChanged(0);
    }

    internal static string FormatTask(ThreadUsageSnapshot task)
    {
        if (task.Status is not (ThreadUsageStatus.Available or ThreadUsageStatus.Partial)) return string.Empty;
        var body = new StringBuilder("Task: ").Append(UsageText.EscapeMarkdown(task.ThreadId ?? "Unknown"))
            .Append(".\n\n**Server estimate, not a bill or quota percentage.** Last read: ")
            .Append(task.UpdatedAt.ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture)).Append(".\n\n")
            .Append("Estimated credits: ").Append(Micro(task.EstimatedUsageCreditsMicros))
            .Append("; estimated USD: ").Append(Micro(task.EstimatedUsageUsdMicros)).Append(".\n\n")
            .Append("| Model / effort / speed | Input | Cached input | Net new input | Output | Total | Estimated credits |\n")
            .Append("| --- | ---: | ---: | ---: | ---: | ---: | ---: |\n");
        foreach (var group in task.Groups ?? [])
            body.Append("| ").Append(UsageText.EscapeMarkdown(string.Join(" / ", group.Model ?? "Unknown model", group.ReasoningEffort ?? "Unknown effort", group.Speed ?? "Unknown speed")))
                .Append(" | ").Append(Tokens(group.InputTokens)).Append(" | ").Append(Tokens(group.CachedInputTokens))
                .Append(" | ").Append(Tokens(group.NetNewInputTokens)).Append(" | ").Append(Tokens(group.OutputTokens))
                .Append(" | ").Append(Tokens(group.TotalTokens)).Append(" | ").Append(Micro(group.EstimatedUsageCreditsMicros)).Append(" |\n");
        return body.Append("\nCached and net-new input are components of input; do not add them to input again. Missing values are not zero.\n").ToString();
    }

    private static string Micro(long? value) => value is { } amount ? (amount / 1_000_000m).ToString("0.######", CultureInfo.InvariantCulture) : "Not reported";
    private static string Tokens(long? value) => value?.ToString("N0", CultureInfo.InvariantCulture) ?? "Not reported";
    private void OnUpdated(object? sender, EventArgs args) => Refresh();
    public void Dispose() { lock (_gate) { _disposed = true; _service.Updated -= OnUpdated; } }
}

internal sealed partial class TaskUsageForm : FormContent
{
    private readonly CodexUsageService _service;
    internal TaskUsageForm(CodexUsageService service)
    {
        _service = service;
        TemplateJson = """
        {"type":"AdaptiveCard","version":"1.5","body":[{"type":"Input.Text","id":"threadId","label":"Codex task ID","placeholder":"Paste the task ID","maxLength":128,"isRequired":true}],"actions":[{"type":"Action.Submit","title":"Read task estimate"}]}
        """;
    }

    public override CommandResult SubmitForm(string payload)
    {
        try
        {
            if (string.IsNullOrEmpty(payload) || payload.Length > 4096) return CommandResult.KeepOpen();
            using var doc = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 8 });
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("threadId", out var id)
                && id.ValueKind == JsonValueKind.String) _ = _service.ReadTaskUsageAsync(id.GetString()!);
        }
        catch (JsonException) { }
        return CommandResult.KeepOpen();
    }
}
