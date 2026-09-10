using System.Text.Json;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Microsoft.CmdPal.Common.Commands;
using Xunit;

namespace CodexUsageDock.Tests;

public sealed class CodexProfileStoreTests : IDisposable
{
    private readonly TestEnvironment _environment = new();

    public void Dispose() => _environment.Dispose();

    [Fact]
    public void UpsertPersistsAndReplacesNamesCaseInsensitively()
    {
        var executable = _environment.PathFor("codex.exe");
        var home = _environment.PathFor("codex-home");
        File.WriteAllText(executable, string.Empty);
        Directory.CreateDirectory(home);
        var store = new CodexProfileStore(_environment.PathFor("profiles.json"));

        Assert.True(store.TryUpsert("Work", executable, home, out var first, out var firstError), firstError);
        Assert.NotNull(first);
        Assert.Equal(32, first!.Id.ToString("N").Length);
        Assert.Equal(executable, first.SourceOptions.ExecutablePath);
        Assert.Equal(home, first.SourceOptions.HomePath);

        Assert.True(store.TryUpsert("work", null, null, out var replacement, out var replacementError), replacementError);
        Assert.NotNull(replacement);
        Assert.Equal(first.Id, replacement!.Id);
        Assert.Equal("work", replacement.DisplayName);
        Assert.Null(replacement.SourceOptions.ExecutablePath);
        Assert.Null(replacement.SourceOptions.HomePath);

        var reloaded = new CodexProfileStore(_environment.PathFor("profiles.json"));
        var saved = Assert.Single(reloaded.Profiles);
        Assert.Equal(replacement.Id, saved.Id);
        Assert.Equal("work", saved.DisplayName);
        Assert.Null(saved.SourceOptions.ExecutablePath);
        Assert.Null(saved.SourceOptions.HomePath);
    }

    [Fact]
    public void InvalidNamesAndPathsAreRejectedAndTheProfileCountIsBounded()
    {
        var store = new CodexProfileStore(_environment.PathFor("profiles.json"));

        Assert.False(store.TryUpsert(string.Empty, null, null, out _, out var emptyNameError));
        Assert.Contains("1 to 40", emptyNameError, StringComparison.Ordinal);
        Assert.False(store.TryUpsert(new string('x', 41), null, null, out _, out _));
        Assert.False(store.TryUpsert("bad\u0001name", null, null, out _, out _));

        var relativeExecutable = Path.Combine("relative", "codex.exe");
        Assert.False(store.TryUpsert("Relative", relativeExecutable, null, out _, out var relativeError));
        Assert.DoesNotContain(relativeExecutable, relativeError, StringComparison.Ordinal);

        var missingHome = Path.Combine(_environment.PathFor("missing"), "home");
        Assert.False(store.TryUpsert("Missing home", null, missingHome, out _, out var missingHomeError));
        Assert.DoesNotContain(missingHome, missingHomeError, StringComparison.Ordinal);

        for (var index = 0; index < CodexProfileStore.MaximumProfiles; index++)
        {
            Assert.True(store.TryUpsert($"Profile {index}", null, null, out _, out var error), error);
        }

        Assert.False(store.TryUpsert("Profile 8", null, null, out _, out var limitError));
        Assert.Contains("eight", limitError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(CodexProfileStore.MaximumProfiles, store.Profiles.Count);
        Assert.Equal(CodexProfileStore.MaximumProfiles, store.Profiles.Select(profile => profile.Id).Distinct().Count());
    }

    [Fact]
    public void ReloadKeepsOfflinePathsButPersistsOnlyProfileFields()
    {
        var unavailableExecutable = Path.Combine(_environment.PathFor("offline"), "codex.exe");
        var unavailableHome = _environment.PathFor("offline-home");
        var path = _environment.PathFor("profiles.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            schemaVersion = CodexProfileStore.SchemaVersion,
            profiles = new[]
            {
                new
                {
                    id = Guid.NewGuid().ToString("N"),
                    displayName = "Offline WSL",
                    executablePath = unavailableExecutable,
                    homePath = unavailableHome,
                    accountEmail = "private@example.com",
                },
            },
        }));

        var store = new CodexProfileStore(path);
        var loaded = Assert.Single(store.Profiles);
        Assert.Equal(unavailableExecutable, loaded.SourceOptions.ExecutablePath);
        Assert.Equal(unavailableHome, loaded.SourceOptions.HomePath);

