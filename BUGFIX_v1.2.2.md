# BugFix v1.2.2 – Cache & Title Handling

## Probleme gefunden & gefixt

### 🔴 Bug 1: Cache-Key Normalisierung (KRITISCH)

**Problem:**
Der Cache wird mit dem **unnormalisierten** Seriennamen als Key gespeichert. Das führt zu:
- "Baka & Test: Summon the Beasts" → cache key 1
- "Baka to Test Shoukanjuu" → cache key 2
- Wenn die Serie umbenannt wird (zB auf Jellyfin), wird der **alte Cache ignoriert**
- → **Neue Suche wird gestartet** → **gleiche falsche ID wird gefunden**

**Ursache:**
```csharp
// FALSCH (v1.2.1):
malId = GetCachedMalId(cacheScope, seriesName, seasonNum, cfg.CacheTtlDays);
SetCachedMalId(cacheScope, seriesName, seasonNum, malId);
```

Der `seriesName` variiert je nach Jellyfin-Metadaten.

**Lösung:**
```csharp
// RICHTIG (v1.2.2):
var normalizedSeriesName = NormalizeTitle(seriesName);
malId = GetCachedMalId(cacheScope, normalizedSeriesName, seasonNum, cfg.CacheTtlDays);
SetCachedMalId(cacheScope, normalizedSeriesName, seasonNum, malId);
```

Alle Cache-Lookups/Writes verwenden jetzt den **normalisierten** Namen (Leerzeichen normalisiert, Unicode konvertiert, lowercase).

**Auswirkung auf deine Freundin:**
- Wenn die Serie in Jellyfin einen anderen Namen hat, funktioniert der Cache jetzt **trotzdem**
- Erste Sync dauert länger (neue Suche), aber dann wird das Ergebnis **wirklich gecacht**

### 🔴 Bug 2: Inconsistenter Title-Handling (FindIdInUserList)

**Problem:**
Bei der Suche für S2+ wird `norm` (normalisiert) für SeasonNumber-Check verwendet, aber `baseT` (aus `orig` konstruiert) für Score-Berechnung:

```csharp
// FALSCH (v1.2.1):
var baseT = NormalizeTitle(StripSeasonSuffix(orig));
var score = Similarity(baseQ, baseT);
if (!ContainsSeasonNumber(norm, seasonNum)) score *= 0.4;  // ← nutzt 'norm'!
```

Das kann zu Inconsistenzen führen, wenn `norm` und `orig` unterschiedlich sind.

**Lösung:**
```csharp
// RICHTIG (v1.2.2):
var baseT = NormalizeTitle(StripSeasonSuffix(orig));
var score = Similarity(baseQ, baseT);
if (!ContainsSeasonNumber(orig, seasonNum)) score *= 0.4;  // ← nutzt 'orig'!
```

## Was brauchst du tun?

### Für v1.2.2 Release:
1. **Kompiliere**: `bash ./build.sh`
2. **Test lokal**: Überprüfe dass `MalSyncService.cs` keine Syntax-Fehler hat
3. **Bump Version**: `Jellyfin.Plugin.MalSync.csproj` → `1.2.2.0`
4. **Commit & Tag**: `git commit` → `git tag v1.2.2` → `git push`

### Für deine Freundin:
1. **Update auf v1.2.2**
2. **Lösche alles** (Config, Plugin, Jellyfin-Neustart) wie gehabt
3. **Erste Sync** wird länger dauern (neuer MAL-Lookup)
4. **Ab Sync 2** sollte Cache funktionieren
5. **Wichtig**: Wenn `seriesName` in Jellyfin anders ist (zB "Baka to Test" statt "Baka & Test"), funktioniert der Cache **jetzt trotzdem** ✅

## Code-Details

**Datei:** `Jellyfin.Plugin.MalSync/Services/MalSyncService.cs`

**Änderungen:**
- Zeile 165: `var normalizedSeriesName = NormalizeTitle(seriesName);` (vor Schleife)
- Zeilen 173, 176, 178, 188, 227: Alle Cache-Calls nutzen `normalizedSeriesName`
- Zeile 765: `ContainsSeasonNumber(orig, seasonNum)` statt `norm`
