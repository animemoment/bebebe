(() => {
"use strict";

const $ = id => document.getElementById(id);
const canvas = $("graph"), ctx = canvas.getContext("2d");
const minimapCanvas = $("minimapCanvas"), miniCtx = minimapCanvas ? minimapCanvas.getContext("2d") : null;
const tooltip = $("nodeTooltip"), edgeTooltip = $("edgeTooltip"), toast = $("toast");

// Core State
let data = null, nodes = [], edges = [];
let visibleNodesCache = [], visibleEdgesCache = [], visibleSet = new Set();
let selected = null, hovered = null, hoveredEdge = null, dragging = null;
let pan = { x: 0, y: 0 }, scale = 1;
let filterSeverity = "all", lastPointer = { x: 0, y: 0 };
let isPaused = true, simAlpha = 0;
let renderScheduled = false;

// Extended 50+ Feature State
let isolatedNode = null;
let activeCycleNodes = new Set();
let activePathNodes = new Set(), activePathEdges = new Set();
let nodeSpacing = 180;
let neighborhoodDepth = 0;
let heatmapMode = "default";
let quickFilter = "all";
let useRegex = false;
let showArrows = true;
let curvedEdges = true;
let showAllLabels = false;
let minimapVisible = true;
let fps = 60, lastFrameTime = performance.now(), frameCount = 0;

// What-If & Advanced Profiling State
let whatIfActive = false;
let cutEdges = new Set(); // key format: "source->target"
let appliedIgnores = [];
let originalHealthScore = 100;
let simulatedCycles = [];
let benchmarkBottlenecksOnly = false;

// Local Storage Collections
const FAVORITES_KEY = "dg_favorites_v2";
const NOTES_KEY = "dg_notes_v2";
let favorites = new Set(JSON.parse(localStorage.getItem(FAVORITES_KEY) || "[]"));
let nodeNotes = new Map(Object.entries(JSON.parse(localStorage.getItem(NOTES_KEY) || "{}")));

const confidenceRank = { high: 2, medium: 1, low: 0 };

function setStatus(t) {
    const el = $("status");
    if (el) el.textContent = t;
}

function esc(s) {
    return String(s ?? "").replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
}

function showToast(msg) {
    if (!toast) return;
    toast.textContent = msg;
    toast.classList.remove("hidden");
    clearTimeout(toast._timer);
    toast._timer = setTimeout(() => toast.classList.add("hidden"), 3200);
}

// 0% CPU Idle On-Demand Canvas Rendering
function requestRender() {
    if (!renderScheduled) {
        renderScheduled = true;
        requestAnimationFrame(() => {
            renderScheduled = false;
            draw();
            drawMinimap();
        });
    }
}

// Theme Management
function setTheme(theme) {
    document.body.setAttribute("data-theme", theme);
    localStorage.setItem("dg_theme", theme);
    if ($("themeSelector")) $("themeSelector").value = theme;
    requestRender();
}

const savedTheme = localStorage.getItem("dg_theme") || "dark";
setTheme(savedTheme);

// Multi-hop neighborhood calculator (BFS)
function getNeighborhoodSet(centerNode, depth) {
    if (!centerNode || depth <= 0) return null;
    const visited = new Set([centerNode.id]);
    let currentQueue = [centerNode.id];

    for (let d = 0; d < depth; d++) {
        const nextQueue = [];
        for (const currId of currentQueue) {
            for (let i = 0; i < edges.length; i++) {
                const e = edges[i];
                if (e.source === currId && !visited.has(e.target)) {
                    visited.add(e.target);
                    nextQueue.push(e.target);
                } else if (e.target === currId && !visited.has(e.source)) {
                    visited.add(e.source);
                    nextQueue.push(e.source);
                }
            }
        }
        currentQueue = nextQueue;
        if (!currentQueue.length) break;
    }
    return visited;
}

// Master Filtering Pipeline
function updateFilters() {
    if (!nodes.length) return;
    const rawQ = $("search").value.trim();
    let queryRegex = null;

    if (rawQ) {
        if (useRegex) {
            try {
                queryRegex = new RegExp(rawQ, "i");
            } catch (e) {
                queryRegex = null;
            }
        } else {
            queryRegex = new RegExp(rawQ.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'), "i");
        }
    }

    const showCS = $("showCS").checked;
    const showGD = $("showGD").checked;
    const showScene = $("showScene").checked;
    const selectedFolder = $("folderFilter").value;

    let baseNodes = nodes.filter(n => {
        // Language filter
        if (n.language === "C#" && !showCS) return false;
        if (n.language === "GDScript" && !showGD) return false;
        if (n.language === "Scene" && !showScene) return false;

        // Folder filter
        if (selectedFolder !== "all" && n.folder !== selectedFolder) return false;

        // Quick filter pills
        if (quickFilter === "cycles" && !n.in_cycle) return false;
        if (quickFilter === "god" && !n.is_god_object) return false;
        if (quickFilter === "orphans" && !n.is_orphan) return false;
        if (quickFilter === "dead" && n.orphan_status !== "truly_dead") return false;
        if (quickFilter === "hot" && !(n.functions_detail || []).some(f => f.is_hot_path) && !(data?.hot_paths || []).some(h => h.file === n.id)) return false;

        // Search match
        if (queryRegex) {
            const matchTarget = `${n.id} ${n.classes.join(" ")} ${n.namespace || ""}`;
            if (!queryRegex.test(matchTarget)) return false;
        }

        return true;
    });

    // Subsystem isolation filter
    if (isolatedNode) {
        const connectedSet = new Set([isolatedNode.id]);
        edges.forEach(e => {
            if (e.source === isolatedNode.id) connectedSet.add(e.target);
            if (e.target === isolatedNode.id) connectedSet.add(e.source);
        });
        baseNodes = baseNodes.filter(n => connectedSet.has(n.id));
    }

    // Multi-hop neighborhood depth filter
    if (selected && neighborhoodDepth > 0) {
        const nSet = getNeighborhoodSet(selected, neighborhoodDepth);
        if (nSet) {
            baseNodes = baseNodes.filter(n => nSet.has(n.id));
        }
    }

    visibleNodesCache = baseNodes;
    visibleSet = new Set(visibleNodesCache.map(n => n.id));

    const edgeType = $("edgeFilter").value;
    const minRank = Number($("minConfidence").value);

    visibleEdgesCache = edges.filter(e => {
        if (!visibleSet.has(e.source) || !visibleSet.has(e.target)) return false;
        if (edgeType !== "all" && e.type !== edgeType) return false;
        if (confidenceRank[e.confidence] < minRank) return false;
        return true;
    });

    $("visibleCount").textContent = `${visibleNodesCache.length.toLocaleString()} узлов · ${visibleEdgesCache.length.toLocaleString()} связей`;
    requestRender();
}

function setSimPaused(paused) {
    isPaused = paused;
    const txt = isPaused ? "▶ Старт" : "⏸ Стоп";
    if ($("toggleSim")) $("toggleSim").textContent = txt;
    if ($("toggleSimCanvas")) $("toggleSimCanvas").textContent = isPaused ? "▶" : "⏸";
    if (!isPaused) {
        simAlpha = 0.65;
        runPhysicsStep();
    }
}

function resize() {
    const r = canvas.getBoundingClientRect(), dpr = window.devicePixelRatio || 1;
    canvas.width = Math.max(1, Math.floor(r.width * dpr));
    canvas.height = Math.max(1, Math.floor(r.height * dpr));
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    requestRender();
}
window.addEventListener("resize", resize);

// Color Heatmap Computations
function getNodeColor(n) {
    if (heatmapMode === "profiling") {
        const ms = n.profiling?.ms_per_frame ?? 0;
        if (n.profiling?.is_bottleneck || ms >= 4.0) return "#ef4444"; // bottleneck red
        if (ms >= 1.5) return "#f59e0b"; // amber
        if (ms >= 0.4) return "#fbbf24"; // yellow
        if (ms >= 0.05) return "#38bdf8"; // blue
        return "#334155"; // low / grey
    }
    if (heatmapMode === "mutations") {
        const mutCount = (n.functions_detail || []).reduce((acc, f) => acc + (f.mutations?.length || 0), 0);
        if (mutCount >= 10) return "#ec4899"; // hot pink
        if (mutCount >= 4) return "#f59e0b";  // orange
        if (mutCount > 0) return "#38bdf8";   // cyan
        return "#334155";
    }
    if (heatmapMode === "instability") {
        // Robert C. Martin Instability I: 0 (stable, green) -> 1 (unstable, red)
        const i = n.instability ?? 0.5;
        if (i < 0.25) return "#22c55e"; // bright green
        if (i < 0.5) return "#84cc16";  // lime
        if (i < 0.75) return "#f59e0b"; // amber
        return "#ef4444";               // crimson
    }
    if (heatmapMode === "blast") {
        const b = n.blast_radius ?? 0;
        if (b === 0) return "#64748b";
        if (b < 4) return "#38bdf8";
        if (b < 10) return "#fbbf24";
        return "#f43f5e";
    }
    if (heatmapMode === "loc") {
        const lines = n.lines || 0;
        if (lines < 150) return "#67e8f9";
        if (lines < 400) return "#38bdf8";
        if (lines < 800) return "#f59e0b";
        return "#ef4444";
    }
    if (heatmapMode === "centrality") {
        const deg = n.degree || 0;
        if (deg < 3) return "#64748b";
        if (deg < 8) return "#38bdf8";
        if (deg < 15) return "#a855f7";
        return "#ec4899"; // God-Object hot magenta
    }
    if (heatmapMode === "layer") {
        const lyr = n.layer || "domain";
        if (lyr === "autoload") return "#c084fc";
        if (lyr === "presentation") return "#38bdf8";
        if (lyr === "infrastructure") return "#fbbf24";
        if (lyr === "core") return "#34d399";
        return "#60a5fa"; // domain
    }
    if (heatmapMode === "folder") {
        let hash = 0;
        for (let i = 0; i < n.folder.length; i++) hash = n.folder.charCodeAt(i) + ((hash << 5) - hash);
        const hue = Math.abs(hash % 360);
        return `hsl(${hue}, 70%, 60%)`;
    }
    if (heatmapMode === "dead") {
        if (n.orphan_status === "truly_dead") return "#ef4444";
        if (n.orphan_status === "scene_entry_point") return "#a855f7";
        return "#334155";
    }
    if (heatmapMode === "hot") {
        const isHot = (n.functions_detail || []).some(f => f.is_hot_path) || (data?.hot_paths || []).some(h => h.file === n.id) || (n.functions || []).some(f => ["_process", "_physics_process", "Update", "FixedUpdate"].includes(f));
        return isHot ? "#f59e0b" : "#334155";
    }

    // Default by language
    if (n.language === "C#") return "#61a9ff";
    if (n.language === "GDScript") return "#54d6b0";
    return "#e879f9"; // Scene
}

// Graph Data Initialization & What-If Engine
function edgeKey(source, target) {
    return `${source}->${target}`;
}

function distToSegment(px, py, x1, y1, x2, y2) {
    const dx = x2 - x1, dy = y2 - y1;
    const l2 = dx * dx + dy * dy;
    if (l2 === 0) return Math.hypot(px - x1, py - y1);
    let t = ((px - x1) * dx + (py - y1) * dy) / l2;
    t = Math.max(0, Math.min(1, t));
    return Math.hypot(px - (x1 + t * dx), py - (y1 + t * dy));
}

function computeSCC(nodeList, edgeList) {
    let index = 0;
    const indices = new Map();
    const lowlink = new Map();
    const onStack = new Map();
    const stack = [];
    const sccs = [];

    const adj = new Map();
    nodeList.forEach(n => adj.set(n.id, []));
    edgeList.forEach(e => {
        if (adj.has(e.source)) adj.get(e.source).push(e.target);
    });

    function strongConnect(u) {
        indices.set(u, index);
        lowlink.set(u, index);
        index++;
        stack.push(u);
        onStack.set(u, true);

        const neighbors = adj.get(u) || [];
        for (const v of neighbors) {
            if (!indices.has(v)) {
                strongConnect(v);
                lowlink.set(u, Math.min(lowlink.get(u), lowlink.get(v)));
            } else if (onStack.get(v)) {
                lowlink.set(u, Math.min(lowlink.get(u), indices.get(v)));
            }
        }

        if (lowlink.get(u) === indices.get(u)) {
            const scc = [];
            let w;
            do {
                w = stack.pop();
                onStack.set(w, false);
                scc.push(w);
            } while (w !== u);
            if (scc.length > 1) {
                sccs.push(scc);
            }
        }
    }

    for (const n of nodeList) {
        if (!indices.has(n.id)) {
            strongConnect(n.id);
        }
    }
    return sccs;
}

function recalculateWhatIf() {
    if (!data) return;
    const remainingEdges = edges.filter(e => !cutEdges.has(edgeKey(e.source, e.target)));
    const sccs = computeSCC(nodes, remainingEdges);
    simulatedCycles = sccs;

    const initialCycles = data.stats.cycles || 0;
    const remainingCyclesCount = sccs.length;
    const brokenCycles = Math.max(0, initialCycles - remainingCyclesCount);

    let simHealth = originalHealthScore;
    if (initialCycles > 0) {
        const cyclePenalty = 30;
        const recovered = (brokenCycles / initialCycles) * cyclePenalty;
        simHealth = Math.min(100, Math.round(originalHealthScore + recovered));
    }

    if ($("whatIfStats")) {
        $("whatIfStats").innerHTML = `Разорвано связей: <b>${cutEdges.size}</b> · Оставшихся циклов: <b>${remainingCyclesCount}</b> (ликвидировано: <b>${brokenCycles}</b>) · Прогноз здоровья: <b>${simHealth}%</b>`;
    }
    if ($("healthScoreVal")) {
        $("healthScoreVal").textContent = cutEdges.size > 0 ? `${simHealth}% (симуляция)` : `${originalHealthScore}%`;
    }
    const hBadge = $("healthScoreBadge");
    if (hBadge) {
        hBadge.classList.remove("warn", "bad");
        if (simHealth < 50) hBadge.classList.add("bad");
        else if (simHealth < 75) hBadge.classList.add("warn");
    }

    activeCycleNodes.clear();
    sccs.forEach(comp => comp.forEach(id => activeCycleNodes.add(id)));

    if (selected) inspect(selected);
    requestRender();
}

function setWhatIfMode(active) {
    whatIfActive = active;
    const banner = $("whatIfBanner");
    const btn = $("whatIfToggleBtn");
    if (banner) banner.classList.toggle("hidden", !whatIfActive);
    if (btn) btn.classList.toggle("active", whatIfActive);

    if (whatIfActive) {
        showToast("✂️ Режим What-If включен! Кликайте по связям на графе или кнопкам ✂️ в инспекторе");
        recalculateWhatIf();
    } else {
        cutEdges.clear();
        recalculateWhatIf();
        $("healthScoreVal").textContent = `${originalHealthScore}%`;
        showToast("Режим What-If завершен");
    }
    requestRender();
}

function initGraph(d) {
    data = d;
    if (d && d.root !== "Demo Project" && (!d.version || d.version !== "2.1.0")) {
        console.warn("Outdated backend version detected:", d.version);
        showToast("⚠️ Обнаружен ответ от старого процесса! Закройте предыдущие app.exe в Диспетчере задач и перезапустите DependencyGraphVisualizer.exe", 10000);
    }
    selected = null;
    hovered = null;
    hoveredEdge = null;
    isolatedNode = null;
    activeCycleNodes.clear();
    activePathNodes.clear();
    activePathEdges.clear();
    cutEdges.clear();
    whatIfActive = false;
    $("whatIfBanner")?.classList.add("hidden");
    $("whatIfToggleBtn")?.classList.remove("active");
    resize();

    const W = canvas.clientWidth || 1000, H = canvas.clientHeight || 700;
    nodes = d.files.map((f, i) => {
        const parts = f.path.split("/");
        const folder = parts.length > 1 ? parts.slice(0, parts.length - 1).join("/") : "root";
        return {
            ...f,
            folder,
            x: W / 2, y: H / 2, vx: 0, vy: 0, pinned: false,
            r: Math.max(7, Math.min(18, 6 + Math.log2((f.degree || 0) + 1) * 1.5))
        };
    });

    const map = new Map(nodes.map(n => [n.id, n]));
    edges = d.edges.map(e => ({ ...e, a: map.get(e.source), b: map.get(e.target) })).filter(e => e.a && e.b);

    // Populate Folders dropdown
    const folders = [...new Set(nodes.map(n => n.folder))].sort();
    $("folderFilter").innerHTML = '<option value="all">Все папки проекта</option>' +
        folders.map(fld => `<option value="${esc(fld)}">${esc(fld)}</option>`).join("");

    // Populate Edge Types dropdown
    const types = [...new Set(d.edges.map(e => e.type))].sort();
    $("edgeFilter").innerHTML = '<option value="all">Все типы</option>' +
        types.map(t => `<option value="${esc(t)}">${esc(t)}</option>`).join("");

    // Populate Path Finder dropdowns & Node Comparator dropdowns
    const sortedOptions = [...nodes].sort((a,b) => a.id.localeCompare(b.id));
    const optsHtml = sortedOptions.map(n => `<option value="${esc(n.id)}">${esc(n.id)}</option>`).join("");
    $("pathStartNode").innerHTML = '<option value="">Начальный узел (A)...</option>' + optsHtml;
    $("pathEndNode").innerHTML = '<option value="">Конечный узел (B)...</option>' + optsHtml;
    $("compareNodeA").innerHTML = '<option value="">Первый узел...</option>' + optsHtml;
    $("compareNodeB").innerHTML = '<option value="">Второй узел...</option>' + optsHtml;

    // Applied ignores note
    appliedIgnores = d.applied_ignores || [];
    if ($("appliedIgnoreNote")) {
        if (appliedIgnores.length > 0) {
            const preview = appliedIgnores.slice(0, 3).join(", ") + (appliedIgnores.length > 3 ? ` (+${appliedIgnores.length - 3})` : "");
            $("appliedIgnoreNote").textContent = `Исключено правил: ${appliedIgnores.length} (${preview})`;
        } else {
            $("appliedIgnoreNote").textContent = "Исключений не задано (.graphignore пуст)";
        }
    }

    // Stats Grid
    $("stats").innerHTML = [
        ["Файлы", d.stats.files],
        ["Связи", d.stats.edges],
        ["Строки", d.stats.lines],
        ["C#", d.stats.csharp],
        ["GDScript", d.stats.gdscript],
        ["Сцены", d.stats.scenes || 0],
        ["Циклы", d.stats.cycles],
        ["Hubs", d.stats.god_objects || 0]
    ].map(([a, b]) => `<div class="stat"><b>${Number(b || 0).toLocaleString()}</b><span>${a}</span></div>`).join("");

    // Architecture Health Score
    originalHealthScore = d.health_score ?? 100;
    const hScore = originalHealthScore;
    $("healthScoreVal").textContent = `${hScore}%`;
    const hBadge = $("healthScoreBadge");
    hBadge.classList.remove("warn", "bad");
    if (hScore < 50) hBadge.classList.add("bad");
    else if (hScore < 75) hBadge.classList.add("warn");

    $("findingCount").textContent = d.findings.length;
    $("quickStatsBadge").textContent = d.stats.files;
    $("canvasHint").classList.add("hidden");

    renderFindings();
    renderFavorites();
    updateFilters();
    layoutNodes();
    fitGraph();
    setStatus(`${d.stats.files} файлов · ${d.stats.edges} связей`);
    requestRender();
}

// Layout Modes Engine (5 Structural Layouts)
function layoutNodes() {
    if (!nodes.length) return;
    const W = canvas.clientWidth || 1000, H = canvas.clientHeight || 700;
    const mode = $("layout").value;
    const spacing = nodeSpacing;

    if ($("activeLayoutName")) {
        const layoutLabels = {
            force: "Силовая органика",
            modules: "Кластеры по папкам",
            radial: "Концентрические орбиты",
            hierarchy: "Архитектурные слои",
            grid: "Сетка каталога"
        };
        $("activeLayoutName").textContent = layoutLabels[mode] || mode;
    }

    if (mode === "modules") {
        // Module Folder Cluster Layout
        const folderMap = new Map();
        nodes.forEach(n => {
            if (!folderMap.has(n.folder)) folderMap.set(n.folder, []);
            folderMap.get(n.folder).push(n);
        });

        const folderList = Array.from(folderMap.keys()).sort();
        const numFolders = folderList.length;
        const cols = Math.ceil(Math.sqrt(numFolders * 1.5));
        const clusterGap = Math.max(260, spacing * 2.0);

        folderList.forEach((fldName, fIdx) => {
            const fCol = fIdx % cols;
            const fRow = Math.floor(fIdx / cols);
            const cx = 160 + fCol * clusterGap;
            const cy = 160 + fRow * clusterGap;
            const fNodes = folderMap.get(fldName);
            const subCols = Math.ceil(Math.sqrt(fNodes.length));
            const innerGap = Math.max(45, spacing * 0.3);

            fNodes.forEach((n, idx) => {
                const sc = idx % subCols;
                const sr = Math.floor(idx / subCols);
                n.x = cx + (sc - subCols / 2) * innerGap;
                n.y = cy + (sr - Math.ceil(fNodes.length / subCols) / 2) * innerGap;
                n.vx = 0; n.vy = 0;
            });
        });
        setSimPaused(true);
        requestRender();
        return;
    }

    if (mode === "radial") {
        // Concentric Rings Layout (Hubs in center, outer orbits)
        const sorted = [...nodes].sort((a, b) => (b.degree || 0) - (a.degree || 0));
        let idx = 0, ring = 0;
        const baseRadius = spacing * 0.8;
        const ringSpacing = spacing * 0.9;

        while (idx < sorted.length) {
            const radius = baseRadius + ring * ringSpacing;
            const targetStep = Math.max(60, spacing * 0.38);
            const capacity = ring === 0 ? Math.min(5, sorted.length) : Math.max(10, Math.floor((2 * Math.PI * radius) / targetStep));
            const count = Math.min(capacity, sorted.length - idx);

            for (let i = 0; i < count; i++) {
                const n = sorted[idx++];
                const a = (2 * Math.PI * i) / count;
                n.x = W / 2 + Math.cos(a) * radius;
                n.y = H / 2 + Math.sin(a) * radius;
                n.vx = 0; n.vy = 0;
            }
            ring++;
        }
        setSimPaused(true);
        requestRender();
        return;
    }

    if (mode === "hierarchy") {
        // Architectural Layer Flow: Left to Right
        const layerOrder = { autoload: 0, presentation: 1, domain: 2, infrastructure: 3, core: 4 };
        const layers = new Map();
        for (let l = 0; l <= 4; l++) layers.set(l, []);

        nodes.forEach(n => {
            const lIdx = layerOrder[n.layer] ?? 2;
            layers.get(lIdx).push(n);
        });

        const colSpacing = Math.max(260, spacing * 1.6);
        const rowSpacing = Math.max(50, spacing * 0.32);

        for (let l = 0; l <= 4; l++) {
            const arr = layers.get(l);
            const maxRows = Math.max(12, Math.floor((H - 120) / rowSpacing));
            arr.forEach((n, i) => {
                const subCol = Math.floor(i / maxRows);
                const row = i % maxRows;
                n.x = 120 + l * colSpacing + subCol * 130;
                n.y = 80 + row * rowSpacing;
                n.vx = 0; n.vy = 0;
            });
        }
        setSimPaused(true);
        requestRender();
        return;
    }

    if (mode === "grid") {
        // Catalog Grid Layout: Matrix
        const sorted = [...nodes].sort((a, b) => a.id.localeCompare(b.id));
        const cols = Math.max(5, Math.floor(Math.sqrt(sorted.length * 1.6)));
        const colGap = Math.max(190, spacing * 1.25);
        const rowGap = Math.max(48, spacing * 0.32);

        sorted.forEach((n, i) => {
            const c = i % cols;
            const r = Math.floor(i / cols);
            n.x = 120 + c * colGap;
            n.y = 80 + r * rowGap;
            n.vx = 0; n.vy = 0;
        });
        setSimPaused(true);
        requestRender();
        return;
    }

    // Force layout (Organic): Wide spiral initial placement + 40 physics passes
    nodes.forEach((n, i) => {
        if (!n.pinned) {
            const a = i * 2.3999632;
            const r = Math.min(Math.min(W, H) * 0.65, 22 * Math.sqrt(i + 1));
            n.x = W / 2 + Math.cos(a) * r;
            n.y = H / 2 + Math.sin(a) * r;
            n.vx = 0; n.vy = 0;
        }
    });

    for (let i = 0; i < 40; i++) {
        tickPhysics(true);
    }
    setSimPaused(true);
    requestRender();
}

// Rigid Physics Engine with Anti-Collision
function tickPhysics(force = false) {
    if (!nodes.length) return;
    const W = canvas.clientWidth || 1000, H = canvas.clientHeight || 700;
    const visible = visibleNodesCache;
    const es = visibleEdgesCache;
    const len = visible.length;
    if (!len) return;

    const alpha = force ? 1.0 : simAlpha;
    const spacing = nodeSpacing;
    const rep = (spacing * 10) * alpha;
    const step = len > 260 ? Math.ceil(len / 100) : 1;

    for (let i = 0; i < len; i++) {
        const a = visible[i];
        if (a.pinned) continue;
        let fx = 0, fy = 0;
        for (let j = 0; j < len; j += step) {
            if (i === j) continue;
            const b = visible[j];
            const dx = a.x - b.x, dy = a.y - b.y;
            const d2 = dx * dx + dy * dy + 36;
            if (d2 > 600000) continue;
            const d = Math.sqrt(d2);

            const f = Math.min(22, (rep / d2)) * step;
            fx += (dx / d) * f;
            fy += (dy / d) * f;

            // Anti-Collision Buffer
            const minDist = a.r + b.r + (spacing * 0.32);
            if (d < minDist) {
                const overlap = (minDist - d) * 0.45 * alpha;
                fx += (dx / d) * overlap;
                fy += (dy / d) * overlap;
            }
        }
        fx += (W / 2 - a.x) * 0.0006 * alpha;
        fy += (H / 2 - a.y) * 0.0006 * alpha;

        const fMag = Math.hypot(fx, fy);
        if (fMag > 24) {
            fx = (fx / fMag) * 24;
            fy = (fy / fMag) * 24;
        }
        a.vx = (a.vx + fx) * 0.68;
        a.vy = (a.vy + fy) * 0.68;
    }

    const targetDist = spacing;
    for (let i = 0; i < es.length; i++) {
        const e = es[i], a = e.a, b = e.b;
        if (!a || !b) continue;
        const dx = b.x - a.x, dy = b.y - a.y;
        const d = Math.hypot(dx, dy) || 1;
        const delta = d - targetDist;
        const springForce = Math.max(-12, Math.min(12, delta * 0.024)) * alpha;
        const fx = (dx / d) * springForce;
        const fy = (dy / d) * springForce;
        if (!a.pinned) { a.vx += fx; a.vy += fy; }
        if (!b.pinned) { b.vx -= fx; b.vy -= fy; }
    }

    const maxV = 16 * Math.max(0.2, alpha);
    for (let i = 0; i < len; i++) {
        const a = visible[i];
        if (a.pinned) continue;
        const vMag = Math.hypot(a.vx, a.vy);
        if (vMag > maxV) {
            a.vx = (a.vx / vMag) * maxV;
            a.vy = (a.vy / vMag) * maxV;
        }
        a.x += a.vx;
        a.y += a.vy;
        if (!Number.isFinite(a.x) || !Number.isFinite(a.y)) {
            a.x = W / 2 + (Math.random() - 0.5) * 80;
            a.y = H / 2 + (Math.random() - 0.5) * 80;
            a.vx = 0; a.vy = 0;
        }
    }

    if (!force) {
        simAlpha *= 0.975;
        if (simAlpha < 0.003) {
            simAlpha = 0;
            isPaused = true;
            setSimPaused(true);
            for (let i = 0; i < len; i++) { visible[i].vx = 0; visible[i].vy = 0; }
        }
    }
}

function runPhysicsStep() {
    if (isPaused || simAlpha <= 0.002) return;
    tickPhysics();
    requestRender();
    if (!isPaused && simAlpha > 0.002) {
        requestAnimationFrame(runPhysicsStep);
    }
}

function world(p) { return { x: (p.x - pan.x) / scale, y: (p.y - pan.y) / scale }; }

// Arrowhead Drawing Helper
function drawArrowhead(ctx, fromX, fromY, toX, toY, nodeRadius, color) {
    const dx = toX - fromX, dy = toY - fromY;
    const len = Math.hypot(dx, dy);
    if (len < nodeRadius + 12) return;

    // Position arrowhead just outside node boundary
    const tX = toX - (dx / len) * (nodeRadius + 3);
    const tY = toY - (dy / len) * (nodeRadius + 3);

    const angle = Math.atan2(dy, dx);
    const arrowLen = 8;
    const arrowAngle = Math.PI / 7;

    ctx.fillStyle = color;
    ctx.beginPath();
    ctx.moveTo(tX, tY);
    ctx.lineTo(tX - arrowLen * Math.cos(angle - arrowAngle), tY - arrowLen * Math.sin(angle - arrowAngle));
    ctx.lineTo(tX - arrowLen * Math.cos(angle + arrowAngle), tY - arrowLen * Math.sin(angle + arrowAngle));
    ctx.closePath();
    ctx.fill();
}

// Master Canvas Drawing Function
function draw() {
    const now = performance.now();
    frameCount++;
    if (now - lastFrameTime >= 1000) {
        fps = frameCount;
        frameCount = 0;
        lastFrameTime = now;
        if ($("fpsCounter")) $("fpsCounter").textContent = `${fps} FPS`;
    }

    const w = canvas.clientWidth, h = canvas.clientHeight;
    if (!w || !h) return;
    ctx.clearRect(0, 0, w, h);

    const ns = visibleNodesCache;
    const es = visibleEdgesCache;
    const activeNode = hovered || selected;

    const neighborSet = new Set();
    if (activeNode) {
        for (let i = 0; i < es.length; i++) {
            const e = es[i];
            if (e.source === activeNode.id) neighborSet.add(e.target);
            if (e.target === activeNode.id) neighborSet.add(e.source);
        }
    }

    ctx.save();
    ctx.translate(pan.x || 0, pan.y || 0);
    ctx.scale(scale || 1, scale || 1);

    // Viewport Culling Bounds
    const pad = 120;
    const minVx = -pan.x / scale - pad, maxVx = (w - pan.x) / scale + pad;
    const minVy = -pan.y / scale - pad, maxVy = (h - pan.y) / scale + pad;

    // Draw Folder Module Bounding Boxes (if modules layout is active)
    if ($("layout").value === "modules" && ns.length > 0) {
        const fldBounds = new Map();
        for (let i = 0; i < ns.length; i++) {
            const n = ns[i];
            if (!fldBounds.has(n.folder)) {
                fldBounds.set(n.folder, { minX: n.x, maxX: n.x, minY: n.y, maxY: n.y, count: 1 });
            } else {
                const b = fldBounds.get(n.folder);
                b.minX = Math.min(b.minX, n.x);
                b.maxX = Math.max(b.maxX, n.x);
                b.minY = Math.min(b.minY, n.y);
                b.maxY = Math.max(b.maxY, n.y);
                b.count++;
            }
        }

        ctx.font = "11px Inter, sans-serif";
        ctx.textAlign = "left";
        for (const [folderName, b] of fldBounds.entries()) {
            const padBox = 26;
            const bx = b.minX - padBox;
            const by = b.minY - padBox;
            const bw = (b.maxX - b.minX) + padBox * 2;
            const bh = (b.maxY - b.minY) + padBox * 2;

            if (bx + bw < minVx || bx > maxVx || by + bh < minVy || by > maxVy) continue;

            ctx.fillStyle = "rgba(255, 255, 255, 0.02)";
            ctx.strokeStyle = "rgba(255, 255, 255, 0.08)";
            ctx.lineWidth = 1;
            ctx.beginPath();
            ctx.roundRect(bx, by, bw, bh, 8);
            ctx.fill();
            ctx.stroke();

            ctx.fillStyle = "rgba(255, 255, 255, 0.35)";
            ctx.fillText(`📁 ${folderName} (${b.count})`, bx + 8, by - 6);
        }
    }

    // 1. Draw Edges
    const hasFocus = activeNode || activeCycleNodes.size > 0 || activePathEdges.size > 0 || activePathNodes.size > 0;

    if (!hasFocus) {
        // High/Medium confidence batch
        ctx.beginPath();
        ctx.strokeStyle = "#4d6b8f";
        ctx.lineWidth = 1;
        ctx.globalAlpha = 0.5;
        for (let i = 0; i < es.length; i++) {
            const e = es[i], a = e.a, b = e.b;
            if (!a || !b || e.confidence === "low" || cutEdges.has(edgeKey(e.source, e.target))) continue;
            if (a.x < minVx && b.x < minVx) continue;
            if (a.x > maxVx && b.x > maxVx) continue;
            if (a.y < minVy && b.y < minVy) continue;
            if (a.y > maxVy && b.y > maxVy) continue;

            if (curvedEdges) {
                const mx = (a.x + b.x) / 2 + (b.y - a.y) * 0.08;
                const my = (a.y + b.y) / 2 + (a.x - b.x) * 0.08;
                ctx.moveTo(a.x, a.y);
                ctx.quadraticCurveTo(mx, my, b.x, b.y);
            } else {
                ctx.moveTo(a.x, a.y);
                ctx.lineTo(b.x, b.y);
            }
        }
        ctx.stroke();

        // Low confidence heuristic batch
        ctx.beginPath();
        ctx.strokeStyle = "#384759";
        ctx.lineWidth = 0.75;
        ctx.globalAlpha = 0.25;
        for (let i = 0; i < es.length; i++) {
            const e = es[i], a = e.a, b = e.b;
            if (!a || !b || e.confidence !== "low" || cutEdges.has(edgeKey(e.source, e.target))) continue;
            if (a.x < minVx && b.x < minVx) continue;
            if (a.x > maxVx && b.x > maxVx) continue;
            if (a.y < minVy && b.y < minVy) continue;
            if (a.y > maxVy && b.y > maxVy) continue;

            ctx.moveTo(a.x, a.y);
            ctx.lineTo(b.x, b.y);
        }
        ctx.stroke();

    } else {
        // Dim non-focused edges
        ctx.beginPath();
        ctx.strokeStyle = "#273444";
        ctx.lineWidth = 0.75;
        ctx.globalAlpha = 0.05;
        for (let i = 0; i < es.length; i++) {
            const e = es[i];
            if (cutEdges.has(edgeKey(e.source, e.target))) continue;
            const isConn = activeNode && (e.source === activeNode.id || e.target === activeNode.id);
            const isCyc = activeCycleNodes.has(e.source) && activeCycleNodes.has(e.target);
            const isPath = activePathEdges.has(e);

            if (!isConn && !isCyc && !isPath) {
                const a = e.a, b = e.b;
                if (!a || !b) continue;
                if (a.x < minVx && b.x < minVx) continue;
                if (a.x > maxVx && b.x > maxVx) continue;
                if (a.y < minVy && b.y < minVy) continue;
                if (a.y > maxVy && b.y > maxVy) continue;
                ctx.moveTo(a.x, a.y);
                ctx.lineTo(b.x, b.y);
            }
        }
        ctx.stroke();

        // Highlight cycle edges in bright neon red
        if (activeCycleNodes.size > 0) {
            ctx.beginPath();
            ctx.strokeStyle = "#ff4f64";
            ctx.lineWidth = 2.8;
            ctx.globalAlpha = 1;
            for (let i = 0; i < es.length; i++) {
                const e = es[i];
                if (cutEdges.has(edgeKey(e.source, e.target))) continue;
                if (activeCycleNodes.has(e.source) && activeCycleNodes.has(e.target)) {
                    ctx.moveTo(e.a.x, e.a.y);
                    ctx.lineTo(e.b.x, e.b.y);
                    if (showArrows) drawArrowhead(ctx, e.a.x, e.a.y, e.b.x, e.b.y, e.b.r, "#ff4f64");
                }
            }
            ctx.stroke();
        }

        // Highlight path edges in gold
        if (activePathEdges.size > 0) {
            ctx.beginPath();
            ctx.strokeStyle = "#fbbf24";
            ctx.lineWidth = 3.2;
            ctx.globalAlpha = 1;
            for (const e of activePathEdges) {
                if (cutEdges.has(edgeKey(e.source, e.target))) continue;
                ctx.moveTo(e.a.x, e.a.y);
                ctx.lineTo(e.b.x, e.b.y);
                if (showArrows) drawArrowhead(ctx, e.a.x, e.a.y, e.b.x, e.b.y, e.b.r, "#fbbf24");
            }
            ctx.stroke();
        }

        // Highlight connected edges to active node
        if (activeNode) {
            for (let i = 0; i < es.length; i++) {
                const e = es[i];
                if (cutEdges.has(edgeKey(e.source, e.target))) continue;
                if (e.source === activeNode.id || e.target === activeNode.id) {
                    const isOutgoing = (e.source === activeNode.id);
                    const color = isOutgoing ? "#6bb5ff" : "#34d399";
                    ctx.beginPath();
                    ctx.strokeStyle = color;
                    ctx.lineWidth = 2.4;
                    ctx.globalAlpha = 0.95;
                    ctx.moveTo(e.a.x, e.a.y);
                    ctx.lineTo(e.b.x, e.b.y);
                    ctx.stroke();

                    if (showArrows) {
                        drawArrowhead(ctx, e.a.x, e.a.y, e.b.x, e.b.y, e.b.r, color);
                    }
                }
            }
        }
    }

    // Draw What-If Cut Edges
    if (cutEdges.size > 0) {
        ctx.save();
        ctx.setLineDash([5, 5]);
        ctx.strokeStyle = "#ef4444";
        ctx.lineWidth = 2.0;
        ctx.globalAlpha = 0.9;
        ctx.font = "11px Inter, sans-serif";
        ctx.fillStyle = "#ef4444";
        ctx.textAlign = "center";
        ctx.textBaseline = "middle";

        for (let i = 0; i < es.length; i++) {
            const e = es[i];
            if (cutEdges.has(edgeKey(e.source, e.target))) {
                const a = e.a, b = e.b;
                if (!a || !b) continue;
                ctx.beginPath();
                ctx.moveTo(a.x, a.y);
                ctx.lineTo(b.x, b.y);
                ctx.stroke();

                const mx = (a.x + b.x) / 2;
                const my = (a.y + b.y) / 2;
                ctx.fillText("✂️", mx, my);
            }
        }
        ctx.restore();
    }

    // 2. Draw Nodes
    for (let i = 0; i < ns.length; i++) {
        const n = ns[i];
        if (n.x + n.r < minVx || n.x - n.r > maxVx || n.y + n.r < minVy || n.y - n.r > maxVy) continue;

        const isAct = (activeNode && (n.id === activeNode.id || neighborSet.has(n.id)));
        const isCyc = activeCycleNodes.has(n.id);
        const isPath = activePathNodes.has(n.id);
        const isDim = hasFocus && !isAct && !isCyc && !isPath;

        ctx.globalAlpha = isDim ? 0.2 : 1.0;

        // Special Auras
        if (n.is_autoload) {
            ctx.beginPath();
            ctx.arc(n.x, n.y, n.r + 4, 0, Math.PI * 2);
            ctx.fillStyle = "rgba(192, 132, 252, 0.25)";
            ctx.fill();
        }
        if (n.is_god_object) {
            ctx.beginPath();
            ctx.arc(n.x, n.y, n.r + 3, 0, Math.PI * 2);
            ctx.strokeStyle = "rgba(251, 191, 36, 0.8)";
            ctx.lineWidth = 2;
            ctx.stroke();
        }

        // Main Node Body
        ctx.beginPath();
        ctx.arc(n.x, n.y, n.r, 0, Math.PI * 2);
        ctx.fillStyle = getNodeColor(n);
        ctx.fill();

        // Node Border / Ring
        if (n.in_cycle || isCyc) {
            ctx.strokeStyle = "#ff4f64";
            ctx.lineWidth = 2.2;
            ctx.stroke();
        } else if (n.pinned) {
            ctx.strokeStyle = "#ffffff";
            ctx.lineWidth = 2;
            ctx.stroke();
        } else {
            ctx.strokeStyle = "rgba(255, 255, 255, 0.25)";
            ctx.lineWidth = 1;
            ctx.stroke();
        }

        // Selected Halo
        if (selected && selected.id === n.id) {
            ctx.beginPath();
            ctx.arc(n.x, n.y, n.r + 6, 0, Math.PI * 2);
            ctx.strokeStyle = "#60a5fa";
            ctx.lineWidth = 2.5;
            ctx.stroke();
        }

        // Bookmark star badge
        if (favorites.has(n.id)) {
            ctx.fillStyle = "#fbbf24";
            ctx.font = "10px sans-serif";
            ctx.fillText("★", n.x + n.r - 2, n.y - n.r + 2);
        }
    }

    // 3. Draw Labels
    ctx.textAlign = "center";
    ctx.textBaseline = "middle";

    for (let i = 0; i < ns.length; i++) {
        const n = ns[i];
        if (n.x + n.r < minVx || n.x - n.r > maxVx || n.y + n.r < minVy || n.y - n.r > maxVy) continue;

        const isAct = (activeNode && (n.id === activeNode.id || neighborSet.has(n.id)));
        const shouldShowLabel = showAllLabels || isAct || activeCycleNodes.has(n.id) || activePathNodes.has(n.id) || scale > 1.25;

        if (shouldShowLabel) {
            const labelText = n.classes.length ? n.classes[0] : n.id.split("/").pop();
            const ly = n.y + n.r + 10;

            ctx.font = isAct ? "bold 11px Inter, sans-serif" : "10px Inter, sans-serif";
            const textWidth = ctx.measureText(labelText).width;

            // Background pill for crisp readability
            ctx.fillStyle = "rgba(10, 14, 20, 0.85)";
            ctx.beginPath();
            ctx.roundRect(n.x - textWidth / 2 - 4, ly - 7, textWidth + 8, 14, 4);
            ctx.fill();

            ctx.fillStyle = isAct ? "#ffffff" : "#c5d4e6";
            ctx.fillText(labelText, n.x, ly);
        }
    }

    ctx.restore();
}

// Interactive MiniMap Radar Drawing
function drawMinimap() {
    if (!minimapCanvas || !miniCtx || !minimapVisible) return;
    const mw = minimapCanvas.width, mh = minimapCanvas.height;
    miniCtx.clearRect(0, 0, mw, mh);

    if (!nodes.length) return;

    // Compute bounding box of all nodes
    let minX = Infinity, maxX = -Infinity, minY = Infinity, maxY = -Infinity;
    for (let i = 0; i < nodes.length; i++) {
        const n = nodes[i];
        if (n.x < minX) minX = n.x;
        if (n.x > maxX) maxX = n.x;
        if (n.y < minY) minY = n.y;
        if (n.y > maxY) maxY = n.y;
    }

    const pad = 80;
    minX -= pad; maxX += pad; minY -= pad; maxY += pad;
    const gW = maxX - minX || 1;
    const gH = maxY - minY || 1;

    const scaleX = mw / gW;
    const scaleY = mh / gH;
    const miniScale = Math.min(scaleX, scaleY);

    const offX = (mw - gW * miniScale) / 2 - minX * miniScale;
    const offY = (mh - gH * miniScale) / 2 - minY * miniScale;

    // Draw all node dots
    miniCtx.fillStyle = "rgba(97, 169, 255, 0.65)";
    for (let i = 0; i < nodes.length; i++) {
        const n = nodes[i];
        const mx = n.x * miniScale + offX;
        const my = n.y * miniScale + offY;
        miniCtx.fillRect(mx - 1, my - 1, 2, 2);
    }

    // Draw visible camera viewport rectangle
    const cw = canvas.clientWidth, ch = canvas.clientHeight;
    const vx1 = (-pan.x / scale) * miniScale + offX;
    const vy1 = (-pan.y / scale) * miniScale + offY;
    const vW = (cw / scale) * miniScale;
    const vH = (ch / scale) * miniScale;

    miniCtx.strokeStyle = "#38bdf8";
    miniCtx.lineWidth = 1.5;
    miniCtx.fillStyle = "rgba(56, 189, 248, 0.15)";
    miniCtx.beginPath();
    miniCtx.roundRect(vx1, vy1, vW, vH, 3);
    miniCtx.fill();
    miniCtx.stroke();
}

// Fit Graph to Viewport
function fitGraph() {
    const list = visibleNodesCache.length ? visibleNodesCache : nodes;
    if (!list.length) return;
    const w = canvas.clientWidth, h = canvas.clientHeight;
    let minX = Infinity, maxX = -Infinity, minY = Infinity, maxY = -Infinity;
    list.forEach(n => {
        minX = Math.min(minX, n.x); maxX = Math.max(maxX, n.x);
        minY = Math.min(minY, n.y); maxY = Math.max(maxY, n.y);
    });

    const gw = Math.max(10, maxX - minX), gh = Math.max(10, maxY - minY);
    scale = Math.max(0.06, Math.min(1.8, Math.min((w - 140) / gw, (h - 140) / gh)));
    pan.x = (w - gw * scale) / 2 - minX * scale;
    pan.y = (h - gh * scale) / 2 - minY * scale;
    requestRender();
}

function zoomAt(x, y, f) {
    const before = world({ x, y });
    scale = Math.max(0.04, Math.min(4, scale * f));
    pan.x = x - before.x * scale;
    pan.y = y - before.y * scale;
    requestRender();
}

// Tooltips
function showTooltip(n, clientX, clientY) {
    if (!tooltip) return;
    tooltip.innerHTML = `
        <b>${esc(n.classes[0] || n.id.split("/").pop())}</b>
        <span>${esc(n.path)}</span>
        <div style="margin-top:4px;font-size:10px;color:#a4b8d1">
            Связей: <b>${n.degree || 0}</b> (Вх: ${n.fan_in}, Исх: ${n.fan_out}) · Строк: <b>${n.lines || 0}</b>
            ${n.is_autoload ? ' · <span style="color:#d8b4fe">★ Autoload</span>' : ''}
            ${n.in_cycle ? ' · <span style="color:#ff6d7d">⚠️ В цикле</span>' : ''}
        </div>
    `;
    tooltip.style.left = `${clientX}px`;
    tooltip.style.top = `${clientY}px`;
    tooltip.classList.remove("hidden");
}

function hideTooltip() {
    if (tooltip) tooltip.classList.add("hidden");
    if (edgeTooltip) edgeTooltip.classList.add("hidden");
}

// Shortest Path Finder (BFS)
function findShortestPath(startId, endId) {
    if (!startId || !endId || startId === endId) return null;
    const queue = [[startId]];
    const visited = new Set([startId]);
    const parentEdge = new Map();

    const adj = new Map();
    edges.forEach(e => {
        if (!adj.has(e.source)) adj.set(e.source, []);
        adj.get(e.source).push(e);
    });

    while (queue.length) {
        const path = queue.shift();
        const curr = path[path.length - 1];

        if (curr === endId) {
            return path;
        }

        const outEdges = adj.get(curr) || [];
        for (const e of outEdges) {
            if (!visited.has(e.target)) {
                visited.add(e.target);
                queue.push([...path, e.target]);
            }
        }
    }
    return null;
}

// Node Inspector Details
function inspect(n) {
    selected = n;
    const box = $("inspector");
    const badge = $("inspectorBadge");

    if (!n) {
        box.innerHTML = '<div class="empty">Кликни на любой узел на графе для детального анализа.</div>';
        if (badge) badge.classList.add("hidden");
        requestRender();
        return;
    }

    if (badge) badge.classList.remove("hidden");

    const inEdges = edges.filter(e => e.target === n.id);
    const outEdges = edges.filter(e => e.source === n.id);
    const isFav = favorites.has(n.id);
    const note = nodeNotes.get(n.id) || "";

    const tags = [];
    tags.push(`<span class="tag ${n.language === 'C#' ? 'cs' : n.language === 'GDScript' ? 'gd' : 'scene'}">${esc(n.language)}</span>`);
    if (n.is_autoload) tags.push('<span class="tag autoload">★ Autoload Singleton</span>');
    if (n.in_cycle) tags.push('<span class="tag error">⚠️ В цикле зависимостей</span>');
    if (n.is_god_object) tags.push('<span class="tag warning">👑 God-Object Hub</span>');
    if (n.orphan_status === "truly_dead") tags.push('<span class="tag error">💀 Мертвый код (0 связей)</span>');
    else if (n.orphan_status === "scene_entry_point") tags.push('<span class="tag autoload">🌲 Точка входа сцены</span>');
    else if (n.is_orphan) tags.push('<span class="tag">🏝 Изолированный</span>');
    tags.push(`<span class="tag">Слой: ${esc(n.layer || "domain")}</span>`);

    // Plain Russian metric interpretations
    const iVal = n.instability ?? 0.5;
    let iHint = "⚖️ Сбалансированный модуль (умеренная связность)";
    let iClass = "info";
    if (iVal <= 0.25) {
        iHint = "🛡️ Базовый фундамент: от него зависят другие. Менять аккуратно!";
        iClass = "good";
    } else if (iVal >= 0.75) {
        iHint = "🍃 Верхний фасад: зависит от других. Легко и безопасно менять.";
        iClass = "info";
    }

    const bVal = n.blast_radius ?? 0;
    const bHint = bVal === 0 ? "✓ Безопасный лист (риск регрессии 0)" : `💥 При ошибке в этом файле сломается до ${bVal} компонентов`;
    const bClass = bVal > 8 ? "danger" : bVal > 3 ? "warn" : "good";

    const hasHotPath = (n.functions_detail || []).some(f => f.is_hot_path);

    // Scene tree block if available
    let sceneTreeHtml = "";
    if (n.node_tree && n.node_tree.length > 0) {
        sceneTreeHtml = `
            <div class="detail-subtitle">Иерархия нод сцены (${n.node_tree.length} нод)</div>
            <div class="scene-tree-box">
                ${n.node_tree.map(node => `
                    <div class="scene-node-item">
                        <span>${node.parent && node.parent !== '.' ? '└─ ' : '📦 '}<b>${esc(node.name)}</b></span>
                        <span class="scene-node-type">${esc(node.type || 'Node')}</span>
                        ${node.script ? `<span class="scene-node-script">📜 ${esc(node.script.split('/').pop())}</span>` : ''}
                    </div>
                `).join("")}
            </div>
        `;
    }

    box.innerHTML = `
        <div class="node-title">${esc(n.classes[0] || n.id.split("/").pop())}</div>
        <div class="detail-path">${esc(n.path)}</div>
        <div>${tags.join("")}</div>

        ${n.profiling ? `
            <div class="func-passport" style="border-left-color:${n.profiling.is_bottleneck ? '#ef4444' : '#f59e0b'};background:rgba(245, 158, 11, 0.08);margin:8px 0;padding:8px">
                <div style="display:flex;justify-content:space-between;align-items:center">
                    <b style="color:${n.profiling.is_bottleneck ? '#ef4444' : '#f59e0b'}">⚡ Нагрузка (CPU Profiler):</b>
                    ${n.profiling.is_bottleneck ? '<span class="tag error" style="font-size:9px">🔥 BOTTLENECK</span>' : ''}
                </div>
                <div style="display:flex;gap:12px;margin-top:6px;font-size:11px">
                    <span>⏱ <b>${n.profiling.ms_per_frame} мс</b>/кадр</span>
                    <span>📊 <b>${n.profiling.cpu_percent}%</b> CPU</span>
                    <span>💾 <b>${n.profiling.alloc_kb_per_sec} КБ/с</b></span>
                </div>
            </div>
        ` : ''}

        ${hasHotPath ? `
            <div class="hot-path-banner">
                ⚡ <b>Внимание: Горячий путь (60-FPS)!</b>
                Этот файл исполняется каждый кадр (<code>_process</code> / <code>Update</code>). Категорически избегайте аллокаций памяти <code>new</code>, поиска нод по строкам и тяжелых циклов.
            </div>
        ` : ''}

        <div class="metric-grid">
            <div class="metric">
                <span>Нестабильность (I)</span>
                <b>${n.instability ?? 0.5}</b>
                <div class="metric-hint ${iClass}">${iHint}</div>
            </div>
            <div class="metric">
                <span>Blast Radius</span>
                <b>${n.blast_radius ?? 0} файлов</b>
                <div class="metric-hint ${bClass}">${bHint}</div>
            </div>
            <div class="metric">
                <span>Fan-In (Ca)</span>
                <b>${n.fan_in}</b>
                <div class="metric-hint">📥 Клиенты: ${n.fan_in} файлов вызывают этот код</div>
            </div>
            <div class="metric">
                <span>Fan-Out (Ce)</span>
                <b>${n.fan_out}</b>
                <div class="metric-hint">📤 Зависимости: ${n.fan_out} внешних файлов нужно для работы</div>
            </div>
            <div class="metric">
                <span>Строк кода</span>
                <b>${(n.lines || 0).toLocaleString()}</b>
                <div class="metric-hint ${n.lines > 500 ? 'warn' : ''}">${n.lines > 500 ? '⚠️ Монолитный размер (разделите)' : '✓ Компактный размер файла'}</div>
            </div>
            <div class="metric">
                <span>Distance (D)</span>
                <b>${n.distance_sequence ?? 0}</b>
                <div class="metric-hint">📐 Баланс абстрактности архитектуры</div>
            </div>
        </div>

        <div class="inspector-actions-wrap">
            <button id="copyDepsListBtn" class="primary" style="background:#0284c7;font-weight:700">📋 Список связей</button>
            <button id="copySkeletonBtn" class="primary" style="background:#059669;font-weight:700">💀 Скелет API (-90% токенов)</button>
            <button id="fileAiPromptBtn" class="primary" style="background:#2563eb">🤖 ИИ Промпт</button>
            <button id="fileAiQueryBtn" class="primary" style="background:#7c3aed;font-weight:700">🎯 Запрос ИИ к файлу</button>
            <button id="favToggleBtn" class="secondary">${isFav ? '★ В избранном' : '☆ В избранное'}</button>
            <button id="isolateBtn" class="secondary">${isolatedNode?.id === n.id ? 'Вернуть все' : '🔍 Изолировать'}</button>
            <button id="compareBtn" class="secondary">⚖ Сравнить...</button>
            <button id="excludeFileBtn" class="secondary" title="Исключить файл из анализа">🚫 Исключить файл</button>
            <button id="excludeDirBtn" class="secondary" title="Исключить всю папку">📁 Исключить папку</button>
            <button id="copyPathBtn" class="secondary">📋 Копировать путь</button>
            <button id="openVsCodeBtn" class="secondary">💻 Открыть в IDE</button>
        </div>

        ${sceneTreeHtml}

        <div class="note-box">
            <div class="detail-subtitle">Заметка архитектора</div>
            <textarea id="nodeNoteArea" placeholder="Добавь архитектурную заметку к этому файлу (сохраняется автоматически)...">${esc(note)}</textarea>
        </div>

        <div class="detail-subtitle">Входящие зависимости (Кто зависит от этого файла: ${inEdges.length})</div>
        <div class="link-list">
            ${inEdges.length ? inEdges.map(e => {
                const isCut = cutEdges.has(edgeKey(e.source, e.target));
                return `
                    <div style="display:flex;align-items:center;gap:4px">
                        <button class="link-item" data-jump="${esc(e.source)}" style="flex:1">← ${esc(e.source)} <span class="muted" style="font-size:10px">(${esc(e.type)})</span></button>
                        <button class="secondary whatif-cut-btn" data-cut-src="${esc(e.source)}" data-cut-tgt="${esc(e.target)}" title="${isCut ? 'Восстановить связь' : 'Разорвать связь в What-If'}" style="padding:2px 6px;font-size:10px;color:${isCut ? '#22c55e' : '#ef4444'}">${isCut ? '✓' : '✂️'}</button>
                    </div>
                `;
            }).join("") : '<div class="empty" style="padding:6px 8px">Нет входящих зависимостей.</div>'}
        </div>

        <div class="detail-subtitle">Исходящие зависимости (От кого зависит этот файл: ${outEdges.length})</div>
        <div class="link-list">
            ${outEdges.length ? outEdges.map(e => {
                const isCut = cutEdges.has(edgeKey(e.source, e.target));
                return `
                    <div style="display:flex;align-items:center;gap:4px">
                        <button class="link-item" data-jump="${esc(e.target)}" style="flex:1">→ ${esc(e.target)} <span class="muted" style="font-size:10px">(${esc(e.type)})</span></button>
                        <button class="secondary whatif-cut-btn" data-cut-src="${esc(e.source)}" data-cut-tgt="${esc(e.target)}" title="${isCut ? 'Восстановить связь' : 'Разорвать связь в What-If'}" style="padding:2px 6px;font-size:10px;color:${isCut ? '#22c55e' : '#ef4444'}">${isCut ? '✓' : '✂️'}</button>
                    </div>
                `;
            }).join("") : '<div class="empty" style="padding:6px 8px">Нет исходящих связей.</div>'}
        </div>

        <div class="detail-subtitle">Функции и поведение (${(n.functions_detail || []).length})</div>
        <div class="func-list">
            ${(n.functions_detail || []).length ? (n.functions_detail || []).map(fn => {
                const fnName = fn.name || "anonymous";
                const callers = fn.callers || [];
                const doc = fn.docstring || fn.doc || fn.passport?.docstring || "";
                const summary = fn.summary_ru || fn.passport?.summary_ru || "";
                const mutations = fn.mutations || fn.passport?.mutations || [];
                const emits = fn.emits || fn.passport?.emits || [];
                const spawns = fn.spawns || fn.passport?.spawns || [];
                return `
                <div class="func-item">
                    <div class="func-header" data-toggle-func="${esc(fnName)}">
                        <span style="display:flex;align-items:center;gap:6px;overflow:hidden;text-overflow:ellipsis">
                            <span style="color:#f59e0b">⚡</span>
                            <b style="color:var(--text)">${esc(fnName)}</b>
                            <span class="muted" style="font-size:10px">(${esc(fn.params || '')})</span>
                        </span>
                        <div style="display:flex;align-items:center;gap:5px;flex-shrink:0">
                            ${fn.is_hot_path ? '<span class="tag warning" style="font-size:9px;margin:0">⚡ 60-FPS</span>' : ''}
                            <span class="func-callers-badge">${callers.length} вызовов</span>
                        </div>
                    </div>
                    <div class="func-body hidden" id="func_body_${esc(fnName)}">
                        ${doc ? `<div class="func-passport func-doc">📖 <i>${esc(doc)}</i></div>` : ''}
                        ${summary ? `<div class="func-passport">🎯 <b>Назначение:</b> ${esc(summary)}</div>` : ''}
                        ${mutations.length ? `<div style="margin:4px 0"><span style="color:#fbbf24;font-size:10px;font-weight:600">⚡ Мутации состояния:</span> ${mutations.map(m => `<span class="passport-tag mut">${esc(m)}</span>`).join(" ")}</div>` : ''}
                        ${emits.length ? `<div style="margin:4px 0"><span style="color:#c084fc;font-size:10px;font-weight:600">📡 Сигналы:</span> ${emits.map(s => `<span class="passport-tag emit">${esc(s)}</span>`).join(" ")}</div>` : ''}
                        ${spawns.length ? `<div style="margin:4px 0"><span style="color:#34d399;font-size:10px;font-weight:600">📦 Создает объекты (new):</span> ${spawns.map(sp => `<span class="passport-tag spawn">${esc(sp)}</span>`).join(" ")}</div>` : ''}

                        <h5 style="margin-top:8px">📥 Кто вызывает (Callers в проекте):</h5>
                        ${callers.length ? `<ul>${callers.map(c => `<li><button class="link-item" data-jump="${esc(c)}" style="padding:2px 0">← ${esc(c)}</button></li>`).join("")}</ul>` : '<div class="muted">Внешних вызовов не найдено (локальный метод)</div>'}

                        <h5 style="margin-top:8px">📤 Кто даёт данные / Входной поток:</h5>
                        <div class="muted">
                            ${fn.params ? `• Параметры: <code>${esc(fn.params)}</code><br>` : '• Нет параметров<br>'}
                            ${fn.accessed_autoloads?.length ? `• Синглтоны (Autoload): <code>${esc(fn.accessed_autoloads.join(", "))}</code><br>` : ''}
                            ${fn.triggering_signals?.length ? `• Сигналы: <code>${esc(fn.triggering_signals.join(", "))}</code><br>` : ''}
                        </div>

                        <div style="display:flex;gap:6px;margin-top:8px">
                            <button class="primary" data-fn-rengen="${esc(fnName)}" data-fn-file="${esc(n.path)}" style="flex:1.2;font-size:10.5px;background:#0284c7;font-weight:600;padding:4px 8px">🔍 360° Рентген</button>
                            <button class="secondary" data-copy-func="${esc(fnName)}" style="flex:1;font-size:10px;padding:4px 6px">📋 Срез</button>
                            <button class="secondary" data-query-func="${esc(fnName)}" data-query-file="${esc(n.path)}" style="font-size:10px;padding:4px 6px" title="Точечная выборка в AI Query Builder">🎯 Query</button>
                        </div>
                    </div>
                </div>
            `;}).join("") : '<div class="empty" style="padding:6px 8px">Функций не обнаружено.</div>'}
        </div>
    `;

    // Hook Inspector Events
    box.querySelectorAll("[data-jump]").forEach(btn => btn.addEventListener("click", () => {
        const targetNode = nodes.find(x => x.id === btn.dataset.jump);
        if (targetNode) {
            inspect(targetNode);
            pan.x = canvas.clientWidth / 2 - targetNode.x * scale;
            pan.y = canvas.clientHeight / 2 - targetNode.y * scale;
            requestRender();
        }
    }));

    box.querySelectorAll("[data-toggle-func]").forEach(btn => btn.addEventListener("click", () => {
        const fBody = $(`func_body_${btn.dataset.toggleFunc}`);
        if (fBody) fBody.classList.toggle("hidden");
    }));

    box.querySelectorAll("[data-copy-func]").forEach(btn => btn.addEventListener("click", () => {
        const fn = (n.functions_detail || []).find(f => f.name === btn.dataset.copyFunc);
        if (fn) {
            const txt = formatFunctionContext(n, fn);
            navigator.clipboard.writeText(txt);
            showToast(`✓ Контекст функции ${fn.name}() скопирован в буфер!`);
        }
    }));

    box.querySelectorAll("[data-fn-rengen]").forEach(btn => btn.addEventListener("click", (e) => {
        e.stopPropagation();
        openFunctionDeepDiveModal(btn.dataset.fnRengen, btn.dataset.fnFile || n.path);
    }));

    box.querySelectorAll("[data-query-func]").forEach(btn => btn.addEventListener("click", (e) => {
        e.stopPropagation();
        openAiQueryModal(btn.dataset.queryFile || n.path, btn.dataset.queryFunc);
    }));

    $("fileAiQueryBtn")?.addEventListener("click", () => {
        openAiQueryModal(n.path, "");
    });

    $("copyDepsListBtn")?.addEventListener("click", () => {
        const txt = formatNodeDepsList(n);
        navigator.clipboard.writeText(txt);
        showToast("✓ Список связей скопирован в буфер!");
    });

    $("copySkeletonBtn")?.addEventListener("click", () => {
        if (n.skeleton && n.skeleton.trim().length > 0) {
            navigator.clipboard.writeText(n.skeleton);
            const tokEst = Math.round(n.skeleton.length / 4);
            showToast(`✓ Скелет API скопирован (~${tokEst} токенов, -90% расхода)!`);
        } else {
            const fallback = `// API Скелет для ${n.path}\n// Файл ${n.language === 'Scene' ? 'является сценой .tscn' : 'не содержит явных определений классов'}.`;
            navigator.clipboard.writeText(fallback);
            showToast("✓ Сигнатура файла скопирована в буфер!");
        }
    });

    $("favToggleBtn")?.addEventListener("click", () => {
        if (favorites.has(n.id)) {
            favorites.delete(n.id);
            showToast(`Удалено из избранного: ${n.id.split("/").pop()}`);
        } else {
            favorites.add(n.id);
            showToast(`★ Добавлено в избранное: ${n.id.split("/").pop()}`);
        }
        localStorage.setItem(FAVORITES_KEY, JSON.stringify([...favorites]));
        renderFavorites();
        inspect(n);
        requestRender();
    });

    $("isolateBtn")?.addEventListener("click", () => {
        isolatedNode = (isolatedNode?.id === n.id) ? null : n;
        updateFilters();
        fitGraph();
        showToast(isolatedNode ? `Изолирована подсистема: ${n.id.split("/").pop()}` : "Снята изоляция");
    });

    $("compareBtn")?.addEventListener("click", () => {
        $("compareNodeA").value = n.id;
        openModal("compareModal");
        renderNodeComparison();
    });

    $("copyPathBtn")?.addEventListener("click", () => {
        navigator.clipboard.writeText(n.path);
        showToast("✓ Путь скопирован в буфер");
    });

    $("openVsCodeBtn")?.addEventListener("click", () => {
        const fullPath = data?.root ? `${data.root}/${n.path}`.replace(/\\/g, "/") : n.path;
        window.location.href = `vscode://file/${fullPath}`;
        showToast("Открываю в IDE...");
    });

    $("fileAiPromptBtn")?.addEventListener("click", () => {
        const prompt = generateSingleFilePrompt(n, inEdges, outEdges);
        openAiModal(`Рефакторинг файла: ${n.id.split("/").pop()}`, prompt);
    });

    box.querySelectorAll(".whatif-cut-btn").forEach(btn => btn.addEventListener("click", () => {
        const k = edgeKey(btn.dataset.cutSrc, btn.dataset.cutTgt);
        if (cutEdges.has(k)) {
            cutEdges.delete(k);
            showToast(`✓ Восстановлена связь: ${btn.dataset.cutSrc.split('/').pop()} → ${btn.dataset.cutTgt.split('/').pop()}`);
        } else {
            cutEdges.add(k);
            showToast(`✂️ Разорвана связь: ${btn.dataset.cutSrc.split('/').pop()} → ${btn.dataset.cutTgt.split('/').pop()}`);
            if (!whatIfActive) setWhatIfMode(true);
        }
        recalculateWhatIf();
        inspect(n);
    }));

    $("excludeFileBtn")?.addEventListener("click", () => {
        const input = $("ignoreInput");
        if (!input) return;
        const cur = input.value.split(",").map(x => x.trim()).filter(Boolean);
        if (!cur.includes(n.path)) {
            cur.push(n.path);
            input.value = cur.join(", ");
            $("applyIgnoreBtn")?.click();
            showToast(`🚫 Файл ${n.path.split('/').pop()} добавлен в исключения`);
        }
    });

    $("excludeDirBtn")?.addEventListener("click", () => {
        const input = $("ignoreInput");
        if (!input) return;
        const folderPattern = `${n.folder}/*`;
        const cur = input.value.split(",").map(x => x.trim()).filter(Boolean);
        if (!cur.includes(folderPattern)) {
            cur.push(folderPattern);
            input.value = cur.join(", ");
            $("applyIgnoreBtn")?.click();
            showToast(`📁 Папка ${n.folder} добавлена в исключения`);
        }
    });

    $("nodeNoteArea")?.addEventListener("input", e => {
        const val = e.target.value.trim();
        if (val) nodeNotes.set(n.id, val);
        else nodeNotes.delete(n.id);
        localStorage.setItem(NOTES_KEY, JSON.stringify(Object.fromEntries(nodeNotes)));
    });

    requestRender();
}

// Bookmarks List Renderer
function renderFavorites() {
    const list = $("favoritesList");
    if (!list) return;
    $("favCount").textContent = favorites.size;
    if (!favorites.size) {
        list.innerHTML = '<div class="empty">Нажми ★ в инспекторе узла, чтобы сохранить сюда.</div>';
        return;
    }
    list.innerHTML = [...favorites].map(id => `
        <div class="fav-item" data-fav-jump="${esc(id)}">
            <span>★ ${esc(id.split("/").pop())}</span>
            <button class="fav-item-del" data-fav-del="${esc(id)}" title="Удалить">✕</button>
        </div>
    `).join("");

    list.querySelectorAll("[data-fav-jump]").forEach(el => el.addEventListener("click", e => {
        if (e.target.dataset.favDel) return;
        const targetNode = nodes.find(x => x.id === el.dataset.favJump);
        if (targetNode) {
            inspect(targetNode);
            pan.x = canvas.clientWidth / 2 - targetNode.x * scale;
            pan.y = canvas.clientHeight / 2 - targetNode.y * scale;
            requestRender();
        }
    }));

    list.querySelectorAll("[data-fav-del]").forEach(btn => btn.addEventListener("click", e => {
        e.stopPropagation();
        favorites.delete(btn.dataset.favDel);
        localStorage.setItem(FAVORITES_KEY, JSON.stringify([...favorites]));
        renderFavorites();
        requestRender();
    }));
}

// Architectural Findings Renderer
function renderFindings() {
    const box = $("findings");
    if (!box || !data) return;

    const allFindings = data.findings || [];
    const allCount = allFindings.length;
    const errCount = allFindings.filter(f => f.severity === "error").length;
    const warnCount = allFindings.filter(f => f.severity === "warning").length;
    const infoCount = allFindings.filter(f => f.severity === "info").length;

    const pAll = document.querySelector('.filter-pill[data-severity="all"]');
    const pErr = document.querySelector('.filter-pill[data-severity="error"]');
    const pWarn = document.querySelector('.filter-pill[data-severity="warning"]');
    const pInfo = document.querySelector('.filter-pill[data-severity="info"]');
    if (pAll) pAll.textContent = `Все ${allCount}`;
    if (pErr) pErr.textContent = `Ошибки ${errCount}`;
    if (pWarn) pWarn.textContent = `Предупреждения ${warnCount}`;
    if (pInfo) pInfo.textContent = `Инфо ${infoCount}`;

    const fs = allFindings.filter(f => filterSeverity === "all" || f.severity === filterSeverity);

    if (!fs.length) {
        box.innerHTML = '<div class="empty">Архитектурных проблем в этой категории не найдено.</div>';
        return;
    }

    box.innerHTML = fs.map((f, i) => `
        <div class="finding" data-finding-idx="${i}">
            <div class="finding-head">
                <span class="severity ${esc(f.severity)}"></span>
                <span>${esc(f.title)}</span>
                <span class="count-badge" style="margin-left:auto">${f.nodes.length}</span>
            </div>
            <p>${esc(f.description)}</p>
        </div>
    `).join("");

    box.querySelectorAll(".finding").forEach((el, idx) => el.addEventListener("click", () => {
        const f = fs[idx];
        if (!f) return;
        activeCycleNodes = new Set(f.nodes);
        const cycleObjs = nodes.filter(n => activeCycleNodes.has(n.id));

        if (cycleObjs.length > 0) {
            inspect(cycleObjs[0]);
            let minX = Infinity, maxX = -Infinity, minY = Infinity, maxY = -Infinity;
            cycleObjs.forEach(n => {
                minX = Math.min(minX, n.x); maxX = Math.max(maxX, n.x);
                minY = Math.min(minY, n.y); maxY = Math.max(maxY, n.y);
            });
            const w = canvas.clientWidth, h = canvas.clientHeight;
            const gw = Math.max(80, maxX - minX), gh = Math.max(80, maxY - minY);
            scale = Math.max(0.1, Math.min(2.0, Math.min((w - 180) / gw, (h - 180) / gh)));
            pan.x = (w - gw * scale) / 2 - minX * scale;
            pan.y = (h - gh * scale) / 2 - minY * scale;
            showToast(`Выделены проблемные узлы (${f.nodes.length})`);
            requestRender();
        }
    }));
}

// Side-by-Side Node Comparator
function renderNodeComparison() {
    const idA = $("compareNodeA").value;
    const idB = $("compareNodeB").value;
    const container = $("compareTableContainer");

    const nodeA = nodes.find(n => n.id === idA);
    const nodeB = nodes.find(n => n.id === idB);

    if (!nodeA || !nodeB) {
        container.innerHTML = '<div class="empty">Выберите оба узла для детального сопоставления.</div>';
        return;
    }

    const inA = new Set(edges.filter(e => e.target === nodeA.id).map(e => e.source));
    const inB = new Set(edges.filter(e => e.target === nodeB.id).map(e => e.source));
    const sharedIn = [...inA].filter(x => inB.has(x));

    const outA = new Set(edges.filter(e => e.source === nodeA.id).map(e => e.target));
    const outB = new Set(edges.filter(e => e.source === nodeB.id).map(e => e.target));
    const sharedOut = [...outA].filter(x => outB.has(x));

    container.innerHTML = `
        <table class="compare-table">
            <thead>
                <tr>
                    <th>Метрика / Параметр</th>
                    <th>${esc(nodeA.id.split("/").pop())}</th>
                    <th>${esc(nodeB.id.split("/").pop())}</th>
                </tr>
            </thead>
            <tbody>
                <tr><td>Язык / Тип</td><td>${esc(nodeA.language)}</td><td>${esc(nodeB.language)}</td></tr>
                <tr><td>Архитектурный слой</td><td>${esc(nodeA.layer)}</td><td>${esc(nodeB.layer)}</td></tr>
                <tr><td>Строк кода (LoC)</td><td>${nodeA.lines}</td><td>${nodeB.lines}</td></tr>
                <tr><td>Fan-In (Входящие Ca)</td><td>${nodeA.fan_in}</td><td>${nodeB.fan_in}</td></tr>
                <tr><td>Fan-Out (Исходящие Ce)</td><td>${nodeA.fan_out}</td><td>${nodeB.fan_out}</td></tr>
                <tr><td>Нестабильность (I)</td><td>${nodeA.instability}</td><td>${nodeB.instability}</td></tr>
                <tr><td>Радиус поражения (Blast)</td><td>${nodeA.blast_radius} файлов</td><td>${nodeB.blast_radius} файлов</td></tr>
                <tr><td>Участие в циклах</td><td>${nodeA.in_cycle ? '⚠️ Да' : '✓ Нет'}</td><td>${nodeB.in_cycle ? '⚠️ Да' : '✓ Нет'}</td></tr>
                <tr><td>Общие потребители (Входящие)</td><td colspan="2">${sharedIn.length ? sharedIn.map(esc).join(", ") : 'Нет общих'}</td></tr>
                <tr><td>Общие зависимости (Исходящие)</td><td colspan="2">${sharedOut.length ? sharedOut.map(esc).join(", ") : 'Нет общих'}</td></tr>
            </tbody>
        </table>
    `;
}

$("compareNodeA")?.addEventListener("change", renderNodeComparison);
$("compareNodeB")?.addEventListener("change", renderNodeComparison);

// AI Assistance Suite Generators
function openAiModal(title, text) {
    const modal = $("aiModal");
    $("aiModalTitle").textContent = title;
    $("aiModalTextarea").value = text;
    const tokenEst = Math.round(text.length / 4);
    $("aiModalTokenEst").textContent = `~${tokenEst.toLocaleString()} токенов контекста`;
    modal.classList.remove("hidden");
}

function generateMasterRefactorPrompt() {
    if (!data) return "Запустите анализ проекта перед генерацией.";
    const topGods = nodes.filter(n => n.is_god_object).slice(0, 8);
    const topBlast = [...nodes].sort((a,b) => (b.blast_radius||0) - (a.blast_radius||0)).slice(0, 8);
    const cycles = data.cycle_details || [];

    return `# Архитектурный аудит и мастер-план рефакторинга кодовой базы

## 1. Сводка проекта
- **Путь**: \`${data.root}\`
- **Индекс архитектурного здоровья**: ${data.health_score}/100
- **Всего файлов**: ${data.stats.files} (C#: ${data.stats.csharp}, GDScript: ${data.stats.gdscript}, Сцен: ${data.stats.scenes || 0})
- **Связей в графе**: ${data.stats.edges}
- **Всего строк кода**: ${data.stats.lines.toLocaleString()}
- **Критических циклов**: ${data.stats.cycles}
- **God-Objects / Монолитных хабов**: ${data.stats.god_objects || 0}

## 2. Критические циклические зависимости (Circular Dependency Cycles)
${cycles.length ? cycles.map((c, i) => `### Цикл #${i + 1} (${c.size} файлов)
- Узлы: ${c.nodes.map(n => `\`${n}\``).join(" ↔ ")}
- Слабое звено для разрыва: \`${c.weakest_link ? `${c.weakest_link.source} → ${c.weakest_link.target}` : 'N/A'}\`
- Рекомендация: ${c.recommendation}
`).join("\n") : "Циклических зависимостей не обнаружено."}

## 3. Критические хабы с наибольшим радиусом поражения (Top Blast Radius)
Изменения в этих файлах вызывают максимальный риск регрессии:
${topBlast.map(n => `- \`${n.path}\` — Blast Radius: **${n.blast_radius}** файлов, Fan-in: ${n.fan_in}, Fan-out: ${n.fan_out}, Instability: ${n.instability}`).join("\n")}

## 4. Монолитные классы (God-Objects)
${topGods.map(n => `- \`${n.path}\` (${n.lines} строк, ${n.degree} связей). Нарушает Single Responsibility Principle (SRP).`).join("\n")}

## 5. Задание для AI-ассистента (Claude / GPT-4o / Cursor)
1. Разработай пошаговый план устранения обнаруженных циклических зависимостей, применяя **Dependency Inversion Principle (DIP)** или паттерн **Event Aggregator / Signal Bus**.
2. Предложи архитектуру интерфейсов и модулей для декомпозиции топ God-Objects.
3. Сохраняй обратную совместимость API и укажи, какие тесты необходимо добавить для покрытия уязвимых мест с высоким Blast Radius.
`;
}

function generateCycleBreakerPrompt() {
    if (!data) return "";
    const cycles = data.cycle_details || [];
    return `# Целевой промпт разрыва циклических зависимостей (DIP)

В проекте обнаружено ${cycles.length} закрытых циклов зависимостей.

${cycles.map((c, idx) => `
### Цикл #${idx + 1}
- **Участники цикла**:
${c.nodes.map(n => `  - \`${n}\``).join("\n")}
- **Рекомендуемое место разрыва**:
  ${c.weakest_link ? `Разрезать ребро \`${c.weakest_link.source}\` → \`${c.weakest_link.target}\` (${c.weakest_link.type})` : 'Вынести общий интерфейс'}
- **Решение**:
  ${c.recommendation}
`).join("\n\n")}

**Инструкция**:
Напиши точный код интерфейсов (C# \`interface\` или GDScript \`class_name\`), который нужно создать, чтобы убрать прямую связь между этими компонентами.
`;
}

function generateSingleFilePrompt(node, inEdges, outEdges) {
    return `# План рефакторинга файла: \`${node.path}\`

## Архитектурные метрики
- **Язык**: ${node.language}
- **Слой**: ${node.layer}
- **Строк кода**: ${node.lines}
- **Нестабильность I**: ${node.instability} (${node.instability < 0.3 ? 'Очень стабильный (Core)' : node.instability > 0.7 ? 'Нестабильный (Coordinator)' : 'Средний'})
- **Радиус поражения (Blast Radius)**: ${node.blast_radius} зависимых файлов
- **В цикле**: ${node.in_cycle ? 'Да ⚠️' : 'Нет ✓'}
- **God-Object**: ${node.is_god_object ? 'Да ⚠️' : 'Нет ✓'}

## Входящие зависимости (${inEdges.length})
${inEdges.slice(0, 15).map(e => `- \`${e.source}\` (${e.type})`).join("\n")}

