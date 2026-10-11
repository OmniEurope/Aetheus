// SPDX-License-Identifier: EUPL-1.2
const test = require('node:test');
const assert = require('node:assert/strict');
const path = require('node:path');
const { JSDOM } = require('jsdom');

function loadHost() {
    const dom = new JSDOM('<!doctype html><html><body><div id="editor"></div></body></html>', {
        url: 'https://localhost/'
    });
    global.window = dom.window;
    global.document = dom.window.document;
    const source = path.resolve(__dirname, '../../src/Aetheus.Front/wwwroot/js/monaco-frame-host.js');
    delete require.cache[source];
    require(source);
    // init creates and registers the frame synchronously, then waits for it to load; jsdom never loads
    // the editor page, so the wait is left to fail on its own.
    window.monacoInterop.init('editor', 'name: ci', null, true).catch(() => {});
    return document.querySelector('#editor iframe');
}

for (const method of ['dispose', 'disposeDiffEditor', 'disposeEditor']) {
    test(`SITE-FUNC-MONACO-DISPOSE-001: ${method} of a frame Blazor already removed is a no-op`, () => {
        const frame = loadHost();
        frame.remove();

        assert.doesNotThrow(() => window.monacoInterop[method]('editor'));
    });

    test(`SITE-FUNC-MONACO-DISPOSE-001: ${method} releases a ready editor once and is idempotent`, () => {
        const frame = loadHost();
        let released = 0;
        frame.contentWindow.monacoInterop = { [method]: root => { assert.equal(root, 'monaco-root'); released++; } };

        window.monacoInterop[method]('editor');
        assert.doesNotThrow(() => window.monacoInterop[method]('editor'));

        assert.equal(released, 1);
        assert.equal(document.querySelector('#editor iframe'), null);
    });
}

test('SITE-FUNC-MONACO-DISPOSE-001: the other operations still refuse an editor that is not ready', () => {
    const frame = loadHost();
    frame.remove();

    assert.throws(() => window.monacoInterop.getValue('editor'), /Monaco editor is not ready/);
});
