using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Allocine
{
    /// <summary>
    /// Plugin configuration.
    /// </summary>
    public class PluginConfiguration : BasePluginConfiguration
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="PluginConfiguration"/> class.
        /// </summary>
        public PluginConfiguration()
        {
            WriteNativeAllocineIds = true;
        }

        /// <summary>
        /// Gets or sets a value indicating whether the plugin may write
        /// <c>ProviderIds["Allocine"]</c> after an exact IMDb or TMDb match.
        /// </summary>
        public bool WriteNativeAllocineIds { get; set; }
    }
}
