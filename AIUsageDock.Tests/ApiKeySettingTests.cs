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
