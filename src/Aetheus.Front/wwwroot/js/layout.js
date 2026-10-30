window.Aetheus = window.Aetheus || {};

Aetheus.goBack = function () {
    window.history.back();
};

// Recette R3-29: a page opened on a pasted or typed address gets its logical parent (computed by
// LogicalParentRoute in .NET) as the history entry just before it, so the browser's Back stays in the
// document and the Blazor router shows the parent without a reload. Once per document, and only when
// the document was opened on this very entry: not on a reload or a return through the history, not
// once the application has moved (Blazor numbers its entries in history.state._index), not after a
// full reload the application asked for from one of its own pages. The parent takes index -1 and the
// page keeps index 0, the number Blazor read at start, so its own numbering stays consistent.
Aetheus._isFirstEntryOfFreshDocument = function (state) {
    const entry = performance.getEntriesByType('navigation')[0];
    if (!entry || entry.type !== 'navigate') return false;
    if (state && (state.aetheusParentEntry || (state._index ?? 0) !== 0)) return false;
    return !(history.length > 1 && document.referrer
        && new URL(document.referrer).origin === location.origin);
};

Aetheus.seedParentHistoryEntry = function (parentHref) {
    if (Aetheus._parentEntrySeedAttempted) return false;
    Aetheus._parentEntrySeedAttempted = true;
    const here = location.href;
    const state = history.state;
    try {
        if (!Aetheus._isFirstEntryOfFreshDocument(state)) return false;
        const parent = new URL(parentHref, document.baseURI);
        if (parent.origin !== location.origin || parent.href === here) return false;
        history.replaceState({ _index: -1, aetheusParentEntry: true }, '', parent.href);
        history.pushState({ userState: state?.userState, _index: 0, aetheusParentEntry: true }, '', here);
        return true;
    } catch {
        // The history is put back as the browser made it: Back behaves as before.
        if (location.href !== here) history.replaceState(state, '', here);
        return false;
    }
};

// Shares rotated token values with other browser tabs through a single storage-event listener
// restricted to the two token keys.
Aetheus.watchAuthStorage = function (dotnetRef, tokenKey, refreshTokenKey) {
    Aetheus._authSession = dotnetRef;
    if (Aetheus._authStorageWatched) return;
    Aetheus._authStorageWatched = true;
    window.addEventListener('storage', function (event) {
        if (event.key !== tokenKey && event.key !== refreshTokenKey) return;
        dotnetRef.invokeMethodAsync('OnStorageChanged', event.key, event.newValue);
    });
};

// Web analytics: whether the visitor is signed in, as a yes/no only. The analytics bootstrap asks at
// send time; the app answers from memory (a synchronous call Blazor WebAssembly supports). No token
// and no account ever reach the analytics module.
Aetheus.analyticsSignedIn = function () {
    try {
        return Aetheus._authSession ? Aetheus._authSession.invokeMethod('IsSignedInForAnalytics') === true : false;
    } catch {
        return false;
    }
};

// Returns the signed-in visitor's opaque aetheus:avid keyed hash from memory, never a token,
// account name or email address. Returns undefined when signed out.
Aetheus.analyticsVisitor = function () {
    try {
        const id = Aetheus._authSession ? Aetheus._authSession.invokeMethod('VisitorForAnalytics') : null;
        return typeof id === 'string' && id.length > 0 ? id : undefined;
    } catch {
        return undefined;
    }
};

// The application says its session is known, or that the visitor changed (sign-in, sign-out). The
// measurement, once loaded, leaves its handler here; until then the call is a no-op.
Aetheus.analyticsIdentityChanged = function () {
    Aetheus._analyticsSessionKnown = true;
    if (typeof Aetheus._onAnalyticsIdentity === 'function') Aetheus._onAnalyticsIdentity();
};

Aetheus.hideSplash = function () {
    // Fade out and remove the single boot splash (#app-splash, declared in index.html).
    // Idempotent: a missing element (already removed) is a no-op.
    const el = document.getElementById('app-splash');
    if (!el) return;
    el.classList.add('app-splash--hidden');
    const remove = function () {
        // Removing the splash lets omni-boot.js hand the look back to OmniThemeScope (data-theme-hold).
        if (el.parentNode) el.parentNode.removeChild(el);
    };
    el.addEventListener('transitionend', remove, { once: true });
    // Fallback if transitionend never fires (reduced-motion, display quirks, interrupted transition).
    setTimeout(remove, 600);
};

