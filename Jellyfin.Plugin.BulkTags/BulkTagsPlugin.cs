using System;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.BulkTags
{
    public class BulkTagsPlugin : BasePlugin<PluginConfiguration>, IHasWebPages
    {
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger<BulkTagsPlugin> _logger;

        public BulkTagsPlugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer, ILibraryManager libraryManager, ILogger<BulkTagsPlugin> logger)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
            _libraryManager = libraryManager;
            _logger = logger;
        }

        public static BulkTagsPlugin? Instance { get; private set; }

        internal ILibraryManager LibraryManager => _libraryManager;

        public override string Name => "Bulk Tags";

        public override Guid Id => new Guid("3c5f8a9a-0f3e-4a6b-9b8b-2c1a7b1c4f10");

        public override string ConfigurationFileName => "configPage.html";

        public IEnumerable<PluginPageInfo> GetPages()
        {
            return
            [
                new PluginPageInfo
                {
                    Name = ConfigurationFileName,
                    EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html"
                },
                new PluginPageInfo
                {
                    Name = "bulkTags.html",
                    DisplayName = "Bulk Tags",
                    EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.bulkTags.html",
                    EnableInMainMenu = true,
                    MenuSection = "main",
                    MenuIcon = "label"
                }
            ];
        }

        public BulkTagsService GetBulkTagsService()
        {
            var serviceLogger = _logger as ILogger<BulkTagsService> ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<BulkTagsService>.Instance;
            return new BulkTagsService(_libraryManager, serviceLogger);
        }
    }
}
