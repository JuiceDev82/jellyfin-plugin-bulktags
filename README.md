# Bulk Tags

A Jellyfin server plugin for adding and removing tags across many library items at once,
instead of editing them one at a time.

> **Requires Jellyfin 12.0.** This plugin targets `net10.0` and is built against the
> Jellyfin 12 assemblies. It will not load on Jellyfin 10.11 or earlier.

## Features

- **Search** movies, series, episodes and collections by up to two terms combined with
  `AND` / `OR`, matching against Title, Overview, Tags and **In Collection**.
- **Find everything in a collection** — with *In Collection* enabled, searching
  `kids collection` returns the movies and series that belong to it, not just items whose
  own title happens to contain those words.
- **Exclude by tag** — hide anything that already carries a given tag, which makes it easy
  to work through a backlog.
- **Bulk add / remove tags** on any selection of results.
- **Bulk metadata lock / unlock**, either for a selection or for every item of a chosen type.
- **Collection membership** shown per item, so you can see what a title belongs to.
- **Audit log and search history**, each capped at the 100 most recent entries.

## Installation

Build the plugin, then copy the output into a subfolder of your Jellyfin `plugins` directory:

```bash
dotnet publish Jellyfin.Plugin.BulkTags -c Release
```

Copy `Jellyfin.Plugin.BulkTags.dll` and `manifest.json` into a new folder such as
`plugins/BulkTags/`, then restart the server. The `plugins` directory is typically:

| Platform | Path |
| --- | --- |
| Linux | `/var/lib/jellyfin/plugins/` |
| Windows | `%ProgramData%\Jellyfin\Server\plugins\` |
| Docker | `/config/plugins/` |

After restarting, **Bulk Tags** appears in the main menu of the web UI.

## Building

Requires the **.NET 10 SDK**. The Jellyfin assemblies come from nuget.org and restore
automatically:

```bash
dotnet build Jellyfin.Plugin.BulkTags.sln -c Release
```

They are referenced with `ExcludeAssets="runtime"`, so they are used for compilation only
and are never copied into the plugin output — the server supplies them at runtime.

## Permissions

Every `/BulkTags/*` endpoint is gated behind `[Authorize(Policy = Policies.RequiresElevation)]`,
so only Jellyfin administrators can call them. This matters: Jellyfin does **not** install a
global authorization filter, and its default policy applies only to endpoints explicitly marked
`[Authorize]`. Removing that attribute would silently expose these library-mutating endpoints
to anonymous callers.

The web UI authenticates with the standard `Authorization: MediaBrowser ...` header. The legacy
`X-Emby-Token` header is rejected by Jellyfin 12 by default, since
`ServerConfiguration.EnableLegacyAuthorization` now defaults to `false`.

## API

All endpoints require an administrator token.

| Method | Route | Purpose |
| --- | --- | --- |
| `GET` | `/BulkTags/Search` | Search items, or list the 25 most recent when no terms are given |
| `POST` | `/BulkTags/AddTags` | Add tags to the given item IDs |
| `POST` | `/BulkTags/RemoveTags` | Remove tags from the given item IDs |
| `POST` | `/BulkTags/SetMetadataLock` | Lock or unlock metadata |
| `GET` | `/BulkTags/AuditLog` | Read the audit log |
| `POST` | `/BulkTags/AuditLog/Clear` | Clear the audit log |
| `GET` | `/BulkTags/SearchHistory` | Read saved searches |
| `POST` | `/BulkTags/SearchHistory/Clear` | Clear saved searches |

Tags are normalized to trimmed lowercase before being written.

## Notes

- The audit log and search history are stored as JSON next to the plugin assembly
  (`bulk-tags-audit-log.json`, `bulk-tags-search-history.json`).
- Supported item types are `Movie`, `Series`, `Episode` and `BoxSet`. `BoxSet` is Jellyfin's
  internal name for a collection, and the web UI labels it **Collections**; a collection is
  tagged as an item in its own right, which is separate from the Collections column showing
  what a title belongs to.
- Search combines a fast indexed query with a per-type in-memory cache that is rebuilt every
  10 minutes, and is invalidated whenever tags are written with `RefreshSearchResults` set.
- Searching *In Collection* is served only by the cache path. The fast path seeds Jellyfin's
  own title search, which cannot find an item by the name of a collection it belongs to, so
  the first such search after a cache expiry pays for building the type cache.

## License

[GPL-3.0](LICENSE)
