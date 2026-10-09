window.Aetheus = window.Aetheus || {};

Aetheus.goBack = function () {
    window.history.back();
};

// PLAN-005 lot 9 / D50: another tab of this browser rotated the tokens. The "storage" event only
// fires in the OTHER tabs, which is exactly who needs the new values; only the two token keys are
// forwarded, and the listener is registered once per page.
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

// Recette R-471: the opaque identifier of the signed-in account, answered from memory like the yes/no
// above. It is a keyed hash minted by the backend (the token's aetheus:avid claim), never the token, the
// account name or an e-mail address; undefined when nobody is signed in.
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
        if (el.parentNode) el.parentNode.removeChild(el);
        // The splash was the last thing painted from the boot copy of the tokens: OmniThemeScope paints
        // the application from here on.
        Aetheus.releaseBootTheme();
    };
    el.addEventListener('transitionend', remove, { once: true });
    // Fallback if transitionend never fires (reduced-motion, display quirks, interrupted transition).
    setTimeout(remove, 600);
};

// PLAN-008 lot 11, STD-SHELL: the stored appearance is 'dark', 'light' or 'system'. OmniThemeScope paints the
// application in it; the document root carries the same mode, resolved to light or dark, for what lives
// outside the scope (the boot splash, the page behind it) and for the app.css rules keyed on the dark half,
// which the scope's own 'system' value cannot select. 'system' follows the OS and repaints when the OS
// preference changes, without a reload. Anything else reads as dark, the application's default.
Aetheus.effectiveTheme = function (appearance) {
    if (appearance === 'light') return 'light';
    if (appearance !== 'system') return 'dark';
    return window.matchMedia && window.matchMedia('(prefers-color-scheme: light)').matches ? 'light' : 'dark';
};

Aetheus.setOmniTheme = function (appearance) {
    Aetheus._appearance = appearance;
    document.documentElement.setAttribute('data-omni-theme', Aetheus.effectiveTheme(appearance));

    if (!Aetheus._systemTheme && window.matchMedia) {
        Aetheus._systemTheme = window.matchMedia('(prefers-color-scheme: light)');
        Aetheus._systemTheme.addEventListener('change', function () {
            if (Aetheus._appearance === 'system') {
                document.documentElement.setAttribute('data-omni-theme', Aetheus.effectiveTheme('system'));
            }
        });
    }
    return Aetheus.effectiveTheme(appearance);
};

// STD-SHELL: the boot copy of the theme. OmniThemeScope writes the tokens of the chosen theme, palette and
// font on its own element once the application runs, which is after the boot splash has been painted. So
// that the splash shows in those colours, this keeps a copy of the tokens the scope painted, under the half
// they were painted for ({ "dark": {...} } or { "light": {...} }); the boot script of index.html lays it on
// the document root before the first frame, and releaseBootTheme removes it once the scope has taken over.
// Nothing here resolves a theme: the values are exactly the ones OE painted.
Aetheus.followThemeScope = function (scopeId) {
    var scope = document.getElementById(scopeId);
    if (!scope || Aetheus._themeScopeObserver) return;
    var copy = function () {
        var half = {};
        var count = 0;
        for (var i = 0; i < scope.style.length; i++) {
            var name = scope.style[i];
            if (name.indexOf('--') !== 0) continue;
            half[name] = scope.style.getPropertyValue(name);
            count++;
        }
        if (count === 0) {
            localStorage.removeItem('aetheus_theme_tokens');
            return;
        }
        var mode = scope.getAttribute('data-omni-theme');
        if (mode !== 'light' && mode !== 'dark') {
            mode = window.matchMedia && window.matchMedia('(prefers-color-scheme: light)').matches ? 'light' : 'dark';
        }
        var stored = {};
        stored[mode] = half;
        localStorage.setItem('aetheus_theme_tokens', JSON.stringify(stored));
    };
    copy();
    Aetheus._themeScopeObserver = new MutationObserver(copy);
    Aetheus._themeScopeObserver.observe(scope, { attributes: true, attributeFilter: ['style', 'data-omni-theme'] });
};

