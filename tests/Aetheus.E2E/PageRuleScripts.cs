// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.E2E;

/// <summary>
/// The two browser-side functions of the page-rule sweep, kept as C# raw strings so that the selector guard of
/// <c>SelectorGuardTests</c> reads every app-owned class they target. Both are function expressions for
/// <c>IPage.EvaluateAsync</c>.
/// </summary>
internal static class PageRuleScripts
{
    /// <summary>
    /// Waits until the page stopped changing: the signature of <c>#main-content</c> (its scroll size and the number of
    /// its descendants) is unchanged for 300 ms and no loader, skeleton or pending grid is visible. Event based, never a
    /// fixed delay. Resolves <c>{ settled, ms, changes, busyOn, signature }</c>; <c>settled</c> is false when the page
    /// still changed after the budget, and the other fields say what kept it from settling.
    /// </summary>
    public const string Settle = """
        (maxMs = 8000, quietMs = 300) => new Promise(resolve => {
          const start = performance.now();
          const busySelector = '.aetheus-loader, .omni-skeleton, [aria-busy="true"], .omni-data-grid__pending, .omni-data-grid__preparation, .omni-data-grid__state--loading';
          const pendingElement = () => [...document.querySelectorAll(busySelector)].find(el => {
            const r = el.getBoundingClientRect();
            return r.width > 0 && r.height > 0 && getComputedStyle(el).visibility !== 'hidden';
          });
          const signature = () => {
            const main = document.getElementById('main-content') || document.body;
            return main.scrollHeight + '|' + main.scrollWidth + '|' + main.querySelectorAll('*').length;
          };
          let last = signature();
          let since = start;
          let changes = 0;
          let busyOn = '';
          const tick = () => {
            const now = performance.now();
            const current = signature();
            const pending = pendingElement();
            if (current !== last) { last = current; since = now; changes++; }
            if (pending) {
              since = now;
              const chain = [];
              for (let node = pending, i = 0; node && node.id !== 'main-content' && node !== document.body && i < 4; node = node.parentElement, i++)
                chain.unshift(node.tagName.toLowerCase() + [...node.classList].slice(0, 2).map(name => '.' + name).join(''));
              busyOn = chain.join('>');
            } else busyOn = '';
            const report = { ms: Math.round(now - start), changes, busyOn, signature: last };
            if (now - since >= quietMs) { resolve({ settled: true, ...report }); return; }
            if (now - start > maxMs) { resolve({ settled: false, ...report }); return; }
            setTimeout(tick, 50);
          };
          tick();
        })
        """;

