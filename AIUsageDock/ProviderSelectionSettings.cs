using System.Text.Json;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace AIUsageDock;

internal sealed class ProviderSelectionSettings
{
    private readonly string _path;
    private readonly Settings _settings = new();

    public ProviderSelectionSettings(string path)
    {
        _path = path;
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

        _settings.SettingsChanged += (_, _) => PersistAndNotify();
    }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AIUsageDock", "settings.json");

    public Settings ToolkitSettings => _settings;

    public event EventHandler? Changed;

    public bool IsEnabled(string providerId) => _settings.GetSetting<bool>(providerId);

    private void PersistAndNotify()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, _settings.ToJson());
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
