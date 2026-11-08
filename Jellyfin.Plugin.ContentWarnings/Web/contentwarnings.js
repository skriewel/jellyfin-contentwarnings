/* Content Warnings – community content warnings from DoesTheDogDie.com on movie and series pages.
 * Spoiler-conscious: collapsed shows only how many warnings there are; topic names appear when expanded.
 * Personal choices ("my topics") are stored in the user's display preferences on the server. */
(function () {
    'use strict';

    var cfg = window.ContentWarningsConfig || {};
    if (window.ContentWarnings || cfg.enabled === false) return;

    var PREF_ID = 'contentwarnings';
    var de = /^de\b/i.test(document.documentElement.lang || navigator.language || '');
    var T = de ? {
        title: 'Inhaltswarnungen', seriesTitle: 'Inhaltswarnungen der Serie', none: 'keine gemeldet',
        topics: function (n) { return n === 1 ? '1 Thema' : n + ' Themen'; },
        mine: function (n) { return n === 1 ? '1 deiner Themen' : n + ' deiner Themen'; },
        yes: 'ja', no: 'nein', more: 'Details auf DoesTheDogDie.com', settings: 'Meine Themen',
        onlyMine: 'Nur meine Themen anzeigen', expanded: 'Immer aufgeklappt anzeigen',
        filter: 'Themen filtern …', save: 'Speichern', cancel: 'Abbrechen', loading: 'Lade Themen …',
        hint: 'Markierte Themen werden hervorgehoben und schon im eingeklappten Zustand gezählt.'
    } : {
        title: 'Content warnings', seriesTitle: 'Content warnings for the series', none: 'none reported',
        topics: function (n) { return n === 1 ? '1 topic' : n + ' topics'; },
        mine: function (n) { return n === 1 ? '1 of your topics' : n + ' of your topics'; },
        yes: 'yes', no: 'no', more: 'Details on DoesTheDogDie.com', settings: 'My topics',
        onlyMine: 'Only show my topics', expanded: 'Always show expanded',
        filter: 'Filter topics …', save: 'Save', cancel: 'Cancel', loading: 'Loading topics …',
        hint: 'Selected topics are highlighted and counted even while collapsed.'
    };

    var cache = {};          // itemId -> Promise<data>
    var prefsPromise = null; // Promise<{mine:Set, onlyMine:bool, expanded:bool|null, raw:object}>
    var seq = 0;

    function api() { return window.ApiClient; }

    function esc(s) {
        return String(s == null ? '' : s).replace(/[&<>"']/g, function (c) {
            return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
        });
    }

    function currentItemId() {
        var h = location.hash || '';
        if (!/^#\/?details\b/i.test(h)) return null;
        var m = /[?&]id=([0-9a-f-]{32,36})/i.exec(h);
        return m ? m[1].replace(/-/g, '').toLowerCase() : null;
    }

    function activePage() {
        var pages = document.querySelectorAll('.itemDetailPage');
        for (var i = pages.length - 1; i >= 0; i--) {
            if (!pages[i].classList.contains('hide')) return pages[i];
        }
        return null;
    }

    // ------------------------------------------------------------------ personal preferences

    function prefsUrl() {
        return api().getUrl('DisplayPreferences/' + PREF_ID, { userId: api().getCurrentUserId(), client: 'emby' });
    }

    function loadPrefs() {
        if (!prefsPromise) {
            prefsPromise = api().getJSON(prefsUrl()).catch(function () { return null; }).then(function (raw) {
                var cp = (raw && raw.CustomPrefs) || {};
                return {
                    raw: raw,
                    mine: new Set(String(cp.cwMine || '').split(',').filter(Boolean).map(Number)),
                    onlyMine: cp.cwOnlyMine === 'true',
                    expanded: cp.cwExpanded === 'true' ? true : (cp.cwExpanded === 'false' ? false : null)
                };
            });
        }
        return prefsPromise;
    }

    function savePrefs(p) {
        var raw = p.raw || { Id: PREF_ID, SortBy: 'SortName', SortOrder: 'Ascending', RememberIndexing: false, RememberSorting: false, Client: 'emby', CustomPrefs: {} };
        raw.CustomPrefs = raw.CustomPrefs || {};
        raw.CustomPrefs.cwMine = Array.from(p.mine).join(',');
        raw.CustomPrefs.cwOnlyMine = p.onlyMine ? 'true' : 'false';
        raw.CustomPrefs.cwExpanded = p.expanded === null ? '' : String(p.expanded);
        p.raw = raw;
        return api().ajax({ type: 'POST', url: prefsUrl(), data: JSON.stringify(raw), contentType: 'application/json' });
    }

    // ------------------------------------------------------------------ rendering

    function fetchData(id) {
        if (!cache[id]) {
            cache[id] = api().getJSON(api().getUrl('ContentWarnings/Item/' + id)).catch(function (e) {
                delete cache[id];
                throw e;
            });
        }
        return cache[id];
    }

    function attribution(siteUrl) {
        return '<a class="cw-attrib" href="' + esc(siteUrl || 'https://www.doesthedogdie.com') + '" target="_blank" rel="noopener">Powered by DoesTheDogDie.com</a>';
    }

    function blockHtml(data, prefs) {
        var title = data.scope === 'parentSeries' ? T.seriesTitle : T.title;
        var topics = data.topics || [];
        if (!topics.length) {
            return '<div class="cw-row cw-empty"><span class="material-icons cw-icon" aria-hidden="true">verified_user</span>'
                + '<span class="cw-title">' + esc(title) + ': ' + esc(T.none) + '</span>' + attribution(data.siteUrl) + '</div>';
        }

        var mineCount = topics.filter(function (t) { return prefs.mine.has(t.id); }).length;
        var shown = prefs.onlyMine ? topics.filter(function (t) { return prefs.mine.has(t.id); }) : topics;
        var expanded = prefs.expanded !== null ? prefs.expanded : !!cfg.expandedByDefault;

        var groups = {};
        var order = [];
        shown.forEach(function (t) {
            var g = t.group || t.category || 'Other';
            if (!groups[g]) { groups[g] = []; order.push(g); }
            groups[g].push(t);
        });

        var body = order.map(function (g) {
            return '<div class="cw-group"><div class="cw-group-title">' + esc(g) + '</div><ul>'
                + groups[g].map(function (t) {
                    return '<li class="cw-topic' + (prefs.mine.has(t.id) ? ' cw-is-mine' : '') + '">'
                        + '<span class="cw-name">' + esc(t.name) + '</span>'
                        + (t.severity ? '<span class="cw-sev">' + esc(t.severity) + '</span>' : '')
                        + '<span class="cw-votes">' + t.yes + ' ' + esc(T.yes) + ' · ' + t.no + ' ' + esc(T.no) + '</span>'
                        + '</li>';
                }).join('') + '</ul></div>';
        }).join('');

        return '<details class="cw-details"' + (expanded ? ' open' : '') + '>'
            + '<summary class="cw-row">'
            + '<span class="material-icons cw-icon" aria-hidden="true">warning_amber</span>'
            + '<span class="cw-title">' + esc(title) + '</span>'
            + '<span class="cw-count">' + esc(T.topics(topics.length)) + '</span>'
            + (mineCount ? '<span class="cw-mine">' + esc(T.mine(mineCount)) + '</span>' : '')
            + '<span class="material-icons cw-chevron" aria-hidden="true">expand_more</span>'
            + '</summary>'
            + '<div class="cw-body">' + (body || '<div class="cw-note">—</div>')
            + '<div class="cw-actions">'
            + '<button type="button" class="cw-btn cw-open-settings"><span class="material-icons" aria-hidden="true">tune</span>' + esc(T.settings) + '</button>'
            + (data.dddUrl ? '<a class="cw-btn" href="' + esc(data.dddUrl) + '" target="_blank" rel="noopener"><span class="material-icons" aria-hidden="true">open_in_new</span>' + esc(T.more) + '</a>' : '')
            + '</div><div class="cw-settings hide"></div></div>'
            + '</details>'
            + attribution(data.siteUrl);
    }

    function render(page, id, data, prefs) {
        var host = page.querySelector('.detailPagePrimaryContent .detailSection') || page.querySelector('.detailPagePrimaryContent');
        if (!host) return;
        page.querySelectorAll('.cw-block').forEach(function (n) { n.remove(); });
        var block = document.createElement('div');
        block.className = 'cw-block';
        block.setAttribute('data-cw-item', id);
        block.innerHTML = blockHtml(data, prefs);
        host.appendChild(block);

        var open = block.querySelector('.cw-open-settings');
        if (open) open.addEventListener('click', function () { openSettings(block, page, id, data); });
    }

    // ------------------------------------------------------------------ "my topics" panel

    function openSettings(block, page, id, data) {
        var panel = block.querySelector('.cw-settings');
        if (!panel.classList.contains('hide')) { panel.classList.add('hide'); return; }
        panel.classList.remove('hide');
        panel.innerHTML = '<div class="cw-note">' + esc(T.loading) + '</div>';
        Promise.all([api().getJSON(api().getUrl('ContentWarnings/Topics')), loadPrefs()]).then(function (r) {
            var groups = r[0] || [];
            var prefs = r[1];
            var sel = new Set(prefs.mine);
            panel.innerHTML = '<div class="cw-note">' + esc(T.hint) + '</div>'
                + '<label class="cw-check"><input type="checkbox" class="cw-only-mine"' + (prefs.onlyMine ? ' checked' : '') + '> ' + esc(T.onlyMine) + '</label>'
                + '<label class="cw-check"><input type="checkbox" class="cw-expanded"' + ((prefs.expanded !== null ? prefs.expanded : !!cfg.expandedByDefault) ? ' checked' : '') + '> ' + esc(T.expanded) + '</label>'
                + '<input type="search" class="cw-filter" placeholder="' + esc(T.filter) + '">'
                + '<div class="cw-topic-list">' + groups.map(function (g) {
                    return '<div class="cw-group"><div class="cw-group-title">' + esc(g.group) + '</div>'
                        + g.topics.map(function (t) {
                            return '<label class="cw-check cw-topic-check" data-text="' + esc((t.name + ' ' + t.category).toLowerCase()) + '">'
                                + '<input type="checkbox" value="' + t.id + '"' + (sel.has(t.id) ? ' checked' : '') + '> ' + esc(t.name)
                                + '<span class="cw-cat">' + esc(t.category) + '</span></label>';
                        }).join('') + '</div>';
                }).join('') + '</div>'
                + '<div class="cw-actions"><button type="button" class="cw-btn cw-save">' + esc(T.save) + '</button>'
                + '<button type="button" class="cw-btn cw-cancel">' + esc(T.cancel) + '</button></div>';

            panel.querySelector('.cw-filter').addEventListener('input', function (e) {
                var q = e.target.value.trim().toLowerCase();
                panel.querySelectorAll('.cw-topic-check').forEach(function (l) {
                    l.classList.toggle('hide', !!q && l.getAttribute('data-text').indexOf(q) < 0);
                });
            });
            panel.querySelector('.cw-cancel').addEventListener('click', function () { panel.classList.add('hide'); });
            panel.querySelector('.cw-save').addEventListener('click', function () {
                prefs.mine = new Set(Array.from(panel.querySelectorAll('.cw-topic-check input:checked')).map(function (i) { return Number(i.value); }));
                prefs.onlyMine = panel.querySelector('.cw-only-mine').checked;
                prefs.expanded = panel.querySelector('.cw-expanded').checked;
                savePrefs(prefs).catch(function (e) { console.warn('[ContentWarnings]', e); }).then(function () {
                    render(page, id, data, prefs);
                    var d = page.querySelector('.cw-details');
                    if (d) d.open = true;
                });
            });
        }).catch(function (e) {
            panel.innerHTML = '<div class="cw-note">—</div>';
            console.warn('[ContentWarnings]', e);
        });
    }

    // ------------------------------------------------------------------ page lifecycle

    function waitForPage(mySeq) {
        return new Promise(function (resolve) {
            var tries = 0;
            (function check() {
                if (mySeq !== seq) return resolve(null);
                var page = activePage();
                if (page && page.querySelector('.detailPagePrimaryContent')) return resolve(page);
                if (++tries > 100) return resolve(null);
                setTimeout(check, 100);
            })();
        });
    }

    function update() {
        var mySeq = ++seq;
        var id = currentItemId();
        if (!id || !api() || !api().getCurrentUserId || !api().getCurrentUserId()) return;
        waitForPage(mySeq).then(function (page) {
            if (!page || mySeq !== seq) return;
            var existing = page.querySelector('.cw-block');
            if (existing && existing.getAttribute('data-cw-item') === id) return;
            if (existing) existing.remove();
            Promise.all([fetchData(id), loadPrefs()]).then(function (r) {
                var data = r[0];
                if (mySeq !== seq || !data || (data.status !== 'ok' && data.status !== 'not_found')) return;
                if (data.status === 'not_found') return;
                render(page, id, data, r[1]);
            }).catch(function (e) { console.warn('[ContentWarnings]', e); });
        });
    }

    document.addEventListener('viewshow', function () { setTimeout(update, 0); }, true);
    window.addEventListener('hashchange', function () { setTimeout(update, 0); });
    setInterval(function () { cache = {}; }, 10 * 60 * 1000);
    update();

    window.ContentWarnings = { version: cfg.version, refresh: function () { cache = {}; prefsPromise = null; seq++; document.querySelectorAll('.cw-block').forEach(function (n) { n.remove(); }); update(); } };
})();
