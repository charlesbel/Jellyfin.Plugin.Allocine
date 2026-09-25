using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Allocine
{
    /// <summary>
    /// Register plugin services.
    /// </summary>
    public class Registrator : IPluginServiceRegistrator
    {
        /// <summary>
        /// Registers the services.
        /// </summary>
        /// <param name="serviceCollection">The service collection.</param>
        /// <param name="applicationHost">The application host.</param>
        public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
        {
            serviceCollection.AddSingleton<AllocineService>();
            serviceCollection.AddSingleton<IAllocineRatingProvider>(static services => services.GetRequiredService<AllocineService>());
            serviceCollection.AddSingleton<IAllocineMappingProvider>(static services => services.GetRequiredService<AllocineService>());
            serviceCollection.AddSingleton<AllocineRatingStore>();
            serviceCollection.AddSingleton<AllocineRatingCacheService>();
            serviceCollection.AddSingleton<AllocineMetadataProvider>();
            serviceCollection.AddTransient<AllocineRefreshTask>();
            serviceCollection.AddSingleton<IStartupFilter, ScriptInjectionStartupFilter>();
        }
    }
}