        Assert.True(store.TryUpsert("Offline WSL", null, null, out _, out var error), error);
        var saved = File.ReadAllText(path);
        Assert.DoesNotContain("private@example.com", saved, StringComparison.Ordinal);
        Assert.DoesNotContain("accountEmail", saved, StringComparison.Ordinal);
    }

    [Fact]
    public void MalformedAndOversizedProfileDocumentsFailSafelyAndLoadAtMostEight()
    {
        var path = _environment.PathFor("profiles.json");
        File.WriteAllText(path, "null");

        var malformed = new CodexProfileStore(path);
        Assert.Empty(malformed.Profiles);
        Assert.Contains("could not be read", malformed.StorageError, StringComparison.Ordinal);
        Assert.DoesNotContain("null", malformed.StorageError, StringComparison.OrdinalIgnoreCase);

        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            schemaVersion = CodexProfileStore.SchemaVersion,
            profiles = Enumerable.Range(0, CodexProfileStore.MaximumProfiles + 4)
                .Select(index => new
                {
                    id = Guid.NewGuid().ToString("N"),
                    displayName = $"Profile {index}",
                    executablePath = (string?)null,
                    homePath = (string?)null,
                })
                .ToArray(),
        }));

        var bounded = new CodexProfileStore(path);
        Assert.Equal(CodexProfileStore.MaximumProfiles, bounded.Profiles.Count);
    }

    [Fact]
    public void RemoveUsesStableIdentityAndPersistsTheDeletion()
    {
        var path = _environment.PathFor("profiles.json");
        var store = new CodexProfileStore(path);
        Assert.True(store.TryUpsert("Work", null, null, out var profile, out var error), error);
        Assert.NotNull(profile);
        Assert.True(store.TryGet(profile!.Id, out var found));
        Assert.Equal(profile, found);

        Assert.False(store.TryRemove(Guid.Empty, out var invalidIdError));
        Assert.Contains("identifier", invalidIdError, StringComparison.OrdinalIgnoreCase);
        Assert.True(store.TryRemove(profile.Id, out var removeError), removeError);
        Assert.Empty(store.Profiles);
        Assert.Empty(new CodexProfileStore(path).Profiles);
    }

    [Fact]
    public void ProfilesPageOffersFormUseAndConfirmedRemoval()
    {
        var executable = _environment.PathFor("codex.exe");
        var home = _environment.PathFor("codex-home");
        File.WriteAllText(executable, string.Empty);
        Directory.CreateDirectory(home);
        var store = new CodexProfileStore(_environment.PathFor("profiles.json"));
        Assert.True(store.TryUpsert("Work", executable, home, out _, out var error), error);

        using var page = new CodexProfilesPage(store);
        var items = page.GetItems();
        Assert.Equal(2, items.Length);
        var add = Assert.Single(items, item => item.Title == "Add or replace profile");
        var formPage = Assert.IsType<NewProfileFormPage>(add.Command);
        var form = Assert.IsAssignableFrom<FormContent>(Assert.Single(formPage.GetContent().OfType<FormContent>()));
        Assert.Contains("\"id\":\"name\"", form.TemplateJson, StringComparison.Ordinal);
        Assert.Contains("\"id\":\"executablePath\"", form.TemplateJson, StringComparison.Ordinal);
        Assert.Contains("\"id\":\"homePath\"", form.TemplateJson, StringComparison.Ordinal);

        var selected = new List<CodexProfileSelectedEventArgs>();
        page.ProfileSelected += (_, args) => selected.Add(args);
        var profileItem = Assert.Single(items, item => item.Title == "Work");
        var use = Assert.IsAssignableFrom<IInvokableCommand>(profileItem.Command);
        use.Invoke(page);
        var selection = Assert.Single(selected);
        Assert.Equal("Work", selection.Name);
        Assert.Equal(executable, selection.Options.ExecutablePath);
        Assert.Equal(home, selection.Options.HomePath);

        var deleteContext = Assert.IsType<CommandContextItem>(Assert.Single(profileItem.MoreCommands));
        var confirmation = Assert.IsType<ConfirmableCommand>(deleteContext.Command);
        Assert.Equal("Delete profile", deleteContext.Title);
        confirmation.Command.Invoke(page);
        Assert.Empty(store.Profiles);
        Assert.Single(page.GetItems());
    }

    [Fact]
    public void NewProfileFormUpsertsAProfileWithoutApplyingIt()
    {
        var store = new CodexProfileStore(_environment.PathFor("profiles.json"));
        using var page = new NewProfileFormPage(store);
        var form = Assert.IsAssignableFrom<FormContent>(Assert.Single(page.GetContent().OfType<FormContent>()));
        var result = form.SubmitForm(JsonSerializer.Serialize(new
        {
            name = "Work",
            executablePath = string.Empty,
            homePath = string.Empty,
        }), "{}");

        Assert.NotNull(result);
        var profile = Assert.Single(store.Profiles);
        Assert.Equal("Work", profile.DisplayName);
        Assert.Null(profile.SourceOptions.ExecutablePath);
        Assert.Null(profile.SourceOptions.HomePath);
    }

    [Theory]
    [InlineData("{\"name\":\"Work\",\"executablePath\":42}")]
    [InlineData("{\"name\":\"Work\",\"homePath\":false}")]
    [InlineData("{\"name\":\"Work\",\"homePath\":{}}")]
    public void NewProfileFormRejectsNonStringPathValues(string payload)
    {
        var store = new CodexProfileStore(_environment.PathFor("profiles.json"));
        using var page = new NewProfileFormPage(store);
        var form = Assert.IsAssignableFrom<FormContent>(Assert.Single(page.GetContent().OfType<FormContent>()));

        var result = form.SubmitForm(payload, "{}");

        Assert.NotNull(result);
        Assert.Empty(store.Profiles);
    }
}
