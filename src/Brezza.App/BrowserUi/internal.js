(() => {
    'use strict';

    const protocolVersion = 1;
    const monogramColors = ['#2563eb', '#0d9488', '#16a34a', '#ca8a04', '#ea580c', '#dc2626', '#db2777', '#7c3aed'];
    const page = document.body.dataset.page;
    const $ = id => document.getElementById(id);
    const titleKeys = { newtab: 'app.name', settings: 'settings.title', history: 'history.title', bookmarks: 'bookmarks.title' };
    const searchPresets = [
        { name: 'Bing', template: 'https://www.bing.com/search?q={query}' },
        { name: 'Google', template: 'https://www.google.com/search?q={query}' },
        { name: 'DuckDuckGo', template: 'https://duckduckgo.com/?q={query}' },
        { name: 'Baidu', template: 'https://www.baidu.com/s?wd={query}' },
    ];

    let locale = {};
    let currentLanguage = '';
    let items = [];
    let savedSettings = null;

    const send = (type, args = {}) => window.chrome.webview.postMessage(JSON.stringify({ version: protocolVersion, type, args }));

    function t(key, values) {
        let text = locale[key] || key;
        if (values) for (const name in values) text = text.split('{' + name + '}').join(values[name]);
        return text;
    }

    async function loadLocale(language) {
        const normalized = language === 'zh-Hans' ? 'zh-Hans' : 'en';
        if (normalized === currentLanguage) return;
        try {
            locale = await (await fetch(`locales/${normalized}.json`, { cache: 'no-store' })).json();
        } catch (error) {
            console.error('Locale could not be loaded', normalized, error);
            return;
        }
        currentLanguage = normalized;
        document.documentElement.lang = normalized;
        document.title = t(titleKeys[page]);
        document.querySelectorAll('[data-i18n]').forEach(element => element.textContent = t(element.dataset.i18n));
        document.querySelectorAll('[data-i18n-aria-label]').forEach(element => element.setAttribute('aria-label', t(element.dataset.i18nAriaLabel)));
        document.querySelectorAll('[data-i18n-placeholder]').forEach(element => element.placeholder = t(element.dataset.i18nPlaceholder));
    }

    // ---------- Shared shell ----------

    document.querySelectorAll('[data-open]').forEach(button => {
        button.addEventListener('click', () => {
            if (button.getAttribute('aria-current') !== 'page') send(button.dataset.open);
        });
    });
    document.querySelectorAll('[data-close-page]').forEach(button => {
        button.addEventListener('click', () => send('closeInternalPage'));
    });

    if (page === 'newtab') {
        $('newtab-focus').addEventListener('click', () => send('focusAddress'));
    }

    // ---------- History and bookmarks ----------

    function monogram(url) {
        let host = '';
        try { host = new URL(url).hostname.replace(/^www\./, ''); } catch { host = url; }
        let hash = 0;
        for (const char of host) hash = (hash * 31 + char.charCodeAt(0)) | 0;
        return { letter: (host[0] || '?').toUpperCase(), color: monogramColors[Math.abs(hash) % monogramColors.length] };
    }

    function removeIcon() {
        const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
        svg.setAttribute('viewBox', '0 0 20 20');
        svg.setAttribute('aria-hidden', 'true');
        const path = document.createElementNS('http://www.w3.org/2000/svg', 'path');
        path.setAttribute('d', 'm6 6 8 8m0-8-8 8');
        svg.append(path);
        return svg;
    }

    function renderList() {
        const list = $('history-list');
        const query = $('list-filter').value.trim().toLowerCase();
        const visible = query
            ? items.filter(item => (item.title || '').toLowerCase().includes(query) || item.url.toLowerCase().includes(query))
            : items;

        list.replaceChildren();
        $('list-count').textContent = items.length ? t('list.count', { count: visible.length }) : '';
        if (!visible.length) {
            const empty = document.createElement('li');
            empty.className = 'empty-state';
            empty.textContent = t(items.length ? 'list.noMatches' : list.dataset.empty);
            list.append(empty);
            return;
        }

        for (const item of visible) {
            const row = document.createElement('li');
            row.className = 'item';

            const open = document.createElement('button');
            open.className = 'item-open history-item';
            open.type = 'button';
            open.title = item.url;
            const mark = monogram(item.url);
            const icon = document.createElement('span');
            icon.className = 'item-icon';
            icon.setAttribute('aria-hidden', 'true');
            icon.style.setProperty('--monogram', mark.color);
            icon.textContent = mark.letter;
            const text = document.createElement('span');
            text.className = 'item-text';
            const title = document.createElement('span');
            title.className = 'item-title';
            title.textContent = item.title || item.url;
            const url = document.createElement('span');
            url.className = 'item-url';
            url.textContent = item.url;
            text.append(title, url);
            open.append(icon, text);
            open.addEventListener('click', () => send('navigate', { text: item.url }));
            row.append(open);

            if (list.hasAttribute('data-removable')) {
                const remove = document.createElement('button');
                remove.className = 'item-remove';
                remove.type = 'button';
                remove.title = t('bookmarks.remove');
                remove.setAttribute('aria-label', `${t('bookmarks.remove')}: ${item.title || item.url}`);
                remove.append(removeIcon());
                remove.addEventListener('click', () => send('removeBookmark', { url: item.url }));
                row.append(remove);
            }
            list.append(row);
        }
    }

    if (page === 'history' || page === 'bookmarks') {
        $('list-filter').addEventListener('input', renderList);
        $('list-filter').addEventListener('keydown', event => {
            if (event.key === 'Escape' && event.target.value) {
                event.target.value = '';
                renderList();
            }
        });
    }

    // ---------- Settings ----------

    function renderPresets() {
        const container = $('search-presets');
        container.replaceChildren();
        for (const option of [...searchPresets, { name: t('settings.custom'), template: '' }]) {
            const label = document.createElement('label');
            label.className = 'choice';
            const input = document.createElement('input');
            input.type = 'radio';
            input.name = 'search-preset';
            input.value = option.template;
            input.addEventListener('change', onPresetChanged);
            label.append(input, option.name);
            container.append(label);
        }
    }

    function selectPreset(template) {
        const preset = searchPresets.find(item => item.template === template);
        const value = preset ? preset.template : '';
        document.querySelectorAll('input[name="search-preset"]').forEach(input => input.checked = input.value === value);
        $('custom-template').hidden = !!preset;
    }

    function onPresetChanged(event) {
        const custom = !event.target.value;
        $('custom-template').hidden = !custom;
        if (custom) {
            $('search-template').focus();
            $('search-template').select();
        } else {
            $('search-template').value = event.target.value;
        }
        updateDirtyState();
    }

    function currentSettings() {
        return { searchTemplate: $('search-template').value.trim(), language: $('language').value };
    }

    function updateDirtyState() {
        const current = currentSettings();
        const dirty = !!savedSettings &&
            (current.searchTemplate !== savedSettings.searchTemplate || current.language !== savedSettings.language);
        $('save-settings').disabled = !dirty;
        $('reset-settings').disabled = !dirty;
        $('saved').textContent = dirty ? t('settings.unsaved') : '';
        $('saved').className = dirty ? '' : 'status-saved';
        if (dirty) {
            $('error').textContent = '';
            $('search-template').removeAttribute('aria-invalid');
        }
    }

    function renderSettings(values) {
        savedSettings = { searchTemplate: values.searchTemplate || '', language: values.language || '' };
        renderPresets();
        $('search-template').value = savedSettings.searchTemplate;
        $('language').value = savedSettings.language;
        selectPreset(savedSettings.searchTemplate);
        updateDirtyState();
    }

    if (page === 'settings') {
        $('search-template').addEventListener('input', updateDirtyState);
        $('language').addEventListener('change', updateDirtyState);
        $('reset-settings').addEventListener('click', () => renderSettings(savedSettings));
        $('settings-form').addEventListener('submit', event => {
            event.preventDefault();
            $('error').textContent = '';
            send('saveSettings', currentSettings());
        });
    }

    // ---------- Host messages ----------

    window.chrome.webview.addEventListener('message', async event => {
        const message = typeof event.data === 'string' ? JSON.parse(event.data) : event.data;
        if (!message || message.version !== protocolVersion) return;
        if (message.type === 'internalState') {
            if (message.page !== page) return;
            document.body.classList.toggle('private', !!message.isPrivate);
            await loadLocale(message.language);
            if (page === 'newtab') {
                const privateMode = !!message.isPrivate;
                $('newtab-title').textContent = t(privateMode ? 'start.privateTitle' : 'app.name');
                $('newtab-subtitle').textContent = t(privateMode ? 'start.privateBody' : 'start.subtitle');
                $('newtab-mark-icon').setAttribute('href', privateMode ? '#i-private' : '#i-brezza');
            } else if (page === 'settings') {
                renderSettings({ searchTemplate: message.searchTemplate, language: message.configuredLanguage || '' });
            } else {
                items = message.items || [];
                renderList();
            }
        } else if (message.type === 'settingsSaved') {
            await loadLocale(message.language);
            renderSettings(currentSettings());
            $('saved').textContent = t('settings.saved');
        } else if (message.type === 'settingsError') {
            $('error').textContent = t('settings.invalid');
            $('search-template').setAttribute('aria-invalid', 'true');
            selectPreset('');
            $('search-template').focus();
        }
    });

    send('internalReady', { page });
})();