Aetheus.setLang = function (lang) {
    document.documentElement.lang = lang.substring(0, 2);
};

// HBSY: phase-align the heartbeat ring's CSS fill loop with the real age of the last beat.
// Setting a negative animation-delay makes the loop start mid-cycle instead of from empty,
// so a freshly-loaded page shows the ring already part-filled to match the true elapsed time
// rather than resyncing only at the next beat. `secondsIntoCadence` is the real elapsed time
// modulo the cadence (computed component-side). A no-op when the element is gone.
Aetheus.syncHeartbeatPhase = function (el, secondsIntoCadence) {
    if (!el) return;
    el.style.animationDelay = '-' + secondsIntoCadence + 's';
};

Aetheus.lockTitle = function (title) {
    // Set the initial document title once. We intentionally do NOT install
    // an interval that overrides the title - Blazor's <PageTitle> updates
    // document.title per page, and a periodic re-write would clobber it
    // (e.g. showing "Aetheus" on the login screen instead of "Login").
    if (!document.title || document.title === 'Aetheus') {
        document.title = title;
    }
    if (window._aetheusTitleInterval) {
        clearInterval(window._aetheusTitleInterval);
        window._aetheusTitleInterval = null;
    }
};

// Prefixes every page title with a short tag (e.g. the dev-banner label "P4") so worktree
// tabs are distinguishable at a glance: "P4 <original title>". Blazor's <PageTitle> rewrites
// document.title on each navigation, so we observe the <title> element and re-apply the prefix
// after each change. No prefix is applied when the tag is empty (e.g. production) - the title
// stays "<original title>".
Aetheus.setTitlePrefix = function (prefix) {
    const pfx = (prefix || '').trim();
    if (!pfx) return;

    const apply = function () {
        const current = document.title || '';
        if (!current.startsWith(pfx + ' ')) {
            // Re-assigning document.title triggers the observer once more, but the guard above
            // makes that pass a no-op, so there is no infinite loop.
            document.title = pfx + ' ' + current;
        }
    };

    apply();

    // Observe <head> (it persists) rather than the <title> element directly: Blazor's HeadOutlet
    // can REPLACE the title node on navigation, which would silently detach an observer bound to
    // the old node. Watching the head subtree catches both text edits and node swaps.
    if (window._aetheusTitlePrefixObserver) {
        window._aetheusTitlePrefixObserver.disconnect();
    }
    const observer = new MutationObserver(apply);
    observer.observe(document.head, { childList: true, subtree: true, characterData: true });
    window._aetheusTitlePrefixObserver = observer;
};

Aetheus.copyToClipboard = async function (text) {
    try {
        await navigator.clipboard.writeText(text);
        return true;
    } catch {
        return false;
    }
};

// Captures clicks on busy controls before Blazor receives them to prevent duplicate keyboard
// submissions while preserving the busy appearance (STD-BUSY).
window.addEventListener('click', function (e) {
    if (e.target instanceof Element && e.target.closest('.btn-busy')) {
        e.preventDefault();
        e.stopImmediatePropagation();
    }
}, true);

// Detects clipped text so the toast offers Show more only when needed.
Aetheus.isClamped = function (el) {
    return !!el && el.scrollHeight > el.clientHeight + 1;
};

// Scroll a log terminal element to its bottom / top. Used by the pipeline run log viewer for
// auto-follow and the "jump to top/bottom" controls. Works with a virtualized child list because
// the virtualization spacer makes scrollHeight reflect the full (virtual) content height.
Aetheus.scrollToBottom = function (el) {
    if (!el) { return; }
    // Scroll once, then again on the next two frames: the virtualized list renders more rows as the
    // scroll position moves, growing scrollHeight, so a single assignment lands short of the true tail.
    el.scrollTop = el.scrollHeight;
    requestAnimationFrame(function () {
        el.scrollTop = el.scrollHeight;
        requestAnimationFrame(function () { el.scrollTop = el.scrollHeight; });
    });
};
// Brings the element with this id into view (R-40: the run strip leads down to the detailed timeline).
Aetheus.scrollToId = function (id) {
    var el = document.getElementById(id);
    if (el) { el.scrollIntoView({ behavior: 'smooth', block: 'start' }); }
};
// Moves the focus to the field with this id (R-24: the comment field of the approval page opened by the mail's Comment link).
Aetheus.focusById = function (id) {
    var el = document.getElementById(id);
    if (el) { el.focus(); }
};
Aetheus.scrollToTop = function (el) {
    if (el) { el.scrollTop = 0; }
};

