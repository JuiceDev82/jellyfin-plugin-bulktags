using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.BulkTags
{
    public class BulkTagsService
    {
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger<BulkTagsService> _logger;

        public BulkTagsService(ILibraryManager libraryManager, ILogger<BulkTagsService> logger)
        {
            _libraryManager = libraryManager;
            _logger = logger;
        }

        public async Task<BulkTagsOperationResult> AddTagsToItemsAsync(IEnumerable<string> itemIds, IEnumerable<string> tagsToAdd)
        {
            var requestedIds = itemIds?.ToList() ?? [];
            var result = CreateOperationResult("add", requestedIds, tagsToAdd);

            if (requestedIds.Count == 0)
            {
                _logger.LogWarning("No item IDs provided for bulk tagging");
                return result;
            }

            if (tagsToAdd == null || !tagsToAdd.Any())
            {
                _logger.LogWarning("No tags provided for bulk tagging");
                return result;
            }

            var tagList = NormalizeTags(tagsToAdd);
            if (tagList.Count == 0)
            {
                _logger.LogWarning("No valid tags provided for bulk tagging after normalization");
                return result;
            }

            result.Tags = tagList;
            _logger.LogInformation("Adding tags {Tags} to {Count} items", string.Join(", ", tagList), requestedIds.Count);

            foreach (var itemId in requestedIds)
            {
                try
                {
                    if (!Guid.TryParse(itemId, out var parsedItemId))
                    {
                        _logger.LogWarning("Item ID {ItemId} is not a valid GUID", itemId);
                        continue;
                    }

                    var item = _libraryManager.GetItemById(parsedItemId);
                    if (item == null)
                    {
                        _logger.LogWarning("Item with ID {ItemId} not found", itemId);
                        continue;
                    }

                    // Get existing tags
                    var existingTags = item.Tags?.ToList() ?? new List<string>();

                    // Add new tags if they don't already exist
                    var tagsAdded = new List<string>();
                    foreach (var tag in tagList)
                    {
                        if (!existingTags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                        {
                            existingTags.Add(tag);
                            tagsAdded.Add(tag);
                        }
                    }

                    if (tagsAdded.Any())
                    {
                        item.Tags = existingTags.ToArray();
                        await _libraryManager.UpdateItemAsync(item, item.FindParent<Folder>(), ItemUpdateType.MetadataEdit, CancellationToken.None);
                        AddUpdatedItem(result, item);
                        _logger.LogInformation("Added tags {Tags} to item '{ItemName}'", string.Join(", ", tagsAdded), item.Name);
                    }
                    else
                    {
                        _logger.LogInformation("Item '{ItemName}' already has all specified tags", item.Name);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error adding tags to item {ItemId}", itemId);
                }
            }

            _logger.LogInformation("Bulk tagging operation completed");
            return result;
        }

        public async Task<BulkTagsOperationResult> RemoveTagsFromItemsAsync(IEnumerable<string> itemIds, IEnumerable<string> tagsToRemove)
        {
            var requestedIds = itemIds?.ToList() ?? [];
            var result = CreateOperationResult("remove", requestedIds, tagsToRemove);

            if (requestedIds.Count == 0)
            {
                _logger.LogWarning("No item IDs provided for bulk tag removal");
                return result;
            }

            if (tagsToRemove == null || !tagsToRemove.Any())
            {
                _logger.LogWarning("No tags provided for bulk tag removal");
                return result;
            }

            var tagList = NormalizeTags(tagsToRemove);
            if (tagList.Count == 0)
            {
                _logger.LogWarning("No valid tags provided for bulk tag removal after normalization");
                return result;
            }

            result.Tags = tagList;
            _logger.LogInformation("Removing tags {Tags} from {Count} items", string.Join(", ", tagList), requestedIds.Count);

            foreach (var itemId in requestedIds)
            {
                try
                {
                    if (!Guid.TryParse(itemId, out var parsedItemId))
                    {
                        _logger.LogWarning("Item ID {ItemId} is not a valid GUID", itemId);
                        continue;
                    }

                    var item = _libraryManager.GetItemById(parsedItemId);
                    if (item == null)
                    {
                        _logger.LogWarning("Item with ID {ItemId} not found", itemId);
                        continue;
                    }

                    // Get existing tags
                    var existingTags = item.Tags?.ToList() ?? new List<string>();

                    // Remove specified tags
                    var tagsRemoved = new List<string>();
                    foreach (var tag in tagList)
                    {
                        if (existingTags.RemoveAll(t => t.Equals(tag, StringComparison.OrdinalIgnoreCase)) > 0)
                        {
                            tagsRemoved.Add(tag);
                        }
                    }

                    if (tagsRemoved.Any())
                    {
                        item.Tags = existingTags.ToArray();
                        await _libraryManager.UpdateItemAsync(item, item.FindParent<Folder>(), ItemUpdateType.MetadataEdit, CancellationToken.None);
                        AddUpdatedItem(result, item);
                        _logger.LogInformation("Removed tags {Tags} from item '{ItemName}'", string.Join(", ", tagsRemoved), item.Name);
                    }
                    else
                    {
                        _logger.LogInformation("Item '{ItemName}' did not have any of the specified tags to remove", item.Name);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error removing tags from item {ItemId}", itemId);
                }
            }

            _logger.LogInformation("Bulk tag removal operation completed");
            return result;
        }

        public async Task<BulkTagsOperationResult> SetMetadataLockAsync(IEnumerable<string> itemIds, bool isLocked)
        {
            var requestedIds = itemIds?.ToList() ?? [];
            var result = CreateOperationResult(isLocked ? "lock-metadata" : "unlock-metadata", requestedIds, []);

            if (requestedIds.Count == 0)
            {
                _logger.LogWarning("No item IDs provided for metadata lock update");
                return result;
            }

            _logger.LogInformation(
                "{Action} metadata for {Count} items",
                isLocked ? "Locking" : "Unlocking",
                requestedIds.Count);

            foreach (var itemId in requestedIds)
            {
                try
                {
                    if (!Guid.TryParse(itemId, out var parsedItemId))
                    {
                        _logger.LogWarning("Item ID {ItemId} is not a valid GUID", itemId);
                        continue;
                    }

                    var item = _libraryManager.GetItemById(parsedItemId);
                    if (item == null)
                    {
                        _logger.LogWarning("Item with ID {ItemId} not found", itemId);
                        continue;
                    }

                    var updateOutcome = TrySetMetadataLockState(item, isLocked);
                    if (!updateOutcome.FoundProperty)
                    {
                        _logger.LogWarning("Item '{ItemName}' does not expose a writable metadata lock property", item.Name);
                        continue;
                    }

                    if (!updateOutcome.Changed)
                    {
                        continue;
                    }

                    await _libraryManager.UpdateItemAsync(item, item.FindParent<Folder>(), ItemUpdateType.MetadataEdit, CancellationToken.None);
                    AddUpdatedItem(result, item);
                    _logger.LogInformation(
                        "{Action} metadata for item '{ItemName}'",
                        isLocked ? "Locked" : "Unlocked",
                        item.Name);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error updating metadata lock for item {ItemId}", itemId);
                }
            }

            _logger.LogInformation("Bulk metadata lock operation completed");
            return result;
        }

        private static BulkTagsOperationResult CreateOperationResult(string action, List<string> requestedIds, IEnumerable<string> tags)
        {
            return new BulkTagsOperationResult
            {
                Action = action,
                Tags = NormalizeTags(tags ?? []),
                ItemNames = [],
                ItemTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            };
        }

        private static void AddUpdatedItem(BulkTagsOperationResult result, BaseItem item)
        {
            var itemName = item.Name;
            if (!string.IsNullOrWhiteSpace(itemName))
            {
                result.ItemNames.Add(itemName);
            }

            var itemType = item.GetType().Name;
            if (!string.IsNullOrWhiteSpace(itemType))
            {
                result.ItemTypes.Add(itemType);
            }
        }

        private static List<string> NormalizeTags(IEnumerable<string> tags)
        {
            return tags
                .Select(tag => (tag ?? string.Empty).Trim().ToLowerInvariant())
                .Where(tag => !string.IsNullOrWhiteSpace(tag))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static MetadataLockUpdateOutcome TrySetMetadataLockState(BaseItem item, bool isLocked)
        {
            var outcome = new MetadataLockUpdateOutcome();
            var itemType = item.GetType();

            foreach (var propertyName in new[] { "IsLocked", "IsMetadataLocked" })
            {
                var property = itemType.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
                if (property?.PropertyType != typeof(bool) || !property.CanRead || !property.CanWrite)
                {
                    continue;
                }

                outcome.FoundProperty = true;
                var currentValue = (bool)(property.GetValue(item) ?? false);
                if (currentValue == isLocked)
                {
                    continue;
                }

                property.SetValue(item, isLocked);
                outcome.Changed = true;
            }

            return outcome;
        }

        private sealed class MetadataLockUpdateOutcome
        {
            public bool FoundProperty { get; set; }

            public bool Changed { get; set; }
        }
    }
}
