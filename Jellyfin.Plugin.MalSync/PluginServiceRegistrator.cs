using Jellyfin.Plugin.MalSync.Services;
using Jellyfin.Plugin.MalSync.Tasks;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.MalSync;

/// <summary>
/// Registers all plugin services into the Jellyfin DI container.
/// </summary>
public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddHttpClient("MalSync")
            .ConfigureHttpClient(c =>
            {
                c.Timeout = TimeSpan.FromSeconds(30);
                c.DefaultRequestHeaders.Add("User-Agent", "JellyfinMalSync/1.0");
            });

        serviceCollection.AddSingleton<MalAuthService>();
        serviceCollection.AddSingleton<MalSyncService>();
        serviceCollection.AddSingleton<JellyseerrImportService>();
        serviceCollection.AddTransient<IScheduledTask, Tasks.JellyseerrImportTask>();
    }
}