// Tells the run page when the reader scrolls a log terminal by hand: away from the tail (so a live
// run stops dragging them back to the newest line) or back to it (following resumes). Only scrolls
// that follow a gesture of the reader count; the page's own scrollToBottom never does.
Aetheus.watchLogFollow = function (el, dotnet) {
    if (!el || el.__aetheusLogFollow) { return; }
    var gestureUntil = 0;
    var reported = true;
    var gesture = function () { gestureUntil = Date.now() + 600; };
    ['wheel', 'touchstart', 'touchmove', 'pointerdown', 'keydown'].forEach(function (type) {
        el.addEventListener(type, gesture, { passive: true });
    });
    el.addEventListener('scroll', function () {
        if (Date.now() > gestureUntil) { return; }
        var atBottom = el.scrollHeight - el.scrollTop - el.clientHeight < 24;
        if (atBottom === reported) { return; }
        reported = atBottom;
        dotnet.invokeMethodAsync('OnUserScrolled', atBottom);
    }, { passive: true });
    el.__aetheusLogFollow = true;
};

Aetheus.initFormShortcuts = function () {
    document.addEventListener('keydown', function (e) {
        if ((e.ctrlKey || e.metaKey) && e.key === 'Enter') {
            // Prefer dialog forms (modal) over page forms
            var form = document.querySelector('.omni-dialog form') || document.querySelector('form');
            if (form) {
                var submitBtn = form.querySelector('button[type="submit"]');
                if (submitBtn && !submitBtn.disabled) {
                    e.preventDefault();
                    submitBtn.click();
                }
            }
        }
    });
};


// Lightweight localStorage helpers used by ActiveOrganizationService and any other client
// code that wants to persist a small string scalar across reloads. We swallow exceptions
// because Safari private mode and some embedded contexts throw on access.
Aetheus.getLocal = function (key) {
    try { return localStorage.getItem(key); } catch { return null; }
};

Aetheus.setLocal = function (key, value) {
    try {
        if (value === null || value === undefined) localStorage.removeItem(key);
        else localStorage.setItem(key, value);
    } catch { /* ignore */ }
};

// R1-66: the keys of this browser that start with one of the prefixes, with their values, for the
// one-time copy of the preferences kept here before they moved to the server.
Aetheus.localEntries = function (prefixes) {
    const entries = {};
    try {
        for (let i = 0; i < localStorage.length; i++) {
            const key = localStorage.key(i);
            if (key && prefixes.some(prefix => key.startsWith(prefix))) entries[key] = localStorage.getItem(key);
        }
    } catch { /* blocked storage: nothing to copy */ }
    return entries;
};

// Compact drawer viewport watcher (Astraia parity). A matchMedia("(max-width: 63.99rem)") listener
// pushes the mobile/desktop boolean into MainLayout via [JSInvokable] OnViewportChanged, so the
// sidebar collapses to an overlay on phones/tablets and restores the in-flow rail on wider screens.
Aetheus._viewportMql = null;
Aetheus._viewportRef = null;
Aetheus._onViewportChange = function (e) {
    if (Aetheus._viewportRef) {
        Aetheus._viewportRef.invokeMethodAsync('OnViewportChanged', e.matches);
    }
};
Aetheus.watchViewport = function (dotNetRef) {
    Aetheus._viewportRef = dotNetRef;
    Aetheus._viewportMql = window.matchMedia('(max-width: 63.99rem)');
    Aetheus._viewportMql.addEventListener('change', Aetheus._onViewportChange);
    // Return the promise: MainLayout keeps the opaque splash mounted until this initial state has
    // been applied and rendered, eliminating the cold-load drawer/backdrop flash.
    return dotNetRef.invokeMethodAsync('OnViewportChanged', Aetheus._viewportMql.matches);
};
Aetheus.disposeViewportWatcher = function () {
    if (Aetheus._viewportMql) {
        Aetheus._viewportMql.removeEventListener('change', Aetheus._onViewportChange);
        Aetheus._viewportMql = null;
    }
    Aetheus._viewportRef = null;
};