## Исходящие зависимости (${outEdges.length})
${outEdges.slice(0, 15).map(e => `- \`${e.target}\` (${e.type})`).join("\n")}

## Задание
Проанализируй связность этого файла. Как декомпозировать его на более узкие интерфейсы и устранить избыточную связанность, минимизируя влияние на зависимые файлы?
`;
}

function generateMermaidDiagram() {
    if (!data) return "";
    const limit = Math.min(60, visibleNodesCache.length);
    const subNodes = visibleNodesCache.slice(0, limit);
    const subSet = new Set(subNodes.map(n => n.id));
    const subEdges = visibleEdgesCache.filter(e => subSet.has(e.source) && subSet.has(e.target)).slice(0, 100);

    const sanitize = s => s.replace(/[^a-zA-Z0-9_]/g, "_");

    let out = "```mermaid\ngraph TD\n";
    // Group subgraphs by folder
    const folders = new Map();
    subNodes.forEach(n => {
        if (!folders.has(n.folder)) folders.set(n.folder, []);
        folders.get(n.folder).push(n);
    });

    for (const [fld, fNodes] of folders.entries()) {
        out += `  subgraph ${sanitize(fld)} ["📁 ${fld}"]\n`;
        fNodes.forEach(n => {
            const label = n.classes[0] || n.id.split("/").pop();
            out += `    ${sanitize(n.id)}["${label}"]\n`;
        });
        out += "  end\n";
    }

    subEdges.forEach(e => {
        out += `  ${sanitize(e.source)} --> ${sanitize(e.target)}\n`;
    });
    out += "```\n";
    return out;
}

