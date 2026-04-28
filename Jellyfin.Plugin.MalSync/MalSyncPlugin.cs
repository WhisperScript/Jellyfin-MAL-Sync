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

    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = $"{GetType().Namespace}.Web.configPage.html",
        };
    }
}
