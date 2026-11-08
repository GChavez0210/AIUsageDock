using Microsoft.CommandPalette.Extensions.Toolkit;
using Xunit;

namespace AIUsageDock.Tests;

public sealed class ApiKeySettingTests
{
    [Fact]
    public void EnteredKeyGoesToSecretStoreAndNotToSettingsFile()
    {
        var path = TempPath();
        var secrets = new MemorySecretStore();
        try
        {
            var selection = new ProviderSelectionSettings(path, secrets);
            var raised = 0;
            selection.ApiKeyChanged += (_, _) => raised++;

            Submit(selection, """{"ollamaApiKey":"  sk-test-123  "}""");

            Assert.Equal("sk-test-123", selection.OllamaApiKey);
            Assert.Equal("sk-test-123", secrets.Values["ollama-api-key"]);
            Assert.DoesNotContain("sk-test-123", File.ReadAllText(path));
            Assert.Equal(1, raised);

            // Toggling a provider leaves the saved key alone and does not re-raise.
            Submit(selection, """{"claude":"false","ollamaApiKey":""}""");
            Assert.Equal("sk-test-123", selection.OllamaApiKey);
            Assert.Equal(1, raised);

            Assert.Equal("sk-test-123", new ProviderSelectionSettings(path, secrets).OllamaApiKey);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ClearKeywordRemovesSavedKey()
    {
        var path = TempPath();
        var secrets = new MemorySecretStore();
        secrets.Values["ollama-api-key"] = "old";
        try
        {
            var selection = new ProviderSelectionSettings(path, secrets);
            Assert.Equal("old", selection.OllamaApiKey);

            Submit(selection, """{"ollamaApiKey":"CLEAR"}""");

            Assert.Null(selection.OllamaApiKey);
            Assert.False(secrets.Values.ContainsKey("ollama-api-key"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void KeyFoundInSettingsFileIsMigratedAndScrubbed()
    {
        var path = TempPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{"ollamaApiKey":"leaked-key"}""");
        var secrets = new MemorySecretStore();
        try
        {
            var selection = new ProviderSelectionSettings(path, secrets);

            Assert.Equal("leaked-key", selection.OllamaApiKey);
            Assert.DoesNotContain("leaked-key", File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void KeyFieldIsMaskedAndNeverEchoed()
    {
        var secrets = new MemorySecretStore();
        secrets.Values["ollama-api-key"] = "secret-value";
        var selection = new ProviderSelectionSettings(TempPath(), secrets);

        var form = Assert.IsType<SettingsForm>(Assert.Single(selection.ToolkitSettings.ToContent()));
        var card = form.TemplateJson + form.DataJson;

        Assert.Contains("\"Password\"", card);
        Assert.DoesNotContain("secret-value", card);
    }

    [Fact]
    public void KeyFieldSitsDirectlyUnderTheOllamaToggleIndented()
    {
        var selection = new ProviderSelectionSettings(TempPath(), new MemorySecretStore());
        var form = Assert.IsType<SettingsForm>(Assert.Single(selection.ToolkitSettings.ToContent()));
        var body = System.Text.Json.Nodes.JsonNode.Parse(form.TemplateJson)!["body"]!.AsArray();

        var rows = body.Select(node => node!.ToJsonString()).ToList();
        var ollamaIndex = rows.FindIndex(row => row.Contains("\"id\":\"ollama\""));

        // Ollama toggle, then its "Dock shows" dropdown, then its API key; all indented.
        Assert.Equal("ollamaDock", IndentedInput(body[ollamaIndex + 1]!)["id"]!.GetValue<string>());
        var key = IndentedInput(body[ollamaIndex + 2]!);
        Assert.Equal("Input.Text", key["type"]!.GetValue<string>());
        Assert.Equal("ollamaApiKey", key["id"]!.GetValue<string>());
    }

    [Fact]
    public void EveryProviderHasADockDropdownUnderItsToggle()
    {
        var selection = new ProviderSelectionSettings(TempPath(), new MemorySecretStore());
        var form = Assert.IsType<SettingsForm>(Assert.Single(selection.ToolkitSettings.ToContent()));
        var body = System.Text.Json.Nodes.JsonNode.Parse(form.TemplateJson)!["body"]!.AsArray();
        var rows = body.Select(node => node!.ToJsonString()).ToList();

        foreach (var id in new[] { "claude", "codex", "antigravity", "ollama" })
        {
            var toggle = rows.FindIndex(row => row.Contains($"\"id\":\"{id}\""));
            var dropdown = IndentedInput(body[toggle + 1]!);
            Assert.Equal("Input.ChoiceSet", dropdown["type"]!.GetValue<string>());
            Assert.Equal(id + "Dock", dropdown["id"]!.GetValue<string>());
            Assert.Equal("auto", dropdown["value"]!.GetValue<string>());
        }
    }

    [Fact]
    public void DockDropdownOffersReportedWindowsAndPersistsChoice()
    {
        var path = TempPath();
        try
        {
            var selection = new ProviderSelectionSettings(path, new MemorySecretStore());
            var raised = 0;
            selection.DockChoiceChanged += (_, _) => raised++;
            var fable = new Models.UsageWindow(Models.UsageWindowKind.Weekly, "Week (Fable only)", 83, null, 10080);
            selection.ReportWindows("claude", [new Models.UsageWindow(Models.UsageWindowKind.Weekly, "Week (all models)", 71, null, 10080), fable]);

            var form = Assert.IsType<SettingsForm>(Assert.Single(selection.ToolkitSettings.ToContent()));
            Assert.Contains("Week (Fable only) · 7d", System.Text.RegularExpressions.Regex.Unescape(form.TemplateJson));
            var value = Bands.DockChoice.ValueFor(fable);
            Assert.Contains(value, form.TemplateJson);

            form.SubmitForm($$"""{"claudeDock":"{{value}}"}""", "{}");

            Assert.Equal(value, selection.DockChoiceFor("claude"));
            Assert.Equal(1, raised);

            // After a restart the choice is kept and offered before Claude reports again.
            var reloaded = new ProviderSelectionSettings(path, new MemorySecretStore());
            Assert.Equal(value, reloaded.DockChoiceFor("claude"));
            var reloadedForm = Assert.IsType<SettingsForm>(Assert.Single(reloaded.ToolkitSettings.ToContent()));
            Assert.Contains("Week (Fable only) · 7d (not reported now)", System.Text.RegularExpressions.Regex.Unescape(reloadedForm.TemplateJson));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static System.Text.Json.Nodes.JsonNode IndentedInput(System.Text.Json.Nodes.JsonNode row)
    {
        Assert.Equal("ColumnSet", row["type"]!.GetValue<string>());
        var columns = row["columns"]!.AsArray();
        Assert.Equal("20px", columns[0]!["width"]!.GetValue<string>());
        return columns[1]!["items"]![0]!;
    }

    [Fact]
    public void BarColorChoicePersists()
    {
        var path = TempPath();
        try
        {
            var selection = new ProviderSelectionSettings(path, new MemorySecretStore());
            Assert.Equal(Pages.BarColorMode.Status, selection.BarColors);

            Submit(selection, """{"barColors":"accent"}""");

            Assert.Equal(Pages.BarColorMode.Accent, selection.BarColors);
            Assert.Equal(Pages.BarColorMode.Accent, new ProviderSelectionSettings(path, new MemorySecretStore()).BarColors);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RefreshIntervalDefaultsToTenMinutesAndIsConfigurable()
    {
        var path = TempPath();
        try
        {
            var selection = new ProviderSelectionSettings(path, new MemorySecretStore());
            var raised = 0;
            selection.RefreshIntervalChanged += (_, _) => raised++;
            Assert.Equal(TimeSpan.FromMinutes(10), selection.RefreshInterval);

            Submit(selection, """{"refreshMinutes":"30"}""");
            Assert.Equal(TimeSpan.FromMinutes(30), selection.RefreshInterval);
            Assert.Equal(1, raised);

            // An unchanged interval does not re-raise; a hand-edited value falls back.
            Submit(selection, """{"claude":"false"}""");
            Assert.Equal(1, raised);
            Submit(selection, """{"refreshMinutes":"7"}""");
            Assert.Equal(TimeSpan.FromMinutes(10), selection.RefreshInterval);

            Assert.Equal(TimeSpan.FromMinutes(10), new ProviderSelectionSettings(path, new MemorySecretStore()).RefreshInterval);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void Submit(ProviderSelectionSettings selection, string json)
    {
        var form = Assert.IsType<SettingsForm>(Assert.Single(selection.ToolkitSettings.ToContent()));
        form.SubmitForm(json, "{}");
    }

    private static string TempPath() => Path.Combine(Path.GetTempPath(), "AIUsageDock.Tests", Guid.NewGuid() + ".json");

    private sealed class MemorySecretStore : ISecretStore
    {
        public Dictionary<string, string> Values { get; } = [];

        public string? Read(string name) => Values.TryGetValue(name, out var value) ? value : null;

        public void Write(string name, string? value)
        {
            if (value is null)
            {
                Values.Remove(name);
            }
            else
            {
                Values[name] = value;
            }
        }
    }
}
