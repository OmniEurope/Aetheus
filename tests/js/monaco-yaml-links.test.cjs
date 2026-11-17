// SPDX-License-Identifier: EUPL-1.2
const test = require('node:test');
const assert = require('node:assert/strict');
const path = require('node:path');

// R-35: the YAML link provider recognizes `pipeline:` and `extends:` references and routes the custom
// `pipeline:` / `template:` schemes back to the .NET editor ref. monaco-yaml.js is loaded against a minimal
// stand-in of the Monaco API (only what the registration reads); this does not prove Monaco's own Ctrl-click
// handling, only what the provider returns and what the opener forwards.
function loadInterop() {
    const registered = { provider: null, opener: null };
    global.monaco = {
        Range: class { constructor(startLineNumber, startColumn, endLineNumber, endColumn) { Object.assign(this, { startLineNumber, startColumn, endLineNumber, endColumn }); } },
        languages: { registerLinkProvider: (language, provider) => { registered.language = language; registered.provider = provider; } },
        editor: { registerLinkOpener: opener => { registered.opener = opener; } }
    };
    global.window = {};
    const source = path.resolve(__dirname, '../../src/Aetheus.Front/wwwroot/js/monaco-yaml.js');
    delete require.cache[source];
    require(source);
    return { interop: global.window.monacoInterop, registered };
}

function modelOf(yaml) {
    const lines = yaml.split('\n');
    return { getLineCount: () => lines.length, getLineContent: number => lines[number - 1] };
}

function linksOf(registered, yaml) {
    return registered.provider.provideLinks(modelOf(yaml)).links;
}

test('an extends line becomes a template link over the name@version only', () => {
    const { interop, registered } = loadInterop();
    interop._registerPipelineLinks({ invokeMethodAsync() {} });

    const links = linksOf(registered, 'name: ci\nextends: ci-dotnet@3\n');

    assert.equal(registered.language, 'yaml');
    assert.equal(links.length, 1);
    assert.equal(links[0].url, 'template:' + encodeURIComponent('ci-dotnet@3'));
    assert.deepEqual(
        [links[0].range.startLineNumber, links[0].range.startColumn, links[0].range.endLineNumber, links[0].range.endColumn],
        [2, 10, 2, 21]);
});

test('a quoted extends with a trailing comment links the reference without the quotes', () => {
    const { interop, registered } = loadInterop();
    interop._registerPipelineLinks({ invokeMethodAsync() {} });

    const links = linksOf(registered, "extends: 'ci-dotnet@3'  # pinned\n");

    assert.equal(links.length, 1);
    assert.equal(links[0].url, 'template:' + encodeURIComponent('ci-dotnet@3'));
    // `extends: '` is 10 columns, so the reference starts at column 11 and `ci-dotnet@3` is 11 characters long.
    assert.deepEqual([links[0].range.startColumn, links[0].range.endColumn], [11, 22]);
});

test('a bare legacy extends is linked, a commented one or another key is not', () => {
    const { interop, registered } = loadInterop();
    interop._registerPipelineLinks({ invokeMethodAsync() {} });

    const links = linksOf(registered, 'extends: legacy\n# extends: other@1\nscript: echo extends: x@1\n');

    assert.deepEqual(links.map(link => link.url), ['template:legacy']);
});

test('the pipeline: links are unchanged', () => {
    const { interop, registered } = loadInterop();
    interop._registerPipelineLinks({ invokeMethodAsync() {} });

    const links = linksOf(registered, '  - pipeline: "deploy"\n');

    assert.equal(links.length, 1);
    assert.equal(links[0].url, 'pipeline:deploy');
});

test('the opener forwards template: and pipeline: links to the matching .NET callbacks, and ignores other schemes', () => {
    const { interop, registered } = loadInterop();
    const calls = [];
    interop._registerPipelineLinks({ invokeMethodAsync: (method, value) => calls.push([method, value]) });

    assert.equal(registered.opener.open({ scheme: 'template', path: 'ci-dotnet@3' }), true);
    assert.equal(registered.opener.open({ scheme: 'pipeline', path: 'deploy' }), true);
    assert.equal(registered.opener.open({ scheme: 'https', path: '/x' }), false);

    assert.deepEqual(calls, [['OnTemplateLinkActivated', 'ci-dotnet@3'], ['OnPipelineLinkActivated', 'deploy']]);
});
