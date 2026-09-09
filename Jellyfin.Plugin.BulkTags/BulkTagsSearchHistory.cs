using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.BulkTags
{
    public sealed class BulkTagsSearchHistory
    {
        private static readonly object FileLock = new();
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true
        };

        private readonly string _filePath;
        private readonly ILogger _logger;

        public BulkTagsSearchHistory(ILogger logger)
        {
            _logger = logger;
            var assemblyLocation = typeof(BulkTagsPlugin).Assembly.Location;
            var pluginDirectory = Path.GetDirectoryName(assemblyLocation) ?? AppContext.BaseDirectory;
            _filePath = Path.Combine(pluginDirectory, "bulk-tags-search-history.json");
        }

        public IReadOnlyList<BulkTagsSearchHistoryEntry> ReadEntries()
        {
            lock (FileLock)
            {
                return ReadEntriesInternal();
            }
        }

        public void Append(BulkTagsSearchHistoryEntry entry)
        {
            lock (FileLock)
            {
                var entries = ReadEntriesInternal()
                    .Where(existing => !string.Equals(existing.HistoryKey, entry.HistoryKey, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                entries.Insert(0, entry);
                SaveEntries(entries.Take(10).ToList());
            }
        }

        public void Clear()
        {
            lock (FileLock)
            {
                SaveEntries([]);
            }
        }

        private List<BulkTagsSearchHistoryEntry> ReadEntriesInternal()
        {
            try
            {
                if (!File.Exists(_filePath))
                {
                    return [];
                }

                var json = File.ReadAllText(_filePath);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return [];
                }

                return JsonSerializer.Deserialize<List<BulkTagsSearchHistoryEntry>>(json, JsonOptions) ?? [];
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read bulk tags search history from {FilePath}", _filePath);
                return [];
            }
        }

        private void SaveEntries(List<BulkTagsSearchHistoryEntry> entries)
        {
            try
            {
                var directory = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var json = JsonSerializer.Serialize(entries, JsonOptions);
                File.WriteAllText(_filePath, json);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to write bulk tags search history to {FilePath}", _filePath);
            }
        }
    }

    public sealed class BulkTagsSearchHistoryEntry
    {
        public string TimestampUtc { get; set; } = string.Empty;

        public string SearchTerm1 { get; set; } = string.Empty;

        public string SearchOperator { get; set; } = "AND";

        public string SearchTerm2 { get; set; } = string.Empty;

        public string WithoutTag { get; set; } = string.Empty;

        public string[] IncludeTypes { get; set; } = [];

        public string[] IncludeFields { get; set; } = [];

        public string HistoryKey { get; set; } = string.Empty;
    }

    public sealed class BulkTagsSearchHistoryResponse
    {
        public List<BulkTagsSearchHistoryEntry> Entries { get; set; } = [];
    }
}
