# Content Guard for Jellyfin

Per-user content blocking by **tag, genre and rating**, built to fix the gaps in Jellyfin's own
"Block items with tags" parental control.

## Why the built-in option misses things

Jellyfin (10.11) checks blocked tags against an "inherited tags" list saved in its database at
metadata-refresh time. Episodes don't store their parents' tags at all, and a tag you add to a
series later doesn't reach its seasons and episodes until they're refreshed. So episodes leak into
Next Up, Latest and Continue Watching, and newly tagged items stay visible.

## What Content Guard does differently

| | Built-in | Content Guard |
|---|---|---|
| Episodes/seasons inherit series tags | Only after refresh, unreliable | Always, computed live |
| Takes effect after tagging | After metadata refresh | About 5 seconds, no rescan |
| Matching | Exact | Case-insensitive, `*` wildcards (`Horror*`) |
| Block by genre | No | Yes |
| Block by rating (e.g. `TV-MA`), with episodes inheriting the series rating | Max-rating only, episodes are often unrated | Yes, plus an optional "hide unrated" setting |
| Allow-only mode | Allowed tags | Allowed tags with inheritance |
| Reusable rule sets | Per user, one at a time | Profiles assigned to many users |
| Direct links / playback of a hidden item | Varies by endpoint | 404 on details, streams, PlaybackInfo, seasons and episodes |
| "Why is this hidden?" | Nothing | Tester and per-profile preview in the dashboard |

## How it works

A global API filter runs on every request from a user who has an active profile:

1. **Direct access.** Any route that targets a hidden item (`itemId`, `seriesId`, `mediaSourceId`, etc.)
   returns 404. That covers details pages, PlaybackInfo, video/audio streams and HLS, and the season and episode lists.
2. **Browsing.** Hidden item ids are passed into the `/Items` query's `excludeItemIds`, so library grids
   page and count correctly. A background index is rebuilt automatically when the library or settings change.
3. **Everything else.** Hidden items are removed from every item list the API returns: home rows,
   Latest, Next Up, Resume, search hints, collections, similar items, and so on.

Because enforcement happens on the server, it applies the same way on every client
(web, Android/Fire TV, iOS, Roku, Kodi, and your HallFlix TV app).

## Install (manual)

1. Build it: push this repo to GitHub. The included Actions workflow produces `content-guard.zip`
   (download it from the run's **Artifacts**, or push a tag like `v1.0.0` to get a Release).
2. Unzip `Jellyfin.Plugin.ContentGuard.dll` into a new folder in Jellyfin's plugins directory:
   - Unraid Docker: `/config/plugins/ContentGuard_1.0.0.0/` (on the host this is usually
     `/mnt/user/appdata/jellyfin/plugins/ContentGuard_1.0.0.0/`)
3. Restart Jellyfin. **Dashboard → Plugins → Content Guard** (it's also in the dashboard sidebar).

## Use

1. **Add profile**, then name it (for example "Kids").
2. Tick the users it applies to.
3. Add blocked tags, genres or ratings. Press Enter after each one. `*` is a wildcard.
4. **Save**, then **Preview hidden items** to check the result.
5. Use **Test as a user** to search a title and see whether it's visible, and if not, which rule hides it.

Tip: tag the **series**, not each episode. Episodes and seasons pick up the series tag automatically.

You can leave Jellyfin's own "Block items with tags" settings in place or clear them. The two don't conflict.

## Compatibility

Targets **Jellyfin 10.11.x** (`net9.0`). For a 10.10.x server, change `TargetFramework` to `net8.0`
and the package versions to `10.10.*` in the `.csproj`.

## Notes / limits

- Admins are only restricted if you tick them in a profile.
- Image URLs are normally requested without login, so a poster can still load if someone has its
  exact URL. Hidden items never show up in lists for it to be found.
- Child counts on a visible parent (for example "12 episodes") can include hidden children.