function generatePlantUml() {
    if (!data) return "";
    const sanitize = s => s.replace(/[^a-zA-Z0-9_]/g, "_");
    let out = "@startuml\nskinparam componentStyle rectangle\n\n";

    const limit = Math.min(50, visibleNodesCache.length);
    const subNodes = visibleNodesCache.slice(0, limit);
    const subSet = new Set(subNodes.map(n => n.id));
    const subEdges = visibleEdgesCache.filter(e => subSet.has(e.source) && subSet.has(e.target)).slice(0, 80);

    subNodes.forEach(n => {
        const label = n.classes[0] || n.id.split("/").pop();
        out += `component [${label}] as ${sanitize(n.id)}\n`;
    });
    out += "\n";
    subEdges.forEach(e => {
        out += `${sanitize(e.source)} ..> ${sanitize(e.target)} : ${e.type}\n`;
    });
    out += "\n@enduml\n";
    return out;
}

function generateCompactDigest() {
    if (!data) return "";
    let out = `# Компактный архитектурный дайджест проекта (${data.stats.files} файлов)\n\n`;
    out += "| Модуль / Файл | Язык | LoC | Ca | Ce | Instability | Blast | Роль |\n";
    out += "| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :--- |\n";

    const topNodes = [...nodes].sort((a,b) => (b.degree || 0) - (a.degree || 0)).slice(0, 40);
    topNodes.forEach(n => {
        out += `| \`${n.path}\` | ${n.language} | ${n.lines} | ${n.fan_in} | ${n.fan_out} | ${n.instability} | ${n.blast_radius} | ${n.is_autoload ? 'Autoload' : n.layer} |\n`;
    });
    return out;
}

