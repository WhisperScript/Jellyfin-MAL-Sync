/*
 * MAL Sync – "Open on MyAnimeList" button for Jellyfin's item detail pages.
 *
 * This runs inside the Jellyfin web client, not on a plugin page, so it has to
 * be injected. Load it with the JavaScript Injector plugin (or any equivalent)
 * using this three-line entry, which keeps the logic shipping with the plugin:
 *
 *     var s = document.createElement('script');
 *     s.src = '/web/ConfigurationPage?name=MalSyncItemButton';
 *     document.head.appendChild(s);
 *
 * Set the injector entry to require authentication — it needs a signed-in
 * ApiClient, and the MAL match it shows is per user.
 *
 * Jellyfin's own item pages only link to MyAnimeList when the item literally
 * carries a MyAnimeList provider ID. This asks the plugin instead, so it also
 * works for series that MAL Sync matched by title or that the user corrected
 * by hand.
 */
(function () {
    'use strict';

    if (window.__malSyncItemButton) return;
    window.__malSyncItemButton = true;

    var MARKER = 'malSyncItemLink';
    var lastItemId = null;
    var pending = 0;

    // Resolved item id → array of {malId, malTitle, malUrl, seasonNumber}.
    // Detail pages get revisited constantly; caching keeps this to one request.
    var cache = {};

    function base() {
        return (window.ApiClient && ApiClient.serverAddress)
            ? ApiClient.serverAddress().replace(/\/$/, '') : '';
    }

    /** Item id of the detail page currently on screen, or null elsewhere. */
    function currentItemId() {
        var hash = window.location.hash || '';
        if (hash.indexOf('/details') === -1) return null;
        var m = hash.match(/[?&]id=([0-9a-fA-F-]{32,36})/);
        return m ? m[1] : null;
    }

    function styles() {
        if (document.getElementById('ms-item-button-style')) return;
        var css = document.createElement('style');
        css.id = 'ms-item-button-style';
        css.textContent =
            '.itemExternalLinks .malSyncItemLink{border:0;padding:0;margin:0}' +
            '.itemExternalLinks .malSyncItemLink::after{content:none}' +
            '.malSyncItemLink{display:inline-flex;align-items:center;gap:.35em;' +
            'margin:.35em .5em .35em 0;padding:.3em .75em;border-radius:4px;' +
            'border:1px solid rgba(127,127,127,.45);text-decoration:none;color:inherit;' +
            'font-size:.85em;font-weight:600;line-height:1.6;white-space:nowrap}' +
            '.malSyncItemLink:hover{background:rgba(46,81,162,.25);border-color:#2e51a2;' +
            'text-decoration:none}' +
            '.malSyncItemLink::after{content:"↗";opacity:.6;font-size:.9em}';
        document.head.appendChild(css);
    }

    /**
     * Where the link should go, most natural spot first. Jellyfin's markup has
     * changed across versions, so this degrades instead of assuming one layout.
     */
    function findHost(page) {
        return page.querySelector('.itemExternalLinks')
            || page.querySelector('.itemMiscInfo-primary')
            || page.querySelector('.itemMiscInfo')
            || page.querySelector('.detailPagePrimaryContainer')
            || null;
    }

    function render(page, seasons) {
        Array.prototype.forEach.call(
            page.querySelectorAll('.' + MARKER), function (n) { n.remove(); });

        if (!seasons || !seasons.length) return;

        var host = findHost(page);
        if (!host) return;

        // Jellyfin hides the external-links row while it is empty.
        host.classList.remove('hide');

        styles();

        // One link for a plain series, one per season when a series spans
        // several MAL entries — which is the normal case for anime.
        var multiple = seasons.length > 1;
        seasons.forEach(function (s) {
            var a = document.createElement('a');
            // "button-link" is what Jellyfin gives IMDb and TMDB, so the link sits in
            // the row looking like the others rather than like an add-on.
            a.className = host.classList.contains('itemExternalLinks')
                ? 'button-link ' + MARKER
                : MARKER;
            a.href = s.malUrl;
            a.target = '_blank';
            a.rel = 'noopener noreferrer';
            a.textContent = multiple
                ? 'MyAnimeList: S' + s.seasonNumber
                : 'MyAnimeList';
            a.title = s.malTitle
                ? s.malTitle + ' — open on MyAnimeList'
                : 'Open on MyAnimeList';
            host.appendChild(a);
        });
    }

    function activePage() {
        // Jellyfin keeps old views in the DOM; only the visible one counts.
        var pages = document.querySelectorAll('#itemDetailPage, .itemDetailPage, [data-type="details"]');
        for (var i = 0; i < pages.length; i++) {
            if (pages[i].offsetParent !== null) return pages[i];
        }
        return null;
    }

    function update() {
        var itemId = currentItemId();
        if (!itemId) { lastItemId = null; return; }

        var page = activePage();
        if (!page) return;

        // Already drawn for this item on this view.
        if (itemId === lastItemId && page.querySelector('.' + MARKER)) return;

        if (cache[itemId]) {
            lastItemId = itemId;
            render(page, cache[itemId]);
            return;
        }

        if (pending) return;
        pending++;

        fetch(base() + '/MalSync/series/' + itemId + '/mal', {
            headers: { 'X-Emby-Token': ApiClient.accessToken() },
        })
            .then(function (r) { return r.ok ? r.json() : null; })
            .then(function (data) {
                var seasons = (data && data.seasons) || [];
                cache[itemId] = seasons;
                lastItemId = itemId;
                var live = activePage();
                if (live && currentItemId() === itemId) render(live, seasons);
            })
            .catch(function () { /* not an anime, no match, or not signed in */ })
            .then(function () { pending--; });
    }

    // Detail pages are swapped in without a page load, so watch both the route
    // and the DOM, and coalesce the bursts a render causes.
    var timer = null;
    function schedule() {
        clearTimeout(timer);
        timer = setTimeout(update, 250);
    }

    window.addEventListener('hashchange', function () {
        lastItemId = null;
        schedule();
    });
    document.addEventListener('viewshow', schedule);
    new MutationObserver(schedule).observe(document.body, { childList: true, subtree: true });

    schedule();
}());
