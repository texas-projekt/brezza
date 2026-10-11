(() => {
    'use strict';

    const protocolVersion = 1;
    const monogramColors = ['#2563eb', '#0d9488', '#16a34a', '#ca8a04', '#ea580c', '#dc2626', '#db2777', '#7c3aed'];
    const $ = id => document.getElementById(id);

    const chromeBar = $('chrome-bar');
    const tabsElement = $('tabs');
    const form = $('address-form');
    const address = $('OmniBox');
    const addressField = address.parentElement;
    const addressDisplay = $('omnibox-display');
    const siteIndicator = $('site-indicator');
    const notice = $('notice');

    let state = { tabs: [], activeTabId: null, isPrivate: false, canGoBack: false, canGoForward: false, bookmarked: false };
    let language = '';
    let locale = {};
    let localeLoadSequence = 0;

    // Address editing: while dirty, host state updates must not overwrite what the user is typing.
    let lastActiveTabId = null;
    let lastActiveUrl = null;
    let addressDirty = false;
    let selectOnMouseUp = false;

    let lastLayout = '';
    let lastNotice = null;

    function send(type, args = {}) {
        window.chrome.webview.postMessage(JSON.stringify({ version: protocolVersion, type, args }));
    }

    function t(key, values) {
        let text = locale[key] || key;
        if (values) for (const name in values) text = text.split('{' + name + '}').join(values[name]);
        return text;
    }

    function svgIcon(name, className = 'icon') {
        const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
        svg.setAttribute('class', className);
        svg.setAttribute('aria-hidden', 'true');
        const use = document.createElementNS('http://www.w3.org/2000/svg', 'use');
        use.setAttribute('href', '#i-' + name);
        svg.append(use);
        return svg;
    }

    // ---------- Locale ----------

    async function loadLocale(nextLanguage) {
        const normalized = nextLanguage === 'zh-Hans' ? 'zh-Hans' : 'en';
        const sequence = ++localeLoadSequence;
        let loaded;
        try {
            loaded = await (await fetch(`locales/${normalized}.json`, { cache: 'no-store' })).json();
        } catch (error) {
            console.error('Locale could not be loaded', normalized, error);
            if (normalized === 'en') return;
            loaded = await (await fetch('locales/en.json', { cache: 'no-store' })).json();
        }
        if (sequence !== localeLoadSequence) return;

        locale = loaded;
        language = normalized;
        document.documentElement.lang = normalized;
        document.querySelectorAll('[data-i18n]').forEach(element => {
            element.textContent = t(element.dataset.i18n);
        });
        document.querySelectorAll('[data-i18n-aria-label]').forEach(element => {
            element.setAttribute('aria-label', t(element.dataset.i18nAriaLabel));
        });
        document.querySelectorAll('[data-i18n-title]').forEach(element => {
            element.title = t(element.dataset.i18nTitle);
        });
        document.querySelectorAll('[data-i18n-placeholder]').forEach(element => {
            element.placeholder = t(element.dataset.i18nPlaceholder);
        });
        render();
        if (lastNotice) showNotice(lastNotice);
    }

    // ---------- URL presentation ----------

    function describeUrl(url) {
        if (!url) return { kind: 'empty' };
        let parsed;
        try {
            parsed = new URL(url);
        } catch {
            return { kind: 'other' };
        }
        const scheme = parsed.protocol.replace(/:$/, '').toLowerCase();
        if (scheme === 'brezza') {
            const page = parsed.hostname.toLowerCase();
            return page === 'newtab' ? { kind: 'empty', parsed, page } : { kind: 'internal', parsed, page };
        }
        if (scheme === 'https') return { kind: 'secure', parsed };
        if (scheme === 'http') return { kind: 'insecure', parsed };
        if (scheme === 'file') return { kind: 'file', parsed };
        return { kind: 'other', parsed };
    }

    const indicatorIcons = { empty: 'search', internal: 'brezza', secure: 'lock', insecure: 'warning', file: 'file', other: 'globe' };
    const indicatorLabels = {
        empty: 'security.search', internal: 'security.internal', secure: 'security.secure',
        insecure: 'security.insecure', file: 'security.file', other: 'security.other',
    };

    function renderAddressDisplay(url) {
        const info = describeUrl(url);
        addressDisplay.replaceChildren();
        if (!info.parsed || info.kind === 'empty' || info.kind === 'file' || info.kind === 'other') {
            addressDisplay.textContent = info.kind === 'empty' ? '' : url;
            return;
        }

        const { parsed } = info;
        const prefix = info.kind === 'secure' ? '' : parsed.protocol + '//';
        const rest = url.slice(url.indexOf(parsed.host) + parsed.host.length);
        const host = document.createElement('span');
        host.className = 'host';
        host.textContent = parsed.host;
        addressDisplay.append(prefix, host, rest === '/' ? '' : rest);
    }

    function monogramFor(url) {
        const host = describeUrl(url).parsed?.hostname.replace(/^www\./, '') || '';
        let hash = 0;
        for (const char of host) hash = (hash * 31 + char.charCodeAt(0)) | 0;
        return { letter: (host[0] || '?').toUpperCase(), color: monogramColors[Math.abs(hash) % monogramColors.length] };
    }

    function tabTitle(tab) {
        if (describeUrl(tab.url).page === 'newtab') return t(tab.isPrivate ? 'tab.newPrivate' : 'tab.new');
        if (!tab.url) return t(tab.isPrivate ? 'tab.newPrivate' : 'tab.new');
        if (tab.title && tab.title !== 'New Tab') return tab.title;
        return describeUrl(tab.url).parsed?.host || tab.url;
    }

    // ---------- Tabs ----------

    function renderTabIcon(container, tab) {
        const info = describeUrl(tab.url);
        let glyph = null;
        if (tab.isPrivate) glyph = 'private';
        else if (info.kind === 'empty') glyph = 'brezza';
        else if (info.kind === 'internal') glyph = { settings: 'settings', history: 'history', bookmarks: 'bookmarks' }[info.page] || 'brezza';
        else if (info.kind === 'file') glyph = 'file';

        const key = glyph ? 'g:' + glyph : 'm:' + tab.url;
        if (container.dataset.key === key) return;
        container.dataset.key = key;
        container.replaceChildren();
        container.classList.toggle('glyph', !!glyph);
        if (glyph) {
            container.style.removeProperty('--monogram');
            container.append(svgIcon(glyph));
        } else {
            const monogram = monogramFor(tab.url);
            container.style.setProperty('--monogram', monogram.color);
            container.textContent = monogram.letter;
        }
    }

    const tabSizeObserver = new ResizeObserver(entries => {
        for (const entry of entries) {
            const width = entry.contentRect.width;
            entry.target.classList.toggle('narrow', width < 96);
            entry.target.classList.toggle('tiny', width < 64);
        }
    });

    function createTabElement(tab) {
        const element = document.createElement('div');
        element.className = 'tab';
        element.role = 'tab';
        element.id = 'TabSelect_' + tab.id;
        element.dataset.tabId = tab.id;

        const icon = document.createElement('span');
        icon.className = 'tab-icon';
        icon.setAttribute('aria-hidden', 'true');

        const title = document.createElement('span');
        title.className = 'tab-title';

        const close = document.createElement('button');
        close.className = 'tab-close';
        close.type = 'button';
        close.tabIndex = -1;
        close.id = 'TabClose_' + tab.id;
        close.append(svgIcon('close'));
        close.addEventListener('click', event => {
            event.stopPropagation();
            send('closeTab', { tabId: element.dataset.tabId });
        });

        element.append(icon, title, close);
        element.addEventListener('mousedown', event => {
            // Middle click closes; it must not start autoscroll.
            if (event.button === 1) event.preventDefault();
        });
        element.addEventListener('auxclick', event => {
            if (event.button === 1) send('closeTab', { tabId: element.dataset.tabId });
        });
        element.addEventListener('click', () => {
            if (element.dataset.tabId !== state.activeTabId) send('activateTab', { tabId: element.dataset.tabId });
        });
        element.addEventListener('keydown', onTabKeyDown);
        tabSizeObserver.observe(element);
        return element;
    }

    function onTabKeyDown(event) {
        const tabId = event.currentTarget.dataset.tabId;
        const index = state.tabs.findIndex(item => item.id === tabId);
        let target = null;
        if (event.key === 'ArrowLeft' || event.key === 'ArrowRight') {
            const delta = event.key === 'ArrowRight' ? 1 : -1;
            target = state.tabs[(index + delta + state.tabs.length) % state.tabs.length];
        } else if (event.key === 'Home') {
            target = state.tabs[0];
        } else if (event.key === 'End') {
            target = state.tabs[state.tabs.length - 1];
        } else if (event.key === 'Enter' || event.key === ' ') {
            event.preventDefault();
            send('activateTab', { tabId });
            return;
        } else if (event.key === 'Delete') {
            event.preventDefault();
            send('closeTab', { tabId });
            return;
        }

        if (!target) return;
        event.preventDefault();
        send('activateTab', { tabId: target.id });
        $('TabSelect_' + target.id)?.focus();
    }

    function renderTabs() {
        const existing = new Map([...tabsElement.children].map(element => [element.dataset.tabId, element]));
        const tabHadFocus = tabsElement.contains(document.activeElement);
        let previous = null;
        for (const tab of state.tabs) {
            let element = existing.get(tab.id);
            if (element) existing.delete(tab.id);
            else element = createTabElement(tab);

            const active = tab.id === state.activeTabId;
            const title = tabTitle(tab);
            element.classList.toggle('private-tab', !!tab.isPrivate);
            element.setAttribute('aria-selected', String(active));
            element.tabIndex = active ? 0 : -1;
            element.setAttribute('aria-label', tab.isPrivate ? t('tab.privateLabel', { title }) : title);
            element.title = tab.url && !/^brezza:/i.test(tab.url) ? `${title}\n${tab.url}` : title;
            element.querySelector('.tab-title').textContent = title;
            const close = element.querySelector('.tab-close');
            close.setAttribute('aria-label', t('tab.close'));
            close.title = t('tab.closeShortcut');
            renderTabIcon(element.querySelector('.tab-icon'), tab);

            const expectedPosition = previous ? previous.nextElementSibling : tabsElement.firstElementChild;
            if (expectedPosition !== element) tabsElement.insertBefore(element, expectedPosition);
            previous = element;
        }
        for (const removed of existing.values()) {
            tabSizeObserver.unobserve(removed);
            removed.remove();
        }

        const activeElement = $('TabSelect_' + state.activeTabId);
        activeElement?.scrollIntoView({ block: 'nearest', inline: 'nearest' });
        if (tabHadFocus && !tabsElement.contains(document.activeElement)) activeElement?.focus();
    }

    tabsElement.addEventListener('wheel', event => {
        if (event.deltaY === 0) return;
        event.preventDefault();
        tabsElement.scrollLeft += event.deltaY;
    }, { passive: false });

    // ---------- State ----------

    function activeTab() {
        return state.tabs.find(tab => tab.id === state.activeTabId) || null;
    }

    function render() {
        const active = activeTab();
        const url = active?.url || '';
        const info = describeUrl(url);
        document.body.classList.toggle('private', !!state.isPrivate);
        document.title = active ? `${tabTitle(active)} - ${t('app.name')}` : t('app.name');
        renderTabs();

        $('back').disabled = !state.canGoBack;
        $('forward').disabled = !state.canGoForward;
        $('reload').disabled = !url || info.kind === 'empty';

        const bookmark = $('bookmark');
        const bookmarkLabel = t(state.bookmarked ? 'toolbar.removeBookmark' : 'toolbar.bookmark');
        bookmark.hidden = info.kind !== 'secure' && info.kind !== 'insecure';
        bookmark.setAttribute('aria-pressed', String(!!state.bookmarked));
        bookmark.setAttribute('aria-label', bookmarkLabel);
        bookmark.title = bookmarkLabel + ' (Ctrl+D)';

        siteIndicator.dataset.kind = info.kind;
        siteIndicator.querySelector('use').setAttribute('href', '#i-' + indicatorIcons[info.kind]);
        siteIndicator.setAttribute('aria-label', t(indicatorLabels[info.kind]));
        siteIndicator.title = t(indicatorLabels[info.kind]);

        syncAddress(active);
    }

    function syncAddress(active) {
        const isNewTab = active && describeUrl(active.url).page === 'newtab';
        const url = isNewTab ? '' : active?.url || '';
        const tabChanged = state.activeTabId !== lastActiveTabId;
        const urlChanged = url !== lastActiveUrl;
        lastActiveTabId = state.activeTabId;
        lastActiveUrl = url;

        if (tabChanged) {
            addressDirty = false;
            hideNotice();
        } else if (urlChanged) {
            hideNotice();
        }

        if (!addressDirty) {
            address.value = url;
            renderAddressDisplay(url);
        }
        updateAddressPresentation();
        if (tabChanged && isNewTab) focusAddress();
    }

    function updateAddressPresentation() {
        const editing = document.activeElement === address && document.hasFocus();
        addressField.classList.toggle('formatted', !editing && !addressDirty && !!address.value);
    }

    // ---------- Address input ----------

    function focusAddress() {
        address.focus();
        address.select();
    }

    function revertAddress() {
        addressDirty = false;
        const active = activeTab();
        address.value = active && describeUrl(active.url).page === 'newtab' ? '' : active?.url || '';
        renderAddressDisplay(address.value);
        address.select();
    }

    function submitAddress() {
        const text = address.value.trim();
        if (text) {
            finishEditing();
            send('navigate', { text });
        }
    }

    function finishEditing() {
        addressDirty = false;
        address.blur();
    }

    form.addEventListener('submit', event => {
        event.preventDefault();
        submitAddress();
    });

    address.addEventListener('focus', () => {
        updateAddressPresentation();
        if (!addressDirty) {
            address.select();
            selectOnMouseUp = true;
        }
    });

    address.addEventListener('mouseup', event => {
        // Keep the select-all from focusing with the mouse; later clicks place the caret normally.
        if (selectOnMouseUp && address.selectionStart === address.selectionEnd) event.preventDefault();
        selectOnMouseUp = false;
    });

    address.addEventListener('blur', () => {
        // The window losing focus also blurs the input; only a real focus move ends editing.
        if (document.hasFocus()) {
            if (addressDirty && !address.value.trim()) revertAddress();
        }
        updateAddressPresentation();
    });

    address.addEventListener('input', () => {
        addressDirty = true;
        updateAddressPresentation();
    });

    address.addEventListener('keydown', event => {
        if (event.key === 'Escape') {
            event.preventDefault();
            if (addressDirty) {
                revertAddress();
            } else {
                address.blur();
            }
        }
    });

    // ---------- Host layout ----------

    function reportLayout() {
        const height = Math.ceil(chromeBar.getBoundingClientRect().height);
        const key = String(height);
        if (key === lastLayout) return;
        lastLayout = key;
        send('layout', { height });
    }

    new ResizeObserver(reportLayout).observe(chromeBar);

    // ---------- Notice ----------

    function showNotice(message) {
        lastNotice = message;
        const text = message.key ? t(message.key) + (message.detail ? ` (${message.detail})` : '') : message.text || '';
        if (!text) {
            hideNotice();
            return;
        }
        $('notice-text').textContent = text;
        const action = $('notice-action');
        const canReload = message.key === 'notice.pageStopped' || message.key === 'notice.navigationFailed';
        action.hidden = !canReload;
        action.textContent = t('notice.reload');
        notice.hidden = false;
    }

    function hideNotice() {
        lastNotice = null;
        notice.hidden = true;
    }

    $('notice-dismiss').addEventListener('click', hideNotice);
    $('notice-action').addEventListener('click', () => {
        hideNotice();
        send('reload');
    });

    // ---------- Commands ----------

    function openInternalPage(command) {
        finishEditing();
        send(command);
    }

    $('NewTabButton').addEventListener('click', () => send('newTab'));
    $('NewPrivateTabButton').addEventListener('click', () => send('newPrivateTab'));
    $('back').addEventListener('click', () => send('back'));
    $('forward').addEventListener('click', () => send('forward'));
    $('reload').addEventListener('click', () => send('reload'));
    $('bookmark').addEventListener('click', () => send('toggleBookmark'));
    $('bookmarks').addEventListener('click', () => openInternalPage('openBookmarks'));
    $('history').addEventListener('click', () => openInternalPage('openHistory'));
    $('settings').addEventListener('click', () => openInternalPage('openSettings'));
    // Double-clicking empty tab strip space opens a tab, as in other Windows browsers.
    document.querySelector('.tabstrip').addEventListener('dblclick', event => {
        if (event.target === event.currentTarget || event.target === tabsElement) send('newTab');
    });

    function cycleTab(delta) {
        const index = state.tabs.findIndex(tab => tab.id === state.activeTabId);
        const next = state.tabs[(index + delta + state.tabs.length) % state.tabs.length];
        if (next && next.id !== state.activeTabId) send('activateTab', { tabId: next.id });
    }

    document.addEventListener('keydown', event => {
        const key = event.key.toLowerCase();
        let handled = true;
        if (event.ctrlKey && !event.shiftKey && (key === 'l' || key === 'e')) focusAddress();
        else if (event.ctrlKey && !event.shiftKey && key === 't') send('newTab');
        else if (event.ctrlKey && event.shiftKey && key === 'n') send('newPrivateTab');
        else if (event.ctrlKey && key === 'w') { if (state.activeTabId) send('closeTab', { tabId: state.activeTabId }); }
        else if (event.ctrlKey && key === 'tab') cycleTab(event.shiftKey ? -1 : 1);
        else if (event.ctrlKey && /^[1-8]$/.test(key)) { const tab = state.tabs[Number(key) - 1]; if (tab) send('activateTab', { tabId: tab.id }); }
        else if (event.ctrlKey && key === '9') { const tab = state.tabs[state.tabs.length - 1]; if (tab) send('activateTab', { tabId: tab.id }); }
        else if (event.ctrlKey && key === 'd') send('toggleBookmark');
        else if (event.ctrlKey && key === 'h') openInternalPage('openHistory');
        else if ((event.ctrlKey && key === 'r') || key === 'f5') send('reload');
        else if (event.altKey && key === 'arrowleft') send('back');
        else if (event.altKey && key === 'arrowright') send('forward');
        else handled = false;
        if (handled) event.preventDefault();
    });

    window.addEventListener('focus', updateAddressPresentation);
    window.addEventListener('blur', updateAddressPresentation);

    // ---------- Host messages ----------

    window.chrome.webview.addEventListener('message', event => {
        const message = typeof event.data === 'string' ? JSON.parse(event.data) : event.data;
        if (!message || message.version !== protocolVersion) return;
        if (message.type === 'state') {
            state = message.state;
            if (state.language && state.language !== language) void loadLocale(state.language);
            render();
        } else if (message.type === 'focusAddress') {
            focusAddress();
        } else if (message.type === 'notice') {
            showNotice(message);
        }
    });

    void loadLocale('en');
    reportLayout();
    send('ready');
})();