// Data-grid actions are visually icon-only in compact rows. The grid keeps their localized text in
// the DOM for accessibility, but does not copy it to `title`, so mouse users get no tooltip. Apply
// the same contract to every current and future grid button, filter controls included.
(function addDataGridButtonTooltips() {
    function apply(root) {
        const buttons = [];
        if (root instanceof Element && root.matches('.omni-data-grid button')) buttons.push(root);
        if (root.querySelectorAll) buttons.push(...root.querySelectorAll('.omni-data-grid button'));

        buttons.forEach(function (button) {
            if (button.getAttribute('title')) return;
            const text = button.querySelector('.omni-button__content')?.textContent?.trim();
            const label = text || button.getAttribute('aria-label');
            if (label) button.setAttribute('title', label);
        });
    }

    const observer = new MutationObserver(function (mutations) {
        mutations.forEach(function (mutation) {
            mutation.addedNodes.forEach(function (node) {
                if (node.nodeType === Node.ELEMENT_NODE) apply(node);
            });
        });
    });

    function start() {
        apply(document);
        observer.observe(document.body, { childList: true, subtree: true });
    }

    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start, { once: true });
    else start();
})();

function downloadBlob(filename, blob) {
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = filename;
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
    URL.revokeObjectURL(url);
}

window.downloadFile = function (filename, content, mimeType) {
    downloadBlob(filename, new Blob([content], { type: mimeType }));
};

window.downloadFileFromBytes = function (bytes, filename, mimeType) {
    downloadBlob(filename, new Blob([new Uint8Array(bytes)], { type: mimeType || 'application/octet-stream' }));
};

window.downloadFileFromStream = async function (filename, streamRef) {
    const data = await streamRef.arrayBuffer();
    downloadBlob(filename, new Blob([data], { type: 'application/octet-stream' }));
};

window.copyToClipboard = async function (text) {
    try {
        if (navigator.clipboard && window.isSecureContext) {
            await navigator.clipboard.writeText(text);
            return true;
        }
    } catch {
        // fall through
    }
    try {
        const ta = document.createElement('textarea');
        ta.value = text;
        ta.style.position = 'fixed';
        ta.style.opacity = '0';
        document.body.appendChild(ta);
        ta.select();
        const ok = document.execCommand('copy');
        document.body.removeChild(ta);
        return ok;
    } catch {
        return false;
    }
};

// Signals network recovery and browser wake-up so .NET can retry pending connections without
// waiting for a stale backoff timer.
Aetheus._onConnectivityWake = function () {
    if (Aetheus._connectivityRef) {
        Aetheus._connectivityRef.invokeMethodAsync('OnConnectivityWake');
    }
};
Aetheus._onVisibilityWake = function () {
    // Only the return to visible matters. Leaving the tab is not a connectivity event.
    if (document.visibilityState === 'visible') Aetheus._onConnectivityWake();
};
Aetheus.watchConnectivity = function (dotNetRef) {
    Aetheus._connectivityRef = dotNetRef;
    window.addEventListener('online', Aetheus._onConnectivityWake);
    document.addEventListener('visibilitychange', Aetheus._onVisibilityWake);
};
Aetheus.disposeConnectivityWatcher = function () {
    window.removeEventListener('online', Aetheus._onConnectivityWake);
    document.removeEventListener('visibilitychange', Aetheus._onVisibilityWake);
    Aetheus._connectivityRef = null;
};

