# Jellyfin MAL Sync

[![Build & Release](https://github.com/WhisperScript/Jellyfin-MAL-Sync/actions/workflows/build.yml/badge.svg)](https://github.com/WhisperScript/Jellyfin-MAL-Sync/actions/workflows/build.yml)
[![Latest Release](https://img.shields.io/github/v/release/WhisperScript/Jellyfin-MAL-Sync?label=latest)](https://github.com/WhisperScript/Jellyfin-MAL-Sync/releases/latest)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
![Jellyfin](https://img.shields.io/badge/Jellyfin-10.11%2B-00a4dc?logo=jellyfin)

A Jellyfin plugin that keeps your **MyAnimeList** watch progress in sync with what you've watched in Jellyfin — automatically, per user, with no external scripts needed.

---

## ✨ Features

- **Per-user MAL accounts** — each Jellyfin user links their own MAL account via OAuth 2.0 PKCE
- **Live sync log** — watch log lines stream in real time via Server-Sent Events as the sync runs
- **Flexible scheduling** — daily trigger at a set time *or* interval-based (every N minutes)
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
sudo mkdir -p "/var/lib/jellyfin/plugins/MAL Sync_1.0.0.0"
sudo cp Jellyfin.Plugin.MalSync.dll "/var/lib/jellyfin/plugins/MAL Sync_1.0.0.0/"
sudo systemctl restart jellyfin

# Docker (adjust the path to your volume mount)
sudo mkdir -p "/your/jellyfin/data/plugins/MAL Sync_1.0.0.0"
sudo cp Jellyfin.Plugin.MalSync.dll "/your/jellyfin/data/plugins/MAL Sync_1.0.0.0/"
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

1. Open Jellyfin → **Dashboard → Plugins → MAL Sync**
2. Enter your **MAL Client-ID** (from [myanimelist.net/apiconfig](https://myanimelist.net/apiconfig)) and save
   - When creating the MAL app, set the redirect URL to: `http://localhost`
3. Select which **library paths** contain your anime
4. Each user opens the plugin page and clicks **Connect MAL account**
5. A new tab opens — log in to MAL and allow access
6. Paste the redirect URL back into the dialog → done ✅

---

## ⚙️ Configuration reference

| Setting | Default | Description |
|---|---|---|
| MAL Client-ID | *(required)* | Your MAL API application client-id |
| Anime library paths | — | Library paths to treat as anime (selected via checkboxes) |
| Sync schedule | Daily 03:00 | Daily at a fixed time, or every N minutes |
| Min. title similarity | `0.60` | How strictly titles must match (0.0 – 1.0) |
| Never downgrade | `true` | Don't overwrite a higher MAL progress with a lower one |
| Mark Jellyfin from MAL | `false` | Also mark episodes played in Jellyfin if watched on MAL |
| Cache TTL | `30` days | How long MAL-ID lookups are cached locally |

---

## 🗂 Project structure

```
Jellyfin.Plugin.MalSync/
├── Api/
│   └── MalSyncController.cs     REST endpoints for the UI (incl. SSE stream)
├── Configuration/
│   └── PluginConfiguration.cs   Settings + per-user token store
├── Services/
│   ├── MalAuthService.cs        OAuth 2.0 PKCE + token refresh
│   └── MalSyncService.cs        Core sync logic
├── Tasks/
│   └── MalSyncTask.cs           Scheduled task (auto-sync)
├── Web/
│   └── configPage.html          Dashboard UI page
├── MalSyncPlugin.cs             Plugin entry-point
└── PluginServiceRegistrator.cs  DI registration
```

---

## 📄 License

[MIT](LICENSE) © WhisperScript
