// SPDX-License-Identifier: EUPL-1.2
// The diff side of wwwroot/js/omni-code-editor-frame.js: the contract OE's OmniDiffViewer calls on the module
// handed to InteropModulePath (recette R-100, R-101, R-102). The frame page itself is faked: this proves the
// module drives it as OE's own module would, not Monaco.
import test from 'node:test';
import assert from 'node:assert/strict';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { createRequire } from 'node:module';

const { JSDOM } = createRequire(import.meta.url)('jsdom');

const source = pathToFileURL(path.resolve(import.meta.dirname, '../../src/Aetheus.Front/wwwroot/js/omni-code-editor-frame.js')).href;

let fresh = 0;

// Loads a fresh copy of the module over a fresh document, with an iframe whose content is the fake editor.
async function setup({ loadResult = true, mountResult = true } = {}) {
    const dom = new JSDOM('<!doctype html><html><body><div data-omni-theme="light"><div id="host"></div></div></body></html>', {
        url: 'https://localhost/'
    });
    global.window = dom.window;
    global.document = dom.window.document;
    global.MutationObserver = dom.window.MutationObserver;
    global.getComputedStyle = dom.window.getComputedStyle.bind(dom.window);
    global.fetch = async () => ({ ok: true });

    const calls = [];
    const root = document.createElement('div');
    const fake = {
        load: async (...args) => { calls.push(['load', ...args]); return loadResult; },
        mountDiff: (...args) => { calls.push(['mountDiff', ...args]); return mountResult; },
        setDiff: (...args) => calls.push(['setDiff', ...args]),
        configureDiff: (...args) => calls.push(['configureDiff', ...args]),
        goToDiff: (...args) => calls.push(['goToDiff', ...args]),
        disposeDiff: (...args) => calls.push(['disposeDiff', ...args])
    };
    const frameWindow = { omniFrameEditor: fake, document: { getElementById: id => (id === 'omni-code-root' ? root : null) } };
    Object.defineProperty(dom.window.HTMLIFrameElement.prototype, 'contentWindow', { get: () => frameWindow });

    const module = await import(`${source}?case=${fresh++}`);
    const host = document.getElementById('host');
    const dotnet = { invokeMethodAsync() {} };
    return { module, host, dotnet, root, calls };
}

// mountDiff awaits the frame's load event; jsdom does not load the frame page, so it is raised by hand.
async function mount(env, options = { original: 'a', modified: 'b', language: 'yaml', readOnly: true, inline: false }) {
    const pending = env.module.mountDiff(env.host, env.dotnet, options);
    env.host.querySelector('iframe').dispatchEvent(new window.Event('load'));
    return pending;
}

test('exports the contract OmniDiffViewer calls on its interop module', async () => {
    const { module } = await setup();

    for (const name of ['setHeight', 'load', 'mountDiff', 'setDiff', 'configureDiff', 'goToDiff', 'disposeDiff'])
        assert.equal(typeof module[name], 'function', name);
});

test('mountDiff loads Monaco in the frame with what load() was given, mounts both texts there, and says it mounted', async () => {
    const env = await setup();
    assert.equal(await env.module.load('lib/monaco-editor/min/vs', 'fr-FR', 30000), true);

    const options = { original: 'old', modified: 'new', language: 'yaml', readOnly: true, inline: true, originalLabel: 'Before', modifiedLabel: 'After' };
    const mounted = await mount(env, options);

    assert.equal(mounted, true);
    const frame = env.host.querySelector('iframe.omni-code-editor-frame');
    assert.ok(frame, 'the frame is in the host');
    assert.deepEqual(env.calls.find(call => call[0] === 'load'), ['load', 'lib/monaco-editor/min/vs', 'fr-FR', 30000]);
    assert.deepEqual(env.calls.find(call => call[0] === 'mountDiff'), ['mountDiff', env.root, env.dotnet, options]);
});

test('mountDiff says false and leaves no frame when Monaco cannot load or mount in the frame', async () => {
    for (const failure of [{ loadResult: false }, { mountResult: false }]) {
        const env = await setup(failure);
        assert.equal(await mount(env), false);
        assert.equal(env.host.querySelector('iframe'), null);
    }
});

test('mountDiff refuses a host that already holds a diff', async () => {
    const env = await setup();
    assert.equal(await mount(env), true);

    assert.equal(await env.module.mountDiff(env.host, env.dotnet, {}), false);
    assert.equal(env.host.querySelectorAll('iframe').length, 1);
});

test('setDiff, configureDiff and goToDiff reach the frame editor through its root', async () => {
    const env = await setup();
    await mount(env);

    env.module.setDiff(env.host, 'x', 'y');
    env.module.configureDiff(env.host, { original: null, modified: null, readOnly: false });
    env.module.goToDiff(env.host, 'next');
    env.module.goToDiff(env.host, 'previous');

    assert.deepEqual(env.calls.filter(call => ['setDiff', 'configureDiff', 'goToDiff'].includes(call[0])), [
        ['setDiff', env.root, 'x', 'y'],
        ['configureDiff', env.root, { original: null, modified: null, readOnly: false }],
        ['goToDiff', env.root, 'next'],
        ['goToDiff', env.root, 'previous']
    ]);
});

test('disposeDiff releases the frame editor once, removes the frame, and is a no-op when nothing is mounted', async () => {
    const env = await setup();
    assert.doesNotThrow(() => env.module.disposeDiff(env.host));
    await mount(env);

    env.module.disposeDiff(env.host);
    assert.doesNotThrow(() => env.module.disposeDiff(env.host));

    assert.deepEqual(env.calls.filter(call => call[0] === 'disposeDiff'), [['disposeDiff', env.root]]);
    assert.equal(env.host.querySelector('iframe'), null);
});

test('the calls of a host with nothing mounted do nothing and do not throw', async () => {
    const env = await setup();

    assert.doesNotThrow(() => {
        env.module.setDiff(env.host, 'x', 'y');
        env.module.configureDiff(env.host, {});
        env.module.goToDiff(env.host, 'next');
    });
    assert.deepEqual(env.calls, []);
});