// Approvals waiting, in the browser tab's title (decision of 2026-10-06): "(2) <page title>" while any
// waits and, while the tab is in the background, the title alternates every second with the attention
// message so the tab is noticed among the others. The page title is Blazor's <PageTitle>, rewritten at
// every navigation: an observer on <title> puts the count back in front of the new title.
Aetheus.titleAttention = (function () {
    const prefix = /^\(\d+\+?\) /;
    let count = 0;
    let message = '';
    let base = null;
    let written = null;
    let blinkOn = false;
    let timer = null;
    let observer = null;

    function write(title) {
        written = title;
        if (document.title !== title) document.title = title;
    }

    function render() {
        if (base === null) base = document.title.replace(prefix, '');
        if (count === 0) write(base);
        else if (blinkOn) write(message);
        else write('(' + (count > 99 ? '99+' : count) + ') ' + base);
    }

    function onTitleChanged() {
        // Our own write comes back through the observer: only a title the page set is a new base.
        if (document.title === written) return;
        base = document.title.replace(prefix, '');
        render();
    }

    function stopBlinking() {
        if (timer !== null) clearInterval(timer);
        timer = null;
        blinkOn = false;
    }

    function syncBlinking() {
        if (count > 0 && document.visibilityState === 'hidden') {
            if (timer === null) {
                timer = setInterval(function () {
                    blinkOn = !blinkOn;
                    render();
                }, 1000);
            }
        } else {
            stopBlinking();
        }
        render();
    }

    function watch() {
        if (observer !== null) return;
        const head = document.querySelector('head');
        if (head && typeof MutationObserver !== 'undefined') {
            observer = new MutationObserver(onTitleChanged);
            observer.observe(head, { subtree: true, childList: true, characterData: true });
        }
        document.addEventListener('visibilitychange', syncBlinking);
    }

    return {
        set: function (waiting, attentionMessage) {
            count = Math.max(0, waiting | 0);
            message = attentionMessage || '';
            watch();
            syncBlinking();
        }
    };
})();

// The page leaving and regaining the foreground, both ways, for the synchronising indicator: a phone
// put down freezes the tab, and what the realtime side must catch up on is only known on return.
Aetheus._onPageVisibility = function () {
    if (Aetheus._pageVisibilityRef) {
        Aetheus._pageVisibilityRef.invokeMethodAsync('OnPageVisibilityChanged', document.visibilityState === 'visible');
    }
};
Aetheus.watchPageVisibility = function (dotNetRef) {
    Aetheus._pageVisibilityRef = dotNetRef;
    document.addEventListener('visibilitychange', Aetheus._onPageVisibility);
};
Aetheus.disposePageVisibilityWatcher = function () {
    document.removeEventListener('visibilitychange', Aetheus._onPageVisibility);
    Aetheus._pageVisibilityRef = null;
};

// Applies AetheusVirtualList spacer heights through CSSOM and reports scroll positions, avoiding
// style attributes prohibited by CSP.
Aetheus._virtualLists = new WeakMap();

Aetheus.sizeVirtualSpacer = function (spacer, height) {
    if (spacer) spacer.style.setProperty('--vlist-height', height + 'px');
};

// The scrolling ancestor is what the rows move inside; the list itself usually does not scroll.
Aetheus._scrollingAncestor = function (element) {
    var viewport = element.parentElement;
    while (viewport && viewport !== document.body) {
        var overflow = getComputedStyle(viewport).overflowY;
        if (overflow === 'auto' || overflow === 'scroll') break;
        viewport = viewport.parentElement;
    }
    return viewport || document.scrollingElement || document.documentElement;
};

Aetheus.watchVirtualList = function (root, ref) {
    if (!root || !ref) return;

    var viewport = Aetheus._scrollingAncestor(root);

    var pending = false;
    var report = function () {
        if (pending) return;
        pending = true;
        requestAnimationFrame(function () {
            pending = false;
            var top = viewport === document.scrollingElement
                ? window.scrollY - root.offsetTop
                : viewport.scrollTop - (root.offsetTop - viewport.offsetTop);
            var height = viewport === document.scrollingElement ? window.innerHeight : viewport.clientHeight;
            ref.invokeMethodAsync('OnViewportAsync', Math.max(0, top), Math.max(0, height));
        });
    };

    var resize = typeof ResizeObserver === 'function' ? new ResizeObserver(report) : null;
    if (resize) resize.observe(viewport);
    viewport.addEventListener('scroll', report, { passive: true });
    Aetheus._virtualLists.set(root, { viewport: viewport, report: report, resize: resize });
    report();
};

