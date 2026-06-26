using Jellyfin.Plugin.FinTube.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.FinTube;

/// <summary>
/// Registers the plugin's services into Jellyfin's DI container.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<FinTubeDependencyManager>();
        serviceCollection.AddSingleton<FinTubeDownloadQueue>();
    }
}
