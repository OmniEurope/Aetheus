// SPDX-License-Identifier: EUPL-1.2
const test = require('node:test');
const assert = require('node:assert/strict');
const path = require('node:path');
const { JSDOM } = require('jsdom');

function loadLayout(markup = '<div id="app-splash"></div><link href="material-base.css">') {
    const dom = new JSDOM(`<!doctype html><html><head><title>Aetheus</title><link href="material-base.css"></head><body>${markup}</body></html>`, {
        url: 'https://localhost/'
    });
    global.window = dom.window;
    global.document = dom.window.document;
    global.navigator = dom.window.navigator;
    // Provides the location global read by layout.js during initialization.
    global.location = dom.window.location;
    global.MutationObserver = dom.window.MutationObserver;
    // layout.js tests "instanceof Element" when it labels grid buttons.
    global.Element = dom.window.Element;
    // The page-header observer tests node types (Node.ELEMENT_NODE) on every change, a title included.
    global.Node = dom.window.Node;
    global.localStorage = dom.window.localStorage;
    // jsdom has no ResizeObserver; layout.js only needs to be able to create one when it loads.
    global.ResizeObserver = class { observe() {} unobserve() {} disconnect() {} };
    global.requestAnimationFrame = callback => callback();
    global.Aetheus = {};
    dom.window.Aetheus = global.Aetheus;
    const source = path.resolve(__dirname, '../../src/Aetheus.Front/wwwroot/js/layout.js');
    delete require.cache[source];
    require(source);
    return dom;
}

test('layout helpers update language and remove the splash idempotently', () => {
    const dom = loadLayout();
    Aetheus.setLang('fr-BE');
    Aetheus.hideSplash();
    document.getElementById('app-splash').dispatchEvent(new dom.window.Event('transitionend'));
    Aetheus.hideSplash();

    assert.equal(document.documentElement.lang, 'fr');
    // OE owns theme-mode persistence through the theme scope and boot script.
    assert.equal(Aetheus.setOmniTheme, undefined);
    assert.equal(document.documentElement.getAttribute('data-omni-theme'), null);
    assert.equal(document.getElementById('app-splash'), null);
    dom.window.close();
});

test('R-471: the measurement gets the opaque visitor from memory and is told when it changes', () => {
    const dom = loadLayout('');
    assert.equal(Aetheus.analyticsVisitor(), undefined);

    let visitor = 'kP3_aQ-opaque-identifier';
    Aetheus._authSession = { invokeMethod: name => name === 'VisitorForAnalytics' ? visitor : true };
    assert.equal(Aetheus.analyticsVisitor(), 'kP3_aQ-opaque-identifier');
    visitor = null;
    assert.equal(Aetheus.analyticsVisitor(), undefined);
    Aetheus._authSession = { invokeMethod: () => { throw new Error('runtime not ready'); } };
    assert.equal(Aetheus.analyticsVisitor(), undefined);

    // Before the measurement is loaded the call only records that the session is known.
    Aetheus.analyticsIdentityChanged();
    assert.equal(Aetheus._analyticsSessionKnown, true);
    let told = 0;
    Aetheus._onAnalyticsIdentity = () => { told++; };
    Aetheus.analyticsIdentityChanged();
    assert.equal(told, 1);
    dom.window.close();
});

test('clipboard helper reports provider success and failure honestly', async () => {
    const dom = loadLayout('');
    Object.defineProperty(global.navigator, 'clipboard', {
        configurable: true,
        value: { writeText: async value => assert.equal(value, 'expected') }
    });
    assert.equal(await Aetheus.copyToClipboard('expected'), true);
    Object.defineProperty(global.navigator, 'clipboard', {
        configurable: true,
        value: { writeText: async () => { throw new Error('denied'); } }
    });
    assert.equal(await Aetheus.copyToClipboard('expected'), false);
    dom.window.close();
});

test('approvals waiting: the tab title carries the count, follows the page title and blinks while hidden', async (t) => {
    t.mock.timers.enable({ apis: ['setInterval'] });
    const dom = loadLayout();
    const tick = () => new Promise(resolve => setImmediate(resolve));
    const setVisibility = state => {
        Object.defineProperty(document, 'visibilityState', { value: state, configurable: true });
        document.dispatchEvent(new dom.window.Event('visibilitychange'));
    };

    Aetheus.titleAttention.set(2, '● 2 approvals waiting');
    assert.equal(document.title, '(2) Aetheus');

    // A navigation rewrites the title: the count goes back in front of the new one.
    document.title = 'Pipelines - Aetheus';
    await tick();
    assert.equal(document.title, '(2) Pipelines - Aetheus');

    // In the background the title alternates every second with the attention message.
    setVisibility('hidden');
    t.mock.timers.tick(1000);
    assert.equal(document.title, '● 2 approvals waiting');
    t.mock.timers.tick(1000);
    assert.equal(document.title, '(2) Pipelines - Aetheus');
    t.mock.timers.tick(1000);
    assert.equal(document.title, '● 2 approvals waiting');

    // Back in the foreground it stops on the counted title; nothing waiting, the page title alone.
    setVisibility('visible');
    assert.equal(document.title, '(2) Pipelines - Aetheus');
    t.mock.timers.tick(3000);
    assert.equal(document.title, '(2) Pipelines - Aetheus');
    Aetheus.titleAttention.set(0, '');
    assert.equal(document.title, 'Pipelines - Aetheus');
    setVisibility('hidden');
    t.mock.timers.tick(2000);
    assert.equal(document.title, 'Pipelines - Aetheus');
    dom.window.close();
});