function formatNodeDepsList(n) {
    const inEdges = edges.filter(e => e.target === n.id);
    const outEdges = edges.filter(e => e.source === n.id);
    let txt = `Файл: ${n.path}\n`;
    txt += `Язык: ${n.language} | Слой: ${n.layer || 'domain'} | Нестабильность I: ${n.instability ?? 0.5}\n`;
    txt += `Всего связанных файлов: ${inEdges.length + outEdges.length}\n`;
    txt += "=".repeat(50) + "\n";
    txt += `← ВХОДЯЩИЕ ЗАВИСИМОСТИ (файлы, которые зависят от ${n.path.split('/').pop()}): ${inEdges.length}\n`;
    if (inEdges.length) {
        inEdges.forEach(e => { txt += `  • ${e.source} [${e.type}, ${e.confidence}] ${e.detail || ''}\n`; });
    } else {
        txt += "  (нет входящих зависимостей)\n";
    }
    txt += "\n";
    txt += `→ ИСХОДЯЩИЕ ЗАВИСИМОСТИ (${n.path.split('/').pop()} зависит от этих файлов): ${outEdges.length}\n`;
    if (outEdges.length) {
        outEdges.forEach(e => { txt += `  • ${e.target} [${e.type}, ${e.confidence}] ${e.detail || ''}\n`; });
    } else {
        txt += "  (нет исходящих зависимостей)\n";
    }
    if (n.functions_detail && n.functions_detail.length) {
        txt += "\nФУНКЦИИ И МЕТОДЫ:\n";
        n.functions_detail.forEach(fn => {
            const callers = fn.callers && fn.callers.length ? ` | Вызывается в: ${fn.callers.join(', ')}` : " | Локальный вызов";
            txt += `  - ${fn.name}(${fn.params || ''})${callers}\n`;
        });
    }
    txt += "=".repeat(50) + "\n";
    return txt;
}

function formatAllProjectDepsList() {
    if (!data) return "Проект не проанализирован.";
    let txt = `# КАРТА ЗАВИСИМОСТЕЙ ПРОЕКТА: ${data.root}\n`;
    txt += `Всего файлов: ${data.stats.files}, Связей: ${data.stats.edges}, Здоровье архитектуры: ${data.health_score}%\n`;
    txt += "=".repeat(60) + "\n";
    nodes.slice().sort((a,b) => a.id.localeCompare(b.id)).forEach(n => {
        const inEdges = edges.filter(e => e.target === n.id).map(e => e.source);
        const outEdges = edges.filter(e => e.source === n.id).map(e => e.target);
        txt += `\n[${n.path}] (${n.language}, LoC: ${n.lines}, I: ${n.instability})\n`;
        if (inEdges.length) txt += `  ← Входящие (${inEdges.length}): ${inEdges.join(', ')}\n`;
        if (outEdges.length) txt += `  → Исходящие (${outEdges.length}): ${outEdges.join(', ')}\n`;
        if (!inEdges.length && !outEdges.length) txt += "  (изолированный файл)\n";
    });
    return txt;
}

function formatFunctionContext(node, fn) {
    let txt = `АНАЛИЗ ФУНКЦИИ: ${fn.name}\n`;
    txt += `Файл: ${node.path} (${node.language})\n`;
    txt += `Сигнатура: ${fn.return_type || 'void'} ${fn.name}(${fn.params || ''})\n`;
    txt += "-".repeat(50) + "\n";
    txt += `📥 ВХОДЯЩИЕ ВЫЗОВЫ (Кто использует в проекте: ${(fn.callers || []).length}):\n`;
    if (fn.callers && fn.callers.length) {
        fn.callers.forEach(c => { txt += `  ← ${c}\n`; });
    } else {
        txt += "  (внешних вызовов не найдено, используется локально или динамически)\n";
    }
    txt += `\n📦 КТО ДАЁТ ДАННЫЕ (Входной поток):\n`;
    txt += `  • Параметры функции: ${fn.params || 'нет параметров'}\n`;
    if (fn.accessed_autoloads && fn.accessed_autoloads.length) {
        txt += `  • Доступ к глобальным синглтонам (Autoload): ${fn.accessed_autoloads.join(', ')}\n`;
    }
    if (fn.triggering_signals && fn.triggering_signals.length) {
        txt += `  • Привязанные сигналы/триггеры: ${fn.triggering_signals.join(', ')}\n`;
    }
    txt += "=".repeat(50) + "\n";
    return txt;
}

function generateCliCommandsHelp() {
    return `# Консольные команды для ИИ-агентов (Autonomous CLI Mode)

Инструмент поддерживает автономный вызов из терминала (без веб-интерфейса) для ИИ-агентов (Claude Code, Cursor, Aider, Antigravity subagents).

## 1. Вывести список связей конкретного файла:
\`\`\`bash
DependencyGraphVisualizer.exe --cli --project . --deps "scripts/world/World.gd"
\`\`\`

## 2. Найти все вызовы и использование функции в проекте:
\`\`\`bash
DependencyGraphVisualizer.exe --cli --project . --func "generate_region"
\`\`\`

## 3. Вывести скелеты публичных API (-90% токенов):
\`\`\`bash
DependencyGraphVisualizer.exe --cli --project . --skeletons
\`\`\`

## 4. Вывести реестр сигналов Godot и событий C#:
\`\`\`bash
DependencyGraphVisualizer.exe --cli --project . --signals
\`\`\`

## 5. Вывести иерархию нод сцен (.tscn):
\`\`\`bash
DependencyGraphVisualizer.exe --cli --project . --scenes
\`\`\`

## 6. Ревизия мертвого кода (truly dead vs scene entry points):
\`\`\`bash
DependencyGraphVisualizer.exe --cli --project . --orphans
\`\`\`

## 7. Проверить горячие пути (60-FPS Hot-Paths):
\`\`\`bash
DependencyGraphVisualizer.exe --cli --project . --hotpaths
\`\`\`

## 8. Вывести полную карту связей всех файлов проекта:
\`\`\`bash
DependencyGraphVisualizer.exe --cli --project . --all-deps
\`\`\`

## 9. Вывод в формате чистого JSON для скриптов:
\`\`\`bash
DependencyGraphVisualizer.exe --cli --project . --deps "src/agents/Agent.cs" --json
\`\`\`
`;
}

function generateSkeletonsDigest() {
    if (!data) return "Запустите анализ проекта.";
    const totalLines = data.stats.lines || 1;
    const estFullTokens = Math.round(totalLines * 7.5);
    let totalSkeletonTokens = 0;

    let text = `# Скелеты публичных контрактов и API кодовой базы\n\n`;
    text += `> Этот документ содержит только типы, интерфейсы, сигнатуры методов и сигналы БЕЗ тел функций.\n`;
    text += `> Используйте его при работе с Claude / GPT для экономии 90%+ токенов и точного соблюдения архитектуры.\n\n`;

    const skelNodes = nodes.filter(n => n.skeleton && n.skeleton.trim().length > 0);
    if (!skelNodes.length) {
        text += `*(В анализируемом проекте не найдено файлов C# / GDScript с публичными классами/сигнатурами или проект еще не просканирован)*\n`;
        return text;
    }

    skelNodes.forEach(n => {
        totalSkeletonTokens += Math.round(n.skeleton.length / 4);
    });

    const savingsPct = estFullTokens > 0 ? Math.max(70, Math.round((1 - totalSkeletonTokens / estFullTokens) * 100)) : 90;
    text += `**Статистика экономии токенов**:\n`;
    text += `- Исходный код проекта (${skelNodes.length} файлов): ~${estFullTokens.toLocaleString()} токенов\n`;
    text += `- Сжатые скелеты API: ~${totalSkeletonTokens.toLocaleString()} токенов\n`;
    text += `- Экономия контекста и бюджета: **${savingsPct}%**\n\n`;
    text += "=".repeat(60) + "\n\n";

    skelNodes.forEach(n => {
        text += `## Файл: \`${n.path}\` (${n.language}, LoC: ${n.lines}, Instability: ${n.instability})\n`;
        text += "```" + (n.language === "C#" ? "csharp" : "gdscript") + "\n";
        text += n.skeleton.trim() + "\n";
        text += "```\n\n";
    });

    return text;
}

function generateSignalsRegistryMarkdown() {
    if (!data) return "Запустите анализ проекта.";
    const reg = data.signal_registry || {};
    const keys = Object.keys(reg).sort();
    let text = `# Глобальный реестр сигналов Godot и событий C# (${keys.length} сигналов)\n\n`;
    if (!keys.length) {
        text += `*В проекте не найдено объявлений сигналов Godot (signal / [Signal]) или C# событий (event).*\n`;
        text += `*(Если в проекте есть сигналы, убедитесь, что запущен свежий DependencyGraphVisualizer.exe без фоновых зависших копий)*\n`;
        return text;
    }
    text += `| Сигнал / Событие | Где объявлен (Declared) | Кто отправляет (Emits) | Кто слушает (Connected) | Диагностика |\n`;
    text += `| :--- | :--- | :--- | :--- | :--- |\n`;

    keys.forEach(k => {
        const item = reg[k];
        const decl = item.declared_in.length ? item.declared_in.map(x => `\`${x.split('/').pop()}\``).join(", ") : "—";
        const emit = item.emitted_by.length ? item.emitted_by.map(x => `\`${x.split('/').pop()}\``).join(", ") : "—";
        const conn = item.connected_in.length ? item.connected_in.map(x => `\`${x.split('/').pop()}\``).join(", ") : "—";
        let status = "✓ Активен";
        if (!item.connected_in.length) status = "⚠️ Нет слушателей (никто не подключен)";
        else if (!item.emitted_by.length) status = "⚠️ Никогда не вызывается (нет emit)";
        text += `| \`${k}\` | ${decl} | ${emit} | ${conn} | ${status} |\n`;
    });
    return text;
}

function generateSceneTreesMarkdown() {
    if (!data) return "Запустите анализ проекта.";
    const scenes = nodes.filter(n => n.language === "Scene" || (n.node_tree && n.node_tree.length > 0));
    let text = `# Иерархия сцен (.tscn) и дерево нод (${scenes.length} сцен)\n\n`;
    if (!scenes.length) {
        text += `*В проекте не обнаружено файлов сцен .tscn.*\n`;
        text += `*(Убедитесь, что в выбранной директории присутствуют файлы .tscn и запущен свежий DependencyGraphVisualizer.exe)*\n`;
        return text;
    }
    scenes.forEach(s => {
        text += `## Сцена: \`${s.path}\`\n`;
        if (s.node_tree && s.node_tree.length) {
            s.node_tree.forEach(node => {
                const prefix = node.parent && node.parent !== '.' ? '  ├─ ' : '📦 ';
                const scr = node.script ? ` → [Script: \`${node.script}\`]` : '';
                text += `${prefix}\`${node.name}\` (${node.type || 'Node'})${scr}\n`;
            });
        } else {
            text += `(Нод в сцене не найдено или сцена пустая)\n`;
        }
        text += "\n";
    });
    return text;
}

function generateOrphansReviewMarkdown() {
    if (!data) return "Запустите анализ проекта.";
    const dead = nodes.filter(n => n.orphan_status === "truly_dead");
    const sceneEntry = nodes.filter(n => n.orphan_status === "scene_entry_point");

    let text = `# Ревизия мертвого кода и точек входа сцен\n\n`;
    text += `### 💀 1. Действительно мертвый код (${dead.length} файлов)\n`;
    text += `*Эти файлы имеют 0 связей в коде и НЕ привязаны ни к одной сцене проекта. Скорее всего, это забытые черновики или кандидаты на удаление:*\n\n`;
    if (dead.length) {
        dead.forEach(n => {
            text += `- \`${n.path}\` (${n.language}, ${n.lines} строк) — *0 входящих, 0 исходящих*\n`;
        });
    } else {
        text += `✓ Действительно мертвого кода не обнаружено!\n`;
    }

    text += `\n### 🌲 2. Точки входа сцен (${sceneEntry.length} файлов)\n`;
    text += `*Эти файлы не вызываются напрямую из C#/GDScript кода, НО прикреплены к нодам сцен (.tscn). Они запускаются движком Godot при загрузке сцен:*\n\n`;
    if (sceneEntry.length) {
        sceneEntry.forEach(n => {
            text += `- \`${n.path}\` (${n.language}, ${n.lines} строк) — *Прикреплен к сцене: точка входа*\n`;
        });
    } else {
        text += `Точек входа не обнаружено.\n`;
    }
    return text;
}

function generateHotPathsMarkdown() {
    if (!data) return "Запустите анализ проекта.";
    const hp = data.hot_paths || [];
    let text = `# Анализ горячих путей (60-FPS Hot-Paths) и оптимизация\n\n`;
    text += `> В методах, исполняемых каждый кадр (\`_process\`, \`_physics_process\`, \`Update\`), действуют строгие правила производительности.\n\n`;
    text += `### Обнаруженные 60-FPS методы в проекте (${hp.length}):\n\n`;
    if (hp.length) {
        hp.forEach(h => {
            text += `- \`${h.file}\` → **\`${h.method}(${h.params || ''})\`**\n`;
        });
    } else {
        text += `Горячих путей не обнаружено.\n`;
    }

    text += `\n### ⚡ 5 Золотых правил для 60 FPS:\n`;
    text += `1. **Zero Allocations (Никаких \`new\`)**: Не создавайте объекты, списки и строки в \`_process\`. GC вызовет микрофризы.\n`;
    text += `2. **Кэшируйте ноды**: Не используйте \`GetNode()\` или \`$Node\` каждый кадр. Кэшируйте ссылки в \`_ready\`.\n`;
    text += `3. **Используйте Object Pool**: Вместо \`Instantiate()\` / \`queue_free()\` на каждый снаряд используйте пул объектов.\n`;
    text += `4. **Откажитесь от Linq и лямбд в горячих циклах**: Linq в C# аллоцирует итераторы в куче.\n`;
    text += `5. **Тяжелую логику выносите в таймеры**: Если логику не нужно считать 60 раз в секунду, считайте ее раз в 0.2 сек через Timer.\n`;
    return text;
}

function generateWhatIfPlanMarkdown() {
    if (!data) return "Запустите анализ проекта.";
    let text = `# План рефакторинга архитектуры на основе виртуальных разрывов (What-If Sandbox)\n\n`;
    text += `> Этот план сгенерирован в песочнице интерактивного анализа связей. Ниже перечислены виртуально разорванные зависимости и инструкции по внедрению Dependency Inversion (DIP) / Event-Driven архитектуры.\n\n`;

    if (cutEdges.size === 0) {
        text += `*В текущей сессии What-If не выбрано ни одного разрыва связей.* Нажмите кнопку «✂️ What-If» в шапке и кликните по ребрам на графе или кнопкам «✂️» в инспекторе для виртуального разрыва связей.\n`;
        return text;
    }

    const cutList = Array.from(cutEdges).map(k => {
        const [source, target] = k.split("->");
        const edge = edges.find(e => e.source === source && e.target === target);
        return { source, target, edge };
    });

    text += `### ✂️ Разорванные связи (${cutList.length}):\n`;
    cutList.forEach((c, idx) => {
        text += `${idx + 1}. \`${c.source}\` ➔ \`${c.target}\` *(тип: ${c.edge?.type || 'dependency'})*\n`;
    });

    const initCycles = data.stats.cycles || 0;
    const remainingCount = simulatedCycles.length;
    const brokenCount = Math.max(0, initCycles - remainingCount);

    text += `\n### 📊 Прогнозируемый архитектурный эффект:\n`;
    text += `- Было циклов в проекте: **${initCycles}**\n`;
    text += `- Осталось циклов после разрывов: **${remainingCount}**\n`;
    text += `- Ликвидировано циклических зависимостей: **${brokenCount}**\n`;
    text += `- Прогноз индекса здоровья архитектуры: **${$("healthScoreVal")?.textContent || '100%'}**\n\n`;

    text += `### 🛠 Инструкция для ИИ / Архитектора по физической реализации разрывов:\n`;
    cutList.forEach((c, idx) => {
        const srcNode = nodes.find(n => n.id === c.source);
        const tgtNode = nodes.find(n => n.id === c.target);
        text += `#### Разрыв ${idx + 1}: \`${c.source.split('/').pop()}\` не должен зависеть от \`${c.target.split('/').pop()}\` напрямую\n`;
        text += `1. **Проблема**: Прямой импорт или вызов создаёт тесную связь (Coupling) и${c.edge?.in_cycle ? ' циклический дедлок архитектуры' : ' нарушает слоистый поток'}.\n`;
        text += `2. **Решение (Dependency Inversion)**: Выделите интерфейс (например, \`I${tgtNode?.classes[0] || 'Service'}\`) или используйте C# Events / Godot Signals.\n`;
        text += `3. **Реализация**:\n`;
        text += `   - В модуле \`${c.source}\` заменить прямое поле/ссылку на \`${tgtNode?.classes[0] || 'TargetClass'}\` на интерфейс или подписку на сигнал.\n`;
        text += `   - Передавать зависимость через конструктор / \`_ready\` / инъекцию (DI) вместо \`GetNode\` или \`new\`.\n\n`;
    });

    return text;
}

