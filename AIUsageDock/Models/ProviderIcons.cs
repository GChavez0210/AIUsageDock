using Microsoft.CommandPalette.Extensions.Toolkit;

namespace AIUsageDock.Models;

internal static class ProviderIcons
{
    /// <summary>
    /// Fixed-color assets render consistently in the Dock. A PNG is used when the
    /// official mark is a raster (Antigravity's gradient arch); otherwise the SVG.
    /// </summary>
    public static string FilledPath(IUsageProvider provider)
    {
        var png = $"Assets\\icons\\{provider.IconName}-filled.png";
        return File.Exists(Path.Combine(AppContext.BaseDirectory, png))
            ? png
            : $"Assets\\icons\\{provider.IconName}-filled.svg";
    }

    public static IconInfo Filled(IUsageProvider provider) => IconHelpers.FromRelativePath(FilledPath(provider));
}
