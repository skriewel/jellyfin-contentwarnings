using Jellyfin.Plugin.ContentWarnings.Data;
using Jellyfin.Plugin.ContentWarnings.Injection;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.ContentWarnings;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<IStartupFilter, ScriptInjectionStartupFilter>();
        serviceCollection.AddSingleton<WarningStore>();
        serviceCollection.AddSingleton<DddClient>();
        serviceCollection.AddSingleton<WarningService>();
    }
}
