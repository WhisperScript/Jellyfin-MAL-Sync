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
- **MAL → Jellyfin watched sync** (optional)

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

### 1) Admin setup (once)

Open **Dashboard → Plugins → MAL Sync** and configure:

- **MAL Client ID**
   - MAL app redirect URL must be `http://localhost`
- **Anime paths**
- Global sync defaults (never-downgrade, MAL→Jellyfin watched)
- **Jellyseerr URL + API key** (if using imports)

### 2) Per-user setup

Each user opens **MAL Sync** page and:

1. Connects MAL account
2. Optionally sets personal sync overrides
3. Creates **Import Profiles** (MAL statuses + season mode)
4. Runs manual import/sync as needed

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

### Global (admin)

| Setting | Description |
|---|---|
| MAL Client-ID | MAL API application client-id |
| Anime library paths | Paths treated as anime |
| Min. title similarity | Matching threshold (0.0–1.0) |
| Never downgrade | Prevent MAL progress rollback |
| Mark Jellyfin from MAL | Mark Jellyfin watched based on MAL |
| Cache TTL | MAL-ID cache lifetime |
| Jellyseerr URL / API key | Required for MAL → Jellyseerr imports |

### Per-user

| Setting | Description |
|---|---|
| MAL account connection | User OAuth tokens |
| Never downgrade override | User-level sync behavior override |
| Mark Jellyfin from MAL override | User-level MAL→Jellyfin override |
| Import Profiles | Status filters + request season mode |

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
├── Services/
│   ├── MalAuthService.cs
│   ├── MalSyncService.cs
│   └── JellyseerrImportService.cs
├── Tasks/
│   ├── MalSyncTask.cs
│   └── JellyseerrImportTask.cs
├── Web/
│   ├── configPage.html
│   └── userPage.html
├── MalSyncPlugin.cs
└── PluginServiceRegistrator.cs
```

---

## 📄 License

[MIT](LICENSE) © WhisperScript