function generateEventFlowsMarkdown() {
    if (!data) return "Запустите анализ проекта.";
    const flows = data.event_flows || [];
    let text = `# Сквозные цепочки реакций и событий (Event-Flow Pipeline, ${flows.length} цепочек)\n\n`;
    text += `> Документ трассирует сквозной путь событий: [Источник] ➔ [Сигнал] ➔ [Слушатели / Обработчики] ➔ [Дальнейшие вызовы].\n`;
    text += `> Позволяет ИИ понять реактивный поток данных без необходимости читать сотни файлов исходников.\n\n`;

    if (!flows.length) {
        text += `*(В проекте не найдено активных связанных цепочек сигналов и подписчиков)*\n`;
        return text;
    }

    flows.forEach((flow, idx) => {
        const srcFile = flow.origin_file || flow.source_file || "Неизвестный источник";
        const srcMethod = flow.source_method && flow.source_method !== "(signal)" ? ` :: \`${flow.source_method}()\`` : "";
        text += `### ${idx + 1}. Сигнал: \`${flow.signal}\`\n`;
        text += `- **Источник (Origin)**: \`${srcFile}\`${srcMethod}\n`;
        text += `- **Тип потока**: ${flow.is_async ? '⚡ Асинхронный (deferred / await)' : 'Синхронный (Direct emit)'}\n`;
        const lis = flow.listeners || [];
        text += `- **Подписчики и обработчики** (${lis.length}):\n`;
        lis.forEach(l => {
            const lFile = l.file || l.target_file || "Unknown";
            const lMethod = l.method || (l.handlers && l.handlers[0]) || "обрабатывает";
            const lCalls = l.calls || l.downstream_calls || [];
            text += `  - \`${lFile}\` → метод **\`${lMethod}()\`**\n`;
            if (lCalls.length) {
                text += `    - Вызывает дальше: ${lCalls.map(c => `\`${c}()\``).join(", ")}\n`;
            }
        });
        if (flow.downstream_calls && flow.downstream_calls.length) {
            text += `- **Суммарный каскад вызовов**: ${flow.downstream_calls.map(c => `\`${c}\``).join(", ")}\n`;
        }
        text += "\n";
    });
    return text;
}

function generateBenchmarkMarkdown() {
    if (!data) return "Запустите анализ проекта.";
    const prof = data.profiling || {};
    let fileList = [];
    if (Array.isArray(prof.files)) {
        fileList = prof.files;
    } else if (prof.files && typeof prof.files === "object") {
        fileList = Object.entries(prof.files).map(([id, p]) => ({ file: id, ...p }));
    }
    const bottlenecks = prof.bottlenecks || [];

    let text = `# Отчет замеров нагрузки (CPU Simulation & Hot-Path Profiler)\n\n`;
    text += `> Аналитическая симуляция кадра (60 FPS Budget = 16.6 мс). Оценка времени исполнения, нагрузки на GC и узких мест.\n\n`;

    text += `### 📊 Сводка производительности проекта:\n`;
    text += `- **Суммарное время кадра (CPU Frame Time)**: **${prof.total_frame_ms || 0} мс**\n`;
    text += `- **Расход бюджета 60 FPS (16.6 мс)**: **${prof.total_frame_ms ? ((prof.total_frame_ms / 16.6) * 100).toFixed(0) : 0}%**\n`;
    text += `- **Скорость аллокаций памяти (GC Allocations)**: **${prof.total_alloc_kb_per_sec || 0} КБ/с**\n`;
    text += `- **Обнаружено критических узких мест (Bottlenecks)**: **${bottlenecks.length} файлов**\n\n`;

    if (bottlenecks.length > 0) {
        text += `### 🔥 Топ критических узких мест (Тормозят кадр):\n\n`;
        text += `| Файл | мс / кадр | % CPU | GC КБ/с | Горячие методы | Причина |\n`;
        text += `| :--- | :--- | :--- | :--- | :--- | :--- |\n`;
        bottlenecks.forEach(b => {
            const bObj = typeof b === "string" ? (fileList.find(f => f.file === b) || { file: b }) : b;
            const bFile = bObj.file || (typeof b === "string" ? b : "");
            const hotM = (bObj.hot_methods && bObj.hot_methods.length) ? bObj.hot_methods.join(", ") : "—";
            const ms = bObj.ms_per_frame ?? "—";
            const cpu = bObj.cpu_percent ?? "—";
            const alloc = bObj.alloc_kb_per_sec ?? "—";
            const reason = bObj.reason || (ms > 1.2 ? "Превышен лимит кадра" : "Высокие аллокации GC");
            text += `| \`${bFile.split('/').pop() || bFile}\` | **${ms} мс** | ${cpu}% | ${alloc} | \`${hotM}\` | ${reason} |\n`;
        });
        text += "\n";
    }

    text += `### 📋 Детальная разбивка по всем файлам (Топ-30 по нагрузке):\n\n`;
    text += `| Файл | Язык | мс / кадр | Вызовов/кадр | Аллокации |\n`;
    text += `| :--- | :--- | :--- | :--- | :--- |\n`;
    fileList.slice(0, 30).forEach(f => {
        text += `| \`${f.file}\` | ${f.language || 'GDScript/C#'} | ${f.ms_per_frame} мс | ${f.calls_per_frame || 1} | ${f.alloc_kb_per_sec || 0} КБ/с |\n`;
    });

    text += `\n### ⚡ Советы архитектора по оптимизации:\n`;
    text += `1. **Вынос из _process**: Любую логику свыше 1.5 мс/кадр (поиск путей, апдейт интерфейса, расчет цен) перенесите на таймер Timer с частотой 5–10 раз в секунду.\n`;
    text += `2. **Zero Allocation**: Избавьтесь от new и строковой конкатенации в методах кадра, чтобы исключить паузы сборщика мусора (.NET GC / Godot GC).\n`;
    text += `3. **Spatial Indexing**: Вместо обхода списков через foreach используйте координатные сетки или Area2D/3D коллизии движка.\n`;

    return text;
}

function generatePassportsDigestMarkdown() {
    if (!data) return "Запустите анализ проекта.";
    let text = `# Паспорта поведения функций кодовой базы (Function Behavior Passports)\n\n`;
    text += `> Паспорт функции лаконично фиксирует: назначение, docstring, входные параметры, мутации состояния, испускаемые сигналы и создаваемые объекты.\n`;
    text += `> Идеально для нейросети: дает исчерпывающее понимание логики каждого метода без чтения его исходного кода.\n\n`;

    const nodesWithFuncs = nodes.filter(n => n.functions_detail && n.functions_detail.length > 0);
    if (!nodesWithFuncs.length) {
        text += `*(В анализируемом проекте не найдено функций или проект еще не просканирован)*\n`;
        return text;
    }

    nodesWithFuncs.forEach(n => {
        text += `## Файл: \`${n.path}\` (${n.language})\n`;
        n.functions_detail.forEach(fn => {
            text += `### Метод: \`${fn.name}(${fn.params || ''})\`\n`;
            if (fn.docstring) text += `- 📖 **Документация**: *${fn.docstring}*\n`;
            if (fn.summary_ru) text += `- 🎯 **Суть**: ${fn.summary_ru}\n`;
            if (fn.mutations && fn.mutations.length) text += `- ⚡ **Мутирует переменные**: \`${fn.mutations.join("`, `")}\`\n`;
            if (fn.emits && fn.emits.length) text += `- 📡 **Отправляет сигналы**: \`${fn.emits.join("`, `")}\`\n`;
            if (fn.spawns && fn.spawns.length) text += `- 📦 **Создает объекты (new)**: \`${fn.spawns.join("`, `")}\`\n`;
            if (fn.callers && fn.callers.length) text += `- 📥 **Вызывается из**: ${fn.callers.map(c => `\`${c.split('/').pop()}\``).join(", ")}\n`;
            text += "\n";
        });
        text += "---\n\n";
    });

    return text;
}

// Benchmark Modal Engine
function openBenchmarkModal() {
    if (!data || !data.profiling) {
        showToast("Сначала проанализируйте проект");
        return;
    }
    const prof = data.profiling;
    const summaryBar = $("benchmarkSummaryBar");
    if (summaryBar) {
        const frameBudget = 16.6;
        const budgetPct = ((prof.total_frame_ms / frameBudget) * 100).toFixed(0);
        const fileCount = Array.isArray(prof.files) ? prof.files.length : Object.keys(prof.files || {}).length;
        summaryBar.innerHTML = `
            <div>Нагрузка кадра: <b>${prof.total_frame_ms || 0} мс</b> (${budgetPct}% от 60 FPS)</div>
            <div>GC Память: <b>${prof.total_alloc_kb_per_sec || 0} КБ/с</b></div>
            <div>Узких мест: <b style="color:#ef4444">${(prof.bottlenecks || []).length}</b></div>
            <div>Файлов: <b>${fileCount}</b></div>
        `;
    }
    renderBenchmarkTable();
    openModal("benchmarkModal");
}

function renderBenchmarkTable() {
    const wrap = $("benchmarkTableWrap");
    if (!wrap || !data || !data.profiling) return;
    const query = ($("benchmarkSearch")?.value || "").toLowerCase().trim();
    let list = [];
    if (Array.isArray(data.profiling.files)) {
        list = data.profiling.files;
    } else if (data.profiling.files && typeof data.profiling.files === "object") {
        list = Object.entries(data.profiling.files).map(([id, p]) => ({ file: id, ...p }));
    }

    if (benchmarkBottlenecksOnly) {
        list = list.filter(f => f.is_bottleneck);
    }
    if (query) {
        list = list.filter(f => (f.file || "").toLowerCase().includes(query));
    }

    if (!list.length) {
        wrap.innerHTML = `<div class="empty" style="padding:16px">По запросу ничего не найдено.</div>`;
        return;
    }

    let html = `
        <table class="benchmark-table">
            <thead>
                <tr>
                    <th style="width:36px">#</th>
                    <th>Файл проекта</th>
                    <th style="width:85px">мс / кадр</th>
                    <th style="width:65px">% CPU</th>
                    <th style="width:85px">GC КБ/с</th>
                    <th>Горячий метод</th>
                    <th style="width:80px">Действие</th>
                </tr>
            </thead>
            <tbody>
    `;

    list.forEach((f, idx) => {
        const isB = f.is_bottleneck;
        const hot = (f.hot_methods && f.hot_methods.length) ? f.hot_methods.join(", ") : "—";
        html += `
            <tr style="${isB ? 'background:rgba(239, 68, 68, 0.08)' : ''}">
                <td style="color:var(--text-muted)">${idx + 1}</td>
                <td>
                    <div style="font-weight:600">${esc(f.file.split('/').pop())}</div>
                    <div class="muted" style="font-size:10px">${esc(f.file)}</div>
                </td>
                <td style="font-weight:700;color:${isB ? '#ef4444' : f.ms_per_frame > 1.5 ? '#f59e0b' : 'inherit'}">
                    ${f.ms_per_frame} мс
                </td>
                <td>${f.cpu_percent}%</td>
                <td>${f.alloc_kb_per_sec}</td>
                <td><code style="font-size:10px">${esc(hot)}</code></td>
                <td>
                    <button class="secondary" data-jump-bench="${esc(f.file)}" style="padding:2px 8px;font-size:10px">Показать</button>
                </td>
            </tr>
        `;
    });

    html += `</tbody></table>`;
    wrap.innerHTML = html;

    wrap.querySelectorAll("[data-jump-bench]").forEach(b => {
        b.addEventListener("click", () => {
            closeModal("benchmarkModal");
            const node = nodes.find(n => n.id === b.dataset.jumpBench || n.path === b.dataset.jumpBench);
            if (node) {
                inspect(node);
                pan.x = canvas.clientWidth / 2 - node.x * scale;
                pan.y = canvas.clientHeight / 2 - node.y * scale;
                requestRender();
            }
        });
    });
}

// Time-Based Benchmark Simulation Engine
let simInterval = null;
let simElapsedSeconds = 0;
let simTotalDuration = 60;
let simIsRunning = false;

function formatSimTime(sec) {
    const m = Math.floor(sec / 60);
    const s = Math.floor(sec % 60);
    return `${String(m).padStart(2, '0')}:${String(s).padStart(2, '0')}`;
}

function startBenchmarkSimulation() {
    if (!data || !data.profiling) {
        showToast("Сначала запустите анализ проекта");
        return;
    }
    simTotalDuration = Number($("simDurationSelect")?.value || 60);
    simElapsedSeconds = 0;
    simIsRunning = true;

    $("simLiveProgressWrap").style.display = "block";
    $("simRunBtn").style.display = "none";
    $("simStopBtn").style.display = "inline-block";
    $("simStatusText").textContent = "Симуляция выполняется...";
    $("simStatusText").style.color = "#38bdf8";

    updateSimulationMetrics();

    if (simInterval) clearInterval(simInterval);
    const tickIntervalMs = simTotalDuration >= 300 ? 500 : 200;
    const simStep = tickIntervalMs / 1000;

    simInterval = setInterval(() => {
        if (!simIsRunning) {
            clearInterval(simInterval);
            return;
        }
        simElapsedSeconds += simStep;
        if (simElapsedSeconds >= simTotalDuration) {
            simElapsedSeconds = simTotalDuration;
            stopBenchmarkSimulation(true);
        }
        updateSimulationMetrics();
    }, tickIntervalMs);
}

function stopBenchmarkSimulation(completed = false) {
    simIsRunning = false;
    if (simInterval) {
        clearInterval(simInterval);
        simInterval = null;
    }
    $("simRunBtn").style.display = "inline-block";
    $("simStopBtn").style.display = "none";
    if (completed) {
        $("simStatusText").textContent = `✓ Замер завершен за ${formatSimTime(simTotalDuration)}!`;
        $("simStatusText").style.color = "#22c55e";
        showToast(`✓ Замер нагрузки на ${formatSimTime(simTotalDuration)} успешно завершен!`);
    } else {
        $("simStatusText").textContent = `⏹ Замер остановлен на ${formatSimTime(simElapsedSeconds)}.`;
        $("simStatusText").style.color = "#f59e0b";
        showToast("Замер нагрузки остановлен");
    }
}

function updateSimulationMetrics() {
    const prof = data.profiling;
    const progress = Math.min(100, (simElapsedSeconds / simTotalDuration) * 100);
    $("simProgressBar").style.width = `${progress}%`;
    $("simTimeElapsed").textContent = `${formatSimTime(simElapsedSeconds)} / ${formatSimTime(simTotalDuration)}`;

    const loadFraction = Math.min(1.0, (prof.total_frame_ms || 1.0) / 16.6);
    const cpuSec = (simElapsedSeconds * loadFraction).toFixed(1);
    $("simMetricCpu").textContent = `${cpuSec} сек`;

    const allocKbTotal = (simElapsedSeconds * (prof.total_alloc_kb_per_sec || 0));
    const allocMb = (allocKbTotal / 1024).toFixed(1);
    $("simMetricMem").textContent = allocMb >= 1024 ? `${(allocMb / 1024).toFixed(2)} ГБ` : `${allocMb} МБ`;

    const gcPauses = Math.floor(allocKbTotal / 4096);
    $("simMetricGc").textContent = `${gcPauses} пауз`;

    const riskEl = $("simMetricRisk");
    if (prof.bottlenecks.length > 2 || prof.total_frame_ms > 16.6) {
        const estFps = Math.max(15, Math.round(1000 / Math.max(16.6, prof.total_frame_ms)));
        riskEl.textContent = `⚠️ ~${estFps} FPS (Дропы)`;
        riskEl.style.color = "#ef4444";
    } else if (prof.bottlenecks.length > 0 || prof.total_frame_ms > 12.0) {
        riskEl.textContent = `⚡ 50–55 FPS (Фризы)`;
        riskEl.style.color = "#f59e0b";
    } else {
        riskEl.textContent = `✓ 60 FPS (Стабильно)`;
        riskEl.style.color = "#22c55e";
    }
}

// DSM (Dependency Structure Matrix) Engine
function openDsmModal() {
    if (!data || !nodes.length) {
        showToast("Сначала проанализируйте проект");
        return;
    }
    renderDsmTable();
    openModal("dsmModal");
}

function renderDsmTable() {
    const container = $("dsmContainer");
    if (!container || !data || !nodes.length) return;

    const scope = $("dsmScopeSelect")?.value || "top25";
    let targetNodes = [];

    if (scope === "cycles") {
        targetNodes = nodes.filter(n => n.in_cycle || activeCycleNodes.has(n.id));
        if (!targetNodes.length) targetNodes = nodes.slice(0, 25);
    } else if (scope === "top25") {
        targetNodes = [...nodes].sort((a, b) => (b.degree || 0) - (a.degree || 0)).slice(0, 25);
    } else {
        targetNodes = [...nodes].sort((a, b) => (b.degree || 0) - (a.degree || 0)).slice(0, 50);
    }

    const nLen = targetNodes.length;
    const idToIndex = new Map(targetNodes.map((n, i) => [n.id, i]));

    const matrix = Array.from({ length: nLen }, () => Array(nLen).fill(0));
    edges.forEach(e => {
        const r = idToIndex.get(e.source);
        const c = idToIndex.get(e.target);
        if (r !== undefined && c !== undefined) {
            matrix[r][c] += 1;
        }
    });

    let html = `<table class="dsm-table"><thead><tr><th style="width:25px">#</th><th class="row-header">Компонент \\ Вызов</th>`;
    for (let j = 0; j < nLen; j++) {
        html += `<th title="${esc(targetNodes[j].id)}">${j + 1}</th>`;
    }
    html += `</tr></thead><tbody>`;

    for (let i = 0; i < nLen; i++) {
        const nRow = targetNodes[i];
        const shortName = nRow.classes[0] || nRow.id.split('/').pop();
        html += `<tr>`;
        html += `<td style="color:var(--text-muted);font-weight:700">${i + 1}</td>`;
        html += `<th class="row-header" title="${esc(nRow.id)}" data-dsm-jump="${esc(nRow.id)}" style="cursor:pointer">
            <span style="color:${getNodeColor(nRow)}">●</span> ${esc(shortName)}
        </th>`;

        for (let j = 0; j < nLen; j++) {
            if (i === j) {
                html += `<td class="dsm-diag">·</td>`;
            } else {
                const count = matrix[i][j];
                const isFeedback = (i < j && count > 0);
                if (count > 0) {
                    const cls = isFeedback ? "dsm-cell-feedback" : "dsm-cell-flow";
                    const title = `${nRow.id.split('/').pop()} → ${targetNodes[j].id.split('/').pop()} (${count} связей)${isFeedback ? ' [⚠️ ОБРАТНЫЙ ЦИКЛ]' : ''}`;
                    html += `<td class="${cls}" title="${esc(title)}" data-dsm-src="${esc(nRow.id)}" data-dsm-tgt="${esc(targetNodes[j].id)}">${isFeedback ? '⚠️' + count : count}</td>`;
                } else {
                    html += `<td></td>`;
                }
            }
        }
        html += `</tr>`;
    }
    html += `</tbody></table>`;
    container.innerHTML = html;

    container.querySelectorAll("[data-dsm-jump]").forEach(el => el.addEventListener("click", () => {
        const n = nodes.find(x => x.id === el.dataset.dsmJump);
        if (n) {
            closeModal("dsmModal");
            inspect(n);
            pan.x = canvas.clientWidth / 2 - n.x * scale;
            pan.y = canvas.clientHeight / 2 - n.y * scale;
            requestRender();
        }
    }));

    container.querySelectorAll("[data-dsm-src]").forEach(el => el.addEventListener("click", () => {
        const srcNode = nodes.find(x => x.id === el.dataset.dsmSrc);
        const tgtNode = nodes.find(x => x.id === el.dataset.dsmTgt);
        if (srcNode) {
            closeModal("dsmModal");
            inspect(srcNode);
            activePathNodes.clear();
            activePathNodes.add(srcNode.id);
            if (tgtNode) activePathNodes.add(tgtNode.id);
            pan.x = canvas.clientWidth / 2 - srcNode.x * scale;
            pan.y = canvas.clientHeight / 2 - srcNode.y * scale;
            requestRender();
            showToast(`Связь ${srcNode.id.split('/').pop()} → ${tgtNode?.id.split('/').pop()} выделена`);
        }
    }));
}

// PageRank Context Optimizer for LLMs
function generatePageRankContextMarkdown(budgetTokens = 4000) {
    if (!nodes.length) return "Нет данных.";
    const sorted = [...nodes].sort((a, b) => (b.pagerank || 0) - (a.pagerank || 0));
    
    let text = `# Приоритетный архитектурный контекст (PageRank Optimizer)\n\n`;
    text += `> Бюджет токенов: ~${budgetTokens.toLocaleString()} токенов.\n`;
    text += `> Скелеты упорядочены по PageRank (центральности в графе зависимостей проекта).\n\n`;

    let currentTokens = 60;
    let includedCount = 0;

    for (const n of sorted) {
        if (!n.skeleton || !n.skeleton.trim()) continue;
        const skelTokens = Math.round(n.skeleton.length / 3.8);
        if (currentTokens + skelTokens > budgetTokens && includedCount >= 3) {
            break;
        }
        text += `## [PR: ${n.pagerank || 0}] \`${n.path}\` (${n.language})\n`;
        text += `\`\`\`${n.language === 'C#' ? 'csharp' : 'gdscript'}\n${n.skeleton.trim()}\n\`\`\`\n\n`;
        currentTokens += skelTokens;
        includedCount++;
    }

    text += `\n---\n*Включено файлов: ${includedCount} из ${nodes.length}. Расход токенов: ~${currentTokens} токенов.*`;
    return text;
}

// Temporal Coupling Report (Git Churn)
function generateTemporalCouplingMarkdown() {
    const list = data?.temporal_coupling || [];
    let text = `# Скрытая темпоральная связанность (Git Churn Coupling)\n\n`;
    text += `> Анализ истории коммитов Git (совместная частота изменений файлов в коммитах).\n`;
    text += `> Высокая темпоральная связанность при отсутствии связей в коде указывает на неявные скрытые зависимости.\n\n`;

    if (!list.length) {
        text += `*(В проекте не найдено выраженной темпоральной связанности либо репозиторий Git содержит мало коммитов)*\n`;
        return text;
    }

    text += `| Файл A | Файл B | Частота изменений (Churn %) | Совместных коммитов | Статус в коде |\n`;
    text += `| :--- | :--- | :--- | :--- | :--- |\n`;

    list.forEach(item => {
        const hasEdge = edges.some(e => (e.source === item.file_a && e.target === item.file_b) || (e.source === item.file_b && e.target === item.file_a));
        const status = hasEdge ? "✓ Связаны в коде" : "⚠️ **СКРЫТАЯ ЗАВИСИМОСТЬ**";
        text += `| \`${item.file_a}\` | \`${item.file_b}\` | **${item.churn_percent}%** | ${item.co_commits} | ${status} |\n`;
    });

    return text;
}

// Event Flow Modal Engine
function openEventFlowModal() {
    if (!data) {
        showToast("Сначала проанализируйте проект");
        return;
    }
    renderEventFlowList();
    openModal("eventFlowModal");
}

function renderEventFlowList() {
    const wrap = $("eventFlowListWrap");
    if (!wrap || !data) return;
    const query = ($("eventFlowSearch")?.value || "").toLowerCase().trim();
    let flows = data.event_flows || [];

    if (query) {
        flows = flows.filter(f => {
            const sig = (f.signal || "").toLowerCase();
            const src = (f.origin_file || f.source_file || "").toLowerCase();
            const lisMatch = (f.listeners || []).some(l => {
                const fPath = (l.file || l.target_file || "").toLowerCase();
                const mName = (l.method || (l.handlers && l.handlers.join(" ")) || "").toLowerCase();
                return fPath.includes(query) || mName.includes(query);
            });
            return sig.includes(query) || src.includes(query) || lisMatch;
        });
    }

    if (!flows.length) {
        wrap.innerHTML = `
            <div class="empty" style="padding:24px;text-align:center">
                <div style="font-size:24px;margin-bottom:8px">📡</div>
                <b>Цепочек событий не найдено</b>
                <div class="muted" style="font-size:12px;margin-top:4px">В проекте не обнаружено связанных сигналов с активными слушателями или поисковый запрос исключил все результаты.</div>
            </div>`;
        return;
    }

    let html = "";
    flows.forEach(flow => {
        const srcFile = flow.origin_file || flow.source_file || "Неизвестный источник";
        const srcMethod = flow.source_method && flow.source_method !== "(signal)" ? ` → <code>${esc(flow.source_method)}()</code>` : "";
        const listeners = flow.listeners || [];

        const lisHtml = listeners.map(l => {
            const lFile = l.file || l.target_file || "";
            const lMethod = l.method || (l.handlers && l.handlers[0]) || "connect";
            const lCalls = l.calls || l.downstream_calls || [];
            return `
                <div class="event-flow-listener">
                    ↳ Слушатель: <b>${esc(lFile.split('/').pop() || lFile)}</b> → <code>${esc(lMethod)}()</code>
                    ${lCalls.length ? `<div class="muted" style="font-size:10px;margin-top:2px">Вторичные вызовы: ${esc(lCalls.join(", "))}</div>` : ''}
                </div>
            `;
        }).join("");

        html += `
            <div class="event-flow-card">
                <div class="event-flow-header">
                    <span>📡 Сигнал: <b>${esc(flow.signal)}</b></span>
                    <button class="secondary" data-highlight-flow="${esc(flow.signal)}" style="font-size:10px;padding:3px 10px;cursor:pointer">🎯 На графе</button>
                </div>
                <div style="font-size:11px;margin:4px 0">
                    Источник: <code>${esc(srcFile)}</code>${srcMethod}
                    ${flow.is_async ? '<span class="tag warning" style="font-size:9px;margin-left:6px">⚡ Async/Deferred</span>' : ''}
                </div>
                ${lisHtml || '<div class="muted" style="font-size:11px;padding:4px 0">Нет зарегистрированных слушателей в кодовой базе</div>'}
            </div>
        `;
    });

    wrap.innerHTML = html;

    wrap.querySelectorAll("[data-highlight-flow]").forEach(btn => {
        btn.addEventListener("click", () => {
            const flow = (data.event_flows || []).find(f => f.signal === btn.dataset.highlightFlow);
            if (!flow) return;
            closeModal("eventFlowModal");

            activePathNodes.clear();
            activePathEdges.clear();
            const srcPath = flow.origin_file || flow.source_file;
            const originNode = nodes.find(n => n.id === srcPath || n.path === srcPath);
            if (originNode) activePathNodes.add(originNode.id);

            (flow.listeners || []).forEach(l => {
                const lPath = l.file || l.target_file;
                const lisNode = nodes.find(n => n.id === lPath || n.path === lPath);
                if (lisNode) {
                    activePathNodes.add(lisNode.id);
                    if (originNode) {
                        const directEdge = edges.find(e => 
                            (e.source === originNode.id && e.target === lisNode.id) || 
                            (e.source === lisNode.id && e.target === originNode.id)
                        );
                        if (directEdge) {
                            activePathEdges.add(directEdge);
                        } else {
                            const subPath = findShortestPath(originNode.id, lisNode.id) || findShortestPath(lisNode.id, originNode.id);
                            if (subPath && subPath.length > 1) {
                                for (let i = 0; i < subPath.length - 1; i++) {
                                    activePathNodes.add(subPath[i]);
                                    activePathNodes.add(subPath[i + 1]);
                                    const pe = edges.find(e => (e.source === subPath[i] && e.target === subPath[i + 1]) || (e.source === subPath[i + 1] && e.target === subPath[i]));
                                    if (pe) activePathEdges.add(pe);
                                }
                            }
                        }
                    }
                }
            });

            if (originNode) {
                inspect(originNode);
                pan.x = canvas.clientWidth / 2 - originNode.x * scale;
                pan.y = canvas.clientHeight / 2 - originNode.y * scale;
            }
            requestRender();
            showToast(`✓ Цепочка сигнала "${flow.signal}" выделена на графе`);
        });
    });
}

// Chronology, Call Frequency & CPU Clock Cycles Engine
const GD_LIVE_PROFILER_SCRIPT = `# LiveProfiler.gd - Godot 4 Autoload Singleton
# Инструкция: Добавьте этот скрипт в Project Settings -> Globals -> Autoload как "LiveProfiler"
extends Node

class MethodStat:
	var call_count: int = 0
	var total_usec: int = 0
	var last_tick: int = 0

var stats: Dictionary = {}
var report_timer: float = 0.0

func _ready() -> void:
	print("[LiveProfiler] In-Game телеметрия активирована.")

func begin_measure(method_name: String) -> void:
	if not stats.has(method_name):
		stats[method_name] = MethodStat.new()
	stats[method_name].last_tick = Time.get_ticks_usec()

func end_measure(method_name: String) -> void:
	if stats.has(method_name):
		var elapsed = Time.get_ticks_usec() - stats[method_name].last_tick
		stats[method_name].call_count += 1
		stats[method_name].total_usec += elapsed

func _process(delta: float) -> void:
	report_timer += delta
	if report_timer >= 5.0: # Каждые 5 секунд логировать замер
		report_timer = 0.0
		export_json_telemetry()

func export_json_telemetry() -> void:
	var out = {}
	for k in stats.keys():
		var s = stats[k]
		var hz = float(s.call_count) / 5.0
		var avg_ms = (float(s.total_usec) / max(1, s.call_count)) / 1000.0
		out[k] = { "hz": hz, "avg_ms": avg_ms, "calls": s.call_count }
		s.call_count = 0
		s.total_usec = 0
	print("[LiveProfiler Snapshot]: ", JSON.stringify(out))
`;

const CS_LIVE_PROFILER_SCRIPT = `// LiveProfiler.cs - Godot C# / Unity Singleton
// Добавьте в Autoload / Global GameObject
using Godot;
using System;
using System.Collections.Generic;
using System.Diagnostics;

public partial class LiveProfiler : Node
{
    private class Stat {
        public long CallCount = 0;
        public long TotalTicks = 0;
        public Stopwatch Sw = new Stopwatch();
    }

    private static readonly Dictionary<string, Stat> _stats = new();
    private static double _timer = 0.0;

    public static void Begin(string methodName) {
        if (!_stats.TryGetValue(methodName, out var s)) {
            s = new Stat();
            _stats[methodName] = s;
        }
        s.Sw.Restart();
    }

    public static void End(string methodName) {
        if (_stats.TryGetValue(methodName, out var s)) {
            s.Sw.Stop();
            s.CallCount++;
            s.TotalTicks += s.Sw.ElapsedTicks;
        }
    }

    public override void _Process(double delta) {
        _timer += delta;
        if (_timer >= 5.0) {
            _timer = 0.0;
            GD.Print($"[LiveProfiler C#] 5s Window Snapshot: {_stats.Count} methods tracked.");
        }
    }
}
`;

let chronActiveFilter = "all";

function generateChronologyMarkdown() {
    if (!data || !data.chronology) return "Запустите анализ проекта.";
    const chron = data.chronology;
    const methods = chron.methods || [];
    const timelines = chron.timelines || [];

    let text = `# Хронология вызовов, частота (Hz), такты CPU и передача данных\n\n`;
    text += `> Автоматический расчет частоты вызовов в секунду (Hz), тактов процессора (~3.5 GHz) и последовательности вызовов.\n`;
    text += `- **Всего методов в проекте**: ${chron.total_methods || methods.length}\n`;
    text += `- **Активных в симуляции**: ${chron.active_methods || 0}\n\n`;

    text += `### ⏱️ Топ методов по частоте вызовов (Hz) и тактам CPU:\n\n`;
    text += `| Метод / Функция | Файл | Частота (Hz) | Тактов CPU / вызов | Время (мс) | Суммарно мс/с | Память GC | Сложность |\n`;
    text += `| :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- |\n`;

    const sorted = [...methods].sort((a, b) => (b.calls_per_second || 0) - (a.calls_per_second || 0));
    sorted.slice(0, 35).forEach(m => {
        const hz = (m.calls_per_second || 0).toFixed(1).replace(/\.0$/, "");
        const cyc = (m.cycles_per_call || 0).toLocaleString();
        const ms = (m.time_per_call_ms || 0).toFixed(4);
        const totMs = (m.total_ms_per_second || 0).toFixed(2);
        const alloc = (m.alloc_kb_per_call || 0).toFixed(2);
        text += `| \`${m.name}\` | \`${m.file_path}\` | **${hz} Hz** | ${cyc} | ${ms} ms | ${totMs} ms | ${alloc} KB | \`${m.complexity || 'O(1)'}\` |\n`;
    });

    text += `\n### 🌊 Хронологический порядок выполнения и передача данных (Waterfall Flow):\n\n`;
    timelines.slice(0, 10).forEach(tl => {
        const ep = tl.entry_point || {};
        text += `#### Точка входа: \`${ep.name}()\` (${ep.frequency || 60} Hz) в \`${ep.file}\`\n`;
        const steps = tl.steps || [];
        if (!steps.length) {
            text += `*(Внутренних шагов и вызовов не зафиксировано)*\n\n`;
        } else {
            steps.forEach(st => {
                const indent = "  ".repeat(st.depth || 1);
                const argsStr = st.arguments ? ` args: \`${st.arguments}\`` : "";
                const mutStr = (st.mutations && st.mutations.length) ? ` [мутирует: \`${st.mutations.join(", ")}\`]` : "";
                text += `${indent}↳ **Шаг ${st.order}**: \`${st.target_method}()\` (${st.target_file})${argsStr}${mutStr} ~${(st.cycles || 0).toLocaleString()} тактов\n`;
            });
            text += "\n";
        }
    });

    return text;
}

function openChronologyModal() {
    if (!data || !data.chronology) {
        showToast("Сначала проанализируйте проект");
        return;
    }
    const chron = data.chronology;
    const methods = chron.methods || [];
    
    // Update summary metrics
    const totalM = chron.total_methods || methods.length;
    const activeM = chron.active_methods || methods.filter(m => (m.calls_per_second || 0) > 0).length;
    const maxFreq = methods.reduce((acc, m) => Math.max(acc, m.calls_per_second || 0), 0);
    const totalCycles = methods.reduce((acc, m) => acc + (m.cycles_per_second || 0), 0);

    $("chronTotalMethods").textContent = totalM.toLocaleString();
    $("chronActiveMethods").textContent = activeM.toLocaleString();
    $("chronMaxFreq").textContent = `${maxFreq.toFixed(1).replace(/\.0$/, "")} Hz`;
    $("chronTotalCycles").textContent = totalCycles > 1000000 ? `${(totalCycles / 1000000).toFixed(1)}M/с` : totalCycles.toLocaleString();

    // Default tab
    switchChronologyTab("tabTable");
    renderChronologyTable();
    populateChronologyEntryPoints();

    // Set code previews
    const codePreview = $("profilerCodePreview");
    if (codePreview) codePreview.textContent = GD_LIVE_PROFILER_SCRIPT;

    openModal("chronologyModal");
}

function switchChronologyTab(tabId) {
    document.querySelectorAll(".chronology-tab-btn").forEach(btn => {
        btn.classList.toggle("active", btn.dataset.tab === tabId);
    });
    $("chronTabTable")?.classList.toggle("hidden", tabId !== "tabTable");
    $("chronTabWaterfall")?.classList.toggle("hidden", tabId !== "tabWaterfall");
    $("chronTabInGame")?.classList.toggle("hidden", tabId !== "tabInGame");
    if (tabId === "tabWaterfall") {
        renderChronologyWaterfall();
    }
}

function renderChronologyTable() {
    const container = $("chronTableContainer");
    if (!container || !data || !data.chronology) return;

    const query = ($("chronSearchInput")?.value || "").toLowerCase().trim();
    const sortMode = $("chronSortSelect")?.value || "freq_desc";
    let list = [...(data.chronology.methods || [])];

    // Filter by search
    if (query) {
        list = list.filter(m => 
            (m.name || "").toLowerCase().includes(query) ||
            (m.file_path || "").toLowerCase().includes(query)
        );
    }

    // Filter by active pill
    if (chronActiveFilter === "hot") {
        list = list.filter(m => (m.calls_per_second || 0) >= 30);
    } else if (chronActiveFilter === "loops") {
        list = list.filter(m => (m.loops_count || 0) > 0);
    } else if (chronActiveFilter === "allocs") {
        list = list.filter(m => (m.alloc_kb_per_call || 0) > 0 || (m.allocs_count || 0) > 0);
    }

    // Sort
    if (sortMode === "freq_desc") {
        list.sort((a, b) => (b.calls_per_second || 0) - (a.calls_per_second || 0) || (b.cycles_per_call || 0) - (a.cycles_per_call || 0));
    } else if (sortMode === "cycles_desc") {
        list.sort((a, b) => (b.cycles_per_call || 0) - (a.cycles_per_call || 0));
    } else if (sortMode === "total_ms_desc") {
        list.sort((a, b) => (b.total_ms_per_second || 0) - (a.total_ms_per_second || 0));
    } else if (sortMode === "alloc_desc") {
        list.sort((a, b) => (b.alloc_kb_per_call || 0) - (a.alloc_kb_per_call || 0));
    }

    if (!list.length) {
        container.innerHTML = `
            <div class="empty" style="padding:24px;text-align:center">
                <div style="font-size:24px;margin-bottom:8px">⏱️</div>
                <b>Методов не найдено</b>
                <div class="muted" style="font-size:12px;margin-top:4px">Попробуйте изменить поисковый запрос или фильтры.</div>
            </div>`;
        return;
    }

    let html = `
        <table class="chron-table">
            <thead>
                <tr>
                    <th>Метод / Функция</th>
                    <th>Файл / Класс</th>
                    <th>Вызовов в 1 сек (Hz)</th>
                    <th>Тактов CPU / вызов</th>
                    <th>Время / вызов (мс)</th>
                    <th>Суммарно мс/сек</th>
                    <th>Память GC / вызов</th>
                    <th>Сложность</th>
                    <th>Инструменты</th>
                </tr>
            </thead>
            <tbody>
    `;

    list.slice(0, 100).forEach(m => {
        const hz = m.calls_per_second || 0;
        let badgeCls = "rare";
        if (hz >= 60) badgeCls = "hot";
        else if (hz >= 10) badgeCls = "warm";
        else if (hz >= 0.5) badgeCls = "norm";

        const hzStr = hz.toFixed(1).replace(/\.0$/, "");
        const cycStr = (m.cycles_per_call || 0).toLocaleString();
        const msStr = (m.time_per_call_ms || 0).toFixed(4);
        const totMsStr = (m.total_ms_per_second || 0).toFixed(2);
        const allocStr = (m.alloc_kb_per_call || 0) > 0 ? `${(m.alloc_kb_per_call).toFixed(1)} КБ` : "0";
        const shortFile = m.file_path ? m.file_path.split('/').pop() : "";

        html += `
            <tr>
                <td>
                    <b style="font-family:Consolas,monospace;color:#38bdf8">${esc(m.name)}</b>
                    ${m.loops_count > 0 ? `<span title="Содержит ${m.loops_count} цикл(ов)" style="font-size:10px;color:#f59e0b;margin-left:4px">🔄×${m.loops_count}</span>` : ''}
                </td>
                <td title="${esc(m.file_path)}" style="color:var(--text-muted);font-size:11px">${esc(shortFile)}</td>
                <td><span class="hz-badge ${badgeCls}">⚡ ${hzStr} Hz</span></td>
                <td style="font-family:Consolas,monospace;font-weight:600">${cycStr}</td>
                <td style="font-family:Consolas,monospace;color:#94a3b8">${msStr} ms</td>
                <td style="font-family:Consolas,monospace;font-weight:700;color:${totMsStr > 5 ? '#ef4444' : '#e2e8f0'}">${totMsStr} ms</td>
                <td style="font-family:Consolas,monospace;color:#c084fc">${allocStr}</td>
                <td><code style="font-size:10.5px">${esc(m.complexity || 'O(1)')}</code></td>
                <td style="white-space:nowrap">
                    <button class="secondary icon-btn-sm" data-chron-rengen="${esc(m.name)}" data-chron-file="${esc(m.file_path || '')}" style="font-size:10px;padding:2px 6px" title="360° Рентген функции">🔍</button>
                    <button class="secondary icon-btn-sm" data-chron-query="${esc(m.name)}" data-chron-file="${esc(m.file_path || '')}" style="font-size:10px;padding:2px 6px;margin-left:2px" title="AI Query Builder">🎯</button>
                </td>
            </tr>
        `;
    });

    html += `</tbody></table>`;
    if (list.length > 100) {
        html += `<div class="muted" style="padding:8px 12px;font-size:11px;text-align:center;border-top:1px solid var(--border)">Показаны первые 100 из ${list.length} методов. Используйте поиск для точного выбора.</div>`;
    }
    container.innerHTML = html;

    container.querySelectorAll("[data-chron-rengen]").forEach(btn => {
        btn.addEventListener("click", () => openFunctionDeepDiveModal(btn.dataset.chronRengen, btn.dataset.chronFile));
    });
    container.querySelectorAll("[data-chron-query]").forEach(btn => {
        btn.addEventListener("click", () => openAiQueryModal(btn.dataset.chronFile, btn.dataset.chronQuery));
    });
}

function populateChronologyEntryPoints() {
    const sel = $("chronEntrySelect");
    if (!sel || !data || !data.chronology) return;
    const timelines = data.chronology.timelines || [];

    sel.innerHTML = "";
    if (!timelines.length) {
        sel.innerHTML = `<option value="">Нет зарегистрированных цепочек вызовов</option>`;
        return;
    }

    timelines.forEach((tl, idx) => {
        const ep = tl.entry_point || {};
        const shortFile = ep.file ? ep.file.split('/').pop() : "";
        const opt = document.createElement("option");
        opt.value = idx;
        opt.textContent = `${ep.name}() [${ep.frequency || 60} Hz] — ${shortFile} (${(tl.steps || []).length} шагов)`;
        sel.appendChild(opt);
    });
}

function renderChronologyWaterfall() {
    const container = $("chronWaterfallTimeline");
    const statsEl = $("chronWaterfallStats");
    if (!container || !data || !data.chronology) return;

    const timelines = data.chronology.timelines || [];
    const selIdx = Number($("chronEntrySelect")?.value || 0);
    const tl = timelines[selIdx];

    if (!tl) {
        container.innerHTML = `<div class="empty" style="padding:24px;text-align:center">Выберите точку входа выше.</div>`;
        return;
    }

    const ep = tl.entry_point || {};
    const steps = tl.steps || [];

    if (statsEl) {
        statsEl.textContent = `Цепочка: ${steps.length} шагов · Базовая частота: ${ep.frequency || 60} Hz · Файл: ${ep.file ? ep.file.split('/').pop() : ''}`;
    }

    if (!steps.length) {
        container.innerHTML = `
            <div class="empty" style="padding:24px;text-align:center">
                <div style="font-size:24px;margin-bottom:8px">🌊</div>
                <b>Шагов выполнения не зафиксировано</b>
                <div class="muted" style="font-size:12px;margin-top:4px">Внутри метода <code>${esc(ep.name)}</code> нет вторичных вызовов других методов или функций.</div>
            </div>`;
        return;
    }

    let html = "";
    steps.forEach((st, idx) => {
        const shortFile = st.target_file ? st.target_file.split('/').pop() : "";
        const cycStr = (st.cycles || 0).toLocaleString();
        const msStr = (st.time_ms || 0).toFixed(4);
        const argsStr = st.arguments ? esc(st.arguments) : "";
        const muts = st.mutations || [];

        html += `
            <div class="waterfall-step-card" style="margin-left:${Math.min(60, (st.depth - 1) * 20)}px">
                <div class="waterfall-step-num">#${st.order}</div>
                <div class="waterfall-step-body">
                    <div class="waterfall-title-row">
                        <span class="waterfall-fn-name">${esc(st.target_method)}()</span>
                        <span class="waterfall-file-path">${esc(shortFile)}</span>
                    </div>
                    <div class="waterfall-details-row">
                        <span class="waterfall-meta-badge">⚙️ ~${cycStr} тактов</span>
                        <span class="waterfall-meta-badge">⏱️ ${msStr} ms</span>
                        ${argsStr ? `<span class="waterfall-arg-pill" title="Передаваемые аргументы">↳ args: ${argsStr}</span>` : ''}
                        ${muts.map(m => `<span class="waterfall-mut-pill" title="Мутирует переменную состояния">⚡ мутирует: ${esc(m)}</span>`).join("")}
                    </div>
                </div>
            </div>
        `;

        if (idx < steps.length - 1) {
            html += `<div class="waterfall-arrow" style="margin-left:${Math.min(60, (st.depth - 1) * 20) + 14}px">↓</div>`;
        }
    });

    container.innerHTML = html;
}

// Function 360-degree Deep Dive (Рентген функции)
let currentDeepDiveMd = "";

function openFunctionDeepDiveModal(funcName, filePath = "") {
    if (!data) {
        showToast("Сначала проанализируйте проект");
        return;
    }
    const chron = data.chronology || {};
    const methods = chron.methods || [];
    const fnLow = (funcName || "").toLowerCase().trim();
    const fpLow = (filePath || "").toLowerCase().trim();

    let matches = methods.filter(m => {
        const nameMatch = (m.name || "").toLowerCase() === fnLow || (m.name || "").toLowerCase().includes(fnLow);
        if (!nameMatch) return false;
        if (fpLow && !(m.file_path || "").toLowerCase().includes(fpLow)) return false;
        return true;
    });

    if (!matches.length) {
        for (const f of (data.files || [])) {
            if (fpLow && !f.path.toLowerCase().includes(fpLow)) continue;
            for (const fn of (f.functions_detail || [])) {
                if ((fn.name || "").toLowerCase() === fnLow || (fn.name || "").toLowerCase().includes(fnLow)) {
                    matches.push({
                        name: fn.name,
                        file_path: f.path,
                        language: f.language,
                        params: fn.params || "",
                        return_type: fn.return_type || "void",
                        calls_per_second: fn.calls_per_second || (fn.is_hot_path ? 60.0 : 0.0),
                        cycles_per_call: fn.cycles_per_call || 150,
                        cycles_per_second: fn.cycles_per_second || 0,
                        time_per_call_ms: fn.total_ms_per_second ? (fn.total_ms_per_second / (fn.calls_per_second || 1)) : 0.0001,
                        total_ms_per_second: fn.total_ms_per_second || 0,
                        cpu_core_percent: 0,
                        alloc_kb_per_call: 0,
                        complexity: fn.complexity || "O(1)",
                        mutations: fn.mutations || [],
                        emits: fn.emits || [],
                        spawns: fn.spawns || [],
                        calls: fn.calls || [],
                        callers: fn.callers || [],
                        has_loops: false,
                        is_hot_path: fn.is_hot_path || false,
                        doc: fn.docstring || "",
                        summary_ru: fn.summary_ru || ""
                    });
                }
            }
        }
    }

    if (!matches.length) {
        showToast(`Функция '${funcName}' не найдена`);
        return;
    }

    $("deepDiveTitle").textContent = `360° Рентген: ${funcName}()`;
    $("deepDiveSub").textContent = `Найдено совпадений: ${matches.length} | Всесторонний аудит связей, тактов, мутаций и сигналов`;

    let html = "";
    let md = `# 360° РЕНТГЕН ФУНКЦИИ: ${funcName}() (${matches.length} совпадений)\n\n`;

    matches.forEach(m => {
        const hz = (m.calls_per_second || 0);
        const cyc = (m.cycles_per_call || 0);
        const totMs = (m.total_ms_per_second || 0);
        const ms = (m.time_per_call_ms || 0);
        const alloc = (m.alloc_kb_per_call || 0);
        const callers = m.callers || [];
        const calls = m.calls || [];
        const muts = m.mutations || [];
        const emits = m.emits || [];
        const spawns = m.spawns || [];

        const timelines = (chron.timelines || []).filter(tl => tl.file === m.file_path && tl.method === m.name);
        const waterfall = timelines.length ? (timelines[0].steps || []) : [];

        const relatedFlows = (data.event_flows || []).filter(ef => {
            if (ef.origin_file === m.file_path && ef.source_method === m.name) return true;
            return (ef.listeners || []).some(l => l.file === m.file_path && l.method === m.name);
        });

        const shortFile = m.file_path ? m.file_path.split('/').pop() : "";

        html += `
            <div class="deep-dive-card">
                <div style="display:flex;justify-content:space-between;align-items:flex-start;flex-wrap:wrap;gap:8px">
                    <div>
                        <div style="font-family:Consolas,monospace;font-size:15px;font-weight:700;color:#38bdf8">
                            ${esc(m.name)}<span style="color:#94a3b8">(${esc(m.params || '')})</span> <span style="color:#fbbf24">➔ ${esc(m.return_type || 'void')}</span>
                        </div>
                        <div style="font-size:11px;color:var(--text-muted);margin-top:2px">
                            📁 <b>${esc(shortFile)}</b> &bull; <span class="muted">${esc(m.file_path)}</span> &bull; <code>${esc(m.language || 'Code')}</code>
                        </div>
                    </div>
                    <div style="display:flex;gap:6px">
                        ${m.is_hot_path || hz >= 60 ? '<span class="tag error" style="margin:0;font-size:9.5px">🔥 60-FPS HOT-PATH</span>' : '<span class="tag" style="margin:0;font-size:9.5px">Обычный вызов</span>'}
                        <button class="secondary icon-btn-sm" data-dd-query="${esc(m.name)}" data-dd-file="${esc(m.file_path)}" style="font-size:10px;padding:3px 8px">🎯 В Query Builder</button>
                    </div>
                </div>

                ${m.summary_ru ? `<div class="func-passport" style="margin-top:8px">🎯 <b>Назначение:</b> ${esc(m.summary_ru)}</div>` : ''}
                ${m.doc ? `<div class="func-passport func-doc" style="margin-top:6px">📖 <i>${esc(m.doc)}</i></div>` : ''}

                <div class="deep-dive-metrics-grid" style="margin-top:10px">
                    <div class="dd-metric-item">
                        <span class="lbl">Частота в секунду</span>
                        <b class="val" style="color:${hz >= 60 ? '#ef4444' : '#38bdf8'}">${hz.toFixed(1)} Hz</b>
                        <small class="hint">${hz >= 60 ? 'Каждый кадр' : hz > 0 ? 'Периодический' : 'По событию'}</small>
                    </div>
                    <div class="dd-metric-item">
                        <span class="lbl">Тактов CPU / вызов</span>
                        <b class="val" style="color:#fbbf24">${cyc.toLocaleString()}</b>
                        <small class="hint">~3.5 GHz Core</small>
                    </div>
                    <div class="dd-metric-item">
                        <span class="lbl">Время / вызов</span>
                        <b class="val">${ms.toFixed(4)} мс</b>
                        <small class="hint">Суммарно: ${totMs.toFixed(2)} мс/с</small>
                    </div>
                    <div class="dd-metric-item">
                        <span class="lbl">Память GC</span>
                        <b class="val" style="color:#c084fc">${alloc > 0 ? `${alloc.toFixed(1)} КБ` : '0 КБ'}</b>
                        <small class="hint">Аллокации кучи</small>
                    </div>
                    <div class="dd-metric-item">
                        <span class="lbl">Сложность O(N)</span>
                        <b class="val" style="color:#a78bfa">${esc(m.complexity || 'O(1)')}</b>
                        <small class="hint">Циклов: ${m.loops_count || 0}</small>
                    </div>
                </div>

                <div style="margin-top:10px;display:flex;flex-direction:column;gap:6px">
                    <div>
                        <span style="font-size:11px;font-weight:700;color:#fbbf24">⚡ Мутации состояния (переменные):</span>
                        ${muts.length ? muts.map(mu => `<span class="passport-tag mut">${esc(mu)}</span>`).join(" ") : '<span class="muted" style="font-size:11px"> (чистая функция, не меняет состояние)</span>'}
                    </div>
                    <div>
                        <span style="font-size:11px;font-weight:700;color:#c084fc">📡 Испускаемые сигналы:</span>
                        ${emits.length ? emits.map(em => `<span class="passport-tag emit">${esc(em)}</span>`).join(" ") : '<span class="muted" style="font-size:11px"> (не испускает)</span>'}
                    </div>
                    <div>
                        <span style="font-size:11px;font-weight:700;color:#34d399">📦 Создание объектов (new):</span>
                        ${spawns.length ? spawns.map(sp => `<span class="passport-tag spawn">${esc(sp)}</span>`).join(" ") : '<span class="muted" style="font-size:11px"> (не аллоцирует)</span>'}
                    </div>
                </div>

                <div style="display:grid;grid-template-columns:1fr 1fr;gap:10px;margin-top:10px">
                    <div style="background:var(--panel);padding:8px 10px;border-radius:6px;border:1px solid var(--border)">
                        <div style="font-size:11px;font-weight:700;color:var(--text);margin-bottom:6px">📥 Кто вызывает (Callers: ${callers.length})</div>
                        <div style="max-height:110px;overflow-y:auto;font-size:11px">
                            ${callers.length ? callers.map(c => `
                                <div style="margin-bottom:3px">
                                    <button class="link-item" data-jump="${esc(c)}" style="padding:1px 0;font-size:10.5px">← ${esc(c)}</button>
                                </div>
                            `).join("") : '<span class="muted">Внешних вызовов не найдено (локальный/приватный)</span>'}
                        </div>
                    </div>
                    <div style="background:var(--panel);padding:8px 10px;border-radius:6px;border:1px solid var(--border)">
                        <div style="font-size:11px;font-weight:700;color:var(--text);margin-bottom:6px">↳ Кого вызывает (Callees: ${calls.length})</div>
                        <div style="max-height:110px;overflow-y:auto;font-size:11px">
                            ${calls.length ? calls.map(c => `
                                <div style="margin-bottom:3px">
                                    <span style="color:#38bdf8">➔</span> <code>${esc(c)}</code>
                                </div>
                            `).join("") : '<span class="muted">Не вызывает внешних методов</span>'}
                        </div>
                    </div>
                </div>

                ${waterfall.length ? `
                    <div style="margin-top:10px;background:var(--panel);padding:8px 10px;border-radius:6px;border:1px solid var(--border)">
                        <div style="font-size:11px;font-weight:700;color:#38bdf8;margin-bottom:6px">🌊 Порядок вызова цепочки (Waterfall Steps):</div>
                        <div style="font-size:11px;font-family:Consolas,monospace">
                            ${waterfall.map(st => `
                                <div style="padding-left:${(st.depth || 1) * 12}px;margin-bottom:2px">
                                    ↳ <b>Шаг ${st.order}</b>: <code>${esc(st.target_method)}()</code> (${esc(st.target_file?.split('/').pop() || '')}) ~${(st.cycles || 0).toLocaleString()} тактов
                                </div>
                            `).join("")}
                        </div>
                    </div>
                ` : ''}

                ${relatedFlows.length ? `
                    <div style="margin-top:10px;background:var(--panel);padding:8px 10px;border-radius:6px;border:1px solid var(--border)">
                        <div style="font-size:11px;font-weight:700;color:#c084fc;margin-bottom:6px">🌊 Связанные Event-Flow потоки (${relatedFlows.length}):</div>
                        <div style="font-size:11px">
                            ${relatedFlows.map(ef => `
                                <div style="margin-bottom:3px">
                                    📡 Сигнал <code>${esc(ef.signal)}</code> от <code>${esc(ef.origin_file?.split('/').pop() || '')}::${esc(ef.source_method || '')}</code>
                                    ➔ Слушателей: ${(ef.listeners || []).length}
                                </div>
                            `).join("")}
                        </div>
                    </div>
                ` : ''}
            </div>
        `;

        md += `## Функция: \`${m.name}(${m.params || ''}) -> ${m.return_type || 'void'}\`\n`;
        md += `- **Файл**: \`${m.file_path}\` (${m.language || 'Code'})\n`;
        if (m.summary_ru) md += `- **Назначение**: ${m.summary_ru}\n`;
        if (m.doc) md += `- **Документация**: *${m.doc}*\n`;
        md += `- **Метрики**: Частота: **${hz.toFixed(1)} Hz** | Тактов CPU: **${cyc.toLocaleString()}** | Время: **${ms.toFixed(4)} мс** (суммарно: ${totMs.toFixed(2)} мс/с) | Сложность: \`${m.complexity || 'O(1)'}\`\n`;
        md += `- **Мутации**: ${muts.length ? muts.map(x => '`' + x + '`').join(', ') : '(чистая)'}\n`;
        md += `- **Сигналы**: ${emits.length ? emits.map(x => '`' + x + '`').join(', ') : '(нет)'}\n`;
        md += `- **Кого вызывает**: ${calls.length ? calls.map(x => '`' + x + '`').join(', ') : '(нет)'}\n`;
        md += `- **Кто вызывает (Callers)**: ${callers.length ? callers.map(x => '`' + x + '`').join(', ') : '(нет)'}\n\n`;
    });

    currentDeepDiveMd = md;
    $("deepDiveContent").innerHTML = html;

    $("deepDiveContent").querySelectorAll("[data-dd-query]").forEach(btn => {
        btn.addEventListener("click", () => {
            closeModal("functionDeepDiveModal");
            openAiQueryModal(btn.dataset.ddFile, btn.dataset.ddQuery);
        });
    });
    $("deepDiveContent").querySelectorAll("[data-jump]").forEach(btn => {
        btn.addEventListener("click", () => {
            closeModal("functionDeepDiveModal");
            const targetNode = nodes.find(x => x.id === btn.dataset.jump);
            if (targetNode) {
                inspect(targetNode);
                pan.x = canvas.clientWidth / 2 - targetNode.x * scale;
                pan.y = canvas.clientHeight / 2 - targetNode.y * scale;
                requestRender();
            }
        });
    });

    openModal("functionDeepDiveModal");
}

// AI Granular Query & Field Projection Builder
let currentAiQueryFilter = "all";
let currentAiQueryFormat = "json";
let currentAiQueryResultText = "";

function openAiQueryModal(initialFile = "", initialFunc = "") {
    if (!data) {
        showToast("Сначала проанализируйте проект");
        return;
    }

    const fileSel = $("aiQueryFileSelect");
    if (fileSel) {
        fileSel.innerHTML = `<option value="">Все файлы проекта (${nodes.length})</option>`;
        const sortedNodes = [...nodes].sort((a, b) => (a.id || "").localeCompare(b.id || ""));
        sortedNodes.forEach(n => {
            const opt = document.createElement("option");
            opt.value = n.path;
            const shortName = n.id.split('/').pop();
            opt.textContent = `${shortName} (${n.path})`;
            if (initialFile && (n.path.toLowerCase() === initialFile.toLowerCase() || n.path.toLowerCase().endsWith(initialFile.toLowerCase()))) {
                opt.selected = true;
            }
            fileSel.appendChild(opt);
        });
        if (initialFile && !fileSel.value) {
            for (let i = 0; i < fileSel.options.length; i++) {
                if (fileSel.options[i].value.toLowerCase().includes(initialFile.toLowerCase())) {
                    fileSel.selectedIndex = i;
                    break;
                }
            }
        }
    }

    if ($("aiQueryFuncInput")) {
        $("aiQueryFuncInput").value = initialFunc || "";
    }

    renderAiQueryPreview();
    openModal("aiQueryModal");
}

function renderAiQueryPreview() {
    if (!data) return;

    const fileVal = ($("aiQueryFileSelect")?.value || "").trim().toLowerCase();
    const funcVal = ($("aiQueryFuncInput")?.value || "").trim().toLowerCase();
    const limit = parseInt($("aiQueryLimitSelect")?.value || "0", 10);
    const budget = parseInt($("aiQueryBudgetSelect")?.value || "0", 10);

    const checkedFields = Array.from(document.querySelectorAll(".q-field-chk:checked")).map(c => c.value);

    const chron = data.chronology || {};
    let allMethods = chron.methods || [];

    if (!allMethods.length) {
        for (const f of (data.files || [])) {
            for (const fn of (f.functions_detail || [])) {
                allMethods.push({
                    name: fn.name,
                    file_path: f.path,
                    language: f.language,
                    params: fn.params || "",
                    return_type: fn.return_type || "void",
                    calls_per_second: fn.calls_per_second || (fn.is_hot_path ? 60.0 : 0.0),
                    cycles_per_call: fn.cycles_per_call || 150,
                    cycles_per_second: fn.cycles_per_second || 0,
                    time_per_call_ms: 0.0001,
                    total_ms_per_second: fn.total_ms_per_second || 0,
                    cpu_core_percent: 0,
                    alloc_kb_per_call: 0,
                    complexity: fn.complexity || "O(1)",
                    mutations: fn.mutations || [],
                    emits: fn.emits || [],
                    spawns: fn.spawns || [],
                    calls: fn.calls || [],
                    callers: fn.callers || [],
                    has_loops: false,
                    is_hot_path: fn.is_hot_path || false,
                    doc: fn.docstring || "",
                    summary_ru: fn.summary_ru || ""
                });
            }
        }
    }

    let filtered = allMethods.filter(m => {
        if (fileVal && !(m.file_path || "").toLowerCase().includes(fileVal)) return false;
        if (funcVal && !(m.name || "").toLowerCase().includes(funcVal)) return false;
        if (currentAiQueryFilter === "hot" && !m.is_hot_path && (m.calls_per_second || 0) < 60) return false;
        if (currentAiQueryFilter === "mutations" && (!m.mutations || !m.mutations.length)) return false;
        if (currentAiQueryFilter === "loops" && !m.has_loops && (!m.loops_count || m.loops_count <= 0)) return false;
        return true;
    });

    if (limit > 0) {
        filtered = filtered.slice(0, limit);
    }

    const projected = filtered.map(m => {
        const item = {};
        const fieldMap = {
            "name": m.name,
            "params": m.params || "",
            "return_type": m.return_type || "void",
            "mutations": m.mutations || [],
            "calls": m.calls || [],
            "callers": m.callers || [],
            "hz": m.calls_per_second || 0,
            "cycles": m.cycles_per_call || 0,
            "emits": m.emits || [],
            "spawns": m.spawns || [],
            "doc": m.doc || "",
            "summary": m.summary_ru || "",
            "alloc": m.alloc_kb_per_call || 0,
            "complexity": m.complexity || "O(1)"
        };

        if (checkedFields.length === 0) {
            item.name = m.name;
            item.params = m.params || "";
        } else {
            checkedFields.forEach(fld => {
                if (fieldMap.hasOwnProperty(fld)) {
                    item[fld] = fieldMap[fld];
                }
            });
            if (checkedFields.includes("flow")) {
                const flows = (data.event_flows || []).filter(ef => ef.origin_file === m.file_path && ef.source_method === m.name);
                item.flow = flows.map(f => ({ signal: f.signal, listeners_count: (f.listeners || []).length }));
            }
            if (checkedFields.includes("waterfall")) {
                const tls = (chron.timelines || []).filter(tl => tl.file === m.file_path && tl.method === m.name);
                item.waterfall = tls.length ? tls[0].steps : [];
            }
        }
        item.file = m.file_path ? m.file_path.split('/').pop() : "";
        return item;
    });

    let outputText = "";
    const activeProjectDir = $("projectPath")?.value.trim() || ".";

    if (currentAiQueryFormat === "cli") {
        let cmd = `DependencyGraphVisualizer.exe --cli --project "${activeProjectDir}" --query`;
        if (fileVal) cmd += ` --file "${fileVal}"`;
        if (funcVal) cmd += ` --func "${funcVal}"`;
        if (checkedFields.length) cmd += ` --fields="${checkedFields.join(',')}"`;
        if (currentAiQueryFilter === "hot") cmd += ` --min-hz=60`;
        if (currentAiQueryFilter === "mutations") cmd += ` --has-mutations`;
        if (currentAiQueryFilter === "loops") cmd += ` --has-loops`;
        if (limit > 0) cmd += ` --limit=${limit}`;
        if (budget > 0) cmd += ` --budget=${budget}`;
        outputText = `# Выполните в терминале (или передайте ИИ-агенту):\n${cmd}\n\n# Либо через REST API:\n# POST http://127.0.0.1:8765/api/query\n` +
            JSON.stringify({
                path: activeProjectDir,
                file: fileVal || undefined,
                func: funcVal || undefined,
                fields: checkedFields,
                min_hz: currentAiQueryFilter === "hot" ? 60 : undefined,
                has_mutations: currentAiQueryFilter === "mutations" ? true : undefined,
                limit: limit || undefined,
                budget: budget || undefined,
                format: "json"
            }, null, 2);
    } else if (currentAiQueryFormat === "json") {
        const payload = {
            total_matches: filtered.length,
            fields_projected: checkedFields,
            methods: projected
        };
        outputText = JSON.stringify(payload, null, 2);
    } else if (currentAiQueryFormat === "compact") {
        if (!projected.length) {
            outputText = "// 0 совпадений по запросу";
        } else {
            outputText = projected.map(p => {
                const shortFile = p.file || "";
                let line = `[${shortFile}::${p.name || 'func'}(${p.params || ''})]`;
                Object.keys(p).forEach(k => {
                    if (k === 'name' || k === 'file' || k === 'params') return;
                    const val = p[k];
                    if (Array.isArray(val)) {
                        if (val.length) line += ` | ${k}: [${val.join(',')}]`;
                    } else if (typeof val === 'object' && val !== null) {
                        line += ` | ${k}: ${JSON.stringify(val)}`;
                    } else if (val !== undefined && val !== "" && val !== 0) {
                        line += ` | ${k}: ${val}`;
                    }
                });
                return line;
            }).join("\n");
        }
    } else if (currentAiQueryFormat === "markdown") {
        if (!projected.length) {
            outputText = "# 0 совпадений по запросу\n";
        } else {
            outputText = `# AI Query Result (${projected.length} методов, выборка полей: ${checkedFields.join(', ')})\n\n`;
            projected.forEach(p => {
                outputText += `### \`${p.file}::${p.name || 'func'}(${p.params || ''})\`\n`;
                Object.keys(p).forEach(k => {
                    if (k === 'name' || k === 'file' || k === 'params') return;
                    const val = p[k];
                    if (Array.isArray(val)) {
                        outputText += `- **${k}**: ${val.length ? val.map(v => '`' + v + '`').join(', ') : '*(пусто)*'}\n`;
                    } else if (typeof val === 'object' && val !== null) {
                        outputText += `- **${k}**: \`${JSON.stringify(val)}\`\n`;
                    } else {
                        outputText += `- **${k}**: ${val}\n`;
                    }
                });
                outputText += "\n";
            });
        }
    }

    if (budget > 0) {
        const charLimit = budget * 4;
        if (outputText.length > charLimit) {
            outputText = outputText.slice(0, charLimit) + `\n\n... [Лимит бюджета: обрезано на ${budget} токенах. Добавьте фильтры или уменьшите лимит]`;
        }
    }

    currentAiQueryResultText = outputText;
    const prevEl = $("aiQueryPreview");
    if (prevEl) prevEl.textContent = outputText;

    const estTokens = Math.round(outputText.length / 4);
    const fullCodebaseTokens = Math.max(12000, nodes.reduce((acc, n) => acc + (n.lines || 100) * 8, 0));
    const savedPercent = Math.max(0, Math.min(99.8, (1 - (estTokens / fullCodebaseTokens)) * 100)).toFixed(1);

    const estEl = $("aiQueryTokenEstimate");
    if (estEl) estEl.textContent = `Расход: ~${estTokens.toLocaleString()} токенов`;
    const savedEl = $("aiQueryTokenSaved");
    if (savedEl) savedEl.textContent = `Экономия ~${savedPercent}% токенов`;
}

// Ignore API Helpers
async function analyzeWithIgnores(ignoreStr) {
    const path = $("projectPath").value.trim();
    if (!path) { setStatus("Укажи путь к проекту"); $("projectPath").focus(); return; }
    const b = $("analyzeBtn"); b.disabled = true; b.textContent = "Анализ...";
    setStatus("Сканирование файлов…");
    const ignorePatterns = ignoreStr.split(",").map(x => x.trim()).filter(Boolean);
    try {
        const r = await fetch("/api/analyze", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ path, ignore: ignorePatterns })
        });
        const d = await r.json();
        if (!r.ok) throw Error(d.error || "Ошибка анализа");
        initGraph(d);
        showToast("✓ Анализ с учетом исключений обновлен!");
    } catch (e) {
        setStatus("Ошибка"); alert("Не удалось проанализировать проект:\n" + e.message);
    } finally {
        b.disabled = false; b.textContent = "⚡ Анализировать";
    }
}

async function saveIgnoresToGraphignore(ignoreStr) {
    const path = $("projectPath").value.trim();
    if (!path) { showToast("Укажите путь к проекту"); return; }
    const ignorePatterns = ignoreStr.split(",").map(x => x.trim()).filter(Boolean);
    try {
        const r = await fetch("/api/save_ignore", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ path, ignores: ignorePatterns })
        });
        const d = await r.json();
        if (!r.ok) throw Error(d.error || "Ошибка сохранения");
        showToast(`💾 Успешно сохранено ${ignorePatterns.length} правил в .graphignore!`);
        analyzeWithIgnores(ignoreStr);
    } catch (e) {
        showToast("Ошибка сохранения: " + e.message);
    }
}

