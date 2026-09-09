using System.Text.Json;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Microsoft.CmdPal.Common.Commands;

namespace CodexUsageDock;

internal sealed class CodexProfileSelectedEventArgs : EventArgs
{
    internal CodexProfileSelectedEventArgs(string name, CodexSourceOptions options)
    {
        Name = name;
        Options = options;
    }

    internal string Name { get; }

    internal CodexSourceOptions Options { get; }
}

internal sealed partial class CodexProfilesPage : ListPage, IDisposable
{
    private readonly object _gate = new();
    private readonly CodexProfileStore _store;
    private readonly NewProfileFormPage _newProfilePage;
    private bool _disposed;

    internal CodexProfilesPage(CodexProfileStore store)
    {
        _store = store;
        _newProfilePage = new NewProfileFormPage(store, RefreshItems);
        Id = "nl.mathijs.codexusage.profiles";
        Name = "Open";
        Title = "Codex source profiles";
        Icon = new IconInfo("\uE77B");
        PlaceholderText = "Search saved profiles";
    }

    internal event EventHandler<CodexProfileSelectedEventArgs>? ProfileSelected;

    public override IListItem[] GetItems()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return [];
            }
        }

        var items = new List<IListItem>();
        if (_store.StorageError is { Length: > 0 })
        {
            items.Add(new ListItem(new NoOpCommand())
            {
                Title = "Saved profiles unavailable",
                Subtitle = "Saved profiles could not be read. Saving a new profile will replace the unreadable file.",
                Icon = new IconInfo("\uE783"),
            });
        }

        items.Add(new ListItem(_newProfilePage)
        {
            Title = "Add or replace profile",
            Subtitle = "Enter a name and optional Codex source paths",
            Icon = new IconInfo("\uE710"),
            TextToSuggest = "Add or replace profile",
        });

        foreach (var profile in _store.Profiles)
        {
            var id = profile.Id;
            var deleteCommand = new AnonymousCommand(() => RemoveProfile(id))
            {
                Name = "Delete profile",
                Id = $"nl.mathijs.codexusage.profile.delete.{id:N}",
                Result = CommandResult.KeepOpen(),
            };
            var deleteConfirmation = new ConfirmableCommand(
                deleteCommand,
                "Delete this Codex profile?",
                "This removes the saved profile only. It does not change the active source or Codex configuration.",
                () => true)
            {
                Name = "Delete profile",
                Id = $"nl.mathijs.codexusage.profile.confirm-delete.{id:N}",
            };

            items.Add(new ListItem(new UseProfileCommand(this, id))
            {
                Title = profile.DisplayName,
                Subtitle = DescribeSource(profile.SourceOptions),
                TextToSuggest = profile.DisplayName,
                MoreCommands =
                [
                    new CommandContextItem(deleteConfirmation)
                    {
                        Title = "Delete profile",
                        Icon = new IconInfo("\uE74D"),
                    },
                ],
            });
        }

        return [.. items];
    }

    private CommandResult UseProfile(Guid id)
    {
        if (!_store.TryGet(id, out var profile) || profile is null)
        {
            return CommandResult.ShowToast("This Codex profile is no longer available.");
        }

        // Store loading keeps structurally valid offline WSL/network paths. Use
        // validates availability at activation so a stale profile fails closed.
        if (!CodexSourceOptions.TryCreate(
            profile.SourceOptions.ExecutablePath,
            profile.SourceOptions.HomePath,
            out var options,
            out _))
        {
            return CommandResult.ShowToast("The saved Codex source path is invalid or unavailable.");
        }

        ProfileSelected?.Invoke(this, new CodexProfileSelectedEventArgs(profile.DisplayName, options));
        return CommandResult.KeepOpen();
    }

    private void RemoveProfile(Guid id)
    {
        _store.TryRemove(id, out _);
        RefreshItems();
    }

    private void RefreshItems()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
        }

        RaiseItemsChanged(0);
    }

    private static string DescribeSource(CodexSourceOptions options)
    {
        if (options.ExecutablePath is null && options.HomePath is null)
        {
            return "Uses automatic Codex source discovery";
        }

        if (options.ExecutablePath is not null && options.HomePath is not null)
        {
            return "Custom executable and Codex home";
        }

        return options.ExecutablePath is not null ? "Custom executable" : "Custom Codex home";
    }

    private sealed partial class UseProfileCommand : InvokableCommand
    {
        private readonly CodexProfilesPage _owner;
        private readonly Guid _id;

        internal UseProfileCommand(CodexProfilesPage owner, Guid id)
        {
            _owner = owner;
            _id = id;
            Id = $"nl.mathijs.codexusage.profile.use.{id:N}";
        }

        public override string Name => "Use profile";

        public override ICommandResult Invoke() => _owner.UseProfile(_id);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _newProfilePage.Dispose();
        GC.SuppressFinalize(this);
    }
}

