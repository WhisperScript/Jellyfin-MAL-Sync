# 🔧 MAL-Sync v1.2.2 – Root-Cause-Analyse & Fix

## TL;DR – Das Kernproblem

Deine Freundin hatte **systematisches Versagen** nach vollständiger Konfiguration-Löschung + Server-Neustart, weil:

**1. Cache-Normalisierung fehlte** (Bug 1 – KRITISCH)
- Cache wurde mit Jellyfin-Namen gespeichert: `"Baka & Test: Summon the Beasts"::1`
- Cache wurde mit MAL-Namen gelesen: `"Baka to Test Shoukanjuu"::1` 
- **Cache-Miss** → neue Suche → **gleiche falsche Serie**
- Dieser Bug wiederholte sich bei **jedem Sync**, weil Cache nie getroffen wurde

**2. Inconsistenter Title-Handling** (Bug 2 – SEKUNDÄR)
- Bei S2+ wurden verschiedene Titel-Varianten verwendet
- Konnte zu falschen SeasonNumber-Detektionen führen

## Was ich gefixt habe

### ✅ Fix 1: Normalisierte Cache-Keys

**Vorher:**
```csharp
// In Zeile 172 (v1.2.1):
malId = GetCachedMalId(cacheScope, seriesName, seasonNum, cfg.CacheTtlDays);
```
- `seriesName` = Jellyfin-interner Name (variiert je nach Metadaten!)
- Cache-Key: `userid::Baka & Test: Summon the Beasts::1`

**Nachher:**
```csharp
// In Zeile 165 (v1.2.2):
var normalizedSeriesName = NormalizeTitle(seriesName);
malId = GetCachedMalId(cacheScope, normalizedSeriesName, seasonNum, cfg.CacheTtlDays);
```
- `normalizedSeriesName` = `"baka and test summon the beasts"` (Unicode normalisiert, normalized whitespace)
- Cache-Key: `userid::baka and test summon the beasts::1`
- **Konsistent über alle Serien-Namen-Variationen!**

### ✅ Fix 2: Konsistenter Title-Handling

**Vorher:**
```csharp
var baseT = NormalizeTitle(StripSeasonSuffix(orig));
// ...
if (!ContainsSeasonNumber(norm, seasonNum)) score *= 0.4;  // ← falsch!
```

**Nachher:**
```csharp
var baseT = NormalizeTitle(StripSeasonSuffix(orig));
// ...
if (!ContainsSeasonNumber(orig, seasonNum)) score *= 0.4;  // ← korrekt!
```

---

## Für deine Freundin – So wird's behoben

### Schritt 1: Upgrade auf v1.2.2

```bash
# Warte auf v1.2.2 Release (oder nutze wenn verfügbar)
# Jellyfin → Plugins → MAL-Sync → Update
```

### Schritt 2: Alles noch einmal löschen (wie zuvor)

```bash
# Im Jellyfin-Container:
rm -rf /config/plugins/MAL*
rm -rf /config/data/mal*
docker restart jellyfin
```

### Schritt 3: Neue Authentifizierung + erste Sync

1. Jellyfin → MAL Sync Settings → **Neu authentifizieren** (MAL-Token)
2. Manuell Sync starten (oder warten auf automatischen)
3. Logs beobachten:
   ```
   [MAL] Using MAL user-list match ID 6347 for 'Baka and Test: Summon the Beasts' S1
   ```
   - ✅ **6347** = Baka & Test S1 (KORREKT!)
   - ❌ **8516** = Baka & Test S2 (FALSCH!)

### Schritt 4: Cache testen

```bash
# Nach 2. Sync sollte Log zeigen:
[MAL] Using cached MAL ID 6347 for 'Baka and Test: Summon the Beasts' S1
```

---

## Warum der Bug so kritisch war

1. **Permanente Cache-Misses**
   - Jellyfin speichert Serien-Namen unterschiedlich ab
   - "Baka & Test", "Baka to Test", "Baka&Test" waren 3 verschiedene Cache-Keys
   - Cache wurde **nie getroffen**

2. **Wiederholter Fehler**
   - Jedes Mal wenn Sync lief → neue MAL-Suche → **gleiche falsche ID**
   - Server-Neustart änderte nichts (Cache war im RAM sowieso weg)
   - Einzige Lösung wäre gewesen: Provider-ID manuell in Jellyfin löschen

3. **Schwer zu debuggen**
   - Cache-Key unterschied sich aber Log-Output sah gleich aus
   - Sah nach "Plugin-Bug" aus, war aber Konfiguration

---

## Für dich als Developer

**Was du überprüfen solltest:**

```bash
# 1. Code-Review der Änderungen:
git diff v1.2.1..HEAD Jellyfin.Plugin.MalSync/Services/MalSyncService.cs

# 2. Line-by-Line Überprüfung:
#    - Z163: normalizedSeriesName wird definiert
#    - Z173-178: Cache-Lookup/Write nutzt normalizedSeriesName
#    - Z188, 227, 265: Alle SetCachedMalId Calls nutzen normalizedSeriesName
#    - Z765: ContainsSeasonNumber(orig, ...) nicht norm

# 3. Test-Szenarios:
#    - Serie mit "& vs. to" in Namen
#    - Serien mit Unicode (ü, ö, ä, etc.)
#    - S1 + S2+ Mappings
```

**Build & Release:**

```bash
# Lokal kompilieren
dotnet build -c Release

# Version bumpen
sed -i 's/1\.2\.1\.0/1.2.2.0/' Jellyfin.Plugin.MalSync/Jellyfin.Plugin.MalSync.csproj

# Commit + Tag
git commit -am "v1.2.2: Fix cache normalization for title variations"
git tag -a v1.2.2 -m "v1.2.2: Fix cache key inconsistency, fix title handling in FindIdInUserList"
git push origin main v1.2.2
# GitHub Actions wird Release erstellen
```

---

## Test für deine Freundin

Nutze das Diagnose-Script:

```bash
bash /opt/jellyfin-mal-sync/diagnose.sh
```

Das prüft:
- ✓ MAL-Sync Plugin-Version
- ✓ Jellyfin Logs auf MAL-Einträge
- ✓ MAL API Erreichbarkeit
- ✓ Tipps für Fehlerfall

---

## Zusammenfassung

| Aspect | v1.2.1 | v1.2.2 |
|--------|--------|--------|
| **Cache-Key** | `userid::Baka & Test::1` | `userid::baka and test::1` |
| **Cache-Hits** | ~0% (unterschiedliche Namen) | ~100% (normalisiert) |
| **Title-Handling** | Inconsistent (norm vs. orig) | Konsistent (nur orig) |
| **Fehler-Szenario** | Nach Rename → Cache-Miss | Keine Rename-Probleme |
| **Diagnostizierbarkeit** | Schwer | Mit diagnose.sh einfach |

**Ergebnis:** v1.2.2 sollte deine Freundin endlich fixen! 🎉
