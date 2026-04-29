# Jellyfin MAL Sync

[![Build & Release](https://github.com/WhisperScript/Jellyfin-MAL-Sync/actions/workflows/build.yml/badge.svg)](https://github.com/WhisperScript/Jellyfin-MAL-Sync/actions/workflows/build.yml)
[![Latest Release](https://img.shields.io/github/v/release/WhisperScript/Jellyfin-MAL-Sync?label=latest)](https://github.com/WhisperScript/Jellyfin-MAL-Sync/releases/latest)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
![Jellyfin](https://img.shields.io/badge/Jellyfin-10.11%2B-00a4dc?logo=jellyfin)

A Jellyfin plugin that keeps your **MyAnimeList** watch progress in sync with what you've watched in Jellyfin — automatically, per user, with no external scripts needed.

---

## ✨ Features

- **Per-user MAL accounts** — each Jellyfin user links their own MAL account via OAuth 2.0 PKCE
- **User sync page** — dedicated per-user page accessible via sidebar (requires [Plugin Pages](#optional-plugin-pages-sidebar-access))
- **Per-user setting overrides** — each user can override global sync settings (never-downgrade, MAL→Jellyfin) for their own account
- **Live sync log** — watch log lines stream in real time via Server-Sent Events as the sync runs
- **Flexible scheduling** — configured via Jellyfin's built-in Scheduled Tasks (supports daily, interval, and more)
- **Manual sync** from the Jellyfin dashboard at any time
- **Dry-run / preview mode** — see exactly what would change before writing anything to MAL
- **Debug mode** — verbose log showing every series evaluated, MAL ID resolution steps, and sequel chain traversal
- **Sequel chain resolution** — automatically walks the MAL sequel chain to find the correct MAL entry for Season 2, 3, etc.
- **MAL → Jellyfin sync** — optionally mark episodes as played in Jellyfin if already watched on MAL
- **Title similarity matching** — fuzzy matching with a configurable threshold
- **Never-downgrade guard** — won't overwrite a higher MAL progress with a lower count
- **KefinTweaks-compatible UI** — styled with Jellyfin CSS variables, works with any theme

---

## 🚀 Installation

### Via Jellyfin Plugin Repository (recommended)

1. In Jellyfin, go to **Dashboard → Plugins → Repositories**
2. Click **+ New Repository** and add:
   ```
   https://raw.githubusercontent.com/WhisperScript/Jellyfin-MAL-Sync/main/manifest.json
   ```
3. Go to **Catalog**, find **MAL Sync**, and click **Install**
4. Restart Jellyfin

### Manual download

1. Go to the [Releases](https://github.com/WhisperScript/Jellyfin-MAL-Sync/releases/latest) page and download `Jellyfin.Plugin.MalSync_10.11.0.zip`
2. Extract the DLL and copy it into your Jellyfin plugin folder:

```bash
# Standard Linux install
sudo mkdir -p "/var/lib/jellyfin/plugins/MAL Sync_1.1.1"
sudo cp Jellyfin.Plugin.MalSync.dll "/var/lib/jellyfin/plugins/MAL Sync_1.1.1/"
sudo systemctl restart jellyfin

# Docker (adjust the path to your volume mount)
sudo mkdir -p "/your/jellyfin/data/plugins/MAL Sync_1.1.1"
sudo cp Jellyfin.Plugin.MalSync.dll "/your/jellyfin/data/plugins/MAL Sync_1.1.1/"
sudo docker restart jellyfin
```

> **Requires:** Jellyfin 10.11+

### Build from source

**Requirements:** [.NET 9 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/9.0)

```bash
git clone https://github.com/WhisperScript/Jellyfin-MAL-Sync.git
cd Jellyfin-MAL-Sync

dotnet publish Jellyfin.Plugin.MalSync/Jellyfin.Plugin.MalSync.csproj \
  -c Release --output dist --no-self-contained \
  -p:DebugType=none -p:DebugSymbols=false
```

The built DLL will be at `dist/Jellyfin.Plugin.MalSync.dll`.

---

## 🔧 First-time setup

### Admin (one-time)
1. Open Jellyfin → **Dashboard → Plugins → MAL Sync**
2. Enter your **MAL Client-ID** (from [myanimelist.net/apiconfig](https://myanimelist.net/apiconfig)) and save
   - When creating the MAL app, set the redirect URL to: `http://localhost`
3. Select which **library paths** contain your anime
4. Configure global sync defaults (never-downgrade, MAL→Jellyfin mark)

### Each user
5. Open the **MAL Sync** page in the sidebar (under *Plugin Settings* — see [Plugin Pages](#optional-plugin-pages-sidebar-access) below)
   - Admins can also access it from **Dashboard → Plugins → MAL Sync**
6. Click **Connect MAL account**
7. A new tab opens — log in to MAL and allow access
8. Paste the redirect URL back into the dialog → done ✅
9. Optionally override the global sync settings in **Personal Sync Settings**

### Optional: Plugin Pages sidebar access

To show a **MAL Sync** entry in the sidebar for all users, install the [Plugin Pages](https://github.com/jellyfin/jellyfin-plugin-pluginpages) plugin (available in the official Jellyfin catalog), then add the following entry to its configuration (`Dashboard → Plugins → Plugin Pages`):

| Field | Value |
|---|---|
| URL | `configurationpage?name=MalSyncUser` |
| Display text | `MAL Sync` |
| Icon | `sync` |

---

## ⚙️ Configuration reference

### Global settings (admin, Dashboard → Plugins → MAL Sync)

| Setting | Default | Description |
|---|---|---|
| MAL Client-ID | *(required)* | Your MAL API application client-id |
| Anime library paths | — | Library paths to treat as anime (selected via checkboxes) |
| Min. title similarity | `0.60` | How strictly titles must match (0.0 – 1.0) |
| Never downgrade | `true` | Don't overwrite a higher MAL progress with a lower one |
| Mark Jellyfin from MAL | `false` | Also mark episodes played in Jellyfin if watched on MAL |
| Cache TTL | `30` days | How long MAL-ID lookups are cached locally |

### Per-user overrides (user page → Personal Sync Settings)

Each user can override the two sync-behaviour settings for their own account. Leaving a setting unchecked/un-toggled falls back to the global default set by the admin.

| Setting | Description |
|---|---|
| Never downgrade | Personal override: don't let MAL progress go backwards for this account |
| Mark Jellyfin from MAL | Personal override: mark episodes as played in Jellyfin based on this user's MAL list |

---

## 🗂 Project structure

```
Jellyfin.Plugin.MalSync/
├── Api/
│   └── MalSyncController.cs     REST endpoints for the UI (incl. SSE stream, per-user config)
├── Configuration/
│   └── PluginConfiguration.cs   Global settings + per-user token & override store
├── Services/
│   ├── MalAuthService.cs        OAuth 2.0 PKCE + token refresh
│   └── MalSyncService.cs        Core sync logic (respects per-user overrides)
├── Tasks/
│   └── MalSyncTask.cs           Scheduled task (auto-sync)
├── Web/
│   ├── configPage.html          Admin dashboard page (global settings + MAL account)
│   └── userPage.html            User-facing page (MAL account + personal sync settings)
├── MalSyncPlugin.cs             Plugin entry-point
└── PluginServiceRegistrator.cs  DI registration
```

---

## 📄 License

[MIT](LICENSE) © WhisperScript
