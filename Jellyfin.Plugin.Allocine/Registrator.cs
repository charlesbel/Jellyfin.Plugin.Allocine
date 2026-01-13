using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

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

            serviceCollection.AddHostedService<TransformationService>();
        }
    }
}
