using Jellyfin.Plugin.MalSync.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.MalSync;

/// <summary>Jellyfin plugin entry-point for MAL Sync.</summary>
public class MalSyncPlugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public MalSyncPlugin(IApplicationPaths appPaths, IXmlSerializer xmlSerializer)
        : base(appPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static MalSyncPlugin? Instance { get; private set; }

    public override string Name => "MAL Sync";
    public override Guid Id => Guid.Parse("4a7b2c3d-5e6f-7a8b-9c0d-1e2f3a4b5c6d");
    public override string Description => "Synchronises Jellyfin watch progress with MyAnimeList.";

    /// <summary>
    /// Registers the plugin's web pages. The shared runtime is registered as a
    /// page too — Jellyfin serves any registered resource from
    /// /web/ConfigurationPage?name=… and returns a JavaScript content type for
    /// .js resources, which is how both pages pull in one design system.
    /// </summary>
    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = "MalSyncShared",
            EmbeddedResourcePath = $"{GetType().Namespace}.Web.ms-shared.js",
        };

        // Injected into the Jellyfin web client (not a plugin page) to add an
        // "Open on MyAnimeList" link to anime detail pages. Served here so it
        // ships with the plugin instead of being pasted into an injector.
        yield return new PluginPageInfo
        {
            Name = "MalSyncItemButton",
            EmbeddedResourcePath = $"{GetType().Namespace}.Web.ms-item-button.js",
        };

        yield return new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = $"{GetType().Namespace}.Web.configPage.html",
        };

        // User-facing account page — registered in the main sidebar via EnableInMainMenu.
        // Requires: Plugin Pages plugin (github.com/jellyfin/jellyfin-plugin-pluginpages)
        yield return new PluginPageInfo
        {
            Name = "MalSyncUser",
            DisplayName = "MAL Sync",
            EmbeddedResourcePath = $"{GetType().Namespace}.Web.userPage.html",
            EnableInMainMenu = true,
            MenuSection = "user",
            MenuIcon = "sync",
        };
    }
}
