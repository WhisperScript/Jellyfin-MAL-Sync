# Jellyfin MAL Sync

[![Build & Release](https://github.com/WhisperScript/Jellyfin-MAL-Sync/actions/workflows/build.yml/badge.svg)](https://github.com/WhisperScript/Jellyfin-MAL-Sync/actions/workflows/build.yml)
[![Latest Release](https://img.shields.io/github/v/release/WhisperScript/Jellyfin-MAL-Sync?label=latest)](https://github.com/WhisperScript/Jellyfin-MAL-Sync/releases/latest)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
![Jellyfin](https://img.shields.io/badge/Jellyfin-10.11%2B-00a4dc?logo=jellyfin)

Syncs Jellyfin anime watch progress with **MyAnimeList** per user and optionally imports MAL entries into **Jellyseerr** as user-specific requests.

---

## ✨ Features

- **Per-user MAL accounts** (OAuth 2.0 PKCE)
- **Per-user sync settings** (override global defaults)
- **Manual sync + dry-run + debug log streaming**
- **Scheduled sync task** for all authenticated users
- **MAL → Jellyseerr import** with:
   - per-user import profiles (status-based)
   - request-as-user behavior
   - duplicate prevention for pending/approved/declined requests
   - overlap guard (manual + cron import overlap is skipped safely)
- **Automatic season detection** (title parsing, TMDB season-name matching, MAL prequel fallback)
- **Match confidence you can see** — every season shows whether it was matched
  automatically, chosen by you, never checked, unmatched, or needs splitting
- **MAL → Jellyfin watched sync** (optional)
- **Push notifications** via Discord, Slack, Gotify, or any generic webhook:
   - Stale episode ranges, sync errors, sync summary
   - Import errors, import summary
- **Guided setup** — a checklist shows what is still missing, and sections that
  aren't usable yet explain why instead of failing when clicked
- **Plain result after every run** (updated / skipped / errors / duration), with the
  raw log kept out of the way unless something went wrong
- **Admin diagnostics** — verify the MAL client ID and Jellyseerr connection, see
  scheduled task health, and find users whose imports would be skipped
- **Links to MyAnimeList** everywhere an anime appears — the library list, the match
  dialog, search results and import previews — plus an optional button on Jellyfin's
  own item pages

---

## ✅ Requirements

- Jellyfin **10.11+**
- A MAL API app Client ID from <https://myanimelist.net/apiconfig>
- Jellyseerr (optional, only for MAL → Jellyseerr import)

### Important for Jellyseerr imports

For user-specific requests to work correctly:

1. The Jellyfin user must also exist in Jellyseerr.
2. The Jellyfin/Jellyseerr account mapping must be valid in Jellyseerr.
3. The user should have logged into Jellyseerr at least once.

If not, imports for that user are skipped with an error instead of falling back to another account.

---

## 🚀 Installation

### Via Jellyfin repository (recommended)

1. Open **Dashboard → Plugins → Repositories**
2. Add:
    ```
    https://raw.githubusercontent.com/WhisperScript/Jellyfin-MAL-Sync/main/manifest.json
    ```
3. Open **Catalog** → install **MAL Sync**
4. Restart Jellyfin

### Manual

Download latest release and copy `Jellyfin.Plugin.MalSync.dll` to your plugin version folder.

```bash
# Linux (example path)
sudo mkdir -p "/var/lib/jellyfin/plugins/MAL Sync_<version>"
sudo cp Jellyfin.Plugin.MalSync.dll "/var/lib/jellyfin/plugins/MAL Sync_<version>/"
sudo systemctl restart jellyfin

# Docker (adjust mounted data path)
sudo mkdir -p "/your/jellyfin/data/plugins/MAL Sync_<version>"
sudo cp Jellyfin.Plugin.MalSync.dll "/your/jellyfin/data/plugins/MAL Sync_<version>/"
sudo docker restart jellyfin
```

> Tip: Keep `<version>` aligned with the release folder name in your Jellyfin plugin directory.

---

## 🔧 Setup

### 1) Administrator, once

Open **Dashboard → Plugins → MAL Sync**. This page holds server-wide settings only —
your own account and manual runs live on the MAL Sync page in the sidebar.

**Settings tab**

- **MyAnimeList client ID** — create an app at <https://myanimelist.net/apiconfig>
  with **App Type** `other` and **App Redirect URL** exactly `http://localhost`
- **Anime libraries** — tick the folders that hold anime; nothing syncs until one is selected
- **Jellyseerr** — address + API key, only needed if users should request anime from
  their MAL list. Left empty, the whole Requests feature stays hidden
- **Matching and defaults** — match strictness, match cache lifetime, and the two
  sync behaviours new users start with

**Diagnostics tab**

- **Run checks** verifies the client ID against MyAnimeList and the Jellyseerr
  address + API key, and reports the Jellyseerr version
- Lists any user who has MAL connected and request rules but **no matching
  Jellyseerr account** — those imports are skipped, and this is the fastest way to spot it
- Shows both scheduled tasks with their last run, outcome and any error

### 2) Each user

Open **MAL Sync** in the sidebar. A checklist at the top shows what is still missing
and disappears once everything is done.

1. **Connect** the MyAnimeList account — a MAL tab opens, approve access, then paste
   the address you land on back into the page (the broken-looking page is expected)
2. **Sync** — press *Sync now*, or *Test run* first to see what would change without
   touching MyAnimeList
3. **Requests** *(only when the admin configured Jellyseerr)* — add a rule mapping MAL
   statuses to what should be requested, then *Preview* before requesting for real
4. **Library** — see how each season was matched on MyAnimeList and correct anything wrong
5. **Settings** — personal sync behaviour and optional webhook notifications

Sections that cannot be used yet say why rather than failing when clicked.

---

## ⏱ Scheduled tasks

Use Jellyfin **Dashboard → Scheduled Tasks**:

- **Sync watch progress to MyAnimeList**
   - Syncs Jellyfin → MAL for authenticated users
- **Import MAL list to Jellyseerr**
   - Runs MAL → Jellyseerr imports for users with profiles
   - Default trigger: every 12 hours

---

## ⚙️ Configuration reference

### Server settings (Dashboard → Plugins → MAL Sync)

Administrators only — these endpoints reject non-admin users.

| Setting | Description |
|---|---|
| MyAnimeList client ID | Client ID of the shared MAL application |
| Anime libraries | Library folders treated as anime |
| Jellyseerr address / API key | Enables MAL → Jellyseerr requests; empty hides the feature |
| Match strictness | 0.0–1.0. Higher = titles must look more alike to count as a match |
| Re-check matches after | Days a resolved MAL match is reused before being looked up again |
| Protect MyAnimeList progress | Default: never lower someone's episode count on MAL |
| Also mark episodes watched in Jellyfin | Default: use each MAL list to mark Jellyfin episodes played |

### Personal settings (MAL Sync page)

| Setting | Description |
|---|---|
| MyAnimeList account | Per-user OAuth connection |
| Protect MyAnimeList progress | Overrides the server default for this user |
| Also mark episodes watched in Jellyfin | Overrides the server default for this user |
| Request rules | MAL statuses → what gets requested in Jellyseerr, and how much of a series |
| Excluded titles | MAL entries that are never requested, even when a rule matches |
| Match corrections | Manually chosen MAL entry, excluded seasons, and season splits |
| Notifications | Webhook address plus per-event toggles for sync and requests |

---

## 🎯 How matching works

Each Jellyfin season is paired with one MyAnimeList entry, in this order:

1. **Your correction** — an entry you picked yourself always wins
2. **A MyAnimeList ID on the item** — set by a metadata provider or by hand under *Edit metadata*
3. **Your MyAnimeList list** — a title you already track is preferred over a stranger
4. **A title search on MyAnimeList**

The search does not go on titles alone. Candidates are also weighed by **episode
count**, **media type** and **year**, which is what keeps openings, specials, films and
remakes from winning a match they only look right for. Titles are compared with
punctuation removed, and a title also counts as matching when it is the leading part of
a longer one — MyAnimeList likes long subtitles.

### The states you will see

| State | Meaning |
|---|---|
| **Auto** | Matched automatically, or taken from a MyAnimeList ID on the item |
| **Yours** | You picked this entry — never overridden |
| **Not checked** | No sync has looked at this season yet |
| **No match** | A sync searched and found nothing close enough |
| **Needs split** | Matched, but the season holds far more episodes than that entry |
| **Split** | Mapped to several entries by episode range |
| **Excluded** | You told MAL Sync to skip this season |

Each season also shows how many episodes are on each side. `8 of 12 episodes` simply
means four are not in your library yet — that is never treated as a problem. `24
episodes here · 12 on MAL` is the shape that earns a **Needs split**.

If a match is wrong, press **Change**. The dialog asks how the season should be matched —
**one MyAnimeList entry**, or **several split by episode** — and shows only what that
choice needs. The two are alternatives: a split replaces a single entry rather than
sitting alongside it, which is also how the sync treats it. Corrections are per user and
permanent.

### One Jellyfin season, several MyAnimeList entries

Long-running shows are often one season in Jellyfin but several entries on MyAnimeList —
*Ascendance of a Bookworm* is the usual example. Synced against a single entry, progress
silently stops at the end of the first part.

MAL Sync notices this during a sync: when a season holds far more episodes than its
matched entry, it works the split out immediately and the warning arrives with the parts
already listed — for example *Ep 1–12 Part 1*, *Ep 13–24 Part 2* — behind a single
**Apply this split** button. **Adjust first** opens the season with the proposal filled
in, so nothing has to be detected a second time.

Parts are found by following the sequel chain on MyAnimeList, and where the relations do
not connect them, by looking the later parts up by name. That second route is what shows
like *The Case Study of Vanitas* need, since Part 2 is not reachable through the relation
graph. Recaps, specials and spin-offs are deliberately left out.

If the parts cannot be identified at all, the season still gets flagged and you can map
the ranges by hand under **More options**.

---

## 🔗 Opening an anime on MyAnimeList

Inside the plugin, every place an anime appears links to its MyAnimeList page: the
**Library** list, the match dialog (current match *and* every search result), import
previews, excluded titles and season splits. Links open in a new tab.

### On Jellyfin's own item pages

Two ways, which complement each other:

**1. Native link (no setup).** Series, season and episode pages get a **MyAnimeList**
link next to IMDb and TMDB. It uses the MyAnimeList ID stored on the item when there is
one — the field is editable under **Edit metadata**, and an ID entered there is treated
as authoritative by the sync — and otherwise the match MAL Sync worked out itself.

An item page is shared while matches are per user, so the link only appears where every
user agrees on the entry. If two people matched the same season differently, no link is
shown rather than one person's answer. Each user always sees their own on the MAL Sync
page.

**2. Per-user button (needs a JS injector).** Option 1 stays silent where users disagree,
and cannot know which season of a series you are looking at from a series page. For a
link that always reflects *your* match, inject this three-line loader with the
[JavaScript Injector](https://github.com/johnpc/jellyfin-plugin-javascript-injector)
plugin — set the entry to **require authentication**:

```js
var s = document.createElement('script');
s.src = '/web/ConfigurationPage?name=MalSyncItemButton';
document.head.appendChild(s);
```

The script itself ships with the plugin, so it updates along with it. It asks the
plugin what the item on screen maps to *for the signed-in user* and adds a
**MyAnimeList** link to the detail page — one per season where a series spans several
MAL entries. Series with no match get nothing added.

---

## 🧭 Sidebar page note

The user-facing MAL Sync page is registered as a plugin page. Depending on your setup, you may still need the Plugin Pages ecosystem to expose/customize sidebar entries.

---

## 🛠 Troubleshooting

### Jellyseerr requests are created as the wrong user

Make sure:

- the Jellyfin user also exists in Jellyseerr
- the account mapping in Jellyseerr is correct
- the user has logged into Jellyseerr at least once

If the plugin cannot resolve the matching Jellyseerr user, imports for that user are skipped.

### A title is skipped as "not aired yet"

Jellyseerr cannot request a season TMDB has no episodes for. Requesting one anyway
returns success while creating nothing, so those entries are skipped until they exist.
They stay on your MyAnimeList list and are picked up by a later run once the season airs.

### Import is skipped with an existing-request message

This is expected when the title already has a Jellyseerr request entry, including:

- pending
- approved
- declined

The plugin intentionally avoids re-requesting the same item to prevent spam from manual runs or cron overlap.

### Import is skipped because another import is already running

Manual import and scheduled import are protected against overlap per user.

If you see an overlap skip message, wait a few seconds and run it again.

### A user cannot create user-specific Jellyseerr requests

Check all of the following:

- Jellyseerr URL and API key are configured in admin settings
- the Jellyfin user exists in Jellyseerr
- the user has at least one import profile configured
- the user has a valid MAL connection

### Sidebar entry is missing

Depending on your Jellyfin setup, plugin pages may not automatically appear where expected.

Check:

- whether the plugin page is available from plugin settings/admin view
- whether your setup uses Plugin Pages or a custom sidebar plugin
- whether the user has permission to access plugin pages

### MAL authentication does not complete

Confirm that your MAL application is configured with:

- Client ID set in plugin settings
- redirect URL set to `http://localhost`

After approving in MAL, paste the full redirect URL back into the plugin page.

### Jellyseerr import finds titles but requests still do not appear in Sonarr

Possible causes:

- the request is still pending approval in Jellyseerr
- the Jellyseerr user does not have the expected permissions
- Jellyseerr/Sonarr routing rules or profiles are misconfigured
- the requested season already exists in Sonarr and is therefore skipped

---

## 🗂 Project structure

```
Jellyfin.Plugin.MalSync/
├── Api/
│   └── MalSyncController.cs
├── Configuration/
│   └── PluginConfiguration.cs
├── Providers/
│   └── MalExternalId.cs  # registers MyAnimeList as an external ID + link
├── Services/
│   ├── MalAuthService.cs
│   ├── MalSyncService.cs
│   └── JellyseerrImportService.cs
├── Tasks/
│   ├── MalSyncTask.cs
│   └── JellyseerrImportTask.cs
├── Web/
│   ├── ms-shared.js      # design system + API/streaming helpers, shared by both pages
│   ├── ms-item-button.js # optional "Open on MyAnimeList" button for Jellyfin item pages
│   ├── configPage.html   # admin: server settings + diagnostics
│   └── userPage.html     # user: setup, sync, requests, library, settings
├── MalSyncPlugin.cs
└── PluginServiceRegistrator.cs
```

---

## 📄 License

[MIT](LICENSE) © WhisperScript
