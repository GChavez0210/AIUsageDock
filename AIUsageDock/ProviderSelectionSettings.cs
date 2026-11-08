using System.Text.Json;
using AIUsageDock.Bands;
using AIUsageDock.Models;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace AIUsageDock;

internal sealed class ProviderSelectionSettings
{
    internal const string OllamaApiKeySetting = "ollamaApiKey";
    internal const string ClearKeyword = "clear";
    internal const string BarColorsSetting = "barColors";
    internal const string RefreshIntervalSetting = "refreshMinutes";
    internal const int DefaultRefreshMinutes = 10;
    internal static readonly int[] RefreshMinuteChoices = [1, 5, 10, 15, 30, 60];
    private const string OllamaSecretName = "ollama-api-key";

    private static readonly (string Id, string Label)[] KnownProviders =
    [
        ("claude", "Claude Code"),
        ("codex", "Codex"),
        ("antigravity", "Antigravity"),
        ("ollama", "Ollama"),
    ];

    private readonly string _path;
    private readonly ISecretStore _secrets;
    private readonly Settings _settings = new();
    private readonly PasswordSetting _ollamaApiKey;
    private readonly ChoiceSetSetting _barColors;
    private readonly ChoiceSetSetting _refreshInterval;
    private TimeSpan _lastRefreshInterval;
    private readonly Dictionary<string, DockChoiceSetting> _dockChoices = [];
    private readonly Dictionary<string, string> _lastDockValues = [];
    private string? _ollamaApiKeyValue;

