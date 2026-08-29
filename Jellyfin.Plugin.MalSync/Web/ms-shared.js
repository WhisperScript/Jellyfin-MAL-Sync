/*
 * MAL Sync – shared UI runtime.
 *
 * Loaded by both plugin pages (admin + user) from
 *   /web/ConfigurationPage?name=MalSyncShared
 * so the design system and the API/streaming helpers exist exactly once.
 *
 * Everything hangs off window.MalSyncUI. The pages call MalSyncUI.boot(fn)
 * which guarantees the stylesheet is injected before the page renders.
 */
(function () {
    'use strict';

    if (window.MalSyncUI) return;

    // ═══════════════════════════════════════════════════════════════════════
    // DESIGN TOKENS + STYLESHEET
    // ═══════════════════════════════════════════════════════════════════════
    //
    // Surfaces are derived from currentColor so the page follows Jellyfin's
    // light and dark themes instead of assuming a dark background. The plain
    // rgba() declaration is the fallback for engines without color-mix().

    var CSS = `
.ms-root {
    --ms-accent: var(--accent-color, #00a4dc);
    --ms-radius: 8px;
    --ms-radius-sm: 5px;

    --ms-surface: rgba(127,127,127,.10);
    --ms-surface: color-mix(in srgb, currentColor 7%, transparent);
    --ms-surface-raised: rgba(127,127,127,.16);
    --ms-surface-raised: color-mix(in srgb, currentColor 12%, transparent);
    --ms-line: rgba(127,127,127,.28);
    --ms-line: color-mix(in srgb, currentColor 18%, transparent);
    --ms-line-soft: rgba(127,127,127,.16);
    --ms-line-soft: color-mix(in srgb, currentColor 10%, transparent);

    --ms-ok:   #3ba55d;
    --ms-warn: #d9a021;
    --ms-err:  #e04f5f;
    --ms-info: var(--ms-accent);

    width: 100%;
    box-sizing: border-box;
}
.ms-root *, .ms-root *::before, .ms-root *::after { box-sizing: border-box; }

/* Jellyfin wraps plugin pages in a narrow column; let the page breathe. */
:has(> .ms-root) { max-width: none !important; width: 100% !important; }

/* ── Page header ──────────────────────────────────────────────────────── */
.ms-header {
    display: flex; align-items: center; gap: 1em;
    flex-wrap: wrap;
    margin: 0 0 1.5em;
}
.ms-header-text { flex: 1 1 18em; min-width: 0; }
.ms-header h1 {
    font-size: 1.45em; font-weight: 600; margin: 0; line-height: 1.25;
}
.ms-header-sub { font-size: .88em; opacity: .6; margin-top: .25em; line-height: 1.45; }
.ms-header-actions { display: flex; gap: .5em; align-items: center; flex-wrap: wrap; }

/* ── Cards ────────────────────────────────────────────────────────────── */
.ms-card {
    background: var(--ms-surface);
    border: 1px solid var(--ms-line-soft);
    border-radius: var(--ms-radius);
    padding: 1.35em 1.5em;
    margin-bottom: 1.15em;
}
.ms-card:last-child { margin-bottom: 0; }
.ms-card-head {
    display: flex; align-items: baseline; gap: .7em;
    flex-wrap: wrap;
    margin: 0 0 1em;
}
.ms-card-title {
    font-size: 1.02em; font-weight: 600; margin: 0; line-height: 1.3;
}
.ms-card-sub {
    font-size: .85em; opacity: .6; line-height: 1.5;
    margin: -.6em 0 1.1em;
}
.ms-card-note { font-size: .85em; opacity: .6; line-height: 1.5; margin: 0 0 1em; }

/* Two-column layout that collapses on narrow screens. */
.ms-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 30em), 1fr)); gap: 1.15em; align-items: start; }
.ms-grid > .ms-card { margin-bottom: 0; }
.ms-two-col { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 15em), 1fr)); gap: 0 1.2em; }

/* ── Buttons ──────────────────────────────────────────────────────────── */
.ms-btn {
    display: inline-flex; align-items: center; justify-content: center; gap: .5em;
    padding: .55em 1.1em;
    border-radius: var(--ms-radius-sm);
    border: 1px solid var(--ms-line);
    background: transparent;
    color: inherit;
    font-size: .9em; font-weight: 600; font-family: inherit;
    line-height: 1.45; white-space: nowrap;
    cursor: pointer;
    transition: background .13s, border-color .13s, opacity .13s;
}
.ms-btn:hover:not(:disabled) { background: var(--ms-surface-raised); }
.ms-btn:focus-visible { outline: 2px solid var(--ms-accent); outline-offset: 2px; }
.ms-btn:disabled { opacity: .4; cursor: not-allowed; }
.ms-btn-primary {
    background: var(--ms-accent); border-color: var(--ms-accent); color: #fff;
}
.ms-btn-primary:hover:not(:disabled) { background: var(--ms-accent); filter: brightness(1.12); }
.ms-btn-danger { border-color: rgba(224,79,95,.55); color: var(--ms-err); }
.ms-btn-danger:hover:not(:disabled) { background: rgba(224,79,95,.12); }
.ms-btn-sm { padding: .35em .7em; font-size: .82em; }
.ms-btn-quiet { border-color: transparent; opacity: .75; }
.ms-btn-quiet:hover:not(:disabled) { opacity: 1; }
.ms-actions {
    display: flex; flex-wrap: wrap; gap: .55em; align-items: center;
    margin-top: 1.1em;
}
.ms-actions-tight { margin-top: .7em; }

/* ── Status pills ─────────────────────────────────────────────────────── */
.ms-pill {
    display: inline-flex; align-items: center; gap: .4em;
    padding: .22em .7em;
    border-radius: 2em;
    font-size: .76em; font-weight: 600; letter-spacing: .01em;
    white-space: nowrap;
    border: 1px solid var(--ms-line);
}
.ms-pill::before {
    content: ''; width: .5em; height: .5em; border-radius: 50%;
    background: currentColor; flex-shrink: 0;
}
.ms-pill-ok   { color: var(--ms-ok);   border-color: color-mix(in srgb, var(--ms-ok) 45%, transparent); background: color-mix(in srgb, var(--ms-ok) 12%, transparent); }
.ms-pill-warn { color: var(--ms-warn); border-color: color-mix(in srgb, var(--ms-warn) 45%, transparent); background: color-mix(in srgb, var(--ms-warn) 12%, transparent); }
.ms-pill-err  { color: var(--ms-err);  border-color: color-mix(in srgb, var(--ms-err) 45%, transparent); background: color-mix(in srgb, var(--ms-err) 12%, transparent); }
.ms-pill-info { color: var(--ms-info); border-color: color-mix(in srgb, var(--ms-info) 45%, transparent); background: color-mix(in srgb, var(--ms-info) 12%, transparent); }
.ms-pill-mute { opacity: .6; }
.ms-pill-plain::before { display: none; }

/* ── Notes / callouts ─────────────────────────────────────────────────── */
.ms-note {
    display: flex; gap: .7em; align-items: flex-start;
    padding: .75em .95em;
    border-radius: var(--ms-radius-sm);
    border: 1px solid var(--ms-line-soft);
    background: var(--ms-surface);
    font-size: .87em; line-height: 1.55;
    margin-bottom: 1em;
}
.ms-note:last-child { margin-bottom: 0; }
.ms-note-icon { flex-shrink: 0; font-size: 1.05em; line-height: 1.4; opacity: .85; }
.ms-note-body { min-width: 0; flex: 1; }
.ms-note-body strong { font-weight: 600; }
.ms-note a { color: var(--ms-accent); text-decoration: none; }
.ms-note a:hover { text-decoration: underline; }
.ms-note-info { border-color: color-mix(in srgb, var(--ms-info) 35%, transparent); background: color-mix(in srgb, var(--ms-info) 8%, transparent); }
.ms-note-warn { border-color: color-mix(in srgb, var(--ms-warn) 35%, transparent); background: color-mix(in srgb, var(--ms-warn) 8%, transparent); }
.ms-note-err  { border-color: color-mix(in srgb, var(--ms-err) 35%, transparent);  background: color-mix(in srgb, var(--ms-err) 8%, transparent); }
.ms-note-ok   { border-color: color-mix(in srgb, var(--ms-ok) 35%, transparent);   background: color-mix(in srgb, var(--ms-ok) 8%, transparent); }

/* ── Form fields ──────────────────────────────────────────────────────── */
.ms-field { margin-bottom: 1.1em; }
.ms-field:last-child { margin-bottom: 0; }
.ms-label {
    display: block;
    font-size: .85em; font-weight: 600;
    margin-bottom: .35em;
}
.ms-input {
    display: block; width: 100%;
    padding: .55em .75em;
    font-size: .92em; font-family: inherit;
    color: inherit;
    background: var(--ms-surface-raised);
    border: 1px solid var(--ms-line);
    border-radius: var(--ms-radius-sm);
}
.ms-input:focus { outline: none; border-color: var(--ms-accent); }
.ms-input::placeholder { opacity: .45; }
.ms-help { font-size: .8em; opacity: .55; margin-top: .35em; line-height: 1.5; }
.ms-help a { color: var(--ms-accent); text-decoration: none; }
.ms-help a:hover { text-decoration: underline; }

/* Checkbox / radio rows */
.ms-choice {
    display: flex; align-items: flex-start; gap: .7em;
    padding: .6em .75em;
    margin: 0 -.75em;
    border-radius: var(--ms-radius-sm);
    cursor: pointer;
    transition: background .12s;
}
.ms-choice:hover { background: var(--ms-surface-raised); }
.ms-choice > input { margin: .2em 0 0; flex-shrink: 0; width: 1.05em; height: 1.05em; cursor: pointer; accent-color: var(--ms-accent); }
.ms-choice-body { min-width: 0; }
.ms-choice-title { font-size: .9em; font-weight: 600; line-height: 1.4; }
.ms-choice-sub { font-size: .8em; opacity: .55; margin-top: .15em; line-height: 1.45; }
.ms-choice-boxed { border: 1px solid var(--ms-line-soft); margin: 0 0 .5em; }
.ms-choice-boxed:last-child { margin-bottom: 0; }

.ms-choice-grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(11em, 1fr)); gap: .1em .6em; }
.ms-choice-inline { display: flex; align-items: center; gap: .5em; padding: .35em 0; cursor: pointer; font-size: .88em; }
.ms-choice-inline > input { accent-color: var(--ms-accent); cursor: pointer; }

/* Section label inside a card */
.ms-section-label {
    font-size: .75em; font-weight: 700; letter-spacing: .07em; text-transform: uppercase;
    opacity: .5;
    margin: 1.4em 0 .6em;
}
.ms-section-label:first-child { margin-top: 0; }

/* ── Tabs ─────────────────────────────────────────────────────────────── */
.ms-tabs {
    display: flex; gap: .3em;
    border-bottom: 1px solid var(--ms-line);
    margin-bottom: 1.4em;
    overflow-x: auto;
    scrollbar-width: none;
}
.ms-tabs::-webkit-scrollbar { display: none; }
.ms-tab {
    display: inline-flex; align-items: center; gap: .5em;
    padding: .65em 1.05em;
    border: none; background: none; color: inherit; font-family: inherit;
    font-size: .92em; font-weight: 600;
    opacity: .55; cursor: pointer;
    border-bottom: 2px solid transparent;
    margin-bottom: -1px;
    white-space: nowrap;
    transition: opacity .13s, border-color .13s;
}
.ms-tab:hover { opacity: .85; }
.ms-tab.active { opacity: 1; color: var(--ms-accent); border-bottom-color: var(--ms-accent); }
.ms-tab-badge {
    font-size: .72em; font-weight: 700;
    min-width: 1.5em; padding: .05em .4em;
    border-radius: 1em; text-align: center;
    background: var(--ms-surface-raised);
}
.ms-tab.has-alert .ms-tab-badge { background: color-mix(in srgb, var(--ms-warn) 30%, transparent); color: var(--ms-warn); }
.ms-panel { display: none; }
.ms-panel.active { display: block; }

/* ── Collapsible "advanced" disclosure ────────────────────────────────── */
.ms-disclosure { border-top: 1px solid var(--ms-line-soft); margin-top: 1.2em; padding-top: .3em; }
.ms-disclosure-btn {
    display: flex; align-items: center; gap: .5em;
    width: 100%; padding: .6em 0;
    border: none; background: none; color: inherit; font-family: inherit;
    font-size: .85em; font-weight: 600;
    opacity: .65; cursor: pointer; text-align: left;
}
.ms-disclosure-btn:hover { opacity: 1; }
.ms-disclosure-btn::before {
    content: '›';
    display: inline-block;
    font-size: 1.2em; line-height: 1;
    transition: transform .15s;
}
.ms-disclosure.open > .ms-disclosure-btn::before { transform: rotate(90deg); }
.ms-disclosure-body { display: none; padding: .4em 0 .6em; }
.ms-disclosure.open > .ms-disclosure-body { display: block; }

/* ── Setup checklist ──────────────────────────────────────────────────── */
.ms-steps { list-style: none; margin: 0; padding: 0; counter-reset: ms-step; }
.ms-step {
    display: flex; gap: .85em; align-items: flex-start;
    padding: .7em 0;
    border-bottom: 1px solid var(--ms-line-soft);
}
.ms-step:last-child { border-bottom: none; padding-bottom: 0; }
.ms-step-marker {
    counter-increment: ms-step;
    flex-shrink: 0;
    width: 1.7em; height: 1.7em;
    border-radius: 50%;
    border: 1px solid var(--ms-line);
    display: flex; align-items: center; justify-content: center;
    font-size: .8em; font-weight: 700;
    margin-top: .1em;
}
.ms-step-marker::before { content: counter(ms-step); }
.ms-step.done .ms-step-marker {
    border-color: var(--ms-ok);
    background: color-mix(in srgb, var(--ms-ok) 18%, transparent);
    color: var(--ms-ok);
}
.ms-step.done .ms-step-marker::before { content: '✓'; }
.ms-step.blocked .ms-step-marker { border-color: var(--ms-warn); color: var(--ms-warn); }
.ms-step-body { flex: 1; min-width: 0; }
.ms-step-title { font-size: .92em; font-weight: 600; line-height: 1.4; }
.ms-step.done .ms-step-title { opacity: .6; }
.ms-step-sub { font-size: .82em; opacity: .6; margin-top: .2em; line-height: 1.5; }
.ms-step-sub a { color: var(--ms-accent); text-decoration: none; }
.ms-step-sub a:hover { text-decoration: underline; }
.ms-step-action { margin-top: .55em; }

/* ── Run summary strip ────────────────────────────────────────────────── */
.ms-summary {
    display: flex; flex-wrap: wrap; gap: .5em .4em;
    align-items: stretch;
    margin-top: 1em;
}
.ms-stat {
    flex: 1 1 6.5em;
    padding: .6em .8em;
    border: 1px solid var(--ms-line-soft);
    border-radius: var(--ms-radius-sm);
    background: var(--ms-surface-raised);
}
.ms-stat-value { font-size: 1.3em; font-weight: 600; line-height: 1.15; }
.ms-stat-label { font-size: .72em; letter-spacing: .05em; text-transform: uppercase; opacity: .5; margin-top: .2em; }
.ms-stat-ok   .ms-stat-value { color: var(--ms-ok); }
.ms-stat-warn .ms-stat-value { color: var(--ms-warn); }
.ms-stat-err  .ms-stat-value { color: var(--ms-err); }

/* ── Log console ──────────────────────────────────────────────────────── */
.ms-log {
    background: rgba(0,0,0,.35);
    background: color-mix(in srgb, currentColor 6%, transparent);
    border: 1px solid var(--ms-line-soft);
    border-radius: var(--ms-radius-sm);
    padding: .7em .85em;
    font-family: ui-monospace, 'SF Mono', Consolas, 'Fira Mono', monospace;
    font-size: .78em;
    max-height: 22em;
    overflow-y: auto;
    margin-top: .7em;
}
.ms-log-empty { opacity: .35; font-style: italic; text-align: center; padding: .9em; font-size: 1.05em; }
.ms-log-line {
    display: grid;
    grid-template-columns: auto 3.4em 1fr;
    gap: .6em;
    align-items: start;
    padding: .12em 0;
    line-height: 1.5;
}
.ms-log-time { opacity: .4; font-size: .92em; white-space: nowrap; }
.ms-log-tag {
    font-size: .82em; font-weight: 700; letter-spacing: .03em;
    text-align: center;
    border-radius: 3px;
    padding: 0 .3em;
    white-space: nowrap;
    background: var(--ms-surface-raised);
}
.ms-log-msg { min-width: 0; word-break: break-word; white-space: pre-wrap; }
.ms-log-ok   .ms-log-tag, .ms-log-ok   .ms-log-msg { color: var(--ms-ok); }
.ms-log-warn .ms-log-tag, .ms-log-warn .ms-log-msg { color: var(--ms-warn); }
.ms-log-err  .ms-log-tag, .ms-log-err  .ms-log-msg { color: var(--ms-err); }
.ms-log-info .ms-log-tag { color: var(--ms-info); }
.ms-log-dbg  { opacity: .45; }

/* ── Spinner ──────────────────────────────────────────────────────────── */
.ms-spinner {
    display: none;
    width: 1em; height: 1em;
    border: 2px solid var(--ms-line);
    border-top-color: var(--ms-accent);
    border-radius: 50%;
    animation: ms-spin .65s linear infinite;
    flex-shrink: 0;
}
.ms-spinner.active { display: inline-block; }
@keyframes ms-spin { to { transform: rotate(360deg); } }
@media (prefers-reduced-motion: reduce) { .ms-spinner { animation-duration: 2s; } }

.ms-progress { height: 4px; border-radius: 2px; background: var(--ms-surface-raised); overflow: hidden; }
.ms-progress > div { height: 100%; width: 0; background: var(--ms-accent); transition: width .2s ease; }

/* ── Inline status text ───────────────────────────────────────────────── */
.ms-status { font-size: .84em; opacity: .75; }
.ms-status-ok  { color: var(--ms-ok); opacity: 1; }
.ms-status-err { color: var(--ms-err); opacity: 1; }

/* ── Lists / rows ─────────────────────────────────────────────────────── */
.ms-row {
    display: flex; align-items: center; gap: .85em;
    padding: .7em .85em;
    border: 1px solid var(--ms-line-soft);
    border-radius: var(--ms-radius-sm);
    margin-bottom: .5em;
    background: var(--ms-surface-raised);
}
.ms-row:last-child { margin-bottom: 0; }
.ms-row-body { flex: 1; min-width: 0; }
.ms-row-title { font-size: .92em; font-weight: 600; line-height: 1.35; }
.ms-row-sub { font-size: .8em; opacity: .55; margin-top: .15em; line-height: 1.45; }
.ms-row-actions { display: flex; gap: .4em; align-items: center; flex-shrink: 0; }
.ms-row-thumb {
    width: 34px; height: 48px; flex-shrink: 0;
    object-fit: cover; border-radius: 3px;
    background: var(--ms-surface-raised);
}
.ms-truncate { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }

/* ── Library list ─────────────────────────────────────────────────────────── */
.ms-series {
    display: flex; gap: .9em;
    padding: .85em 0;
    border-bottom: 1px solid var(--ms-line-soft);
}
.ms-series:last-child { border-bottom: none; }
.ms-series-poster {
    width: 44px; height: 64px; flex-shrink: 0;
    object-fit: cover; border-radius: 4px;
    background: var(--ms-surface-raised);
}
.ms-series-body { flex: 1; min-width: 0; }
.ms-series-name { font-size: .95em; font-weight: 600; line-height: 1.35; }

.ms-season-row {
    display: flex; align-items: center; gap: .75em;
    margin-top: .5em;
}
.ms-season-label {
    flex-shrink: 0; min-width: 2.4em;
    font-size: .78em; font-weight: 600;
    opacity: .5;
}

/* The parts a proposed split would create, listed inside its warning. */
.ms-split-preview {
    display: flex; flex-wrap: wrap; gap: .35em .5em;
    margin-top: .6em;
}
.ms-split-part {
    padding: .2em .6em;
    border-radius: var(--ms-radius-sm);
    border: 1px solid var(--ms-line-soft);
    background: var(--ms-surface-raised);
    font-size: .92em;
}
.ms-split-part b { font-weight: 600; margin-right: .35em; }

/* Stack of warnings above a card; collapses away when there are none. */
.ms-notices:not(:empty) { margin-bottom: 1.15em; }
.ms-notices > .ms-note:last-child { margin-bottom: 0; }
.ms-season-info { flex: 1; min-width: 0; }

@media (max-width: 46em) {
    .ms-season-row { flex-wrap: wrap; gap: .4em .6em; }
    .ms-season-label { min-width: 0; }
    .ms-season-info { flex-basis: 100%; order: 3; }
}

/* ── Match state ──────────────────────────────────────────────────────────
   Every season row shows exactly one of these, always in the same slot and at
   the same size, so the states are told apart by colour and word rather than by
   having each one laid out differently. */
.ms-state {
    display: inline-flex; align-items: center; gap: .4em;
    padding: .18em .6em;
    border-radius: 2em;
    border: 1px solid var(--ms-line);
    font-size: .74em; font-weight: 600;
    white-space: nowrap; flex-shrink: 0;
    min-width: 7em;
    justify-content: center;
}
.ms-state::before {
    content: ''; flex-shrink: 0;
    width: .5em; height: .5em; border-radius: 50%;
    background: currentColor;
}
.ms-state-auto    { color: inherit; opacity: .55; }
.ms-state-auto::before { opacity: .55; }
.ms-state-mine    { color: var(--ms-accent); border-color: color-mix(in srgb, var(--ms-accent) 45%, transparent); background: color-mix(in srgb, var(--ms-accent) 12%, transparent); }
.ms-state-idle    { color: inherit; opacity: .35; }
.ms-state-idle::before { background: none; border: 1px solid currentColor; }
.ms-state-attention { color: var(--ms-warn); border-color: color-mix(in srgb, var(--ms-warn) 45%, transparent); background: color-mix(in srgb, var(--ms-warn) 12%, transparent); }
.ms-state-off     { color: var(--ms-err); border-color: color-mix(in srgb, var(--ms-err) 40%, transparent); background: color-mix(in srgb, var(--ms-err) 10%, transparent); }

@media (max-width: 40em) {
    .ms-state { min-width: 0; }
}

/* Link out to MyAnimeList. Rendered as an <a> so middle-click and
   "open in new tab" behave the way people expect from a link. */
.ms-mal-link {
    display: inline-flex; align-items: center; gap: .35em;
    padding: .35em .7em;
    border: 1px solid var(--ms-line);
    border-radius: var(--ms-radius-sm);
    font-size: .82em; font-weight: 600;
    color: inherit; text-decoration: none;
    white-space: nowrap; flex-shrink: 0;
    transition: background .13s, border-color .13s;
}
.ms-mal-link:hover {
    background: color-mix(in srgb, #2e51a2 22%, transparent);
    border-color: #2e51a2;
    text-decoration: none;
}
.ms-mal-link:focus-visible { outline: 2px solid var(--ms-accent); outline-offset: 2px; }
.ms-mal-link::after { content: '↗'; font-size: .9em; opacity: .6; }
.ms-mal-link-icon { padding: .35em .5em; }
.ms-mal-link-icon::after { content: none; }

/* Poster overlay variant, sitting opposite the exclude button. */
.ms-poster-link {
    position: absolute; top: .3em; left: .3em;
    display: flex; align-items: center; justify-content: center;
    width: 1.9em; height: 1.9em;
    border-radius: 50%;
    background: rgba(0,0,0,.72); color: #fff;
    font-size: .8em; font-weight: 700; text-decoration: none;
    opacity: 0; transition: opacity .12s;
}
.ms-poster:hover .ms-poster-link,
.ms-poster-link:focus-visible { opacity: 1; }
@media (hover: none) { .ms-poster-link { opacity: 1; } }

.ms-tag {
    display: inline-block;
    padding: .1em .5em; margin: .2em .25em 0 0;
    border-radius: 3px;
    font-size: .74em; font-weight: 600;
    border: 1px solid color-mix(in srgb, var(--ms-accent) 35%, transparent);
    background: color-mix(in srgb, var(--ms-accent) 12%, transparent);
    color: var(--ms-accent);
}

.ms-empty {
    padding: 1.8em 1em;
    text-align: center;
    font-size: .88em;
    opacity: .5;
    line-height: 1.6;
}

/* ── Filter chips ─────────────────────────────────────────────────────── */
.ms-chips { display: flex; flex-wrap: wrap; gap: .4em; }
.ms-chip {
    padding: .3em .8em;
    border-radius: 2em;
    border: 1px solid var(--ms-line);
    background: transparent; color: inherit; font-family: inherit;
    font-size: .8em; font-weight: 600;
    opacity: .65; cursor: pointer;
    transition: opacity .12s, background .12s;
}
.ms-chip:hover { opacity: 1; }
.ms-chip.active {
    opacity: 1;
    border-color: var(--ms-accent);
    background: color-mix(in srgb, var(--ms-accent) 15%, transparent);
    color: var(--ms-accent);
}

/* ── Poster grid ──────────────────────────────────────────────────────── */
.ms-posters { display: grid; grid-template-columns: repeat(auto-fill, minmax(108px, 1fr)); gap: .7em; }
.ms-poster { position: relative; }
.ms-poster img {
    width: 100%; aspect-ratio: 2/3; object-fit: cover;
    border-radius: var(--ms-radius-sm);
    background: var(--ms-surface-raised);
    display: block;
}
.ms-poster-title {
    font-size: .76em; line-height: 1.35; margin-top: .35em;
    display: -webkit-box; -webkit-line-clamp: 2; -webkit-box-orient: vertical;
    overflow: hidden;
}
.ms-poster-sub { font-size: .7em; opacity: .5; margin-top: .1em; }
.ms-poster-action {
    position: absolute; top: .3em; right: .3em;
    width: 1.9em; height: 1.9em;
    border-radius: 50%;
    border: none;
    background: rgba(0,0,0,.72); color: #fff;
    font-size: .85em; line-height: 1;
    cursor: pointer; opacity: 0;
    transition: opacity .12s;
}
.ms-poster:hover .ms-poster-action,
.ms-poster-action:focus-visible { opacity: 1; }
/* Hover never happens on touch, so keep the action reachable there. */
@media (hover: none) { .ms-poster-action { opacity: 1; } }

/* ── Table ────────────────────────────────────────────────────────────── */
.ms-table-wrap { overflow-x: auto; }
.ms-table { width: 100%; border-collapse: collapse; font-size: .87em; }
.ms-table th {
    text-align: left; font-weight: 600; font-size: .82em;
    letter-spacing: .05em; text-transform: uppercase; opacity: .5;
    padding: .5em .7em; white-space: nowrap;
    border-bottom: 1px solid var(--ms-line);
}
.ms-table td {
    padding: .6em .7em;
    border-bottom: 1px solid var(--ms-line-soft);
    vertical-align: middle;
}
.ms-table tr:last-child td { border-bottom: none; }

/* ── Modal ────────────────────────────────────────────────────────────── */
.ms-modal-backdrop {
    display: none;
    position: fixed; inset: 0; z-index: 1000;
    background: rgba(0,0,0,.65);
    padding: 4vh 1em;
    overflow-y: auto;
}
.ms-modal-backdrop.open { display: block; }
.ms-modal {
    position: relative;
    max-width: 46em; margin: 0 auto;
    background: var(--card-background, #1c1c1c);
    border: 1px solid var(--ms-line);
    border-radius: var(--ms-radius);
    padding: 1.5em;
    box-shadow: 0 12px 40px rgba(0,0,0,.45);
}
.ms-modal-close {
    position: absolute; top: .7em; right: .7em;
    width: 2em; height: 2em;
    border: none; border-radius: 50%;
    background: var(--ms-surface-raised); color: inherit;
    font-size: .95em; line-height: 1; cursor: pointer; opacity: .7;
}
.ms-modal-close:hover { opacity: 1; }
.ms-modal-title { font-size: 1.1em; font-weight: 600; margin: 0 2.2em .2em 0; }
.ms-modal-sub { font-size: .85em; opacity: .55; margin: 0 0 1.2em; }
.ms-modal-actions {
    display: flex; flex-wrap: wrap; gap: .5em; align-items: center;
    margin-top: 1.3em; padding-top: 1.1em;
    border-top: 1px solid var(--ms-line-soft);
}

/* ── Search results ───────────────────────────────────────────────────── */
.ms-results { max-height: 20em; overflow-y: auto; margin-top: .6em; }
.ms-result {
    display: flex; gap: .7em; align-items: center;
    padding: .5em .6em;
    border: 1px solid transparent;
    border-radius: var(--ms-radius-sm);
    cursor: pointer;
    text-align: left; width: 100%;
    background: none; color: inherit; font-family: inherit;
}
.ms-result:hover { background: var(--ms-surface-raised); }
.ms-result.selected { border-color: var(--ms-accent); background: color-mix(in srgb, var(--ms-accent) 12%, transparent); }
.ms-result img { width: 34px; height: 48px; object-fit: cover; border-radius: 3px; flex-shrink: 0; background: var(--ms-surface-raised); }
.ms-result-body { min-width: 0; flex: 1; }
.ms-result-title { font-size: .88em; font-weight: 600; line-height: 1.35; }
.ms-result-sub { font-size: .76em; opacity: .5; margin-top: .1em; }

.ms-hidden { display: none !important; }

@media (max-width: 40em) {
    .ms-card { padding: 1.1em 1.05em; }
    .ms-modal { padding: 1.2em 1em; }
    .ms-log-line { grid-template-columns: 3.2em 1fr; }
    .ms-log-time { display: none; }
}
`;

    // ═══════════════════════════════════════════════════════════════════════
    // HTTP
    // ═══════════════════════════════════════════════════════════════════════

    function serverBase() {
        if (window.ApiClient && ApiClient.serverAddress) {
            return ApiClient.serverAddress().replace(/\/$/, '');
        }
        return window.location.origin;
    }

    function authHeaders() {
        var h = {};
        if (window.ApiClient && ApiClient.accessToken) h['X-Emby-Token'] = ApiClient.accessToken();
        return h;
    }

    async function readError(res) {
        var body = await res.json().catch(function () { return {}; });
        var msg = body.error || body.Error || ('HTTP ' + res.status);
        if (res.status === 403) msg = 'Not allowed — this action requires administrator rights.';
        return Object.assign(new Error(msg), { status: res.status, body: body });
    }

    async function apiGet(path) {
        var res = await fetch(serverBase() + path, { headers: authHeaders() });
        if (!res.ok) throw await readError(res);
        return res.json();
    }

    async function apiPost(path, data) {
        var res = await fetch(serverBase() + path, {
            method: 'POST',
            headers: Object.assign({ 'Content-Type': 'application/json' }, authHeaders()),
            body: JSON.stringify(data === undefined ? {} : data),
        });
        if (!res.ok) throw await readError(res);
        return res.json();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // LOG CONSOLE + RUN SUMMARY
    // ═══════════════════════════════════════════════════════════════════════

    // Maps a raw log line to a severity class, a short tag and the summary
    // bucket it counts towards. Buckets drive the result strip so users get a
    // verdict without reading the log at all.
    function classify(line) {
        var t = String(line || '');
        if (t.startsWith('[MAL]'))       return { cls: 'ms-log-ok',   tag: 'MAL',  bucket: 'updated' };
        if (t.startsWith('[REQUEST]'))   return { cls: 'ms-log-ok',   tag: 'REQ',  bucket: 'requested' };
        if (t.startsWith('[DRY RUN]'))   return { cls: 'ms-log-warn', tag: 'DRY',  bucket: 'wouldChange' };
        if (t.startsWith('[SKIP]'))      return { cls: 'ms-log-warn', tag: 'SKIP', bucket: 'skipped' };
        if (t.startsWith('[WARN]'))      return { cls: 'ms-log-warn', tag: 'WARN', bucket: 'warnings' };
        if (t.startsWith('[MAL ERROR]')) return { cls: 'ms-log-err',  tag: 'ERR',  bucket: 'errors' };
        if (t.startsWith('[ERROR]'))     return { cls: 'ms-log-err',  tag: 'ERR',  bucket: 'errors' };
        if (t.startsWith('[DEBUG]') || t.startsWith('[DBG]')) return { cls: 'ms-log-dbg', tag: 'DBG', bucket: null };
        return { cls: 'ms-log-info', tag: 'INFO', bucket: null };
    }

    function clearLog(box, placeholder) {
        box.textContent = '';
        var p = document.createElement('div');
        p.className = 'ms-log-empty';
        p.textContent = placeholder || 'No output yet.';
        box.appendChild(p);
    }

    function appendLog(box, line) {
        var empty = box.querySelector('.ms-log-empty');
        if (empty) empty.remove();

        var info = classify(line);
        var row = document.createElement('div');
        row.className = 'ms-log-line ' + info.cls;

        var time = document.createElement('span');
        time.className = 'ms-log-time';
        time.textContent = new Date().toLocaleTimeString([], { hour12: false });

        var tag = document.createElement('span');
        tag.className = 'ms-log-tag';
        tag.textContent = info.tag;

        var msg = document.createElement('span');
        msg.className = 'ms-log-msg';
        msg.textContent = String(line || '');

        row.appendChild(time);
        row.appendChild(tag);
        row.appendChild(msg);
        box.appendChild(row);

        // Keep pinned to the newest line unless the user scrolled up to read.
        var atBottom = box.scrollHeight - box.scrollTop - box.clientHeight < 40;
        if (atBottom) box.scrollTop = box.scrollHeight;

        return info;
    }

    function renderSummary(target, counts, seconds) {
        var stats = [];
        if (counts.updated)     stats.push(['ok',   counts.updated,     'Updated on MAL']);
        if (counts.requested)   stats.push(['ok',   counts.requested,   'Requested']);
        if (counts.wouldChange) stats.push(['warn', counts.wouldChange, 'Would change']);
        if (counts.skipped)     stats.push(['',     counts.skipped,     'Skipped']);
        if (counts.warnings)    stats.push(['warn', counts.warnings,    'Warnings']);
        if (counts.errors)      stats.push(['err',  counts.errors,      'Errors']);

        target.textContent = '';
        if (!stats.length) {
            var none = document.createElement('div');
            none.className = 'ms-note ms-note-ok';
            none.innerHTML = '<span class="ms-note-icon">✓</span><div class="ms-note-body">'
                + 'Finished — nothing needed changing.</div>';
            target.appendChild(none);
            return;
        }

        stats.push(['', (seconds < 10 ? seconds.toFixed(1) : Math.round(seconds)) + 's', 'Duration']);

        stats.forEach(function (s) {
            var box = document.createElement('div');
            box.className = 'ms-stat' + (s[0] ? ' ms-stat-' + s[0] : '');
            var v = document.createElement('div');
            v.className = 'ms-stat-value';
            v.textContent = s[1];
            var l = document.createElement('div');
            l.className = 'ms-stat-label';
            l.textContent = s[2];
            box.appendChild(v);
            box.appendChild(l);
            target.appendChild(box);
        });
    }

    /**
     * Streams a server-sent-event endpoint into a log box while counting
     * outcomes for the summary strip.
     *
     * EventSource cannot send the Jellyfin auth header, so the SSE framing is
     * parsed off a plain fetch body instead.
     *
     * opts: { url, log, summary, spinner, disable[], onLine, onDone }
     * Returns a handle with .abort() so the caller can offer a Stop button.
     */
    function stream(opts) {
        var log = opts.log;
        var counts = { updated: 0, requested: 0, wouldChange: 0, skipped: 0, warnings: 0, errors: 0 };
        var started = Date.now();
        var buttons = opts.disable || [];
        var controller = new AbortController();
        var aborted = false;

        clearLog(log, 'Running…');
        if (opts.summary) opts.summary.textContent = '';
        if (opts.spinner) opts.spinner.classList.add('active');
        buttons.forEach(function (b) { if (b) b.disabled = true; });

        function handleLine(text) {
            if (!text) return;
            if (opts.onLine && opts.onLine(text) === false) return;
            var info = appendLog(log, text);
            if (info.bucket) counts[info.bucket]++;
        }

        (async function run() {
            try {
                var res = await fetch(serverBase() + opts.url, {
                    headers: authHeaders(),
                    signal: controller.signal,
                });
                if (!res.ok) {
                    counts.errors++;
                    appendLog(log, '[ERROR] Server responded with HTTP ' + res.status + '.');
                } else {
                    var reader = res.body.getReader();
                    var decoder = new TextDecoder();
                    var buffer = '';
                    var done = false;

                    while (!done) {
                        var chunk = await reader.read();
                        if (chunk.done) break;
                        buffer += decoder.decode(chunk.value, { stream: true });

                        var idx;
                        while ((idx = buffer.indexOf('\n\n')) !== -1) {
                            var frame = buffer.slice(0, idx);
                            buffer = buffer.slice(idx + 2);
                            if (!frame.startsWith('data: ')) continue;
                            var text = frame.slice(6);
                            if (text === '[DONE]') { done = true; break; }
                            handleLine(text);
                        }
                    }
                }
            } catch (e) {
                if (!aborted) {
                    counts.errors++;
                    appendLog(log, '[ERROR] ' + (e.message || 'The connection was interrupted.'));
                }
            } finally {
                if (opts.spinner) opts.spinner.classList.remove('active');
                buttons.forEach(function (b) { if (b) b.disabled = false; });
                if (aborted) {
                    appendLog(log, '[WARN] Stopped. Changes made before stopping are kept.');
                } else if (opts.summary) {
                    renderSummary(opts.summary, counts, (Date.now() - started) / 1000);
                }
                if (opts.onDone) opts.onDone(counts, aborted);
            }
        }());

        return {
            abort: function () { aborted = true; controller.abort(); },
        };
    }

    // ═══════════════════════════════════════════════════════════════════════
    // SMALL HELPERS
    // ═══════════════════════════════════════════════════════════════════════

    function el(id) { return document.getElementById(id); }

    function on(id, event, handler) {
        var node = el(id);
        if (node) node.addEventListener(event, handler);
    }

    function show(node, visible) {
        if (!node) return;
        if (typeof node === 'string') node = el(node);
        if (node) node.classList.toggle('ms-hidden', !visible);
    }

    function esc(s) {
        return String(s == null ? '' : s).replace(/[&<>"']/g, function (c) {
            return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
        });
    }

    /** Shows a transient result next to a button ("Saved", "Failed: …"). */
    function flash(node, text, kind) {
        if (typeof node === 'string') node = el(node);
        if (!node) return;
        node.className = 'ms-status' + (kind ? ' ms-status-' + kind : '');
        node.textContent = text;
        if (kind !== 'err') {
            clearTimeout(node._msTimer);
            node._msTimer = setTimeout(function () { node.textContent = ''; }, 2600);
        }
    }

    /** Human-friendly relative time, e.g. "3 hours ago". Empty for no value. */
    function ago(iso) {
        if (!iso) return '';
        var then = new Date(iso).getTime();
        if (isNaN(then)) return '';
        var mins = Math.round((Date.now() - then) / 60000);
        if (mins < 1) return 'just now';
        if (mins < 60) return mins + (mins === 1 ? ' minute ago' : ' minutes ago');
        var hrs = Math.round(mins / 60);
        if (hrs < 24) return hrs + (hrs === 1 ? ' hour ago' : ' hours ago');
        var days = Math.round(hrs / 24);
        if (days < 30) return days + (days === 1 ? ' day ago' : ' days ago');
        return new Date(iso).toLocaleDateString();
    }

    /** Canonical MyAnimeList page for an anime ID. */
    function malUrl(malId) {
        return 'https://myanimelist.net/anime/' + encodeURIComponent(malId);
    }

    /**
     * A link to an anime's MyAnimeList page.
     * variant: 'button' (default), 'icon' (compact) or 'poster' (overlay).
     */
    function malLink(malId, title, variant) {
        var a = document.createElement('a');
        a.href = malUrl(malId);
        a.target = '_blank';
        a.rel = 'noopener noreferrer';
        a.title = title
            ? 'Open “' + title + '” on MyAnimeList'
            : 'Open on MyAnimeList';

        if (variant === 'poster') {
            a.className = 'ms-poster-link';
            a.textContent = '↗';
            a.setAttribute('aria-label', a.title);
        } else if (variant === 'icon') {
            a.className = 'ms-mal-link ms-mal-link-icon';
            a.textContent = 'MAL';
        } else {
            a.className = 'ms-mal-link';
            a.textContent = 'MyAnimeList';
        }
        // Rows and results are clickable themselves; the link must not select them.
        a.addEventListener('click', function (e) { e.stopPropagation(); });
        return a;
    }

    /**
     * Describes a season's match in one consistent shape, so every state gets the
     * same treatment: a coloured pill, a headline and one short line of detail.
     *
     * season: the object returned by /MalSync/series.
     */
    function matchState(season) {
        // A split replaces a single match rather than sitting alongside one: when a
        // season is mapped range by range, that mapping *is* the answer, and the sync
        // ignores any single entry. So it is reported first and on its own terms.
        var ranges = season.episodeRanges || [];
        if (ranges.length && !season.blocked) {
            return {
                tone: 'mine', label: 'Split',
                title: ranges.map(function (r) { return r.malTitle || ('MAL ' + r.malId); }).join('  +  '),
                detail: 'Split into ' + ranges.length + ' parts'
                    + (season.jellyfinEpisodes ? ' · ' + season.jellyfinEpisodes + ' episodes here' : ''),
                hint: 'This season is mapped to ' + ranges.length + ' MyAnimeList entries by episode range: '
                    + ranges.map(function (r) {
                        return 'ep ' + r.episodeFrom + '–' + (r.episodeTo === 0 ? 'end' : r.episodeTo)
                            + ' → ' + (r.malTitle || ('MAL ' + r.malId));
                    }).join(', ') + '.',
                action: 'Change',
            };
        }

        if (season.blocked) {
            return {
                tone: 'off', label: 'Excluded',
                title: 'Not matched — excluded',
                detail: 'Skipped by every sync',
                hint: 'You excluded this season from syncing.',
                action: 'Include',
            };
        }
        if (season.malId && season.splitSuggested) {
            return {
                tone: 'attention', label: 'Needs split',
                title: malDisplayTitle(season),
                detail: malDetail(season),
                hint: 'This season holds far more episodes than its MyAnimeList entry. '
                    + 'It probably covers several MAL entries and needs splitting.',
                action: 'Split',
            };
        }
        if (season.malId) {
            var mine = season.malIdSource === 'pinned';
            return {
                tone: mine ? 'mine' : 'auto',
                label: mine ? 'Yours' : 'Auto',
                title: malDisplayTitle(season),
                detail: malDetail(season),
                hint: mine
                    ? 'You picked this entry yourself.'
                    : (season.malIdSource === 'provider'
                        ? 'Taken from the MyAnimeList ID stored on this item in your library.'
                        : 'Matched automatically by title.'),
                action: 'Change',
            };
        }
        if (season.malIdSource === 'unchecked') {
            return {
                tone: 'idle', label: 'Not checked',
                title: 'Not matched yet',
                detail: 'Resolved on the next sync',
                hint: 'No sync has looked at this season yet. Run a sync, or pick an entry now.',
                action: 'Pick',
            };
        }
        return {
            tone: 'attention', label: 'No match',
            title: 'No match found',
            detail: season.isSpecial ? 'Specials are never matched automatically' : 'Nothing close enough on MyAnimeList',
            hint: season.isSpecial
                ? 'Specials are never matched automatically — pick an entry if you want them synced.'
                : 'A sync searched MyAnimeList and found nothing close enough. Pick the right entry yourself.',
            action: 'Fix',
        };
    }

    /**
     * Episode counts, phrased so the two sides are never confused.
     * Having fewer episodes than MyAnimeList lists is normal — not everything is
     * downloaded — so that reads as "8 of 12", not as a problem. Having more is the
     * signal that a season covers several MAL entries.
     */
    function episodeDetail(season) {
        var here = season.jellyfinEpisodes || 0;
        var mal = season.malEpisodes || 0;

        if (!here && !mal) return '';
        if (!mal) return here + (here === 1 ? ' episode here' : ' episodes here');
        if (!here) return mal + ' episodes on MAL';
        if (here === mal) return here + ' episodes';
        if (here < mal) return here + ' of ' + mal + ' episodes';
        return here + ' episodes here · ' + mal + ' on MAL';
    }

    function malDetail(season) {
        var bits = ['MAL ' + season.malId];
        var eps = episodeDetail(season);
        if (eps) bits.push(eps);
        return bits.join(' · ');
    }

    /**
     * (4) Cached matches from before titles were stored — and any entry MyAnimeList
     * has not named yet — must not surface as a bare ID. Say what it is instead;
     * the real title is filled in lazily by the page and permanently by the next sync.
     */
    function malDisplayTitle(season) {
        if (season.malTitle) return season.malTitle;
        return season.malId ? 'MyAnimeList entry ' + season.malId : '';
    }

    /** The pill element for a state returned by matchState(). */
    function stateBadge(state) {
        var el = document.createElement('span');
        el.className = 'ms-state ms-state-' + state.tone;
        el.textContent = state.label;
        el.title = state.hint;
        return el;
    }

    function statusLabel(status) {
        return ({
            watching: 'Watching',
            plan_to_watch: 'Plan to Watch',
            on_hold: 'On Hold',
            completed: 'Completed',
            dropped: 'Dropped',
        })[status] || status;
    }

    /** Wires every .ms-tabs/.ms-panel pair inside root; remembers nothing. */
    function initTabs(root, onSwitch) {
        var tabs = root.querySelectorAll('.ms-tab');
        tabs.forEach(function (tab) {
            tab.addEventListener('click', function () {
                tabs.forEach(function (t) { t.classList.remove('active'); });
                root.querySelectorAll('.ms-panel').forEach(function (p) { p.classList.remove('active'); });
                tab.classList.add('active');
                var panel = el(tab.dataset.tab);
                if (panel) panel.classList.add('active');
                if (onSwitch) onSwitch(tab.dataset.tab);
            });
        });
    }

    /** Wires every .ms-disclosure inside root so its button toggles the body. */
    function initDisclosures(root) {
        root.querySelectorAll('.ms-disclosure-btn').forEach(function (btn) {
            btn.addEventListener('click', function () {
                btn.parentElement.classList.toggle('open');
            });
        });
    }

    /** Widens Jellyfin's narrow content wrapper so the page can use the width. */
    function expandContainer(root) {
        var node = root.parentElement;
        while (node && node !== document.body) {
            node.style.setProperty('max-width', 'none', 'important');
            node.style.setProperty('width', '100%', 'important');
            var tag = node.tagName.toLowerCase();
            if (tag === 'main' || node.classList.contains('scrollY')
                || node.classList.contains('skinBody') || node.dataset.role === 'page') break;
            node = node.parentElement;
        }
    }

    function injectStyles() {
        if (document.getElementById('ms-shared-styles')) return;
        var style = document.createElement('style');
        style.id = 'ms-shared-styles';
        style.textContent = CSS;
        document.head.appendChild(style);
    }

    /**
     * Waits until the page markup is in the DOM, injects the stylesheet and
     * then runs the page's init. `probeId` is an element that only exists once
     * Jellyfin has swapped the view in.
     */
    function boot(probeId, run) {
        injectStyles();
        var tries = 0;
        (function wait() {
            var node = document.getElementById(probeId);
            if (node) {
                var root = node.closest('.ms-root') || node;
                expandContainer(root);
                try { run(root); } catch (e) {
                    console.error('[MAL Sync] page init failed', e); // eslint-disable-line no-console
                }
                return;
            }
            if (++tries > 150) return;
            setTimeout(wait, 40);
        }());
    }

    window.MalSyncUI = {
        boot: boot,
        injectStyles: injectStyles,
        get: apiGet,
        post: apiPost,
        stream: stream,
        clearLog: clearLog,
        appendLog: appendLog,
        renderSummary: renderSummary,
        el: el,
        on: on,
        show: show,
        esc: esc,
        flash: flash,
        ago: ago,
        malUrl: malUrl,
        malLink: malLink,
        malDisplayTitle: malDisplayTitle,
        matchState: matchState,
        stateBadge: stateBadge,
        statusLabel: statusLabel,
        initTabs: initTabs,
        initDisclosures: initDisclosures,
        serverBase: serverBase,
    };
}());