internal sealed partial class NewProfileFormPage : ContentPage, IDisposable
{
    private readonly object _gate = new();
    private readonly ProfileFormContent _form;
    private MarkdownContent _message;
    private bool _disposed;

    internal NewProfileFormPage(CodexProfileStore store, Action? saved = null)
    {
        _form = new ProfileFormContent(store, HandleSubmit);
        _message = new MarkdownContent("# Add or replace a Codex profile\n\nSave a named source for later use. Paths are checked before they are saved.");
        Id = "nl.mathijs.codexusage.profile.new";
        Name = "Open";
        Title = "Add or replace Codex profile";
        Icon = new IconInfo("\uE710");
        Saved = saved;
    }

    private Action? Saved { get; }

    public override IContent[] GetContent()
    {
        lock (_gate)
        {
            return _disposed ? [] : [_message, _form];
        }
    }

    private CommandResult HandleSubmit(
        string? displayName,
        string? executablePath,
        string? homePath)
    {
        if (_disposed)
        {
            return CommandResult.KeepOpen();
        }

        if (!_form.Store.TryUpsert(displayName, executablePath, homePath, out _, out var error))
        {
            SetMessage($"# Add or replace a Codex profile\n\n**Could not save the profile:** {UsageText.EscapeMarkdown(error ?? "The profile is invalid.")}");
            return CommandResult.KeepOpen();
        }

        Saved?.Invoke();
        SetMessage("# Profile saved\n\nThe profile is ready to select from the list.");
        return CommandResult.GoBack();
    }

    private void SetMessage(string message)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _message = new MarkdownContent(message);
        }

        RaiseItemsChanged(0);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        GC.SuppressFinalize(this);
    }

    private sealed partial class ProfileFormContent : FormContent
    {
        private const string InvalidPathValue = "<invalid-path-value>";
        private readonly Func<string?, string?, string?, CommandResult> _submit;

        internal ProfileFormContent(CodexProfileStore store, Func<string?, string?, string?, CommandResult> submit)
        {
            Store = store;
            _submit = submit;
            TemplateJson = """
            {"type":"AdaptiveCard","version":"1.5","body":[
              {"type":"Input.Text","id":"name","label":"Profile name","placeholder":"Work","maxLength":40,"isRequired":true},
              {"type":"Input.Text","id":"executablePath","label":"Codex executable path (optional)","placeholder":"C:\\Path\\to\\codex.exe","maxLength":1024},
              {"type":"Input.Text","id":"homePath","label":"Codex home path (optional)","placeholder":"C:\\Users\\you\\.codex","maxLength":1024},
              {"type":"TextBlock","text":"Use a full Windows path. A Windows-accessible WSL directory is allowed when available to Windows; this extension does not launch WSL or modify Codex configuration.","wrap":true}
            ],"actions":[{"type":"Action.Submit","title":"Save profile"}]}
            """;
        }

        internal CodexProfileStore Store { get; }

        public override CommandResult SubmitForm(string payload)
        {
            if (string.IsNullOrWhiteSpace(payload) || payload.Length > 8192)
            {
                return _submit(null, null, null);
            }

            try
            {
                using var document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 8 });
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    return _submit(null, null, null);
                }

                if (!TryReadOptionalString(document.RootElement, "executablePath", out var executablePath)
                    || !TryReadOptionalString(document.RootElement, "homePath", out var homePath))
                {
                    // A malformed optional path must not silently select the
                    // default source. The marker is deliberately invalid and
                    // never leaves this form or reaches storage.
                    return _submit(ReadString(document.RootElement, "name"), InvalidPathValue, null);
                }

                return _submit(
                    ReadString(document.RootElement, "name"),
                    executablePath,
                    homePath);
            }
            catch (JsonException)
            {
                return _submit(null, null, null);
            }
        }

        private static bool TryReadOptionalString(JsonElement root, string propertyName, out string? value)
        {
            value = null;
            if (!root.TryGetProperty(propertyName, out var property)
                || property.ValueKind == JsonValueKind.Null)
            {
                return true;
            }

            if (property.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            value = property.GetString();
            return true;
        }

        private static string? ReadString(JsonElement root, string propertyName) =>
            root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }
}