Aetheus.unwatchVirtualList = function (root) {
    var state = root ? Aetheus._virtualLists.get(root) : null;
    if (!state) return;
    state.viewport.removeEventListener('scroll', state.report);
    if (state.resize) state.resize.disconnect();
    Aetheus._virtualLists.delete(root);
};

// Measures the unfolded header and sets data-compact when badges or actions overflow beside the
// title. The data attribute avoids conflicting with Blazor's ownership of the class attribute.
(function fitPageHeaders() {
    var frames = new Set();
    var queued = false;

    function measure(frame) {
        var row = frame.querySelector('.omni-page-header__row');
        if (!row) return;
        frame.removeAttribute('data-compact');
        var title = frame.querySelector('.omni-page-header__title');
        var details = row.querySelector('.omni-page-header__details');
        var overflows = row.scrollWidth > row.clientWidth + 1
            || (!!title && title.scrollWidth > title.clientWidth + 1)
            || (!!details && details.offsetParent !== null && details.offsetTop > row.offsetTop + 4);
        frame.toggleAttribute('data-compact', overflows);
    }

    function flush() {
        queued = false;
        frames.forEach(function (frame) {
            if (!frame.isConnected) { frames.delete(frame); resize.unobserve(frame); return; }
            measure(frame);
        });
    }

    function queue() {
        if (queued) return;
        queued = true;
        requestAnimationFrame(flush);
    }

    var resize = new ResizeObserver(queue);

    function track(root) {
        var found = [];
        if (root instanceof Element && root.matches('.omni-page-header__frame')) found.push(root);
        if (root.querySelectorAll) found.push.apply(found, root.querySelectorAll('.omni-page-header__frame'));
        found.forEach(function (frame) {
            if (frames.has(frame)) return;
            frames.add(frame);
            resize.observe(frame);
        });
    }

    // Content arriving after the first render (badges once the run loads, a section's actions) can
    // change what fits, so any change inside a header is measured again.
    var mutations = new MutationObserver(function (records) {
        records.forEach(function (record) {
            record.addedNodes.forEach(function (node) {
                if (node.nodeType === Node.ELEMENT_NODE) track(node);
            });
            var target = record.target.nodeType === Node.ELEMENT_NODE ? record.target : record.target.parentElement;
            if (target && target.closest && target.closest('.omni-page-header__row')) queue();
        });
    });

    function start() {
        track(document);
        mutations.observe(document.body, { childList: true, subtree: true, characterData: true });
        queue();
    }

    if (typeof ResizeObserver !== 'function') return;
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start, { once: true });
    else start();
})();

// R-46: publishes the width of the page scrollbar (the one of #main-content, OmniMain.Scrollable) as the
// CSS variable --aetheus-scrollbar-width on the root, 0px when it is an overlay or absent. The header spans
// the window while the content area is one scrollbar narrower; app.css gives that width back on the
// header's trailing side so both end on the same line. The root is written through the CSSOM, which the
// shell's style policy allows.
(function syncPageScrollbarWidth() {
    var observed = null;
    var published = null;
    var queued = false;
    var resize = typeof ResizeObserver === 'function' ? new ResizeObserver(queue) : null;

    function measure() {
        queued = false;
        var main = document.getElementById('main-content');
        if (main !== observed) {
            if (observed && resize) resize.unobserve(observed);
            observed = main;
            if (main && resize) resize.observe(main);
        }
        var width = main ? Math.max(0, main.offsetWidth - main.clientWidth) : 0;
        if (width === published) return;
        published = width;
        document.documentElement.style.setProperty('--aetheus-scrollbar-width', width + 'px');
    }

    function queue() {
        if (queued) return;
        queued = true;
        requestAnimationFrame(measure);
    }

    // The main area is rendered after the boot gate, and a scrollbar appears when the content grows,
    // which narrows the content box the observer watches.
    function start() {
        new MutationObserver(function () {
            if (!observed || !observed.isConnected) queue();
        }).observe(document.body, { childList: true, subtree: true });
        queue();
    }

    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start, { once: true });
    else start();
})();

