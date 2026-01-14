using System;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace Jellyfin.Plugin.Allocine
{
    /// <summary>
    /// Service to register the file transformation.
    /// </summary>
    public class TransformationService : IHostedService
    {
        private readonly ILogger<TransformationService> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="TransformationService"/> class.
        /// </summary>
        /// <param name="logger">The logger.</param>
        public TransformationService(ILogger<TransformationService> logger)
        {
            _logger = logger;
        }

        /// <inheritdoc />
        public Task StartAsync(CancellationToken cancellationToken)
        {
            RegisterTransformation();
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task StopAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        private void RegisterTransformation()
        {
            try
            {
                Assembly? fileTransformationAssembly = AssemblyLoadContext.All
                    .SelectMany(x => x.Assemblies)
                    .FirstOrDefault(x => x.FullName?.Contains(".FileTransformation", StringComparison.OrdinalIgnoreCase) ?? false);

                if (fileTransformationAssembly == null)
                {
                    _logger.LogWarning("[Allocine] File Transformation plugin not found. JS injection will not work.");
                    return;
                }

                Type? pluginInterfaceType = fileTransformationAssembly.GetType("Jellyfin.Plugin.FileTransformation.PluginInterface");

                if (pluginInterfaceType != null)
                {
                    var payload = JObject.FromObject(new
                    {
                        id = "4c9abdb3-ddf2-4de0-809d-2faed2aad847",
                        fileNamePattern = "^index\\.html$",
                        callbackAssembly = typeof(HtmlInjector).Assembly.FullName,
                        callbackClass = typeof(HtmlInjector).FullName,
                        callbackMethod = nameof(HtmlInjector.Inject)
                    });

                    pluginInterfaceType.GetMethod("RegisterTransformation")?.Invoke(null, new object?[] { payload });

                    _logger.LogInformation("[Allocine] Transformation registered successfully.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Allocine] Error registering transformation.");
            }
        }
    }
}