    public ProviderSelectionSettings(string path, ISecretStore? secrets = null)
    {
        _path = path;
        _secrets = secrets ?? new CredentialSecretStore();

        // Each provider's own options sit indented under its toggle.
        _ollamaApiKey = new PasswordSetting(OllamaApiKeySetting, string.Empty) { Label = "API key" };
        foreach (var (id, label) in KnownProviders)
        {
            _settings.Add(new ToggleSetting(id, true)
            {
                Label = label,
                Description = $"Show {label} usage and refresh its subscription data",
            });

            var dock = new DockChoiceSetting(DockSettingKey(id)) { Label = "Dock shows" };
            _dockChoices[id] = dock;
            _settings.Add(dock);

            if (id == "ollama")
            {
                _settings.Add(_ollamaApiKey);
            }
        }

        _barColors = new ChoiceSetSetting(
            BarColorsSetting,
            "Usage bar colors",
            "Status colors render as pale tints in the light theme; the accent color is easier to see.",
            [
                new ChoiceSetSetting.Choice("Green, yellow or red by remaining capacity", "status"),
                new ChoiceSetSetting.Choice("Accent color", "accent"),
            ])
        {
            Value = "status",
        };
        _settings.Add(_barColors);

        _refreshInterval = new ChoiceSetSetting(
            RefreshIntervalSetting,
            "Refresh every",
            "How often the Dock bands read usage in the background. Opening a detail page or using Refresh reads it right away.",
            [.. RefreshMinuteChoices.Select(minutes => new ChoiceSetSetting.Choice(
                minutes == 1 ? "1 minute" : minutes == 60 ? "1 hour" : $"{minutes} minutes",
                minutes.ToString(System.Globalization.CultureInfo.InvariantCulture)))])
        {
            Value = DefaultRefreshMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        _settings.Add(_refreshInterval);

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
        RememberDockValues();
        _lastRefreshInterval = RefreshInterval;
        _settings.SettingsChanged += (_, _) => PersistAndNotify();
    }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AIUsageDock", "settings.json");

    public Settings ToolkitSettings => _settings;

    public event EventHandler? Changed;

    /// <summary>Raised after a key is saved or removed from the settings page.</summary>
    public event EventHandler? ApiKeyChanged;

    /// <summary>Raised when any provider's "Dock shows" choice changes.</summary>
    public event EventHandler? DockChoiceChanged;

    /// <summary>Raised when "Refresh every" changes.</summary>
    public event EventHandler? RefreshIntervalChanged;

    /// <summary>The background refresh period; unknown or hand-edited values fall back to the default.</summary>
    public TimeSpan RefreshInterval =>
        int.TryParse(_refreshInterval.Value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var minutes) &&
        RefreshMinuteChoices.Contains(minutes)
            ? TimeSpan.FromMinutes(minutes)
            : TimeSpan.FromMinutes(DefaultRefreshMinutes);

    public bool IsEnabled(string providerId) => _settings.GetSetting<bool>(providerId);

    public Pages.BarColorMode BarColors =>
        string.Equals(_barColors.Value, "accent", StringComparison.OrdinalIgnoreCase)
            ? Pages.BarColorMode.Accent
            : Pages.BarColorMode.Status;

    /// <summary>The key saved from the settings page, or null when none is saved.</summary>
    public string? OllamaApiKey => _ollamaApiKeyValue;

    /// <summary>The <see cref="DockChoice"/> value saved for a provider.</summary>
    public string DockChoiceFor(string providerId) =>
        _dockChoices.TryGetValue(providerId, out var setting) && !string.IsNullOrWhiteSpace(setting.Value)
            ? setting.Value
            : DockChoice.Auto;

    /// <summary>Offers the windows a provider just reported in its "Dock shows" dropdown.</summary>
    public void ReportWindows(string providerId, IReadOnlyList<UsageWindow> windows)
    {
        if (_dockChoices.TryGetValue(providerId, out var setting))
        {
            setting.SetWindows(windows);
        }
    }

    internal static string DockSettingKey(string providerId) => providerId + "Dock";

    private void PersistAndNotify()
    {
        var keyChanged = MoveEnteredKeyToSecretStore();
        DescribeApiKey();
        Persist();
        var dockChanged = RememberDockValues();
        Changed?.Invoke(this, EventArgs.Empty);
        if (keyChanged)
        {
            ApiKeyChanged?.Invoke(this, EventArgs.Empty);
        }

        if (dockChanged)
        {
            DockChoiceChanged?.Invoke(this, EventArgs.Empty);
        }

        if (RefreshInterval != _lastRefreshInterval)
        {
            _lastRefreshInterval = RefreshInterval;
            RefreshIntervalChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool RememberDockValues()
    {
        var changed = false;
        foreach (var (id, setting) in _dockChoices)
        {
            var value = DockChoiceFor(id);
            if (_lastDockValues.TryGetValue(id, out var previous) && previous != value)
            {
                changed = true;
            }

            _lastDockValues[id] = value;
        }

        return changed;
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

    /// <summary>
    /// Wraps an input in the same two-column layout as the Toolkit's toggles, with an
    /// empty toggle-width column, so it reads as part of the provider toggle above it.
    /// Only types registered in the Toolkit's source-generated JSON context can be
    /// nested here, so arrays must be List&lt;Dictionary&lt;string, object&gt;&gt;.
    /// </summary>
    private static Dictionary<string, object> Indent(Dictionary<string, object> input, string? description)
    {
        var items = new List<Dictionary<string, object>> { input };
        if (!string.IsNullOrWhiteSpace(description))
        {
            items.Add(new Dictionary<string, object>
            {
                ["type"] = "TextBlock",
                ["text"] = description,
                ["isSubtle"] = true,
                ["size"] = "Small",
                ["spacing"] = "Small",
                ["wrap"] = true,
            });
        }

        return new Dictionary<string, object>
        {
            ["type"] = "ColumnSet",
            ["spacing"] = "Small",
            ["columns"] = new List<Dictionary<string, object>>
            {
                new() { ["type"] = "Column", ["width"] = "20px", ["items"] = new List<Dictionary<string, object>>() },
                new() { ["type"] = "Column", ["width"] = "stretch", ["items"] = items },
            },
        };
    }

    /// <summary>A masked text box under the Ollama Cloud toggle.</summary>
    private sealed class PasswordSetting(string key, string defaultValue) : TextSetting(key, defaultValue)
    {
        public override Dictionary<string, object> ToDictionary()
        {
            var input = base.ToDictionary();
            input["style"] = "Password";
            input["label"] = Label;
            return Indent(input, Description);
        }
    }

    /// <summary>
    /// The "Dock shows" dropdown under a provider toggle. The Toolkit's ChoiceSetSetting
    /// is sealed and has fixed choices; this one adds the windows the provider reports
    /// at runtime and keeps a saved window choice even before it has been reported.
    /// </summary>
    private sealed class DockChoiceSetting(string key) : TextSetting(key, DockChoice.Auto)
    {
        private readonly object _gate = new();
        private IReadOnlyList<(string Value, string Title)> _windows = [];

        public void SetWindows(IReadOnlyList<UsageWindow> windows)
        {
            var choices = windows
                .OrderBy(window => window.Kind)
                .ThenBy(window => window.Label, StringComparer.OrdinalIgnoreCase)
                .Select(window => (DockChoice.ValueFor(window), DockChoice.TitleFor(window)))
                .DistinctBy(choice => choice.Item1)
                .ToArray();
            lock (_gate)
            {
                _windows = choices;
            }
        }

        public override Dictionary<string, object> ToDictionary()
        {
            List<(string Value, string Title)> options;
            lock (_gate)
            {
                options = [.. DockChoice.Fixed, .. _windows];
            }

            var value = string.IsNullOrWhiteSpace(Value) ? DockChoice.Auto : Value;
            if (options.All(option => option.Value != value))
            {
                options.Add((value, DockChoice.TitleForValue(value) is { } title ? $"{title} (not reported now)" : value));
            }

            var input = new Dictionary<string, object>
            {
                ["type"] = "Input.ChoiceSet",
                ["id"] = Key,
                ["label"] = Label,
                ["style"] = "compact",
                ["value"] = value,
                ["choices"] = options
                    .Select(option => new Dictionary<string, object> { ["title"] = option.Title, ["value"] = option.Value })
                    .ToList(),
            };
            return Indent(input, null);
        }
    }
}