async function browseForIgnore(mode) {
    const projectPath = $("projectPath")?.value?.trim() || "";
    try {
        const r = await fetch("/api/browse_ignore", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ project_path: projectPath, mode })
        });
        const d = await r.json();
        if (d.error) throw new Error(d.error);
        if (!d.patterns || !d.patterns.length) return;

        const input = $("ignoreInput");
        const cur = input.value.split(",").map(x => x.trim()).filter(Boolean);
        const added = [];
        d.patterns.forEach(p => {
            if (!cur.includes(p)) {
                cur.push(p);
                added.push(p);
            }
        });

        if (added.length > 0) {
            input.value = cur.join(", ");
            await analyzeWithIgnores(input.value);
            const label = mode === "folder" ? "Папка" : "Файл(ы)";
            showToast(`✓ ${label} в исключениях: ${added.join(', ')}`);
        } else {
            showToast("Выбранный элемент уже находится в исключениях");
        }
    } catch (e) {
        console.error(e);
        showToast("Ошибка проводника: " + e.message);
    }
}

// Download Helper
function download(filename, text, type = "text/plain") {
    const b = new Blob([text], { type });
    const a = document.createElement("a");
    a.href = URL.createObjectURL(b);
    a.download = filename;
    a.click();
    URL.revokeObjectURL(a.href);
}