// Removes the tokens the boot script laid on the document root (their names are in Aetheus._bootTokenNames).
Aetheus.releaseBootTheme = function () {
    var root = document.documentElement;
    (Aetheus._bootTokenNames || []).forEach(function (name) { root.style.removeProperty(name); });
    Aetheus._bootTokenNames = [];
};

// Recette R-390: OE's text size and control size, 1 to 10. Level 5 is the site as drawn and carries no
// attribute; the others set data-oe-text-size / data-oe-control-size on <html>, which OE's stylesheet
// reads (root font size, control scale). The boot script of index.html applies the same keys.
Aetheus._setScale = function (attribute, level) {
    var root = document.documentElement;
    var value = parseInt(level, 10);
    if (value >= 1 && value <= 10 && value !== 5) root.setAttribute(attribute, String(value));
    else root.removeAttribute(attribute);
};
Aetheus.setTextSize = function (level) { Aetheus._setScale('data-oe-text-size', level); };
Aetheus.setControlSize = function (level) { Aetheus._setScale('data-oe-control-size', level); };

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

Aetheus.focusById = function (id) {
    const el = document.getElementById(id);
    if (!el) return;
    // The id may point at a wrapper (e.g. an OmniPassword/OmniFormField) whose
    // real focusable target is a nested <input>; prefer that when present.
    const target = el.matches('input, textarea, select') ? el : (el.querySelector('input, textarea, select') || el);
    target.focus();
};

Aetheus.copyToClipboard = async function (text) {
    try {
        await navigator.clipboard.writeText(text);
        return true;
    } catch {
        return false;
    }
};

// PLAN-005 lot 4 / D35 (STD-BUSY): a busy button is not disabled, the veil keeps its look, so the
// keyboard (Enter, Space, or Enter in a form's field, which clicks the submit button) could still
// activate it and submit twice. Its clicks are swallowed here, in the capture phase, before Blazor's
// own listeners see them. pointer-events: none already covers the mouse.
window.addEventListener('click', function (e) {
    if (e.target instanceof Element && e.target.closest('.btn-busy')) {
        e.preventDefault();
        e.stopImmediatePropagation();
    }
}, true);

// PLAN-005 lot 2 / D32: true when a line-clamped element hides part of its text, so the toast shows
// "Show more" only when there is more to show.
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

Aetheus.trapFocus = function (containerSelector) {
    const container = document.querySelector(containerSelector);
    if (!container) return;

    const focusable = container.querySelectorAll(
        '[role="button"], button, a, input, select, textarea, [tabindex]:not([tabindex="-1"])'
    );
    if (focusable.length === 0) return;

    focusable[0].focus();
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

// The app used to have no way of knowing the network had come back. During a PC sleep the browser
// freezes timers, and on wake the SignalR retry loop was still sitting inside a backoff step of up
// to 30s - scheduled for a network condition that no longer applied. Both events below say "try
// now"; the .NET side decides whether anything is actually waiting.
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

// PLAN-008 lot 44: the viewport side of AetheusVirtualList. Blazor's own <Virtualize> sizes its
// spacers with a style attribute in markup, which a strict style-src-attr refuses; setProperty is
// the path it leaves open, so the heights are applied here and the scroll position is reported back.
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

// Recette R-324: the page header (OmniPageHeader) folds its badges and actions behind its toggle as
// soon as they no longer fit beside the title, whatever the viewport width, instead of squeezing
// badges or letting buttons fall onto the breadcrumb line; OE folds them on a phone only. The frame is
// measured unfolded (its natural layout) and marked data-compact when anything overflows; its CSS
// lives with the header rules in app.css. The flag is a data attribute because Blazor owns the class
// attribute. The title's sideways scroll and its chevrons (recette R-331) are OE's (omni-page-header.js).
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

// Recette R2-087: a page the reader comes back to finds its scroll positions again: the page's own
// (#main-content) and those of the scrolling blocks inside it, such as a grid that fills the page and
// scrolls its rows itself. The layout reports each navigation twice: before it (leaving: the positions
// are kept under the address being left) and after it (arrived). A return is a browser back/forward
// (popstate) or a move to the page just left, where the "Revenir" button usually leads. Each position is
// put back once its block exists and is tall enough again (rows may still be arriving), and given up as
// soon as the reader scrolls, presses a key or four seconds pass. Any other arrival changes nothing.
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
