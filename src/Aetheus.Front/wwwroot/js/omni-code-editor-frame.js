// The interop module Aetheus hands to OE's Monaco components (OmniCodeEditor, OmniDiffViewer) through
// InteropModulePath. Monaco cannot run in the page under the strict CSP, so each editor or diff is mounted in
// an iframe (/omni-monaco-frame.html) whose own policy allows it; this module creates that frame and forwards
// every call to OE's omni-code-editor.js running inside it.
const frames = new WeakMap();
let monacoLoad = { basePath: null, culture: null, timeoutMs: null };

export function setHeight(root, height) {
    if (!root) return;
    if (height) root.style.setProperty('--omni-code-editor-height', height);
    else root.style.removeProperty('--omni-code-editor-height');
}

// Whether Monaco can be served; what the frame is later asked to load comes from here (a diff is mounted
// with its texts and labels only, not with the path, culture and timeout).
export async function load(basePath, culture, timeoutMs) {
    monacoLoad = { basePath, culture, timeoutMs };
    try {
        const frame = await fetch('/omni-monaco-frame.html', { method: 'HEAD', cache: 'no-store' });
        const loader = await fetch(new URL(`${String(basePath).replace(/\/+$/, '')}/loader.js`, document.baseURI),
            { method: 'HEAD' });
        return frame.ok && loader.ok;
    } catch {
        return false;
    }
}

export async function mount(host, dotnet, options) {
    return mountInFrame(host, options?.label, 'dispose', async (module, root) => {
        if (!await module.load(options.monacoPath, options.culture, options.loadTimeoutMilliseconds))
            throw new Error('Monaco could not load inside the editor frame');
        if (!module.mount(root, dotnet, options))
            throw new Error('Monaco could not mount inside the editor frame');
    });
}

// The diff editor of OmniDiffViewer: options carry the texts, language, readOnly, inline and the labels.
export async function mountDiff(host, dotnet, options) {
    return mountInFrame(host, options?.modifiedLabel, 'disposeDiff', async (module, root) => {
        if (!await module.load(monacoLoad.basePath, monacoLoad.culture, monacoLoad.timeoutMs))
            throw new Error('Monaco could not load inside the diff frame');
        if (!module.mountDiff(root, dotnet, options))
            throw new Error('Monaco could not mount the diff inside the frame');
    });
}

async function mountInFrame(host, title, disposeMethod, mountIn) {
    if (!host || frames.has(host)) return false;

    const frame = document.createElement('iframe');
    frame.className = 'omni-code-editor-frame';
    frame.title = title || document.title;
    const state = { frame, module: null, root: null, observer: null };
    frames.set(host, state);

    const loaded = new Promise((resolve, reject) => {
        frame.onload = () => {
            const module = frame.contentWindow?.omniFrameEditor;
            if (module) resolve(module);
            else reject(new Error('OmniEurope editor frame did not load'));
        };
        frame.onerror = () => reject(new Error('OmniEurope editor frame could not load'));
    });
    frame.src = '/omni-monaco-frame.html';
    host.replaceChildren(frame);

    try {
        const module = await loaded;
        if (frames.get(host) !== state) return false;
        state.module = module;
        state.root = frame.contentWindow.document.getElementById('omni-code-root');
        if (!state.root) throw new Error('OmniEurope editor frame has no root');
        followTheme(host, state);
        await mountIn(module, state.root);
        return true;
    } catch {
        release(host, disposeMethod);
        return false;
    }
}

// The frame has its own document: mirror the host's theme and surface colour into it.
function followTheme(host, state) {
    const scope = host.closest('[data-omni-theme]');
    const syncTheme = () => {
        state.root.ownerDocument.documentElement.setAttribute('data-omni-theme',
            scope?.getAttribute('data-omni-theme') || 'dark');
        const surface = host.closest('.omni-code-editor, .omni-diff-viewer') || host;
        state.root.style.backgroundColor = getComputedStyle(surface).backgroundColor;
    };
    syncTheme();
    if (scope) {
        state.observer = new MutationObserver(syncTheme);
        state.observer.observe(scope, { attributes: true, attributeFilter: ['data-omni-theme', 'style', 'class'] });
    }
}

export function setValue(host, value) {
    const state = frames.get(host);
    if (state?.module) state.module.setValue(state.root, value);
}

export function configure(host, options) {
    const state = frames.get(host);
    if (state?.module) state.module.configure(state.root, options);
}

export function setDiff(host, original, modified) {
    const state = frames.get(host);
    if (state?.module) state.module.setDiff(state.root, original, modified);
}

// original and modified at null leave the texts alone (OmniDiffViewer sends only the options that changed).
export function configureDiff(host, options) {
    const state = frames.get(host);
    if (state?.module) state.module.configureDiff(state.root, options);
}

export function goToDiff(host, direction) {
    const state = frames.get(host);
    if (state?.module) state.module.goToDiff(state.root, direction);
}

export function read(host) {
    const state = frames.get(host);
    return state?.module?.read(state.root) ?? null;
}

export function focus(host) {
    const state = frames.get(host);
    if (state?.module) state.module.focus(state.root);
}

export function dispose(host) {
    release(host, 'dispose');
}

export function disposeDiff(host) {
    release(host, 'disposeDiff');
}

function release(host, disposeMethod) {
    const state = frames.get(host);
    if (!state) return;
    frames.delete(host);
    state.observer?.disconnect();
    if (state.module && state.root) state.module[disposeMethod](state.root);
    state.frame.remove();
}
