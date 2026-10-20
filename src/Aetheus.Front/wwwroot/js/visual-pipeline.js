window.visualPipeline = {
    // The node width of .vp-node in app.css (13.75rem) and VisualPipelineGeometry.NodeWidth: one value,
    // or the layout, the edges and the drawn node disagree (SITE-MOB-006).
    NODE_WIDTH: 220,
    _canvases: {},
    _layoutCache: { key: null, result: null },

    computeLayout: function (stages) {
        if (typeof dagre === 'undefined') {
            console.error('dagre library not loaded');
            return { nodes: [], edges: [] };
        }

        // Memoize: skip re-computation when structure hasn't changed
        const cacheKey = JSON.stringify(stages);
        if (this._layoutCache.key === cacheKey) {
            return this._layoutCache.result;
        }

        const g = new dagre.graphlib.Graph();
        g.setGraph({
            rankdir: 'LR',
            nodesep: 60,
            ranksep: 100,
            marginx: 40,
            marginy: 40
        });
        g.setDefaultEdgeLabel(function () { return {}; });

        for (const stage of stages) {
            const height = 100 + Math.max(0, (stage.stepCount - 1)) * 28;
            g.setNode(stage.id, { label: stage.name, width: this.NODE_WIDTH, height: height });
        }

        for (const stage of stages) {
            if (stage.dependsOn) {
                for (const dep of stage.dependsOn) {
                    const depNode = stages.find(s => s.name === dep);
                    if (depNode) {
                        g.setEdge(depNode.id, stage.id);
                    }
                }
            }
        }

        dagre.layout(g);

        const nodes = [];
        g.nodes().forEach(function (id) {
            const node = g.node(id);
            if (node) {
                nodes.push({
                    id: id,
                    x: node.x - node.width / 2,
                    y: node.y - node.height / 2,
                    width: node.width,
                    height: node.height
                });
            }
        });

        const edges = [];
        g.edges().forEach(function (e) {
            const edge = g.edge(e);
            if (edge && edge.points) {
                edges.push({
                    from: e.v,
                    to: e.w,
                    points: edge.points.map(function (p) { return { x: p.x, y: p.y }; })
                });
            }
        });

        const result = { nodes: nodes, edges: edges };
        this._layoutCache = { key: cacheKey, result: result };
        return result;
    },

    initCanvas: function (elementId, dotNetRef, _isDark) {
        const container = document.getElementById(elementId);
        if (!container) return;

        const inner = container.querySelector('.vp-canvas-inner');
        if (!inner) return;

        const state = {
            scale: 1,
            panX: 0,
            panY: 0,
            isPanning: false,
            startX: 0,
            startY: 0,
            dotNetRef: dotNetRef,
            inner: inner,
            container: container
        };

        this._canvases[elementId] = state;

        // SITE-MOB-005: the canvas is panned by a transform only. overflow: hidden still lets the browser
        // scroll it natively (focusing a node outside the frame, a resize), and that offset added to the pan
        // pushed a centred stage out of the frame. Native scroll is put back to zero whenever it moves.
        container.addEventListener('scroll', function () {
            window.visualPipeline._resetNativeScroll(state);
        });

        container.addEventListener('wheel', function (e) {
            e.preventDefault();
            const delta = e.deltaY > 0 ? -0.1 : 0.1;
            state.scale = Math.min(2, Math.max(0.25, state.scale + delta));
            inner.style.transform = 'translate(' + state.panX + 'px, ' + state.panY + 'px) scale(' + state.scale + ')';
        }, { passive: false });

        container.addEventListener('pointerdown', function (e) {
            if (e.target === container || e.target === inner || e.target.classList.contains('vp-edge-layer')) {
                state.isPanning = true;
                state.startX = e.clientX - state.panX;
                state.startY = e.clientY - state.panY;
                container.setPointerCapture(e.pointerId);
                container.style.cursor = 'grabbing';
                if (dotNetRef) {
                    dotNetRef.invokeMethodAsync('OnCanvasClicked');
                }
            }
        });

        container.addEventListener('pointermove', function (e) {
            if (!state.isPanning) return;
            state.panX = e.clientX - state.startX;
            state.panY = e.clientY - state.startY;
            inner.style.transform = 'translate(' + state.panX + 'px, ' + state.panY + 'px) scale(' + state.scale + ')';
        });

        container.addEventListener('pointerup', function (e) {
            if (state.isPanning) {
                state.isPanning = false;
                container.releasePointerCapture(e.pointerId);
                container.style.cursor = 'grab';
            }
        });

        container.style.cursor = 'grab';
    },

    enableDrag: function (nodeElementId, stageId, dotNetRef) {
        const node = document.getElementById(nodeElementId);
        if (!node || node.dataset.vpDragBound === 'true') return;
        node.dataset.vpDragBound = 'true';

        const handle = node.querySelector('.vp-node-drag-handle');
        if (!handle) return;

        let isDragging = false;
        let moved = false;
        let startX, startY, origLeft, origTop;

        handle.style.cursor = 'grab';

        // Position is driven by the --node-x / --node-y custom properties (CSS maps them to
        // left/top). Read and write those, NOT node.style.left, which is never set.
        const readVar = function (name) {
            return parseFloat(node.style.getPropertyValue(name)) || 0;
        };

        handle.addEventListener('pointerdown', function (e) {
            e.stopPropagation();
            isDragging = true;
            moved = false;
            startX = e.clientX;
            startY = e.clientY;
            origLeft = readVar('--node-x');
            origTop = readVar('--node-y');
            handle.setPointerCapture(e.pointerId);
            handle.style.cursor = 'grabbing';
        });

        handle.addEventListener('pointermove', function (e) {
            if (!isDragging) return;

            const parentId = node.closest('.vp-canvas-outer')?.id;
            const state = parentId ? window.visualPipeline._canvases[parentId] : null;
            const scale = state ? state.scale : 1;

            const dx = (e.clientX - startX) / scale;
            const dy = (e.clientY - startY) / scale;
            if (Math.abs(dx) < 3 && Math.abs(dy) < 3) return;
            moved = true;
            node.style.setProperty('--node-x', (origLeft + dx) + 'px');
            node.style.setProperty('--node-y', (origTop + dy) + 'px');

            // S-UX-VPLE: redraw the edges touching this node live, instead of only at pointerup.
            if (parentId) window.visualPipeline.redrawEdgesForNode(parentId, stageId);
        });

        handle.addEventListener('pointerup', function (e) {
            if (!isDragging) return;
            isDragging = false;
            handle.releasePointerCapture(e.pointerId);
            handle.style.cursor = 'grab';

            const newX = readVar('--node-x');
            const newY = readVar('--node-y');

            if (moved && dotNetRef) {
                dotNetRef.invokeMethodAsync('OnNodeMoved', stageId, newX, newY);
            }
        });

        handle.addEventListener('pointercancel', function () {
            isDragging = false;
            handle.style.cursor = 'grab';
        });
    },

    zoomTo: function (elementId, scale) {
        const state = this._canvases[elementId];
        if (!state) return;
        state.scale = Math.min(2, Math.max(0.25, scale));
        state.inner.style.transform = 'translate(' + state.panX + 'px, ' + state.panY + 'px) scale(' + state.scale + ')';
    },

    fitView: function (elementId) {
        const state = this._canvases[elementId];
        if (!state) return;
        this._resetNativeScroll(state);

        const containerRect = state.container.getBoundingClientRect();
        const nodes = state.inner.querySelectorAll('.vp-node');
        if (nodes.length === 0) return;

        let minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
        nodes.forEach(function (n) {
            const l = parseFloat(n.style.getPropertyValue('--node-x')) || 0;
            const t = parseFloat(n.style.getPropertyValue('--node-y')) || 0;
            const w = n.offsetWidth;
            const h = n.offsetHeight;
            minX = Math.min(minX, l);
            minY = Math.min(minY, t);
            maxX = Math.max(maxX, l + w);
            maxY = Math.max(maxY, t + h);
        });

        const graphW = maxX - minX + 80;
        const graphH = maxY - minY + 80;
        const scaleX = containerRect.width / graphW;
        const scaleY = containerRect.height / graphH;
        state.scale = Math.min(1.5, Math.max(0.25, Math.min(scaleX, scaleY)));
        state.panX = (containerRect.width - graphW * state.scale) / 2 - minX * state.scale + 40;
        state.panY = (containerRect.height - graphH * state.scale) / 2 - minY * state.scale + 40;
        state.inner.style.transform = 'translate(' + state.panX + 'px, ' + state.panY + 'px) scale(' + state.scale + ')';
    },

    _resetNativeScroll: function (state) {
        if (state.container.scrollLeft !== 0) state.container.scrollLeft = 0;
        if (state.container.scrollTop !== 0) state.container.scrollTop = 0;
    },

    getScale: function (elementId) {
        const state = this._canvases[elementId];
        return state ? state.scale : 1;
    },

    // S-TECH-VPNH: real measured node heights keyed by node index, so C# anchors edges to what is rendered.
    getNodeHeights: function (elementId) {
        const state = this._canvases[elementId];
        const map = {};
        if (!state) return map;
        state.inner.querySelectorAll('.vp-node').forEach(function (n) {
            const idx = n.id.substring(n.id.lastIndexOf('-') + 1);
            map[idx] = n.offsetHeight;
        });
        return map;
    },

    // S-UX-VPLE: remember the edge topology (source/target node indices) so a drag can redraw connectors live.
    setEdges: function (elementId, edges) {
        const state = this._canvases[elementId];
        if (state) state.edges = edges || [];
    },

    _edgePath: function (x1, y1, x2, y2) {
        const dx = Math.max(40, Math.abs(x2 - x1) * 0.5);
        return 'M ' + x1.toFixed(1) + ' ' + y1.toFixed(1)
            + ' C ' + (x1 + dx).toFixed(1) + ' ' + y1.toFixed(1)
            + ' ' + (x2 - dx).toFixed(1) + ' ' + y2.toFixed(1)
            + ' ' + x2.toFixed(1) + ' ' + y2.toFixed(1);
    },

    _nodeRect: function (canvasId, idx) {
        const n = document.getElementById('vp-node-' + canvasId + '-' + idx);
        if (!n) return null;
        return {
            x: parseFloat(n.style.getPropertyValue('--node-x')) || 0,
            y: parseFloat(n.style.getPropertyValue('--node-y')) || 0,
            h: n.offsetHeight
        };
    },

    redrawEdgesForNode: function (canvasId, movedIdx) {
        const state = this._canvases[canvasId];
        if (!state || !state.edges) return;
        const layer = state.container.querySelector('.vp-edge-layer');
        if (!layer) return;
        const self = this;
        state.edges.forEach(function (e) {
            if (e.from !== movedIdx && e.to !== movedIdx) return;
            const f = self._nodeRect(canvasId, e.from);
            const t = self._nodeRect(canvasId, e.to);
            if (!f || !t) return;
            const path = layer.querySelector('path[data-from="' + e.from + '"][data-to="' + e.to + '"]');
            if (path) path.setAttribute('d', self._edgePath(f.x + self.NODE_WIDTH, f.y + f.h / 2, t.x, t.y + t.h / 2));
        });
    },

    // S-DES-VPSC: pan the canvas so the given node sits in the middle of the viewport.
    centerNode: function (elementId, nodeElementId) {
        const state = this._canvases[elementId];
        const node = document.getElementById(nodeElementId);
        if (!state || !node) return;
        this._resetNativeScroll(state);
        const x = parseFloat(node.style.getPropertyValue('--node-x')) || 0;
        const y = parseFloat(node.style.getPropertyValue('--node-y')) || 0;
        const rect = state.container.getBoundingClientRect();
        state.panX = rect.width / 2 - (x + node.offsetWidth / 2) * state.scale;
        state.panY = rect.height / 2 - (y + node.offsetHeight / 2) * state.scale;
        state.inner.style.transform = 'translate(' + state.panX + 'px, ' + state.panY + 'px) scale(' + state.scale + ')';
    },

    dispose: function (elementId) {
        delete this._canvases[elementId];
    }
};

// Applies node data coordinates through CSSOM to comply with CSP. Only coordinate changes update
// the position, preserving drag results across unrelated renders.
(function applyNodePositions() {
    function apply(node) {
        if (!node || !node.dataset) return;
        const x = node.dataset.nodeX;
        const y = node.dataset.nodeY;
        if (x !== undefined) node.style.setProperty('--node-x', x + 'px');
        if (y !== undefined) node.style.setProperty('--node-y', y + 'px');
    }

    function applyAll(root) {
        if (!root || !root.querySelectorAll) return;
        if (root.matches && root.matches('.stage-node-positioned')) apply(root);
        root.querySelectorAll('.stage-node-positioned').forEach(apply);
    }

    const observer = new MutationObserver(function (mutations) {
        mutations.forEach(function (mutation) {
            if (mutation.type === 'attributes') { apply(mutation.target); return; }
            mutation.addedNodes.forEach(applyAll);
        });
    });

    function start() {
        applyAll(document.body);
        observer.observe(document.body, {
            childList: true,
            subtree: true,
            attributes: true,
            attributeFilter: ['data-node-x', 'data-node-y']
        });
    }

    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start);
    else start();
})();
