using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.BulkTags
{
    public class PluginConfiguration : BasePluginConfiguration
    {
        public bool EnableLogging { get; set; } = false;
        public int MaxBatchSize { get; set; } = 100;
    }
}