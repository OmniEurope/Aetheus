// SPDX-License-Identifier: EUPL-1.2
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { JSDOM } = require('jsdom');

const repo = path.resolve(__dirname, '../..');

function loadVisualPipeline() {
    const dom = new JSDOM(`<!doctype html><html><body>
        <div id="canvas" class="vp-canvas-outer"><div class="vp-canvas-inner">
            <svg class="vp-edge-layer"><path data-from="0" data-to="1"></path></svg>
            <div id="vp-node-canvas-0" class="vp-node"></div>
            <div id="vp-node-canvas-1" class="vp-node"></div>
        </div></div></body></html>`, { url: 'https://localhost/' });
    global.window = dom.window;
    global.document = dom.window.document;
    global.MutationObserver = dom.window.MutationObserver;
    global.localStorage = dom.window.localStorage;
    const source = path.join(repo, 'src/Aetheus.Front/wwwroot/js/visual-pipeline.js');
    delete require.cache[source];
    require(source);
    const container = document.getElementById('canvas');
    // jsdom does no layout: give the frame a phone-sized box and a native scroll position that can move,
    // as Chromium's does when it scrolls an overflow: hidden frame to reveal a focused node.
    container.getBoundingClientRect = () => ({ left: 12, top: 0, width: 353, height: 300, right: 365, bottom: 300 });
    let scrollLeft = 0;
    let scrollTop = 0;
    Object.defineProperty(container, 'scrollLeft', { get: () => scrollLeft, set: v => { scrollLeft = v; }, configurable: true });
    Object.defineProperty(container, 'scrollTop', { get: () => scrollTop, set: v => { scrollTop = v; }, configurable: true });
    for (const node of container.querySelectorAll('.vp-node')) {
        Object.defineProperty(node, 'offsetWidth', { value: 220, configurable: true });
        Object.defineProperty(node, 'offsetHeight', { value: 100, configurable: true });
    }
    window.visualPipeline.initCanvas('canvas', null, true);
    return { dom, container, vp: window.visualPipeline };
}

test('SITE-MOB-005: centring a stage clears the native scroll the browser added to the frame', () => {
    const { dom, container, vp } = loadVisualPipeline();
    const node = document.getElementById('vp-node-canvas-1');
    node.style.setProperty('--node-x', '400px');
    node.style.setProperty('--node-y', '40px');
    container.scrollLeft = 157;
    container.scrollTop = 12;

    vp.centerNode('canvas', 'vp-node-canvas-1');

    const state = vp._canvases.canvas;
    assert.equal(container.scrollLeft, 0, 'the frame keeps no native horizontal scroll');
    assert.equal(container.scrollTop, 0, 'the frame keeps no native vertical scroll');
    // With the scroll gone, the pan alone puts the node's centre on the frame's centre.
    assert.equal(state.panX, 353 / 2 - (400 + 110));
    assert.equal(state.panY, 300 / 2 - (40 + 50));
    const centreX = state.panX + (400 + 110) * state.scale - container.scrollLeft;
    assert.equal(centreX, 353 / 2);
    dom.window.close();
});

test('SITE-MOB-005: a native scroll of the frame is put back at once', () => {
    const { dom, container } = loadVisualPipeline();
    container.scrollLeft = 157;
    container.scrollTop = 30;
    container.dispatchEvent(new dom.window.Event('scroll'));
    assert.equal(container.scrollLeft, 0);
    assert.equal(container.scrollTop, 0);
    dom.window.close();
});

test('SITE-MOB-005: fit to screen also starts from an unscrolled frame', () => {
    const { dom, container, vp } = loadVisualPipeline();
    container.scrollLeft = 157;
    vp.fitView('canvas');
    assert.equal(container.scrollLeft, 0);
    dom.window.close();
});

test('SITE-MOB-006: the node width is one value in the CSS, the layout, the live edges and the C# edges', () => {
    const { dom, vp } = loadVisualPipeline();
    const css = fs.readFileSync(path.join(repo, 'src/Aetheus.Front/wwwroot/css/app.css'), 'utf8')
        .replace(/\/\*[\s\S]*?\*\//g, '');
    const widths = [...css.matchAll(/(^|[},])\s*\.vp-node\s*\{([^}]*)\}/g)]
        .map(rule => /(?:^|;)\s*width\s*:\s*([\d.]+)rem/.exec(rule[2]))
        .filter(Boolean)
        .map(match => Number(match[1]) * 16);
    const geometry = fs.readFileSync(path.join(repo, 'src/Aetheus.Front/Components/Pipelines/VisualPipelineGeometry.cs'), 'utf8');
    const csharp = Number(/const double NodeWidth = ([\d.]+);/.exec(geometry)[1]);

    // Every .vp-node width rule, a phone media query included: an 11rem one under 40rem squeezed the name out.
    assert.deepEqual(widths, [vp.NODE_WIDTH]);
    assert.equal(csharp, vp.NODE_WIDTH);

    // The live edge redraw anchors on that width.
    const from = document.getElementById('vp-node-canvas-0');
    const to = document.getElementById('vp-node-canvas-1');
    from.style.setProperty('--node-x', '10px');
    from.style.setProperty('--node-y', '0px');
    to.style.setProperty('--node-x', '400px');
    to.style.setProperty('--node-y', '0px');
    vp.setEdges('canvas', [{ from: 0, to: 1 }]);
    vp.redrawEdgesForNode('canvas', 0);
    const d = document.querySelector('path[data-from="0"]').getAttribute('d');
    assert.match(d, new RegExp(`^M ${(10 + vp.NODE_WIDTH).toFixed(1)} 50\\.0 `));
    dom.window.close();
});

test('SITE-MOB-006: the stage name keeps a readable width and the header wraps its badges instead', () => {
    const css = fs.readFileSync(path.join(repo, 'src/Aetheus.Front/wwwroot/css/app.css'), 'utf8')
        .replace(/\/\*[\s\S]*?\*\//g, '');
    const rule = selector => {
        const match = new RegExp(`(?:^|})\\s*${selector.replace(/[.]/g, '\\.')}\\s*\\{([^}]*)\\}`).exec(css);
        assert.ok(match, `${selector} rule missing`);
        return match[1];
    };
    assert.match(rule('.vp-node-header'), /flex-wrap\s*:\s*wrap/);
    assert.match(rule('.vp-node-header-title'), /min-width\s*:\s*5rem/);
});