    /// <summary>
    /// Measures the page that is on screen against the objectively measurable presentation rules (R-30) and returns
    /// <c>{ violations: [{ rule, element, detail }], grids: [{ grid, rowHeight, rows }], facts, path, width, height }</c>.
    /// An <c>element</c> is a stable class-based signature (no id, no data text); <c>detail</c> carries the measured
    /// values and is for the report only, never for the baseline key.
    /// </summary>
    public const string Measure = """
        () => {
          const W = window.innerWidth;
          const H = window.innerHeight;
          const main = document.getElementById('main-content') || document.body;
          const violations = [];
          const grids = [];
          const facts = {};
          const seen = new Set();
          const add = (rule, element, detail) => {
            const key = rule + '|' + element;
            if (seen.has(key)) return;
            seen.add(key);
            violations.push({ rule, element, detail: String(detail) });
          };

          const visible = (el) => {
            const rect = el.getBoundingClientRect();
            if (rect.width <= 0 || rect.height <= 0) return false;
            return getComputedStyle(el).visibility === 'visible';
          };
          const inFlow = (el) => {
            const position = getComputedStyle(el).position;
            return position !== 'absolute' && position !== 'fixed';
          };
          const dynamicClass = /^(omni-u-|is-|has-|viewport-|aetheus-grid-(virtualized|fullheight)|omni-data-grid--(striped|row-hover|virtual|fill|sizing|has-frozen)|omni-stack--|omni-row--|omni-tabs__panel--)|\d{3,}/;
          const one = (el) => el.tagName.toLowerCase() +
            [...el.classList].filter((name) => !dynamicClass.test(name)).slice(0, 3).map((name) => '.' + name).join('');
          const sig = (el, depth = 2) => {
            const parts = [];
            for (let node = el, i = 0; node && node !== main && node !== document.body && i <= depth; node = node.parentElement, i++)
              parts.unshift(one(node));
            return parts.join('>');
          };
          const px = (value) => Math.round(value * 10) / 10;
          const parse = (value) => parseFloat(value) || 0;
          const hasText = (el) => (el.textContent || '').trim().length > 0;

          const lineCount = (el) => {
            const range = document.createRange();
            range.selectNodeContents(el);
            const tops = [];
            for (const rect of range.getClientRects()) {
              if (rect.width < 1 || rect.height < 1) continue;
              if (!tops.some((top) => Math.abs(top - rect.top) < rect.height * 0.6)) tops.push(rect.top);
            }
            return tops.length;
          };
          const clipsX = (el) => ['hidden', 'clip', 'auto', 'scroll'].includes(getComputedStyle(el).overflowX);
          const chroma = (color) => {
            const m = /rgba?\(\s*([\d.]+)[,\s]+([\d.]+)[,\s]+([\d.]+)(?:[,\s/]+([\d.]+))?/.exec(color || '');
            if (!m) return 0;
            const alpha = m[4] === undefined ? 1 : parseFloat(m[4]);
            if (alpha < 0.2) return 0;
            const channels = [parseFloat(m[1]), parseFloat(m[2]), parseFloat(m[3])];
            return Math.max(...channels) - Math.min(...channels);
          };

          // Rule 5: nothing makes the page wider than the viewport.
          const docWidth = Math.max(document.documentElement.scrollWidth, document.body.scrollWidth);
          if (docWidth > W + 1) add('5.page-overflow', 'document', docWidth + '>' + W);
          if (main.scrollWidth > main.clientWidth + 1) add('5.page-overflow', '#main-content', main.scrollWidth + '>' + main.clientWidth);
          for (const blade of main.querySelectorAll('.blade-content'))
            if (visible(blade) && blade.scrollWidth > blade.clientWidth + 1)
              add('5.page-overflow', sig(blade), blade.scrollWidth + '>' + blade.clientWidth);
          const insideClip = (el) => {
            for (let node = el.parentElement; node && node !== main; node = node.parentElement)
              if (clipsX(node)) return true;
            return false;
          };
          const outside = [];
          for (const el of main.querySelectorAll('*')) {
            const rect = el.getBoundingClientRect();
            if (rect.width <= 0 || rect.height <= 0) continue;
            if (rect.right <= W + 1 && rect.left >= -1) continue;
            if (!visible(el) || insideClip(el)) continue;
            outside.push(el);
          }
          for (const el of outside)
            if (!outside.includes(el.parentElement))
              add('5.outside-viewport', sig(el), px(el.getBoundingClientRect().left) + '..' + px(el.getBoundingClientRect().right));

          // Rule 3: grids. Header on one line, no truncated header or cell, row height (compared across widths by the caller).
          const gridRoots = [...main.querySelectorAll('.omni-data-grid')].filter(visible);
          const gridNames = new Map();
          gridRoots.forEach((grid) => {
            const base = [...grid.classList].filter((name) => !/^(omni-data-grid|aetheus-)/.test(name) && !dynamicClass.test(name)).join('.') || 'grid';
            const count = (gridNames.get(base) || 0) + 1;
            gridNames.set(base, count);
            const name = count > 1 ? `${base}#${count}` : base;

            for (const title of grid.querySelectorAll('thead .omni-data-grid__title')) {
              if (!visible(title)) continue;
              const th = title.closest('th');
              const column = (th && th.getAttribute('data-omni-col')) || title.textContent.trim();
              if (lineCount(title) > 1) add('3.grid-header-wrapped', `${name} th[${column}]`, title.textContent.trim());
              for (let node = title; node && node !== th.parentElement; node = node.parentElement)
                if (clipsX(node) && node.scrollWidth > node.clientWidth + 1 && getComputedStyle(node).display !== 'inline') {
                  add('3.grid-header-truncated', `${name} th[${column}]`, node.scrollWidth + '>' + node.clientWidth);
                  break;
                }
            }

            const rows = [...grid.querySelectorAll('tbody tr[data-omni-row-index]')].filter(visible);
            for (const td of grid.querySelectorAll('tbody tr[data-omni-row-index] > td[data-omni-col]')) {
              const column = td.getAttribute('data-omni-label') || td.getAttribute('data-omni-col');
              for (const el of [td, ...td.querySelectorAll('*')]) {
                if (!clipsX(el) || el.scrollWidth <= el.clientWidth + 1 || !hasText(el)) continue;
                if (getComputedStyle(el).display === 'inline') continue;
                add('3.grid-cell-truncated', `${name} td[${column}]`, el.scrollWidth + '>' + el.clientWidth);
                break;
              }
            }
            const heights = rows.map((row) => row.getBoundingClientRect().height).sort((a, b) => a - b);
            grids.push({ grid: name, rowHeight: heights.length ? px(heights[Math.floor(heights.length / 2)]) : 0, rows: heights.length });
          });
          // Rule 3: a grid that is the main content of the page fills at least 75 % of the viewport and the page itself does not scroll.
          if (gridRoots.length === 1) {
            const rect = gridRoots[0].getBoundingClientRect();
            const name = grids[0].grid;
            const header = main.querySelector('.omni-page-header');
            const top = header ? header.getBoundingClientRect().bottom : main.getBoundingClientRect().top;
            const bottom = main.getBoundingClientRect().top - main.scrollTop + main.scrollHeight;
            const share = rect.height / Math.max(1, bottom - top);
            facts.gridShare = px(share);
            if (share >= 0.5) {
              if (rect.height < H * 0.75 - 1) add('3.grid-height', name, px(rect.height) + '<' + px(H * 0.75));
              if (main.scrollHeight > main.clientHeight + 2) add('3.page-scroll-with-grid', name, main.scrollHeight + '>' + main.clientHeight);
            }
          }

          // Rule 4: a text that wraps although its row or block has room for it on one line.
          const flowKids = (container) => [...container.children].filter((kid) => visible(kid) && inFlow(kid) && kid.tagName !== 'SCRIPT');
          const textLeaves = [];
          const skipText = 'pre, code, textarea, .monaco-editor, .omni-data-grid, .omni-markdown, .omni-code, svg';
          for (const el of main.querySelectorAll('*')) {
            if (el.closest(skipText) || !visible(el)) continue;
            let direct = false;
            for (const node of el.childNodes) if (node.nodeType === 3 && node.textContent.trim().length > 0) { direct = true; break; }
            if (!direct) continue;
            if ([...el.children].some((kid) => !['inline', 'inline-block', 'inline-flex', 'contents'].includes(getComputedStyle(kid).display) && kid.getBoundingClientRect().height > 0)) continue;
            textLeaves.push(el);
          }
          const maxContent = (el) => {
            const previous = [el.style.width, el.style.maxWidth, el.style.flex, el.style.minWidth];
            el.style.setProperty('width', 'max-content', 'important');
            el.style.setProperty('max-width', 'none', 'important');
            el.style.setProperty('min-width', '0', 'important');
            el.style.setProperty('flex', 'none', 'important');
            const width = el.getBoundingClientRect().width;
            el.style.width = previous[0];
            el.style.maxWidth = previous[1];
            el.style.flex = previous[2];
            el.style.minWidth = previous[3];
            return width;
          };
          const contentWidthOf = (el) => {
            const s = getComputedStyle(el);
            return el.getBoundingClientRect().width - parse(s.paddingLeft) - parse(s.paddingRight) - parse(s.borderLeftWidth) - parse(s.borderRightWidth);
          };
          for (const leaf of textLeaves) {
            const style = getComputedStyle(leaf);
            if (style.display === 'inline' || style.display === 'contents') continue;
            if (lineCount(leaf) < 2) continue;
            let child = leaf;
            let row = leaf.parentElement;
            for (let climbs = 0; row && row !== main && flowKids(row).length < 2 && climbs < 3; climbs++) { child = row; row = row.parentElement; }
            if (!row || row === main || flowKids(row).length < 2) {
              const wanted = maxContent(leaf);
              const available = contentWidthOf(leaf.parentElement);
              if (wanted <= available + 0.5 && leaf.getBoundingClientRect().width < wanted - 0.5) add('4.text-wrapped-with-room', sig(leaf), px(available - wanted) + 'px free in its block');
              continue;
            }
            const rowStyle = getComputedStyle(row);
            const sideBySide = rowStyle.display.includes('grid') || (rowStyle.display.includes('flex') && rowStyle.flexDirection.startsWith('row'));
            const available = contentWidthOf(row);
            if (sideBySide) {
              const kids = flowKids(row);
              const wanted = kids.reduce((sum, kid) => sum + maxContent(kid), 0) + parse(rowStyle.columnGap) * Math.max(0, kids.length - 1);
              if (wanted <= available + 0.5) add('4.text-wrapped-with-room', sig(leaf), px(available - wanted) + 'px free in its row');
            } else {
              const wanted = maxContent(child);
              if (wanted <= available + 0.5 && child.getBoundingClientRect().width < wanted - 0.5) add('4.text-wrapped-with-room', sig(leaf), px(available - wanted) + 'px free in its block');
            }
          }

          // Rule 2: sibling blocks are separated by the page gap (the declared row gap of the page, else the most common gap).
          const wrappers = '.omni-detail-shell, .omni-detail-shell__content, .blade-content, .omni-tabs, .omni-tabs__panels, .omni-tabs__panel, .app-body-wrapper';
          const edgeBox = (el, which) => {
            let node = el;
            for (let i = 0; i < 6 && node.matches(wrappers); i++) {
              const kids = flowKids(node);
              if (!kids.length) break;
              node = which === 'first' ? kids[0] : kids[kids.length - 1];
            }
            return node.getBoundingClientRect();
          };
          const allPairs = [];
          const declaredGaps = [];
          const walked = new Set();
          const analyse = (container) => {
            const kids = flowKids(container);
            const style = getComputedStyle(container);
            const column = style.display === 'block' || style.display === 'flow-root' || (style.display.includes('flex') && style.flexDirection.startsWith('column'));
            if (kids.length >= 2 && column) {
              if (style.rowGap !== 'normal' && (style.display.includes('flex') || style.display.includes('grid')) && parse(style.rowGap) > 0) declaredGaps.push(parse(style.rowGap));
              for (let i = 1; i < kids.length; i++) {
                const prev = edgeBox(kids[i - 1], 'last');
                const next = edgeBox(kids[i], 'first');
                const outer = { prev: kids[i - 1].getBoundingClientRect(), next: kids[i].getBoundingClientRect() };
                if (outer.next.top < outer.prev.bottom - 1 && outer.next.left < outer.prev.right && outer.next.right > outer.prev.left) continue;
                if (outer.next.top < outer.prev.top + 1) continue;
                allPairs.push({ id: sig(kids[i - 1], 1) + ' / ' + sig(kids[i], 1), gap: px(next.top - prev.bottom) });
              }
            }
            for (const kid of kids) if (kid.matches(wrappers) || (kids.length === 1 && kid.children.length > 0)) walk(kid);
          };
          const walk = (container) => {
            if (walked.has(container) || walked.size > 40) return;
            walked.add(container);
            analyse(container);
          };
          const root = main.querySelector('.app-body-wrapper');
          if (root) walk(root);
          const mode = (values) => {
            const counts = new Map();
            for (const value of values) counts.set(value, (counts.get(value) || 0) + 1);
            let best = 0;
            let winner = 0;
            for (const [value, n] of counts) if (n > best || (n === best && value > winner)) { best = n; winner = value; }
            return winner;
          };
          const reference = declaredGaps.length ? mode(declaredGaps.map(Math.round)) : mode(allPairs.filter((pair) => pair.gap >= 1).map((pair) => Math.round(pair.gap)));
          for (const pair of allPairs) {
            if (pair.gap < 1) add('2.blocks-touching', pair.id, '0px, page gap ' + reference + 'px');
            else if (reference > 0 && Math.abs(pair.gap - reference) > 1.5) add('2.block-gap-differs', pair.id, pair.gap + 'px, page gap ' + reference + 'px');
          }
          facts.gapPairs = allPairs.map((pair) => pair.gap).join(',') + ' ref ' + reference;

          // Rule 11: no coloured side stripe (a one-sided coloured border, an inset side shadow, a thin pseudo-element bar).
          const stripeHosts = /card|tile|alert|panel|banner|dialog|window|callout|notice|summary|hero/;
          for (const el of main.querySelectorAll('*')) {
            const rect = el.getBoundingClientRect();
            if (rect.width < 8 || rect.height < 12) continue;
            const style = getComputedStyle(el);
            const widths = { t: parse(style.borderTopWidth), r: parse(style.borderRightWidth), b: parse(style.borderBottomWidth), l: parse(style.borderLeftWidth) };
            const colors = { t: style.borderTopColor, r: style.borderRightColor, b: style.borderBottomColor, l: style.borderLeftColor };
            for (const side of ['l', 'r']) {
              const styleName = side === 'l' ? style.borderLeftStyle : style.borderRightStyle;
              if (widths[side] < 2 || styleName === 'none' || chroma(colors[side]) < 24) continue;
              const alone = widths.t === 0 || widths.b === 0 || widths[side] !== widths.t || colors[side] !== colors.t;
              if (alone) add('11.side-stripe', sig(el), 'border-' + (side === 'l' ? 'left' : 'right') + ' ' + widths[side] + 'px ' + colors[side]);
            }
            const shadow = style.boxShadow;
            if (shadow && shadow !== 'none') {
              for (const part of shadow.split(/,(?![^(]*\))/)) {
                const m = /(rgba?\([^)]+\))\s+(-?[\d.]+)px\s+(-?[\d.]+)px\s+([\d.]+)px\s+(-?[\d.]+)px\s*(inset)?/.exec(part.trim());
                if (m && m[6] && Math.abs(parseFloat(m[2])) >= 2 && parseFloat(m[3]) === 0 && parseFloat(m[4]) === 0 && chroma(m[1]) >= 24)
                  add('11.side-stripe', sig(el), 'inset shadow ' + part.trim());
              }
            }
            if (stripeHosts.test(typeof el.className === 'string' ? el.className : '')) {
              for (const pseudo of ['::before', '::after']) {
                const p = getComputedStyle(el, pseudo);
                if (p.content === 'none' || p.position !== 'absolute') continue;
                const w = parse(p.width);
                const h = parse(p.height);
                if (w >= 2 && w <= 8 && h >= rect.height * 0.6 && chroma(p.backgroundColor) >= 24)
                  add('11.side-stripe', sig(el), pseudo + ' bar ' + w + 'px');
              }
            }
          }

          // Rule 1: the primary action sits in the page header, aligned with the title; the header never holds a row of buttons.
          const headerButtons = [];
          for (const header of main.querySelectorAll('.omni-page-header')) {
            if (!visible(header) || header.closest('.omni-card, .omni-dialog, [role="dialog"]')) continue;
            const row = header.querySelector('.omni-page-header__row');
            const title = header.querySelector('.omni-page-header__title');
            if (!row) continue;
            const areas = row.querySelectorAll('.omni-page-header__actions, .omni-page-header__pinned');
            const controls = [];
            for (const area of areas)
              for (const control of area.querySelectorAll('button, a.omni-button, [role="button"]')) {
                if (!visible(control)) continue;
                if (control.matches('.omni-overflow-menu__trigger, .omni-split-button__toggle, .omni-select-bar__item, [aria-haspopup="menu"], [aria-pressed]')) continue;
                if (control.closest('.omni-overflow-menu__panel, .omni-split-button__menu, [role="menu"]')) continue;
                controls.push(control);
              }
            headerButtons.push(controls.map((control) => one(control) + '|' + (control.getAttribute('aria-label') || control.textContent || '').trim().slice(0, 30)));
            const owner = sig(header, 0);
            if (controls.length >= 2) add('1.header-button-row', owner, controls.length + ' buttons');
            if (title && controls.length >= 1 && W >= 768) {
              const tr = title.getBoundingClientRect();
              const titleCentre = tr.top + tr.height / 2;
              for (const control of controls) {
                const cr = control.getBoundingClientRect();
                if (Math.abs(cr.top + cr.height / 2 - titleCentre) > 12) add('1.header-action-not-aligned', owner + ' ' + one(control), px(cr.top + cr.height / 2 - titleCentre) + 'px');
              }
            }
          }
          facts.headerButtons = headerButtons;

          const primaryOutside = [];
          const headerHasPrimary = [...main.querySelectorAll('.omni-page-header .omni-page-header__actions .omni-button--primary, .omni-page-header .omni-page-header__pinned .omni-button--primary')].length > 0;
          for (const button of main.querySelectorAll('.omni-button--primary')) {
            if (!visible(button) || button.matches('[aria-pressed], .omni-select-bar__item')) continue;
            if (button.closest('.omni-page-header, .omni-data-grid, .omni-card, .omni-dialog, [role="dialog"], form, .omni-wizard, .omni-wizard__actions, .empty-state, .empty-state-actions, .not-found-container')) continue;
            primaryOutside.push(button);
          }
          if (!headerHasPrimary)
            for (const button of primaryOutside) add('1.primary-action-outside-header', sig(button, 2), 'no primary action in the page header');
          facts.primaryOutside = primaryOutside.length;
          facts.headerCount = main.querySelectorAll('.omni-page-header').length;
          facts.gridCount = gridRoots.length;

          return { violations, grids, facts, title: document.title, path: location.pathname, width: W, height: H };
        }
        """;
}