// Vector SVG Export Generator
function exportSvg() {
    if (!data || !visibleNodesCache.length) return;
    const ns = visibleNodesCache;
    const es = visibleEdgesCache;

    let minX = Infinity, maxX = -Infinity, minY = Infinity, maxY = -Infinity;
    ns.forEach(n => {
        minX = Math.min(minX, n.x); maxX = Math.max(maxX, n.x);
        minY = Math.min(minY, n.y); maxY = Math.max(maxY, n.y);
    });
    const pad = 80;
    const vbX = minX - pad, vbY = minY - pad;
    const vbW = (maxX - minX) + pad * 2, vbH = (maxY - minY) + pad * 2;

    let svg = `<svg xmlns="http://www.w3.org/2000/svg" viewBox="${vbX} ${vbY} ${vbW} ${vbH}" width="${vbW}" height="${vbH}" style="background:#0a0e14;font-family:Inter,sans-serif;">\n`;
    svg += `<defs><marker id="arrow" viewBox="0 0 10 10" refX="22" refY="5" markerWidth="6" markerHeight="6" orient="auto"><path d="M 0 1 L 10 5 L 0 9 z" fill="#4d6b8f"/></marker></defs>\n`;

    // Edges
    es.forEach(e => {
        svg += `<line x1="${e.a.x}" y1="${e.a.y}" x2="${e.b.x}" y2="${e.b.y}" stroke="#4d6b8f" stroke-width="1.2" opacity="0.6" marker-end="url(#arrow)"/>\n`;
    });

    // Nodes
    ns.forEach(n => {
        const color = getNodeColor(n);
        svg += `<circle cx="${n.x}" cy="${n.y}" r="${n.r}" fill="${color}" stroke="#ffffff" stroke-width="1.5"/>\n`;
        const label = esc(n.classes[0] || n.id.split("/").pop());
        svg += `<text x="${n.x}" y="${n.y + n.r + 12}" fill="#e6edf5" font-size="10" text-anchor="middle">${label}</text>\n`;
    });

    svg += "</svg>";
    download("architecture-dependency-graph.svg", svg, "image/svg+xml");
    showToast("✓ Векторный SVG успешно экспортирован");
}

// PNG Screenshot Generator
function exportPng() {
    const link = document.createElement("a");
    link.download = "dependency-graph-screenshot.png";
    link.href = canvas.toDataURL("image/png");
    link.click();
    showToast("✓ Снимок экрана PNG сохранен");
}

// Modal Helpers
function openModal(id) { $(id)?.classList.remove("hidden"); }
function closeModal(id) { $(id)?.classList.add("hidden"); }
document.querySelectorAll("[data-close]").forEach(b => b.addEventListener("click", () => closeModal(b.dataset.close)));

// Event Bindings
$("spacingSlider")?.addEventListener("input", e => {
    nodeSpacing = Number(e.target.value);
    $("spacingVal").textContent = `${nodeSpacing}px`;
    layoutNodes();
    fitGraph();
});

$("neighborhoodSlider")?.addEventListener("input", e => {
    neighborhoodDepth = Number(e.target.value);
    $("neighborhoodVal").textContent = neighborhoodDepth === 0 ? "Вся сеть" : `${neighborhoodDepth}-hop глубина`;
    updateFilters();
});

$("themeSelector")?.addEventListener("change", e => setTheme(e.target.value));
$("heatmapMode")?.addEventListener("change", e => { heatmapMode = e.target.value; requestRender(); });
$("folderFilter")?.addEventListener("change", updateFilters);

$("showArrows")?.addEventListener("change", e => { showArrows = e.target.checked; requestRender(); });
$("curvedEdges")?.addEventListener("change", e => { curvedEdges = e.target.checked; requestRender(); });
$("showAllLabels")?.addEventListener("change", e => { showAllLabels = e.target.checked; requestRender(); });

$("regexToggle")?.addEventListener("click", () => {
    useRegex = !useRegex;
    $("regexToggle").classList.toggle("active", useRegex);
    updateFilters();
});

$("clearSearch")?.addEventListener("click", () => {
    $("search").value = "";
    updateFilters();
});

document.querySelectorAll(".q-pill").forEach(p => p.addEventListener("click", () => {
    document.querySelectorAll(".q-pill").forEach(x => x.classList.remove("active"));
    p.classList.add("active");
    quickFilter = p.dataset.qfilter;
    updateFilters();
}));

document.querySelectorAll(".filter-pill").forEach(p => p.addEventListener("click", () => {
    document.querySelectorAll(".filter-pill").forEach(x => x.classList.remove("active"));
    p.classList.add("active");
    filterSeverity = p.dataset.severity;
    renderFindings();
}));

// Path Finder Events
$("findPathBtn")?.addEventListener("click", () => {
    const sId = $("pathStartNode").value;
    const eId = $("pathEndNode").value;
    if (!sId || !eId) {
        showToast("Выберите оба узла для поиска пути");
        return;
    }
    const path = findShortestPath(sId, eId);
    if (!path) {
        $("pathResult").textContent = `Путь между "${sId.split('/').pop()}" и "${eId.split('/').pop()}" не найден.`;
        activePathNodes.clear();
        activePathEdges.clear();
        requestRender();
        return;
    }

    activePathNodes = new Set(path);
    activePathEdges = new Set();
    for (let i = 0; i < path.length - 1; i++) {
        const u = path[i], v = path[i+1];
        const edge = edges.find(e => e.source === u && e.target === v);
        if (edge) activePathEdges.add(edge);
    }

    $("pathResult").innerHTML = `✓ Найден путь из ${path.length} узлов:<br><b>${path.map(x => esc(x.split('/').pop())).join(" → ")}</b>`;
    showToast(`Путь найден: ${path.length} узлов`);
    requestRender();
});

$("clearPathBtn")?.addEventListener("click", () => {
    $("pathStartNode").value = "";
    $("pathEndNode").value = "";
    $("pathResult").textContent = "Выбери два узла для поиска цепочки связей.";
    activePathNodes.clear();
    activePathEdges.clear();
    requestRender();
});

// Browse & Analyze Events
$("browseBtn")?.addEventListener("click", async () => {
    const b = $("browseBtn"); b.disabled = true;
    try {
        const r = await fetch("/api/browse", { method: "POST" });
        const d = await r.json();
        if (d.path) {
            $("projectPath").value = d.path;
            $("analyzeBtn").click();
        }
    } catch (e) { console.error(e); }
    finally { b.disabled = false; }
});

$("analyzeBtn")?.addEventListener("click", async () => {
    const path = $("projectPath").value.trim();
    if (!path) { setStatus("Укажи путь к проекту"); $("projectPath").focus(); return; }
    const b = $("analyzeBtn"); b.disabled = true; b.textContent = "Анализ...";
    setStatus("Сканирование файлов…");
    try {
        const r = await fetch("/api/analyze", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ path }) });
        const d = await r.json();
        if (!r.ok) throw Error(d.error || "Ошибка анализа");
        initGraph(d);
        showToast("✓ Анализ проекта успешно завершен!");
    } catch (e) {
        setStatus("Ошибка"); alert("Не удалось проанализировать проект:\n" + e.message);
    } finally {
        b.disabled = false; b.textContent = "⚡ Анализировать";
    }
});

// Demo Project
$("demoBtn")?.addEventListener("click", () => {
    const files = [
        {
            id: "scripts/main.gd", path: "scripts/main.gd", language: "GDScript", classes: ["Main"], namespace: "", lines: 125,
            fan_in: 0, fan_out: 4, degree: 4, in_cycle: false, layer: "presentation", is_autoload: false, instability: 1.0, blast_radius: 0,
            is_orphan: false, orphan_status: "scene_entry_point", is_scene_attached: true,
            signals: ["world_ready"], functions: ["_ready", "_process", "start_game"],
            skeleton: "class_name Main extends Node\n\nsignal world_ready()\n\nfunc _ready() -> void: pass\nfunc _process(delta: float) -> void: pass\nfunc start_game(mode: int = 0) -> void: pass\n",
            functions_detail: [
                { name: "start_game", params: "mode: int = 0", return_type: "void", callers: [], accessed_autoloads: ["GameManager"], triggering_signals: ["world_ready"], is_hot_path: false },
                { name: "_process", params: "delta: float", return_type: "void", callers: [], accessed_autoloads: [], triggering_signals: [], is_hot_path: true }
            ]
        },
        {
            id: "scripts/autoload/GameManager.cs", path: "scripts/autoload/GameManager.cs", language: "C#", classes: ["GameManager"], namespace: "Core", lines: 450,
            fan_in: 8, fan_out: 2, degree: 10, in_cycle: false, layer: "autoload", is_autoload: true, instability: 0.2, blast_radius: 8,
            is_orphan: false, orphan_status: "active",
            signals: [], functions: [],
            skeleton: "namespace Core {\n    public partial class GameManager : Node {\n        public static GameManager Instance { get; }\n        public void InitGame(string mode) { }\n        public void PauseGame() { }\n    }\n}\n",
            functions_detail: [{ name: "InitGame", params: "string mode", return_type: "void", callers: ["scripts/main.gd"], accessed_autoloads: ["GameManager"], triggering_signals: [], is_hot_path: false }]
        },
        {
            id: "scripts/world/World.gd", path: "scripts/world/World.gd", language: "GDScript", classes: ["World"], namespace: "", lines: 340,
            fan_in: 3, fan_out: 3, degree: 6, in_cycle: true, layer: "domain", is_autoload: false, instability: 0.5, blast_radius: 3,
            is_orphan: false, orphan_status: "active",
            signals: ["region_loaded"], functions: ["generate_region"],
            skeleton: "class_name World extends Node2D\n\nsignal region_loaded(region_name: String)\n\nfunc generate_region(size: Vector2i) -> void: pass\nfunc load_region(name: String) -> bool: pass\n",
            functions_detail: [
                { name: "generate_region", params: "size: Vector2i", return_type: "void", callers: ["scripts/main.gd"], accessed_autoloads: ["GameManager"], triggering_signals: [], is_hot_path: false },
                { name: "load_region", params: "name: String", return_type: "bool", callers: ["scripts/agents/AgentSystem.gd"], accessed_autoloads: [], triggering_signals: ["region_loaded"], is_hot_path: false }
            ]
        },
        {
            id: "scripts/agents/AgentSystem.gd", path: "scripts/agents/AgentSystem.gd", language: "GDScript", classes: ["AgentSystem"], namespace: "", lines: 520,
            fan_in: 3, fan_out: 2, degree: 5, in_cycle: true, layer: "domain", is_autoload: false, instability: 0.4, blast_radius: 3,
            is_orphan: false, orphan_status: "active",
            signals: ["agent_moved"], functions: ["tick_agents"],
            skeleton: "class_name AgentSystem extends Node\n\nsignal agent_moved(agent_id: int, pos: Vector2)\n\nfunc tick_agents(delta: float) -> void: pass\n",
            functions_detail: [{ name: "tick_agents", params: "delta: float", return_type: "void", callers: ["scripts/world/World.gd"], accessed_autoloads: [], triggering_signals: ["agent_moved"], is_hot_path: true }]
        },
        {
            id: "src/economy/MarketSystem.cs", path: "src/economy/MarketSystem.cs", language: "C#", classes: ["MarketSystem"], namespace: "Economy", lines: 610,
            fan_in: 4, fan_out: 2, degree: 6, in_cycle: false, layer: "domain", is_autoload: false, instability: 0.33, blast_radius: 4,
            is_orphan: false, orphan_status: "active",
            signals: [], functions: [],
            skeleton: "namespace Economy {\n    public class MarketSystem {\n        public TradeResult ExecuteTrade(Agent buyer, Patent asset) => default;\n    }\n}\n",
            functions_detail: [{ name: "ExecuteTrade", params: "Agent buyer, Patent asset", return_type: "TradeResult", callers: ["src/agents/Agent.cs"], accessed_autoloads: [], triggering_signals: [], is_hot_path: false }]
        },
        {
            id: "src/economy/Patent.cs", path: "src/economy/Patent.cs", language: "C#", classes: ["Patent"], namespace: "Economy", lines: 160,
            fan_in: 2, fan_out: 0, degree: 2, in_cycle: false, layer: "domain", is_autoload: false, instability: 0.0, blast_radius: 2,
            is_orphan: false, orphan_status: "active",
            signals: [], functions: [],
            skeleton: "namespace Economy {\n    public class Patent {\n        public string Id { get; set; }\n        public float Value { get; set; }\n    }\n}\n"
        },
        {
            id: "src/agents/Agent.cs", path: "src/agents/Agent.cs", language: "C#", classes: ["Agent"], namespace: "Agents", lines: 430,
            fan_in: 6, fan_out: 3, degree: 9, in_cycle: false, layer: "domain", is_autoload: false, instability: 0.33, blast_radius: 6,
            is_orphan: false, orphan_status: "active",
            signals: [], functions: [],
            skeleton: "namespace Agents {\n    public class Agent {\n        public string Name { get; set; }\n        public bool RequestTrade(Patent patent) => default;\n    }\n}\n",
            functions_detail: [{ name: "RequestTrade", params: "Patent patent", return_type: "bool", callers: [], accessed_autoloads: [], triggering_signals: [], is_hot_path: false }]
        },
        {
            id: "src/world/Region.cs", path: "src/world/Region.cs", language: "C#", classes: ["Region"], namespace: "World", lines: 920,
            fan_in: 5, fan_out: 2, degree: 7, in_cycle: false, layer: "domain", is_autoload: false, instability: 0.28, blast_radius: 5,
            is_orphan: false, orphan_status: "active", is_god_object: true,
            signals: [], functions: [],
            skeleton: "namespace World {\n    public class Region {\n        public Vector3 Size { get; }\n        public void Render() { }\n    }\n}\n"
        },
        {
            id: "src/core/MathUtils.cs", path: "src/core/MathUtils.cs", language: "C#", classes: ["MathUtils"], namespace: "Core", lines: 210,
            fan_in: 9, fan_out: 0, degree: 9, in_cycle: false, layer: "core", is_autoload: false, instability: 0.0, blast_radius: 9,
            is_orphan: false, orphan_status: "active",
            signals: [], functions: [],
            skeleton: "namespace Core {\n    public static class MathUtils {\n        public static Vector3 ClampMagnitude(Vector3 vec, float max) => default;\n    }\n}\n",
            functions_detail: [{ name: "ClampMagnitude", params: "Vector3 vec, float max", return_type: "Vector3", callers: ["src/world/Region.cs", "src/agents/Agent.cs"], accessed_autoloads: [], triggering_signals: [], is_hot_path: false }]
        },
        {
            id: "scenes/MainLevel.tscn", path: "scenes/MainLevel.tscn", language: "Scene", classes: ["MainLevel"], namespace: "", lines: 85,
            fan_in: 0, fan_out: 3, degree: 3, in_cycle: false, layer: "presentation", is_autoload: false, instability: 1.0, blast_radius: 0,
            is_orphan: false, orphan_status: "active",
            signals: [], functions: [],
            node_tree: [
                { name: "MainLevel", type: "Node2D", parent: null, script: "scripts/main.gd" },
                { name: "WorldView", type: "SubViewportContainer", parent: ".", script: null },
                { name: "AgentManager", type: "Node", parent: ".", script: "scripts/agents/AgentSystem.gd" },
                { name: "Camera2D", type: "Camera2D", parent: ".", script: null }
            ]
        },
        {
            id: "scripts/drafts/OldInventory.gd", path: "scripts/drafts/OldInventory.gd", language: "GDScript", classes: ["OldInventory"], namespace: "", lines: 95,
            fan_in: 0, fan_out: 0, degree: 0, in_cycle: false, layer: "domain", is_autoload: false, instability: 0.5, blast_radius: 0,
            is_orphan: true, orphan_status: "truly_dead", is_scene_attached: false,
            signals: [], functions: ["legacy_slot_check"],
            skeleton: "class_name OldInventory extends RefCounted\n\nfunc legacy_slot_check() -> void: pass\n"
        }
    ];
    const pairs = [
        ["scripts/main.gd", "scripts/world/World.gd", "resource-load", "high", "res://scripts/world/World.gd"],
        ["scripts/main.gd", "scripts/autoload/GameManager.cs", "autoload-access", "high", "GameManager"],
        ["scenes/MainLevel.tscn", "scripts/main.gd", "scene-script", "high", "Attached Script"],
        ["scripts/world/World.gd", "scripts/agents/AgentSystem.gd", "resource-load", "high", "AgentSystem.gd"],
        ["scripts/agents/AgentSystem.gd", "scripts/world/World.gd", "resource-load", "high", "World.gd"],
        ["src/economy/MarketSystem.cs", "src/economy/Patent.cs", "type-reference", "medium", "Patent"],
        ["src/economy/MarketSystem.cs", "src/agents/Agent.cs", "type-reference", "medium", "Agent"],
        ["src/agents/Agent.cs", "src/core/MathUtils.cs", "type-reference", "medium", "MathUtils"],
        ["src/world/Region.cs", "src/core/MathUtils.cs", "type-reference", "medium", "MathUtils"],
        ["scripts/autoload/GameManager.cs", "src/core/MathUtils.cs", "type-reference", "high", "MathUtils"]
    ];
    const edges = pairs.map(([source, target, type, confidence, detail]) => ({ source, target, type, confidence, detail }));
    const findings = [
        { severity: "error", code: "CYCLE", title: "Circular dependency loop", description: "World.gd и AgentSystem.gd взаимно загружают друг друга через preload.", nodes: ["scripts/world/World.gd", "scripts/agents/AgentSystem.gd"] },
        { severity: "warning", code: "LARGE_FILE", title: "Large Source File", description: "Region.cs содержит 920 строк кода. Рекомендуется декомпозиция.", nodes: ["src/world/Region.cs"] },
        { severity: "info", code: "TRULY_DEAD", title: "Мертвый код (0 связей)", description: "OldInventory.gd не используется в коде и не прикреплен к сценам.", nodes: ["scripts/drafts/OldInventory.gd"] }
    ];
    initGraph({
        root: "Demo Project",
        health_score: 85,
        files, edges, findings,
        cycles: [["scripts/world/World.gd", "scripts/agents/AgentSystem.gd"]],
        cycle_details: [{ nodes: ["scripts/world/World.gd", "scripts/agents/AgentSystem.gd"], size: 2, weakest_link: { source: "scripts/agents/AgentSystem.gd", target: "scripts/world/World.gd", type: "resource-load" }, recommendation: "Использовать Dependency Inversion или сигналы." }],
        signal_registry: {
            "world_ready": { declared_in: ["scripts/main.gd"], emitted_by: ["scripts/main.gd"], connected_in: [] },
            "region_loaded": { declared_in: ["scripts/world/World.gd"], emitted_by: ["scripts/world/World.gd"], connected_in: ["scripts/agents/AgentSystem.gd"] },
            "agent_moved": { declared_in: ["scripts/agents/AgentSystem.gd"], emitted_by: ["scripts/agents/AgentSystem.gd"], connected_in: [] }
        },
        hot_paths: [
            { file: "scripts/main.gd", method: "_process", params: "delta: float" },
            { file: "scripts/agents/AgentSystem.gd", method: "tick_agents", params: "delta: float" }
        ],
        orphans_summary: {
            truly_dead: ["scripts/drafts/OldInventory.gd"],
            scene_entry_points: ["scripts/main.gd"]
        },
        profiling: {
            total_frame_ms: 18.2,
            total_alloc_kb_per_sec: 165.4,
            bottlenecks: [
                { file: "scripts/agents/AgentSystem.gd", ms_per_frame: 5.8, cpu_percent: 32, alloc_kb_per_sec: 78.4, hot_methods: ["tick_agents"], reason: "Тяжелый 60-FPS цикл + аллокации в кадре" },
                { file: "src/world/Region.cs", ms_per_frame: 4.5, cpu_percent: 25, alloc_kb_per_sec: 42.0, hot_methods: ["Render"], reason: "Объемный рендер-метод монолита" }
            ],
            files: [
                { file: "scripts/agents/AgentSystem.gd", language: "GDScript", ms_per_frame: 5.8, cpu_percent: 32, alloc_kb_per_sec: 78.4, calls_per_frame: 60, is_bottleneck: true, hot_methods: ["tick_agents"] },
                { file: "src/world/Region.cs", language: "C#", ms_per_frame: 4.5, cpu_percent: 25, alloc_kb_per_sec: 42.0, calls_per_frame: 60, is_bottleneck: true, hot_methods: ["Render"] },
                { file: "scripts/main.gd", language: "GDScript", ms_per_frame: 2.1, cpu_percent: 12, alloc_kb_per_sec: 18.0, calls_per_frame: 60, is_bottleneck: false, hot_methods: ["_process"] },
                { file: "scripts/world/World.gd", language: "GDScript", ms_per_frame: 1.8, cpu_percent: 10, alloc_kb_per_sec: 12.0, calls_per_frame: 20, is_bottleneck: false, hot_methods: [] }
            ]
        },
        event_flows: [
            {
                signal: "region_loaded",
                origin_file: "scripts/world/World.gd",
                is_async: false,
                listeners: [
                    { file: "scripts/agents/AgentSystem.gd", method: "on_region_loaded", calls: ["tick_agents", "update_positions"] }
                ],
                downstream_calls: ["scripts/agents/AgentSystem.gd::tick_agents", "scripts/agents/AgentSystem.gd::update_positions"]
            },
            {
                signal: "agent_moved",
                origin_file: "scripts/agents/AgentSystem.gd",
                is_async: true,
                listeners: [
                    { file: "scripts/main.gd", method: "on_agent_moved", calls: ["update_hud"] }
                ],
                downstream_calls: ["scripts/main.gd::update_hud"]
            }
        ],
        stats: { files: files.length, edges: edges.length, lines: files.reduce((a,f) => a + f.lines, 0), csharp: 6, gdscript: 4, scenes: 1, cycles: 1, findings: findings.length, health_score: 85 }
    });
    // Add profiling metrics to demo node objects
    nodes.forEach(n => {
        if (n.id === "scripts/agents/AgentSystem.gd") n.profiling = { ms_per_frame: 5.8, cpu_percent: 32, alloc_kb_per_sec: 78.4, is_bottleneck: true };
        else if (n.id === "src/world/Region.cs") n.profiling = { ms_per_frame: 4.5, cpu_percent: 25, alloc_kb_per_sec: 42.0, is_bottleneck: true };
        else if (n.id === "scripts/main.gd") n.profiling = { ms_per_frame: 2.1, cpu_percent: 12, alloc_kb_per_sec: 18.0, is_bottleneck: false };
    });
});

