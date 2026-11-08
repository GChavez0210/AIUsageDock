using System.Text.Json;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace AIUsageDock;

internal sealed class ProviderSelectionSettings
{
    internal const string OllamaApiKeySetting = "ollamaApiKey";
    internal const string ClearKeyword = "clear";
    private const string OllamaSecretName = "ollama-api-key";

    private readonly string _path;
    private readonly ISecretStore _secrets;
    private readonly Settings _settings = new();
    private readonly PasswordSetting _ollamaApiKey;
    private string? _ollamaApiKeyValue;

    public ProviderSelectionSettings(string path, ISecretStore? secrets = null)
    {
        _path = path;
        _secrets = secrets ?? new CredentialSecretStore();
        _settings.Add(new ToggleSetting("claude", true)
        {
            Label = "Claude Code",
            Description = "Show Claude usage and refresh its subscription data",
        });
        _settings.Add(new ToggleSetting("codex", true)
        {
            Label = "Codex",
            Description = "Show Codex usage and refresh its subscription data",
        });
        _settings.Add(new ToggleSetting("antigravity", true)
        {
            Label = "Antigravity",
            Description = "Show Antigravity usage and refresh its subscription data",
        });
        _settings.Add(new ToggleSetting("ollama", true)
        {
            Label = "Ollama Cloud",
            Description = "Show Ollama Cloud usage and refresh its subscription data",
        });
        _ollamaApiKey = new PasswordSetting(OllamaApiKeySetting, string.Empty)
        {
            Label = "Ollama API key",
        };
        _settings.Add(_ollamaApiKey);

        _ollamaApiKeyValue = _secrets.Read(OllamaSecretName);

        try
        {
            if (File.Exists(_path))
            {
                _settings.Update(File.ReadAllText(_path));
            }
        }
        catch (JsonException)
        {
            // Keep defaults when a settings file is malformed.
        }

        // A key that reached settings.json (for example from an older build) is moved
        // into the credential store and scrubbed from the file.
        if (MoveEnteredKeyToSecretStore())
        {
            Persist();
        }

        DescribeApiKey();
        _settings.SettingsChanged += (_, _) => PersistAndNotify();
    }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AIUsageDock", "settings.json");

    public Settings ToolkitSettings => _settings;

    public event EventHandler? Changed;

    /// <summary>Raised after a key is saved or removed from the settings page.</summary>
    public event EventHandler? ApiKeyChanged;

    public bool IsEnabled(string providerId) => _settings.GetSetting<bool>(providerId);

    /// <summary>The key saved from the settings page, or null when none is saved.</summary>
    public string? OllamaApiKey => _ollamaApiKeyValue;

    private void PersistAndNotify()
    {
        var keyChanged = MoveEnteredKeyToSecretStore();
        DescribeApiKey();
        Persist();
        Changed?.Invoke(this, EventArgs.Empty);
        if (keyChanged)
        {
            ApiKeyChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool MoveEnteredKeyToSecretStore()
    {
        var entered = _ollamaApiKey.Value?.Trim();
        if (string.IsNullOrEmpty(entered))
        {
            return false;
        }

        var key = string.Equals(entered, ClearKeyword, StringComparison.OrdinalIgnoreCase) ? null : entered;
        _secrets.Write(OllamaSecretName, key);
        _ollamaApiKeyValue = key;
        _ollamaApiKey.Value = string.Empty;
        return true;
    }

    private void DescribeApiKey()
    {
        if (_ollamaApiKeyValue is null)
        {
            _ollamaApiKey.Placeholder = "Paste an API key from ollama.com/settings/keys";
            _ollamaApiKey.Description =
                $"Saved in Windows Credential Manager, not in the settings file. Without one, the {Providers.OllamaUsageProvider.ApiKeyVariable} environment variable is used.";
        }
        else
        {
            _ollamaApiKey.Placeholder = "•••••••• saved";
            _ollamaApiKey.Description =
                $"A key is saved in Windows Credential Manager. Enter a new key to replace it, or type \"{ClearKeyword}\" to remove it.";
        }
    }

    private void Persist()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, _settings.ToJson());
    }

    /// <summary>A text box that the Adaptive Card renderer masks.</summary>
    private sealed class PasswordSetting(string key, string defaultValue) : TextSetting(key, defaultValue)
    {
        public override Dictionary<string, object> ToDictionary()
        {
            var dictionary = base.ToDictionary();
            dictionary["style"] = "Password";
            return dictionary;
        }
    }
}
