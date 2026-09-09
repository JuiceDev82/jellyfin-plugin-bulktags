using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.BulkTags
{
    public sealed class BulkTagsAuditLog
    {
        private static readonly object FileLock = new();
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true
        };

        private readonly string _filePath;
        private readonly ILogger _logger;

        public BulkTagsAuditLog(ILogger logger)
        {
            _logger = logger;
            var assemblyLocation = typeof(BulkTagsPlugin).Assembly.Location;
            var pluginDirectory = Path.GetDirectoryName(assemblyLocation) ?? AppContext.BaseDirectory;
            _filePath = Path.Combine(pluginDirectory, "bulk-tags-audit-log.json");
        }

        public IReadOnlyList<BulkTagsAuditEntry> ReadEntries()
        {
            lock (FileLock)
            {
                return ReadEntriesInternal();
            }
        }

        public void Append(BulkTagsOperationResult operation)
        {
            lock (FileLock)
            {
                var entries = ReadEntriesInternal().ToList();
                entries.Insert(0, new BulkTagsAuditEntry
                {
                    TimestampUtc = DateTime.UtcNow.ToString("O"),
                    Action = operation.Action,
                    Tags = operation.Tags.ToArray(),
                    UpdatedItemCount = operation.ItemNames.Count,
                    ItemTypes = operation.ItemTypes.OrderBy(type => type, StringComparer.OrdinalIgnoreCase).ToArray(),
                    ItemNames = operation.ItemNames
                        .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                        .ToArray()
                });

                SaveEntries(entries.Take(100).ToList());
            }
        }

        public void Clear()
        {
            lock (FileLock)
            {
                SaveEntries([]);
            }
        }

        private List<BulkTagsAuditEntry> ReadEntriesInternal()
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

                return JsonSerializer.Deserialize<List<BulkTagsAuditEntry>>(json, JsonOptions) ?? [];
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read bulk tags audit log from {FilePath}", _filePath);
                return [];
            }
        }

        private void SaveEntries(List<BulkTagsAuditEntry> entries)
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
                _logger.LogWarning(ex, "Failed to write bulk tags audit log to {FilePath}", _filePath);
            }
        }
    }

    public sealed class BulkTagsAuditEntry
    {
        public string TimestampUtc { get; set; } = string.Empty;

        public string Action { get; set; } = string.Empty;

        public string[] Tags { get; set; } = [];

        public int UpdatedItemCount { get; set; }

        public string[] ItemTypes { get; set; } = [];

        public string[] ItemNames { get; set; } = [];
    }

    public sealed class BulkTagsOperationResult
    {
        public string Action { get; set; } = string.Empty;

        public List<string> Tags { get; set; } = [];

        public HashSet<string> ItemTypes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public List<string> ItemNames { get; set; } = [];
    }

    public sealed class BulkTagsAuditLogResponse
    {
        public List<BulkTagsAuditEntry> Entries { get; set; } = [];
    }
}