// UI Event Handlers
$("search")?.addEventListener("input", updateFilters);
$("toggleSim")?.addEventListener("click", () => setSimPaused(!isPaused));
$("toggleSimCanvas")?.addEventListener("click", () => setSimPaused(!isPaused));
["showCS", "showGD", "showScene", "edgeFilter", "minConfidence"].forEach(id => $(id)?.addEventListener("input", () => {
    if (id === "minConfidence") {
        $("confidenceText").textContent = ["Все, включая эвристические", "Средняя и прямая уверенность", "Только прямые связи"][Number($("minConfidence").value)];
    }
    updateFilters();
}));

$("layout")?.addEventListener("change", () => { layoutNodes(); fitGraph(); });
$("fitBtn")?.addEventListener("click", fitGraph);
$("resetBtn")?.addEventListener("click", () => {
    inspect(null);
    hovered = null;
    isolatedNode = null;
    activeCycleNodes.clear();
    activePathNodes.clear();
    activePathEdges.clear();
    hideTooltip();
    $("search").value = "";
    quickFilter = "all";
    document.querySelectorAll(".q-pill").forEach(p => p.classList.toggle("active", p.dataset.qfilter === "all"));
    updateFilters();
    fitGraph();
    showToast("Все фильтры и фокус сброшены");
});

$("zoomIn")?.addEventListener("click", () => zoomAt(canvas.clientWidth / 2, canvas.clientHeight / 2, 1.25));
$("zoomOut")?.addEventListener("click", () => zoomAt(canvas.clientWidth / 2, canvas.clientHeight / 2, 0.8));
$("centerBtn")?.addEventListener("click", fitGraph);

$("toggleMinimapBtn")?.addEventListener("click", () => {
    minimapVisible = !minimapVisible;
    $("minimapWrap")?.classList.toggle("hidden", !minimapVisible);
});
$("closeMinimap")?.addEventListener("click", () => {
    minimapVisible = false;
    $("minimapWrap")?.classList.add("hidden");
});

$("screenshotBtn")?.addEventListener("click", exportPng);
$("svgExportBtn")?.addEventListener("click", exportSvg);
$("fullscreenBtn")?.addEventListener("click", () => {
    if (!document.fullscreenElement) {
        document.documentElement.requestFullscreen().catch(console.error);
    } else {
        document.exitFullscreen().catch(console.error);
    }
});

$("helpBtn")?.addEventListener("click", () => openModal("helpModal"));

// AI Buttons
$("aiRefactorPlanBtn")?.addEventListener("click", () => openAiModal("Главный план рефакторинга", generateMasterRefactorPrompt()));
$("aiAllDepsListBtn")?.addEventListener("click", () => {
    const txt = formatAllProjectDepsList();
    navigator.clipboard.writeText(txt);
    openAiModal("Полный список связей файлов проекта", txt);
    showToast("✓ Список связей всего проекта скопирован в буфер!");
});
$("aiSkeletonsDigestBtn")?.addEventListener("click", () => {
    const txt = generateSkeletonsDigest();
    navigator.clipboard.writeText(txt);
    openAiModal("Скелеты публичных API (-90% токенов)", txt);
    showToast("✓ Скелеты контрактов скопированы в буфер!");
});
$("aiSignalsRegistryBtn")?.addEventListener("click", () => {
    const txt = generateSignalsRegistryMarkdown();
    navigator.clipboard.writeText(txt);
    openAiModal("Глобальный реестр сигналов и событий", txt);
    showToast("✓ Карта сигналов скопирована в буфер!");
});
$("aiSceneTreesBtn")?.addEventListener("click", () => {
    const txt = generateSceneTreesMarkdown();
    navigator.clipboard.writeText(txt);
    openAiModal("Иерархия сцен (.tscn) и дерево нод", txt);
    showToast("✓ Деревья сцен скопированы в буфер!");
});
$("aiOrphansReviewBtn")?.addEventListener("click", () => {
    const txt = generateOrphansReviewMarkdown();
    navigator.clipboard.writeText(txt);
    openAiModal("Ревизия мертвого кода и точек входа сцен", txt);
    showToast("✓ Отчет по мертвому коду скопирован в буфер!");
});
$("aiHotPathsBtn")?.addEventListener("click", () => {
    const txt = generateHotPathsMarkdown();
    navigator.clipboard.writeText(txt);
    openAiModal("Анализ горячих путей (60-FPS)", txt);
    showToast("✓ Анализ горячих путей скопирован в буфер!");
});
$("aiWhatIfPlanBtn")?.addEventListener("click", () => {
    const txt = generateWhatIfPlanMarkdown();
    navigator.clipboard.writeText(txt);
    openAiModal("План рефакторинга (What-If Sandbox)", txt);
    showToast("✓ План What-If скопирован в буфер!");
});
$("aiEventFlowBtn")?.addEventListener("click", () => {
    const txt = generateEventFlowsMarkdown();
    navigator.clipboard.writeText(txt);
    openAiModal("Цепочки Event-Flow (Реактивные потоки)", txt);
    showToast("✓ Цепочки Event-Flow скопированы в буфер!");
});
$("aiBenchmarkReportBtn")?.addEventListener("click", () => {
    const txt = generateBenchmarkMarkdown();
    navigator.clipboard.writeText(txt);
    openAiModal("Замеры нагрузки CPU и Profiler", txt);
    showToast("✓ Отчет замеров нагрузки скопирован в буфер!");
});
$("aiPassportsDigestBtn")?.addEventListener("click", () => {
    const txt = generatePassportsDigestMarkdown();
    navigator.clipboard.writeText(txt);
    openAiModal("Паспорта функций (Поведение, мутации, сигналы)", txt);
    showToast("✓ Паспорта функций скопированы в буфер!");
});
$("aiCliCommandBtn")?.addEventListener("click", () => {
    const txt = generateCliCommandsHelp();
    openAiModal("CLI команды для автономного использования ИИ", txt);
});
$("aiCycleBreakerBtn")?.addEventListener("click", () => openAiModal("Разрыв циклов (DIP)", generateCycleBreakerPrompt()));
$("aiGodObjectBtn")?.addEventListener("click", () => {
    const gods = nodes.filter(n => n.is_god_object);
    const txt = `# План декомпозиции God-Objects / Hubs (${gods.length} классов)\n\n` +
        gods.map(g => `## \`${g.path}\`\n- Строк: ${g.lines}, Связей: ${g.degree}, Instability: ${g.instability}\n- Предложи разбиение на Single Responsibility сервисы.\n`).join("\n");
    openAiModal("Декомпозиция Hubs", txt);
});
$("aiMermaidBtn")?.addEventListener("click", () => {
    const diag = generateMermaidDiagram();
    navigator.clipboard.writeText(diag);
    openAiModal("Mermaid Диаграмма (скопировано в буфер)", diag);
    showToast("✓ Mermaid диаграмма скопирована в буфер!");
});
$("aiPlantUmlBtn")?.addEventListener("click", () => {
    const diag = generatePlantUml();
    navigator.clipboard.writeText(diag);
    openAiModal("PlantUML Схема (скопировано в буфер)", diag);
    showToast("✓ PlantUML схема скопирована в буфер!");
});
$("aiCompactDigestBtn")?.addEventListener("click", () => openAiModal("Компактный архитектурный дайджест", generateCompactDigest()));
$("aiPageRankBtn")?.addEventListener("click", () => {
    const txt = generatePageRankContextMarkdown(4000);
    navigator.clipboard.writeText(txt);
    openAiModal("PageRank Оптимизатор Контекста для ИИ (4,000 токенов)", txt);
    showToast("✓ Контекст по PageRank скопирован в буфер!");
});
$("aiTemporalBtn")?.addEventListener("click", () => {
    const txt = generateTemporalCouplingMarkdown();
    navigator.clipboard.writeText(txt);
    openAiModal("Скрытая темпоральная связанность (Git Churn)", txt);
    showToast("✓ Темпоральные зависимости скопированы в буфер!");
});
$("aiChronologyBtn")?.addEventListener("click", () => {
    const txt = generateChronologyMarkdown();
    navigator.clipboard.writeText(txt);
    openAiModal("Частота вызовов, такты CPU и передача данных", txt);
    showToast("✓ Хронология и частота вызовов скопированы в буфер!");
});

// Topbar & Mode Switchers
$("whatIfToggleBtn")?.addEventListener("click", () => setWhatIfMode(!whatIfActive));
$("benchmarkModalBtn")?.addEventListener("click", openBenchmarkModal);
$("chronologyModalBtn")?.addEventListener("click", openChronologyModal);
$("dsmModalBtn")?.addEventListener("click", openDsmModal);
$("eventFlowModalBtn")?.addEventListener("click", openEventFlowModal);
$("aiQueryBuilderTopBtn")?.addEventListener("click", () => openAiQueryModal());
$("aiQueryBuilderBtn")?.addEventListener("click", () => openAiQueryModal());

// AI Query Builder Event Handlers
$("aiQueryFileSelect")?.addEventListener("change", renderAiQueryPreview);
$("aiQueryFuncInput")?.addEventListener("input", renderAiQueryPreview);
$("aiQueryLimitSelect")?.addEventListener("change", renderAiQueryPreview);
$("aiQueryBudgetSelect")?.addEventListener("change", renderAiQueryPreview);

document.querySelectorAll(".q-field-chk").forEach(chk => {
    chk.addEventListener("change", renderAiQueryPreview);
});

document.querySelectorAll(".query-filter-pills .q-pill").forEach(pill => {
    pill.addEventListener("click", () => {
        document.querySelectorAll(".query-filter-pills .q-pill").forEach(p => p.classList.remove("active"));
        pill.classList.add("active");
        currentAiQueryFilter = pill.dataset.qf;
        renderAiQueryPreview();
    });
});

document.querySelectorAll(".ai-query-tab-btn").forEach(tab => {
    tab.addEventListener("click", () => {
        document.querySelectorAll(".ai-query-tab-btn").forEach(t => t.classList.remove("active"));
        tab.classList.add("active");
        currentAiQueryFormat = tab.dataset.fmt;
        renderAiQueryPreview();
    });
});

$("aiQuerySelectAllFields")?.addEventListener("click", () => {
    document.querySelectorAll(".q-field-chk").forEach(c => c.checked = true);
    renderAiQueryPreview();
});

$("aiQuerySelectMinFields")?.addEventListener("click", () => {
    document.querySelectorAll(".q-field-chk").forEach(c => {
        c.checked = (c.value === "name" || c.value === "params" || c.value === "mutations" || c.value === "hz");
    });
    renderAiQueryPreview();
});

$("aiQuerySelectClearFields")?.addEventListener("click", () => {
    document.querySelectorAll(".q-field-chk").forEach(c => c.checked = (c.value === "name"));
    renderAiQueryPreview();
});

$("aiQueryCopyBtn")?.addEventListener("click", () => {
    navigator.clipboard.writeText(currentAiQueryResultText);
    showToast("✓ Результат AI Query скопирован в буфер!");
});

$("aiQueryCopyCliBtn")?.addEventListener("click", () => {
    const activeProjectDir = $("projectPath")?.value.trim() || ".";
    const fileVal = ($("aiQueryFileSelect")?.value || "").trim().toLowerCase();
    const funcVal = ($("aiQueryFuncInput")?.value || "").trim().toLowerCase();
    const limit = parseInt($("aiQueryLimitSelect")?.value || "0", 10);
    const budget = parseInt($("aiQueryBudgetSelect")?.value || "0", 10);
    const checkedFields = Array.from(document.querySelectorAll(".q-field-chk:checked")).map(c => c.value);

    let cmd = `DependencyGraphVisualizer.exe --cli --project "${activeProjectDir}" --query`;
    if (fileVal) cmd += ` --file "${fileVal}"`;
    if (funcVal) cmd += ` --func "${funcVal}"`;
    if (checkedFields.length) cmd += ` --fields="${checkedFields.join(',')}"`;
    if (currentAiQueryFilter === "hot") cmd += ` --min-hz=60`;
    if (currentAiQueryFilter === "mutations") cmd += ` --has-mutations`;
    if (currentAiQueryFilter === "loops") cmd += ` --has-loops`;
    if (limit > 0) cmd += ` --limit=${limit}`;
    if (budget > 0) cmd += ` --budget=${budget}`;

    navigator.clipboard.writeText(cmd);
    showToast("✓ CLI команда скопирована в буфер!");
});

$("aiQueryDownloadBtn")?.addEventListener("click", () => {
    let ext = "json";
    let mime = "application/json";
    if (currentAiQueryFormat === "markdown") { ext = "md"; mime = "text/markdown"; }
    else if (currentAiQueryFormat === "compact") { ext = "txt"; mime = "text/plain"; }
    else if (currentAiQueryFormat === "cli") { ext = "sh"; mime = "text/plain"; }
    download(`ai-query-result.${ext}`, currentAiQueryResultText, mime);
    showToast(`✓ Файл ai-query-result.${ext} успешно сохранен`);
});

// Function Deep Dive Controls
$("deepDiveCopyMdBtn")?.addEventListener("click", () => {
    navigator.clipboard.writeText(currentDeepDiveMd);
    showToast("✓ 360° Паспорт функции скопирован в буфер!");
});

$("deepDiveOpenQueryBtn")?.addEventListener("click", () => {
    closeModal("functionDeepDiveModal");
    openAiQueryModal();
});

// Chronology Modal Controls & Tabs
document.querySelectorAll(".chronology-tab-btn").forEach(btn => {
    btn.addEventListener("click", () => switchChronologyTab(btn.dataset.tab));
});
document.querySelectorAll(".chron-pill").forEach(pill => {
    pill.addEventListener("click", () => {
        document.querySelectorAll(".chron-pill").forEach(p => p.classList.remove("active"));
        pill.classList.add("active");
        chronActiveFilter = pill.dataset.filter;
        renderChronologyTable();
    });
});
$("chronSearchInput")?.addEventListener("input", renderChronologyTable);
$("chronSortSelect")?.addEventListener("change", renderChronologyTable);
$("chronEntrySelect")?.addEventListener("change", renderChronologyWaterfall);
$("copyGdProfilerBtn")?.addEventListener("click", () => {
    navigator.clipboard.writeText(GD_LIVE_PROFILER_SCRIPT);
    const prev = $("profilerCodePreview");
    if (prev) prev.textContent = GD_LIVE_PROFILER_SCRIPT;
    showToast("✓ Скрипт LiveProfiler.gd скопирован в буфер!");
});
$("copyCsProfilerBtn")?.addEventListener("click", () => {
    navigator.clipboard.writeText(CS_LIVE_PROFILER_SCRIPT);
    const prev = $("profilerCodePreview");
    if (prev) prev.textContent = CS_LIVE_PROFILER_SCRIPT;
    showToast("✓ Скрипт LiveProfiler.cs скопирован в буфер!");
});

// Simulation & DSM Matrix Event Handlers
$("simRunBtn")?.addEventListener("click", startBenchmarkSimulation);
$("simStopBtn")?.addEventListener("click", () => stopBenchmarkSimulation(false));
$("simDurationSelect")?.addEventListener("change", () => {
    if (!simIsRunning) {
        simTotalDuration = Number($("simDurationSelect").value || 60);
        $("simTimeElapsed").textContent = `00:00 / ${formatSimTime(simTotalDuration)}`;
    }
});
$("dsmScopeSelect")?.addEventListener("change", renderDsmTable);

// What-If Floating Banner Controls
$("whatIfAiPlanBtn")?.addEventListener("click", () => {
    const txt = generateWhatIfPlanMarkdown();
    openAiModal("План рефакторинга (What-If Sandbox)", txt);
});
$("whatIfResetBtn")?.addEventListener("click", () => {
    cutEdges.clear();
    recalculateWhatIf();
    showToast("Все разрывы сброшены");
});
$("whatIfExitBtn")?.addEventListener("click", () => setWhatIfMode(false));

// Ignore & Exclude Handlers
$("browseIgnoreFolderBtn")?.addEventListener("click", () => browseForIgnore("folder"));
$("browseIgnoreFileBtn")?.addEventListener("click", () => browseForIgnore("file"));
$("applyIgnoreBtn")?.addEventListener("click", () => {
    const val = $("ignoreInput")?.value || "";
    analyzeWithIgnores(val);
});
$("saveIgnoreBtn")?.addEventListener("click", () => {
    const val = $("ignoreInput")?.value || "";
    saveIgnoresToGraphignore(val);
});

// Benchmark & Event-Flow Search Handlers
$("benchmarkSearch")?.addEventListener("input", renderBenchmarkTable);
$("benchmarkOnlyBottlenecks")?.addEventListener("click", () => {
    benchmarkBottlenecksOnly = !benchmarkBottlenecksOnly;
    $("benchmarkOnlyBottlenecks")?.classList.toggle("active", benchmarkBottlenecksOnly);
    renderBenchmarkTable();
});
$("eventFlowSearch")?.addEventListener("input", renderEventFlowList);

$("copyAiModalText")?.addEventListener("click", () => {
    navigator.clipboard.writeText($("aiModalTextarea").value);
    showToast("✓ Текст скопирован в буфер обмена!");
});
$("downloadAiModalText")?.addEventListener("click", () => {
    download("architecture-refactoring-prompt.md", $("aiModalTextarea").value);
    showToast("✓ Файл .md сохранен!");
});

// Exports
$("exportJSON")?.addEventListener("click", () => { if (data) download("dependency-graph.json", JSON.stringify(data, null, 2), "application/json"); });
$("exportCSV")?.addEventListener("click", () => {
    if (data) {
        const rows = [["path", "language", "layer", "lines", "fan_in", "fan_out", "instability", "blast_radius", "in_cycle"],
            ...data.files.map(f => [f.path, f.language, f.layer, f.lines, f.fan_in, f.fan_out, f.instability, f.blast_radius, f.in_cycle])];
        download("dependency-nodes.csv", rows.map(r => r.map(x => `"${String(x).replace(/"/g, '""')}"`).join(",")).join("\r\n"), "text/csv;charset=utf-8");
    }
});
$("exportEdgesCSV")?.addEventListener("click", () => {
    if (data) {
        const rows = [["source", "target", "type", "confidence", "detail"],
            ...data.edges.map(e => [e.source, e.target, e.type, e.confidence, e.detail])];
        download("dependency-edges.csv", rows.map(r => r.map(x => `"${String(x).replace(/"/g, '""')}"`).join(",")).join("\r\n"), "text/csv;charset=utf-8");
    }
});

// Canvas Pointer & Dragging Events
canvas.addEventListener("wheel", e => {
    e.preventDefault();
    const r = canvas.getBoundingClientRect();
    zoomAt(e.clientX - r.left, e.clientY - r.top, e.deltaY < 0 ? 1.12 : 0.89);
}, { passive: false });

canvas.addEventListener("pointerdown", e => {
    const r = canvas.getBoundingClientRect(), p = world({ x: e.clientX - r.left, y: e.clientY - r.top });
    lastPointer = { x: e.clientX, y: e.clientY };
    const ns = visibleNodesCache;
    let hit = null;
    for (let i = ns.length - 1; i >= 0; i--) {
        const n = ns[i];
        if (Math.hypot(n.x - p.x, n.y - p.y) < n.r + 6) {
            hit = n;
            break;
        }
    }
    if (hit) {
        dragging = { node: hit, offsetX: hit.x - p.x, offsetY: hit.y - p.y, startX: e.clientX, startY: e.clientY, moved: false };
        canvas.setPointerCapture(e.pointerId);
    } else {
        if (whatIfActive) {
            let clickedEdge = null;
            let bestDist = 10;
            for (let i = 0; i < visibleEdgesCache.length; i++) {
                const ed = visibleEdgesCache[i];
                if (!ed.a || !ed.b) continue;
                const d = distToSegment(p.x, p.y, ed.a.x, ed.a.y, ed.b.x, ed.b.y);
                if (d < bestDist) {
                    bestDist = d;
                    clickedEdge = ed;
                }
            }
            if (clickedEdge) {
                const k = edgeKey(clickedEdge.source, clickedEdge.target);
                if (cutEdges.has(k)) {
                    cutEdges.delete(k);
                    showToast(`✓ Восстановлена связь: ${clickedEdge.source.split('/').pop()} → ${clickedEdge.target.split('/').pop()}`);
                } else {
                    cutEdges.add(k);
                    showToast(`✂️ Разорвана связь: ${clickedEdge.source.split('/').pop()} → ${clickedEdge.target.split('/').pop()}`);
                }
                recalculateWhatIf();
                return;
            }
        }
        dragging = { pan: true, startX: e.clientX, startY: e.clientY, panX: pan.x, panY: pan.y };
        canvas.setPointerCapture(e.pointerId);
    }
});

canvas.addEventListener("pointermove", e => {
    const r = canvas.getBoundingClientRect();
    if (dragging) {
        hideTooltip();
        if (dragging.node) {
            const p = world({ x: e.clientX - r.left, y: e.clientY - r.top });
            dragging.node.x = p.x + dragging.offsetX;
            dragging.node.y = p.y + dragging.offsetY;
            dragging.node.vx = 0; dragging.node.vy = 0;
            if (Math.hypot(e.clientX - dragging.startX, e.clientY - dragging.startY) > 4) dragging.moved = true;
        } else {
            pan.x = dragging.panX + e.clientX - dragging.startX;
            pan.y = dragging.panY + e.clientY - dragging.startY;
        }
        requestRender();
        return;
    }

    const mouse = world({ x: e.clientX - r.left, y: e.clientY - r.top });
    const ns = visibleNodesCache;
    let found = null;
    for (let i = ns.length - 1; i >= 0; i--) {
        const n = ns[i];
        if (Math.hypot(n.x - mouse.x, n.y - mouse.y) <= n.r + 6) {
            found = n;
            break;
        }
    }

    if (found !== hovered) {
        hovered = found;
        canvas.style.cursor = hovered ? "pointer" : "grab";
        if (hovered) {
            showTooltip(hovered, e.clientX - r.left, e.clientY - r.top);
        } else {
            hideTooltip();
        }
        requestRender();
    } else if (hovered) {
        showTooltip(hovered, e.clientX - r.left, e.clientY - r.top);
    }
});

canvas.addEventListener("pointerup", e => {
    if (!dragging) return;
    const d = dragging;
    dragging = null;
    if (d.node) {
        if (!d.moved) {
            inspect(d.node);
        } else {
            d.node.pinned = true;
        }
    }
    requestRender();
});

canvas.addEventListener("pointerleave", () => {
    hovered = null;
    hideTooltip();
    requestRender();
});

canvas.addEventListener("dblclick", e => {
    const r = canvas.getBoundingClientRect(), p = world({ x: e.clientX - r.left, y: e.clientY - r.top });
    const n = visibleNodesCache.find(n => Math.hypot(n.x - p.x, n.y - p.y) < n.r + 6);
    if (n) {
        n.pinned = !n.pinned;
        showToast(n.pinned ? `📌 Узел закреплен: ${n.id.split("/").pop()}` : `Откреплен: ${n.id.split("/").pop()}`);
        requestRender();
    }
});

// MiniMap Radar Click & Pan
minimapCanvas?.addEventListener("pointerdown", e => {
    if (!nodes.length) return;
    const r = minimapCanvas.getBoundingClientRect();
    const mx = e.clientX - r.left, my = e.clientY - r.top;

    let minX = Infinity, maxX = -Infinity, minY = Infinity, maxY = -Infinity;
    for (let i = 0; i < nodes.length; i++) {
        const n = nodes[i];
        if (n.x < minX) minX = n.x;
        if (n.x > maxX) maxX = n.x;
        if (n.y < minY) minY = n.y;
        if (n.y > maxY) maxY = n.y;
    }
    const pad = 80;
    minX -= pad; maxX += pad; minY -= pad; maxY += pad;
    const gW = maxX - minX || 1, gH = maxY - minY || 1;
    const mw = minimapCanvas.width, mh = minimapCanvas.height;
    const miniScale = Math.min(mw / gW, mh / gH);
    const offX = (mw - gW * miniScale) / 2 - minX * miniScale;
    const offY = (mh - gH * miniScale) / 2 - minY * miniScale;

    const targetWx = (mx - offX) / miniScale;
    const targetWy = (my - offY) / miniScale;

    pan.x = canvas.clientWidth / 2 - targetWx * scale;
    pan.y = canvas.clientHeight / 2 - targetWy * scale;
    requestRender();
});

// Keyboard Shortcuts Handler
window.addEventListener("keydown", e => {
    if (e.target.tagName === "INPUT" || e.target.tagName === "TEXTAREA" || e.target.tagName === "SELECT") return;

    if (e.code === "Space") {
        e.preventDefault();
        setSimPaused(!isPaused);
    } else if (e.key === "f" || e.key === "F" || e.key === "а" || e.key === "А") {
        fitGraph();
    } else if (e.key === "Escape") {
        $("resetBtn")?.click();
    } else if (e.key === "m" || e.key === "M" || e.key === "ь" || e.key === "Ь") {
        $("toggleMinimapBtn")?.click();
    } else if (e.key === "t" || e.key === "T" || e.key === "е" || e.key === "Е") {
        const themes = ["dark", "midnight", "cyberpunk", "light"];
        const curTheme = document.body.getAttribute("data-theme") || "dark";
        const nextTheme = themes[(themes.indexOf(curTheme) + 1) % themes.length];
        setTheme(nextTheme);
        showToast(`Тема: ${nextTheme}`);
    } else if (e.key === "1") {
        $("layout").value = "force"; layoutNodes(); fitGraph();
    } else if (e.key === "2") {
        $("layout").value = "modules"; layoutNodes(); fitGraph();
    } else if (e.key === "3") {
        $("layout").value = "radial"; layoutNodes(); fitGraph();
    } else if (e.key === "4") {
        $("layout").value = "hierarchy"; layoutNodes(); fitGraph();
    } else if (e.key === "5") {
        $("layout").value = "grid"; layoutNodes(); fitGraph();
    } else if (e.key === "+" || e.key === "=") {
        zoomAt(canvas.clientWidth / 2, canvas.clientHeight / 2, 1.25);
    } else if (e.key === "-" || e.key === "_") {
        zoomAt(canvas.clientWidth / 2, canvas.clientHeight / 2, 0.8);
    } else if (e.key === "?") {
        openModal("helpModal");
    }
});

$("projectPath")?.addEventListener("keydown", e => { if (e.key === "Enter") $("analyzeBtn").click(); });

resize();
})();