using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.BulkTags
{
    [ApiController]
    [Route("BulkTags")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public class BulkTagsController : ControllerBase
    {
        private static readonly object SearchCacheLock = new();
        private static readonly Dictionary<string, List<BulkTagsSearchItem>> SearchCacheByKey = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, DateTime> SearchCacheUpdatedUtcByKey = new(StringComparer.OrdinalIgnoreCase);
        private static readonly TimeSpan SearchCacheLifetime = TimeSpan.FromMinutes(10);
        private static DateTime CollectionLookupCacheUpdatedUtc = DateTime.MinValue;
        private static Dictionary<string, string[]> CollectionLookupCache = new(StringComparer.OrdinalIgnoreCase);

        private readonly BulkTagsService _bulkTagsService;
        private readonly BulkTagsAuditLog _auditLog;
        private readonly BulkTagsSearchHistory _searchHistory;
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger<BulkTagsController> _logger;

        public BulkTagsController(ILogger<BulkTagsService> logger, ILogger<BulkTagsController> controllerLogger)
        {
            var plugin = BulkTagsPlugin.Instance ?? throw new InvalidOperationException("Bulk Tags plugin is not initialized.");
            _libraryManager = plugin.LibraryManager;
            _bulkTagsService = new BulkTagsService(plugin.LibraryManager, logger);
            _logger = controllerLogger;
            _auditLog = new BulkTagsAuditLog(controllerLogger);
            _searchHistory = new BulkTagsSearchHistory(controllerLogger);
        }

        [HttpGet("Search")]
        public IActionResult Search(
            [FromQuery] string? searchTerm1,
            [FromQuery] string? searchTerm2,
            [FromQuery] string? searchOperator,
            [FromQuery] string? withoutTag,
            [FromQuery] bool includeCollections,
            [FromQuery] string? includeTypes,
            [FromQuery] string? includeFields,
            [FromQuery] int limit = 100)
        {
            var firstTerm = NormalizeTerm(searchTerm1);
            var secondTerm = NormalizeTerm(searchTerm2);
            var excludedTag = NormalizeTerm(withoutTag);
            var hasSearchTerms = firstTerm is not null || secondTerm is not null;
            var cappedLimit = hasSearchTerms ? Math.Clamp(limit, 1, 250) : 25;
            var allowedTypes = ParseIncludedTypes(includeTypes);
            var allowedFields = hasSearchTerms
                ? ParseIncludedFields(includeFields)
                : new HashSet<string>(SupportedFields, StringComparer.OrdinalIgnoreCase);
            var normalizedOperator = NormalizeSearchOperator(searchOperator);

            try
            {
                var searchTimer = Stopwatch.StartNew();
                List<BulkTagsSearchItem> items;
                if (hasSearchTerms)
                {
                    var itemsById = new Dictionary<string, BulkTagsSearchItem>(StringComparer.OrdinalIgnoreCase);

                    foreach (var candidate in GetFastMatches(firstTerm, secondTerm, normalizedOperator, excludedTag, allowedTypes, allowedFields, cappedLimit * 3))
                    {
                        itemsById[candidate.Id] = candidate;
                    }

                    foreach (var candidate in GetCachedMatches(firstTerm, secondTerm, normalizedOperator, excludedTag, allowedTypes, allowedFields))
                    {
                        itemsById[candidate.Id] = candidate;
                    }

                    items = itemsById.Values.ToList();
                }
                else
                {
                    items = GetMostRecentItems(allowedTypes, excludedTag, cappedLimit);
                }

                if (includeCollections)
                {
                    AttachCollections(items);
                }

                var movies = items
                    .Where(item => string.Equals(item.Type, "Movie", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                var series = items
                    .Where(item => string.Equals(item.Type, "Series", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                var episodes = items
                    .Where(item => string.Equals(item.Type, "Episode", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                var suggestedTags = movies
                    .Concat(series)
                    .Concat(episodes)
                    .SelectMany(item => item.Tags)
                    .Where(tag => !string.IsNullOrWhiteSpace(tag))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(tag => tag)
                    .Take(20)
                    .ToArray();

                _logger.LogInformation(
                    "Bulk tag search completed in {ElapsedMs}ms for types {Types}. Search terms present: {HasSearchTerms}. Results: {ResultCount}",
                    searchTimer.ElapsedMilliseconds,
                    string.Join(",", allowedTypes.OrderBy(type => type)),
                    hasSearchTerms,
                    items.Count);

                if (hasSearchTerms || !string.IsNullOrWhiteSpace(excludedTag))
                {
                    _searchHistory.Append(CreateSearchHistoryEntry(
                        firstTerm,
                        secondTerm,
                        normalizedOperator,
                        excludedTag,
                        allowedTypes,
                        allowedFields,
                        hasSearchTerms));
                }

                return Ok(new BulkTagsSearchResponse
                {
                    Movies = movies,
                    Series = series,
                    Episodes = episodes,
                    SuggestedTags = suggestedTags,
                    StatusMessage = hasSearchTerms
                        ? items.Count + " result(s) found in the selected categories."
                        : "Showing the 25 most recent items in the selected categories."
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Bulk tag search failed for terms {FirstTerm} {SearchOperator} {SecondTerm}",
                    firstTerm ?? string.Empty,
                    normalizedOperator,
                    secondTerm ?? string.Empty);
                return Ok(new BulkTagsSearchResponse
                {
                    ErrorMessage = "Search failed on the server. Check Jellyfin logs for details."
                });
            }
        }

        [HttpPost("AddTags")]
        public async Task<IActionResult> AddTags([FromBody] BulkTagsRequest request)
        {
            if (request is null || request.ItemIds.Count == 0 || request.Tags.Count == 0)
            {
                return BadRequest("ItemIds and Tags are required.");
            }

            var result = await _bulkTagsService.AddTagsToItemsAsync(request.ItemIds, request.Tags).ConfigureAwait(false);
            _auditLog.Append(result);
            if (request.RefreshSearchResults)
            {
                InvalidateSearchCache();
            }

            return Ok(new { Message = "Tags added successfully." });
        }

        [HttpPost("RemoveTags")]
        public async Task<IActionResult> RemoveTags([FromBody] BulkTagsRequest request)
        {
            if (request is null || request.ItemIds.Count == 0 || request.Tags.Count == 0)
            {
                return BadRequest("ItemIds and Tags are required.");
            }

            var result = await _bulkTagsService.RemoveTagsFromItemsAsync(request.ItemIds, request.Tags).ConfigureAwait(false);
            _auditLog.Append(result);
            if (request.RefreshSearchResults)
            {
                InvalidateSearchCache();
            }

            return Ok(new { Message = "Tags removed successfully." });
        }

        [HttpPost("SetMetadataLock")]
        public async Task<IActionResult> SetMetadataLock([FromBody] BulkTagsMetadataLockRequest request)
        {
            if (request is null)
            {
                return BadRequest("A request body is required.");
            }

            List<string> itemIds;
            if (request.ItemIds.Count > 0)
            {
                itemIds = request.ItemIds
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            else
            {
                if (request.IncludeTypes.Count == 0)
                {
                    return BadRequest("IncludeTypes is required when no ItemIds are provided.");
                }

                var allowedTypes = request.IncludeTypes
                    .Where(type => SupportedTypes.Contains(type))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                if (allowedTypes.Count == 0)
                {
                    return BadRequest("At least one valid media type is required.");
                }

                itemIds = allowedTypes
                    .SelectMany(GetCandidateItemIdsForType)
                    .Select(id => id.ToString())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            var result = await _bulkTagsService.SetMetadataLockAsync(itemIds, request.IsLocked).ConfigureAwait(false);
            _auditLog.Append(result);

            return Ok(new
            {
                Message = request.IsLocked
                    ? $"Metadata locked for {result.ItemNames.Count} item(s)."
                    : $"Metadata unlocked for {result.ItemNames.Count} item(s)."
            });
        }

        [HttpGet("AuditLog")]
        public IActionResult GetAuditLog()
        {
            return Ok(new BulkTagsAuditLogResponse
            {
                Entries = _auditLog.ReadEntries().ToList()
            });
        }

        [HttpPost("AuditLog/Clear")]
        public IActionResult ClearAuditLog()
        {
            _auditLog.Clear();
            return Ok(new { Message = "Audit log cleared successfully." });
        }

        [HttpGet("SearchHistory")]
        public IActionResult GetSearchHistory()
        {
            return Ok(new BulkTagsSearchHistoryResponse
            {
                Entries = _searchHistory.ReadEntries().ToList()
            });
        }

        [HttpPost("SearchHistory/Clear")]
        public IActionResult ClearSearchHistory()
        {
            _searchHistory.Clear();
            return Ok(new { Message = "Search history cleared successfully." });
        }

        private static bool MatchesSearch(BulkTagsSearchItem item, string? firstTerm, string? secondTerm, string searchOperator, HashSet<string> allowedFields)
        {
            var firstMatches = MatchesAnyField(item, firstTerm, allowedFields);
            var secondMatches = MatchesAnyField(item, secondTerm, allowedFields);

            if (firstTerm is not null && secondTerm is not null)
            {
                return string.Equals(searchOperator, "AND", StringComparison.OrdinalIgnoreCase)
                    ? firstMatches && secondMatches
                    : firstMatches || secondMatches;
            }

            return firstTerm is not null ? firstMatches : secondMatches;
        }

        private static bool MatchesAnyField(BulkTagsSearchItem item, string? term, HashSet<string> allowedFields)
        {
            if (string.IsNullOrWhiteSpace(term))
            {
                return false;
            }

            if (allowedFields.Contains("Title") && (Contains(item.Name, term) || Contains(item.SeriesName, term)))
            {
                return true;
            }

            if (allowedFields.Contains("Overview") && Contains(item.Overview, term))
            {
                return true;
            }

            if (allowedFields.Contains("Tags"))
            {
                return (item.Tags ?? []).Any(tag => Contains(tag, term));
            }

            return false;
        }

        private static bool Contains(string? source, string term)
        {
            return !string.IsNullOrWhiteSpace(source)
                && source.Contains(term, StringComparison.OrdinalIgnoreCase);
        }

        private static string? NormalizeTerm(string? value)
        {
            var trimmed = (value ?? string.Empty).Trim();
            return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
        }

        private static string NormalizeSearchOperator(string? value)
        {
            return string.Equals(value, "OR", StringComparison.OrdinalIgnoreCase) ? "OR" : "AND";
        }

        private static bool HasExcludedTag(BulkTagsSearchItem item, string? excludedTag)
        {
            if (string.IsNullOrWhiteSpace(excludedTag))
            {
                return false;
            }

            return (item.Tags ?? []).Any(tag => string.Equals(tag, excludedTag, StringComparison.OrdinalIgnoreCase));
        }

        private IEnumerable<BulkTagsSearchItem> GetFastMatches(string? firstTerm, string? secondTerm, string searchOperator, string? excludedTag, HashSet<string> allowedTypes, HashSet<string> allowedFields, int limit)
        {
            var results = new List<BulkTagsSearchItem>();
            var seedTerm = firstTerm ?? secondTerm;

            if (!allowedFields.Contains("Title") || string.IsNullOrWhiteSpace(seedTerm))
            {
                return results;
            }

            try
            {
                var query = new InternalItemsQuery();
                SetQueryProperty(query, "SearchTerm", seedTerm);
                SetQueryProperty(query, "Limit", limit);
                SetQueryProperty(query, "Recursive", true);
                SetQueryProperty(query, "IncludeItemTypes", allowedTypes.ToArray());

                foreach (var itemId in _libraryManager.GetItemIds(query))
                {
                    try
                    {
                        var item = _libraryManager.GetItemById(itemId);
                        var candidate = item is null ? null : ToSearchItem(item);
                        if (candidate is null)
                        {
                            continue;
                        }

                        if (MatchesSearch(candidate, firstTerm, secondTerm, searchOperator, allowedFields)
                            && !HasExcludedTag(candidate, excludedTag))
                        {
                            results.Add(candidate);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Skipping fast search item {ItemId}", itemId);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Fast Jellyfin search path failed for term {SearchTerm}", seedTerm);
            }

            return results;
        }

        private IEnumerable<BulkTagsSearchItem> GetCachedMatches(string? firstTerm, string? secondTerm, string searchOperator, string? excludedTag, HashSet<string> allowedTypes, HashSet<string> allowedFields)
        {
            return GetOrBuildSearchCache(allowedTypes)
                .Where(item => allowedTypes.Contains(item.Type))
                .Where(item => MatchesSearch(item, firstTerm, secondTerm, searchOperator, allowedFields))
                .Where(item => !HasExcludedTag(item, excludedTag));
        }

        private List<BulkTagsSearchItem> GetMostRecentItems(HashSet<string> allowedTypes, string? excludedTag, int limit)
        {
            var directItems = GetMostRecentItemsDirect(allowedTypes, excludedTag, limit);
            if (directItems.Count > 0)
            {
                return directItems;
            }

            return GetOrBuildSearchCache(allowedTypes)
                .Where(item => allowedTypes.Contains(item.Type))
                .Where(item => !HasExcludedTag(item, excludedTag))
                .OrderByDescending(item => item.DateAddedTicks)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .Take(limit)
                .ToList();
        }

        private List<BulkTagsSearchItem> GetMostRecentItemsDirect(HashSet<string> allowedTypes, string? excludedTag, int limit)
        {
            var timer = Stopwatch.StartNew();
            var results = new List<BulkTagsSearchItem>();

            foreach (var type in allowedTypes.OrderBy(type => type, StringComparer.OrdinalIgnoreCase))
            {
                var itemIds = GetItemIdsForType(type).ToArray();
                if (itemIds.Length == 0)
                {
                    _logger.LogWarning("Direct recent-items query returned no ids for type {ItemType}", type);
                    return [];
                }

                foreach (var itemId in itemIds)
                {
                    try
                    {
                        var item = _libraryManager.GetItemById(itemId);
                        if (item is null || !string.Equals(item.GetType().Name, type, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var candidate = ToSearchItem(item);
                        if (candidate is not null && !HasExcludedTag(candidate, excludedTag))
                        {
                            results.Add(candidate);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Skipping direct recent {ItemType} item {ItemId}", type, itemId);
                    }
                }
            }

            _logger.LogInformation(
                "Direct recent-items query completed in {ElapsedMs}ms for types {Types} and returned {Count} candidate items",
                timer.ElapsedMilliseconds,
                string.Join(",", allowedTypes.OrderBy(type => type)),
                results.Count);

            return results
                .OrderByDescending(item => item.DateAddedTicks)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .Take(limit)
                .ToList();
        }

        private List<BulkTagsSearchItem> GetOrBuildSearchCache(HashSet<string>? preferredTypes = null)
        {
            lock (SearchCacheLock)
            {
                var requestedTypes = preferredTypes is { Count: > 0 }
                    ? new HashSet<string>(preferredTypes, StringComparer.OrdinalIgnoreCase)
                    : new HashSet<string>(SupportedTypes, StringComparer.OrdinalIgnoreCase);

                foreach (var type in requestedTypes)
                {
                    EnsureTypeCache(type);
                }

                return requestedTypes
                    .Select(GetCacheKey)
                    .Where(cacheKey => SearchCacheByKey.ContainsKey(cacheKey))
                    .SelectMany(cacheKey => SearchCacheByKey[cacheKey])
                    .ToList();
            }
        }

        private void EnsureTypeCache(string type)
        {
            var timer = Stopwatch.StartNew();
            var cacheKey = GetCacheKey(type);
            if (SearchCacheByKey.TryGetValue(cacheKey, out var cachedItems)
                && SearchCacheUpdatedUtcByKey.TryGetValue(cacheKey, out var updatedUtc)
                && DateTime.UtcNow - updatedUtc <= SearchCacheLifetime
                && cachedItems.Count > 0)
            {
                _logger.LogInformation("Reusing {ItemType} cache with {Count} items in {ElapsedMs}ms", type, cachedItems.Count, timer.ElapsedMilliseconds);
                return;
            }

            var rebuilt = new List<BulkTagsSearchItem>();
            var candidateItemIds = GetCandidateItemIdsForType(type).ToArray();
            foreach (var itemId in candidateItemIds)
            {
                try
                {
                    var item = _libraryManager.GetItemById(itemId);
                    if (item is null || !string.Equals(item.GetType().Name, type, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var candidate = ToSearchItem(item);
                    if (candidate is not null)
                    {
                        rebuilt.Add(candidate);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Skipping cached {ItemType} item {ItemId}", type, itemId);
                }
            }

            SearchCacheByKey[cacheKey] = rebuilt;
            SearchCacheUpdatedUtcByKey[cacheKey] = DateTime.UtcNow;
            _logger.LogInformation(
                "Built {ItemType} cache with {Count} items from {CandidateCount} candidate ids in {ElapsedMs}ms",
                type,
                rebuilt.Count,
                candidateItemIds.Length,
                timer.ElapsedMilliseconds);
        }

        private void AttachCollections(IEnumerable<BulkTagsSearchItem> items)
        {
            lock (SearchCacheLock)
            {
                EnsureCollectionLookupCacheLifetime();

                var targetItems = items.ToList();
                var uncachedIds = targetItems
                    .Select(item => item.Id)
                    .Where(id => !CollectionLookupCache.ContainsKey(id))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                if (uncachedIds.Count > 0)
                {
                    PopulateCollectionLookupCache(uncachedIds);
                }

                foreach (var item in targetItems)
                {
                    item.Collections = CollectionLookupCache.TryGetValue(item.Id, out var collections)
                        ? collections
                        : [];
                }
            }
        }

        private static void EnsureCollectionLookupCacheLifetime()
        {
            if (DateTime.UtcNow - CollectionLookupCacheUpdatedUtc <= SearchCacheLifetime)
            {
                return;
            }

            CollectionLookupCache = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            CollectionLookupCacheUpdatedUtc = DateTime.UtcNow;
        }

        private IEnumerable<Guid> GetCandidateItemIdsForType(string type)
        {
            var queriedIds = GetItemIdsForType(type).ToArray();
            if (queriedIds.Length > 0)
            {
                return queriedIds;
            }

            _logger.LogWarning("Falling back to full library scan for type cache {ItemType}", type);
            return _libraryManager.GetItemIds(new InternalItemsQuery());
        }

        private IEnumerable<Guid> GetCandidateCollectionIds()
        {
            var queriedIds = GetItemIdsForType("BoxSet").ToArray();
            if (queriedIds.Length > 0)
            {
                return queriedIds;
            }

            _logger.LogWarning("Falling back to full library scan for collection membership cache");
            return _libraryManager.GetItemIds(new InternalItemsQuery());
        }

        private IEnumerable<Guid> GetItemIdsForType(string type)
        {
            var timer = Stopwatch.StartNew();
            try
            {
                var query = new InternalItemsQuery();
                SetQueryProperty(query, "Recursive", true);
                SetQueryProperty(query, "IncludeItemTypes", new[] { type });
                var itemIds = _libraryManager.GetItemIds(query).ToArray();
                _logger.LogInformation(
                    "Type-specific item id query for {ItemType} returned {Count} ids in {ElapsedMs}ms",
                    type,
                    itemIds.Length,
                    timer.ElapsedMilliseconds);
                return itemIds;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Type-specific item id query failed for {ItemType}", type);
                return [];
            }
        }

        private void PopulateCollectionLookupCache(HashSet<string> targetIds)
        {
            if (targetIds.Count == 0)
            {
                return;
            }

            var membership = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var itemId in GetCandidateCollectionIds())
            {
                try
                {
                    var item = _libraryManager.GetItemById(itemId);
                    if (item is null || !string.Equals(item.GetType().Name, "BoxSet", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var collectionName = item.Name;
                    if (string.IsNullOrWhiteSpace(collectionName))
                    {
                        continue;
                    }

                    foreach (var memberId in GetCollectionMemberIds(item).Where(targetIds.Contains))
                    {
                        if (!membership.TryGetValue(memberId, out var collections))
                        {
                            collections = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            membership[memberId] = collections;
                        }

                        collections.Add(collectionName);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Skipping collection membership scan for collection item {ItemId}", itemId);
                }
            }

            foreach (var targetId in targetIds)
            {
                CollectionLookupCache[targetId] = membership.TryGetValue(targetId, out var collections)
                    ? collections.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray()
                    : [];
            }

            CollectionLookupCacheUpdatedUtc = DateTime.UtcNow;
        }

        private static void InvalidateSearchCache()
        {
            lock (SearchCacheLock)
            {
                SearchCacheByKey.Clear();
                SearchCacheUpdatedUtcByKey.Clear();
                CollectionLookupCache = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
                CollectionLookupCacheUpdatedUtc = DateTime.MinValue;
            }
        }

        private static string GetCacheKey(string type)
        {
            return type;
        }

        private static void SetQueryProperty(object query, string propertyName, object value)
        {
            var property = query.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
            if (property is null || !property.CanWrite)
            {
                return;
            }

            var convertedValue = ConvertQueryValue(property.PropertyType, value);
            if (convertedValue is null && property.PropertyType.IsValueType)
            {
                return;
            }

            property.SetValue(query, convertedValue);
        }

        private static object? ConvertQueryValue(Type targetType, object value)
        {
            if (targetType.IsInstanceOfType(value))
            {
                return value;
            }

            if (targetType.IsArray && value is IEnumerable<string> stringValues)
            {
                var elementType = targetType.GetElementType();
                if (elementType is not null && elementType.IsEnum)
                {
                    var convertedItems = stringValues
                        .Select(text => TryParseEnum(elementType, text))
                        .Where(parsed => parsed is not null)
                        .ToArray();

                    var array = Array.CreateInstance(elementType, convertedItems.Length);
                    for (var index = 0; index < convertedItems.Length; index++)
                    {
                        array.SetValue(convertedItems[index], index);
                    }

                    return array;
                }
            }

            if (targetType.IsEnum && value is string enumText)
            {
                return TryParseEnum(targetType, enumText);
            }

            return value;
        }

        private static object? TryParseEnum(Type enumType, string value)
        {
            if (Enum.TryParse(enumType, value, true, out var parsed))
            {
                return parsed;
            }

            return null;
        }

        private static IEnumerable<string> GetCollectionMemberIds(BaseItem collectionItem)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var collectionType = collectionItem.GetType();
            var yieldedAny = false;

            foreach (var propertyName in new[] { "LinkedChildren", "ItemLinkedChildren", "Children" })
            {
                var property = collectionType.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
                if (property?.GetValue(collectionItem) is System.Collections.IEnumerable values)
                {
                    foreach (var id in ExtractIds(values, seen))
                    {
                        yieldedAny = true;
                        yield return id;
                    }
                }
            }

            if (yieldedAny)
            {
                yield break;
            }

            foreach (var methodName in new[] { "GetLinkedChildren", "GetItemList", "GetRecursiveChildren" })
            {
                var method = collectionType.GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance, []);
                if (method?.Invoke(collectionItem, null) is System.Collections.IEnumerable values)
                {
                    foreach (var id in ExtractIds(values, seen))
                    {
                        yield return id;
                    }
                }
            }
        }

        private static IEnumerable<string> ExtractIds(System.Collections.IEnumerable values, HashSet<string> seen)
        {
            foreach (var value in values)
            {
                var id = value switch
                {
                    Guid guid => guid.ToString(),
                    string text => text,
                    _ => value?.GetType().GetProperty("Id", BindingFlags.Public | BindingFlags.Instance)?.GetValue(value)?.ToString()
                };

                if (!string.IsNullOrWhiteSpace(id) && seen.Add(id))
                {
                    yield return id;
                }
            }
        }

        private static BulkTagsSearchItem? ToSearchItem(BaseItem item)
        {
            var name = item.Name;
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            var isEpisode = string.Equals(item.GetType().Name, "Episode", StringComparison.OrdinalIgnoreCase);
            var seriesName = isEpisode
                ? item.GetType().GetProperty("SeriesName", BindingFlags.Public | BindingFlags.Instance)?.GetValue(item) as string ?? string.Empty
                : string.Empty;
            var seasonNumber = isEpisode ? GetNullableInt(item, "ParentIndexNumber") : null;
            var episodeNumber = isEpisode ? GetNullableInt(item, "IndexNumber") : null;
            var dateAddedUtc = GetDateAddedUtc(item);

            return new BulkTagsSearchItem
            {
                Id = item.Id.ToString(),
                Name = name,
                Type = item.GetType().Name,
                SeriesName = seriesName,
                SeasonNumber = seasonNumber,
                EpisodeNumber = episodeNumber,
                DateAddedUtc = dateAddedUtc.ToString("O"),
                DateAddedTicks = dateAddedUtc.Ticks,
                Overview = item.Overview ?? string.Empty,
                Tags = item.Tags?.OrderBy(tag => tag).ToArray() ?? [],
                Collections = []
            };
        }

        private static int? GetNullableInt(BaseItem item, string propertyName)
        {
            var value = item.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance)?.GetValue(item);
            return value switch
            {
                int number => number,
                long number => (int)number,
                short number => number,
                byte number => number,
                _ => null
            };
        }

        private static DateTime GetDateAddedUtc(BaseItem item)
        {
            var itemType = item.GetType();
            var dateValue = itemType.GetProperty("DateCreated", BindingFlags.Public | BindingFlags.Instance)?.GetValue(item)
                ?? itemType.GetProperty("DateAdded", BindingFlags.Public | BindingFlags.Instance)?.GetValue(item);

            return dateValue switch
            {
                DateTime dateTime => dateTime.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dateTime, DateTimeKind.Utc) : dateTime.ToUniversalTime(),
                DateTimeOffset dateTimeOffset => dateTimeOffset.UtcDateTime,
                _ => DateTime.MinValue
            };
        }

        private static readonly HashSet<string> SupportedTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "Movie",
            "Series",
            "Episode"
        };

        private static readonly HashSet<string> SupportedFields = new(StringComparer.OrdinalIgnoreCase)
        {
            "Title",
            "Overview",
            "Tags"
        };

        private static HashSet<string> ParseIncludedTypes(string? includeTypes)
        {
            var parsed = (includeTypes ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(type => SupportedTypes.Contains(type))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return parsed.Count > 0 ? parsed : new HashSet<string>(SupportedTypes, StringComparer.OrdinalIgnoreCase);
        }

        private static HashSet<string> ParseIncludedFields(string? includeFields)
        {
            var parsed = (includeFields ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(field => SupportedFields.Contains(field))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return parsed.Count > 0 ? parsed : new HashSet<string>(SupportedFields, StringComparer.OrdinalIgnoreCase);
        }

        private static BulkTagsSearchHistoryEntry CreateSearchHistoryEntry(
            string? firstTerm,
            string? secondTerm,
            string searchOperator,
            string? excludedTag,
            HashSet<string> allowedTypes,
            HashSet<string> allowedFields,
            bool hasSearchTerms)
        {
            var includeTypes = allowedTypes
                .OrderBy(type => type, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var includeFields = hasSearchTerms
                ? allowedFields.OrderBy(field => field, StringComparer.OrdinalIgnoreCase).ToArray()
                : [];
            var normalizedFirst = firstTerm ?? string.Empty;
            var normalizedSecond = secondTerm ?? string.Empty;
            var normalizedWithoutTag = excludedTag ?? string.Empty;
            var normalizedOperator = NormalizeSearchOperator(searchOperator);

            return new BulkTagsSearchHistoryEntry
            {
                TimestampUtc = DateTime.UtcNow.ToString("O"),
                SearchTerm1 = normalizedFirst,
                SearchTerm2 = normalizedSecond,
                SearchOperator = normalizedOperator,
                WithoutTag = normalizedWithoutTag,
                IncludeTypes = includeTypes,
                IncludeFields = includeFields,
                HistoryKey = string.Join("|",
                    normalizedFirst,
                    normalizedOperator,
                    normalizedSecond,
                    normalizedWithoutTag,
                    string.Join(",", includeTypes),
                    string.Join(",", includeFields))
            };
        }
    }

    public class BulkTagsRequest
    {
        public List<string> ItemIds { get; set; } = [];

        public List<string> Tags { get; set; } = [];

        public bool RefreshSearchResults { get; set; } = true;
    }

    public class BulkTagsMetadataLockRequest
    {
        public List<string> ItemIds { get; set; } = [];

        public List<string> IncludeTypes { get; set; } = [];

        public bool IsLocked { get; set; }
    }

    public class BulkTagsSearchResponse
    {
        public List<BulkTagsSearchItem> Movies { get; set; } = [];

        public List<BulkTagsSearchItem> Series { get; set; } = [];

        public List<BulkTagsSearchItem> Episodes { get; set; } = [];

        public string[] SuggestedTags { get; set; } = [];

        public string StatusMessage { get; set; } = string.Empty;

        public string ErrorMessage { get; set; } = string.Empty;
    }

    public class BulkTagsSearchItem
    {
        public string Id { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string Type { get; set; } = string.Empty;

        public string SeriesName { get; set; } = string.Empty;

        public int? SeasonNumber { get; set; }

        public int? EpisodeNumber { get; set; }

        public string DateAddedUtc { get; set; } = string.Empty;

        public long DateAddedTicks { get; set; }

        public string Overview { get; set; } = string.Empty;

        public string[] Tags { get; set; } = [];

        public string[] Collections { get; set; } = [];
    }
}
