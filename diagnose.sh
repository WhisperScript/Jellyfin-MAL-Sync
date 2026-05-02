#!/usr/bin/env bash
# Diagnose-Script für MAL-Sync Cache & Title Issues
# Überprüft Cache-Konsistenz und Title-Normalisierung

set -e

echo "=== MAL-Sync Diagnose ==="
echo ""

# Farben
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

# 1. Jellyfin Logs prüfen
echo "1️⃣  Prüfe Jellyfin Logs auf MAL-Sync Nachrichten..."
if [[ -f /var/log/jellyfin/log_*.log ]]; then
    LATEST_LOG=$(ls -t /var/log/jellyfin/log_*.log | head -1)
    
    echo "   → Letzte Log-Datei: $LATEST_LOG"
    
    # Prüfe auf MAL-Sync Einträge
    MAL_ENTRIES=$(grep -i "mal\|MyAnimeList" "$LATEST_LOG" 2>/dev/null | tail -20 || echo "Keine MAL-Sync Einträge")
    
    if [[ "$MAL_ENTRIES" == "Keine MAL-Sync Einträge" ]]; then
        echo -e "${RED}   ✗ Keine MAL-Sync Einträge gefunden!${NC}"
        echo "   → Prüfe ob MAL-Sync Plugin geladen ist"
    else
        echo -e "${GREEN}   ✓ MAL-Sync ist aktiv${NC}"
        echo ""
        echo "   Letzte MAL-Sync Logs:"
        grep -i "mal\|MyAnimeList" "$LATEST_LOG" 2>/dev/null | tail -10 | sed 's/^/      /'
    fi
else
    echo -e "${YELLOW}   ⚠ Log-Datei nicht gefunden${NC}"
fi

echo ""
echo "2️⃣  Prüfe Jellyfin Plugin-Version..."
if [[ -f /jellyfin/plugins/MalSync/manifest.json ]]; then
    VERSION=$(grep -o '"version"[^,]*' /jellyfin/plugins/MalSync/manifest.json | head -1)
    echo -e "   ${GREEN}✓ MAL-Sync Plugin Version: $VERSION${NC}"
else
    echo -e "   ${RED}✗ Plugin manifest nicht gefunden${NC}"
fi

echo ""
echo "3️⃣  Prüfe MAL API Konnektivität..."
if curl -s "https://api.myanimelist.net/v2/anime/6347?fields=title" -H "Authorization: Bearer dummy" >/dev/null 2>&1; then
    echo -e "   ${GREEN}✓ MAL API erreichbar${NC}"
else
    echo -e "   ${YELLOW}⚠ MAL API nicht erreichbar (oder Auth-Error, das ist OK)${NC}"
fi

echo ""
echo "4️⃣  Diagnose-Tipps für deine Freundin:"
echo ""
echo "   Wenn MAL-Sync immer noch nicht funktioniert:"
echo ""
echo "   A) Überprüfe ob v1.2.2 oder neuer geladen ist:"
echo "      → Jellyfin → Plugins → MAL-Sync → Versionsinfo prüfen"
echo ""
echo "   B) Restart Jellyfin NACH Plugin-Installation:"
echo "      docker restart jellyfin"
echo ""
echo "   C) Debug-Modus aktivieren (optional):"
echo "      Jellyfin Logs mit 'debug' Level prüfen"
echo ""
echo "   D) MAL-Authentifizierung prüfen:"
echo "      Jellyfin → MAL Sync Settings → Authentifizieren"
echo ""
echo "   E) Serien-Namen vergleichen:"
echo "      - Jellyfin Serien-Name notieren (zB 'Baka & Test')"
echo "      - MAL-Website nach exaktem Namen suchen"
echo "      - Namen sollten ÄHNLICH sein (nicht exakt!)"
echo ""
echo "   F) Cache prüfen (nach fix v1.2.2+):"
echo "      - Erst nach neuem Sync sollte Cache funktionieren"
echo "      - Wenn Serie zweimal synced, wird Cache genutzt"
echo ""
echo "=== Diagnose abgeschlossen ==="