// Restores main-content and nested scroll positions on back/forward navigation or return to the
// last page. Restoration waits for content height and stops on user interaction or after four
// seconds.
Aetheus.pageScroll = (function () {
    var positions = new Map();
    var current = location.pathname + location.search;
    var previous = null;
    var popped = false;
    var restoring = false;
    var gestures = ['wheel', 'touchstart', 'keydown', 'mousedown'];

    function main() { return document.getElementById('main-content'); }

    // A scrolled block is found again by its tag and classes, and its rank among the blocks alike.
    function selectorOf(element) {
        return element.tagName.toLowerCase() + Array.prototype.map.call(element.classList, function (name) {
            return '.' + CSS.escape(name);
        }).join('');
    }

    function capture(root) {
        var kept = [{ selector: null, index: 0, top: root.scrollTop }];
        root.querySelectorAll('*').forEach(function (element) {
            if (element.scrollTop <= 0) return;
            var selector = selectorOf(element);
            var index = Array.prototype.indexOf.call(root.querySelectorAll(selector), element);
            kept.push({ selector: selector, index: index, top: element.scrollTop });
        });
        return kept;
    }

    // Registered before Blazor starts, so it runs before Blazor handles the same popstate.
    window.addEventListener('popstate', function () { popped = true; });

    function stop() {
        if (!restoring) return;
        restoring = false;
        gestures.forEach(function (name) { window.removeEventListener(name, stop, true); });
    }

    function restore(kept) {
        var started = performance.now();
        var steady = 0;
        restoring = true;
        gestures.forEach(function (name) { window.addEventListener(name, stop, true); });
        (function step() {
            if (!restoring) return;
            var root = main();
            var placed = 0;
            kept.forEach(function (position) {
                var element = !root ? null : position.selector === null ? root
                    : root.querySelectorAll(position.selector)[position.index];
                if (!element || element.scrollHeight - element.clientHeight < position.top - 1) return;
                element.scrollTop = position.top;
                if (Math.abs(element.scrollTop - position.top) <= 1) placed++;
            });
            steady = placed === kept.length ? steady + 1 : 0;
            if (steady >= 10 || performance.now() - started > 4000) { stop(); return; }
            requestAnimationFrame(step);
        })();
    }

    return {
        // The address is the one this helper last saw arrive: on a popstate the browser has already
        // moved location to the destination.
        leaving: function () {
            stop();
            var root = main();
            if (root) positions.set(current, capture(root));
        },
        arrived: function () {
            var url = location.pathname + location.search;
            var isReturn = popped || url === previous;
            popped = false;
            if (url === current) return false;
            previous = current;
            current = url;
            var kept = positions.get(url);
            if (!isReturn || !kept || !kept.some(function (position) { return position.top > 0; })) return false;
            restore(kept.filter(function (position) { return position.top > 0; }));
            return true;
        }
    };
})();

// Daily recap preview. The recap is an e-mail: its look lives in inline style attributes, which the
// shell's policy (style-src-attr 'none') refuses in markup. The HTML is parsed off-document, every
// style attribute is moved aside, the nodes go into a shadow root (so app.css cannot reach them and
// the e-mail's styles cannot leak out), and each style is then applied through the CSSOM, the path
// the policy leaves open. Scripts, frames, links and event attributes are dropped first.
Aetheus.renderEmailPreview = function (host, html) {
    if (!host) return;
    const root = host.shadowRoot || host.attachShadow({ mode: 'open' });
    const parsed = new DOMParser().parseFromString(html || '', 'text/html');
    parsed.querySelectorAll('script, iframe, object, embed, link, meta, base, form').forEach(node => node.remove());
    const styles = [];
    parsed.body.querySelectorAll('*').forEach(element => {
        for (const attribute of Array.from(element.attributes)) {
            if (attribute.name.toLowerCase().startsWith('on')) element.removeAttribute(attribute.name);
        }
        const style = element.getAttribute('style');
        if (style !== null) {
            element.setAttribute('data-preview-style', String(styles.length));
            styles.push(style);
            element.removeAttribute('style');
        }
    });
    const container = document.createElement('div');
    for (const node of Array.from(parsed.body.childNodes)) container.appendChild(document.importNode(node, true));
    container.querySelectorAll('[data-preview-style]').forEach(element => {
        element.style.cssText = styles[Number(element.getAttribute('data-preview-style'))] || '';
        element.removeAttribute('data-preview-style');
    });
    root.replaceChildren(container);
};
