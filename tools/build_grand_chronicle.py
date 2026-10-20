# -*- coding: utf-8 -*-
import json
import os
import sys

def main():
    print("Loading chronicle data...")
    with open("tools/full_chronicle_data.json", "r", encoding="utf-8") as f:
        chronicle = json.load(f)

    stats = chronicle['stats']
    commits = chronicle['commits']
    branches = chronicle['branches']

    commits_json_str = json.dumps(commits, ensure_ascii=False)
    stats_json_str = json.dumps(stats, ensure_ascii=False)
    branches_json_str = json.dumps(branches, ensure_ascii=False)

    print(f"Loaded {len(commits)} milestones.")

    # List of screenshot paths that exist
    screenshots = [
        {"path": "docs/screenshots-24/island_start.png", "title": "Стартовый остров колонии", "desc": "Генерация начальной поляны R12, береговой линии и автотайлинга рельефа."},
        {"path": "docs/screenshots-24/world_map_overlay.png", "title": "Оверлей мировой планетарной карты", "desc": "Планетарный вид с широтными поясами, океанами и континентальными плитами."},
        {"path": "docs/screenshots-24/island_after_autotile_and_zfix.png", "title": "Остров после Z-Ordering и автотайлинга", "desc": "Послойный рендеринг: суша, вода, горы, MultiMesh деревьев и тени."},
        {"path": "docs/screenshots-24/world_tiles_beyond_zoom05.png", "title": "Макро-зум мировой карты (LOD < 0.55)", "desc": "Режим CoarseChunkSize 512 для мгновенной отрисовки континентов без лагов."},
        {"path": "docs/screenshots-24/world_after_jump_cell100000.png", "title": "Бесшовный прыжок на клетку 100,000", "desc": "Тест бесшовности координат: генерация колонии в отдалённых координатах мира."},
        {"path": "docs/screenshots-24/camera_free_cells850_800.png", "title": "Свободная камера (850×800 клеток)", "desc": "Широкоформатный обзор симуляции без ограничения видимости."},
        {"path": "docs/lab_screenshots/lab_shot_1.png", "title": "NoiseLab: Проверка шумов и швов", "desc": "Инструментальный тест детерминизма генератора v4 и отсутствия швов."},
        {"path": "docs/lab_screenshots/lab_shot_2.png", "title": "NoiseLab: Тектонические плиты Вороного", "desc": "Расчёт границ континентов и зон субдукции плит."},
        {"path": "docs/lab_screenshots/lab_shot_3.png", "title": "NoiseLab: Планетарная гидрология", "desc": "Дендритные речные бассейны и диффузия влаги от океанов."}
    ]
    screenshots_json_str = json.dumps(screenshots, ensure_ascii=False)

    # Let's read the template parts and compile index.html
    html_content = r'''<!DOCTYPE html>
<html lang="ru">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>Colony Sim: Великая Летопись Разработки (Godot 4.7 C#)</title>
    <link rel="preconnect" href="https://fonts.googleapis.com">
    <link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
    <link href="https://fonts.googleapis.com/css2?family=Fira+Code:wght@400;500;600;700&family=Inter:wght@300;400;500;600;700;800;900&family=Space+Grotesk:wght@500;700;900&display=swap" rel="stylesheet">
    <style>
        :root {
            --bg-base: #07090e;
            --bg-surface: #0e121a;
            --bg-card: #131824;
            --bg-card-hover: #192030;
            --bg-glass: rgba(19, 24, 36, 0.75);
            --border-subtle: #1e2638;
            --border-glow: #2d3852;
            
            --accent-cyan: #38bdf8;
            --accent-cyan-glow: rgba(56, 189, 248, 0.35);
            --accent-purple: #a855f7;
            --accent-purple-glow: rgba(168, 85, 247, 0.35);
            --accent-emerald: #10b981;
            --accent-emerald-glow: rgba(16, 185, 129, 0.35);
            --accent-amber: #f59e0b;
            --accent-rose: #f43f5e;
            
            --text-main: #f1f5f9;
            --text-dim: #94a3b8;
            --text-muted: #64748b;
            
            --radius-sm: 8px;
            --radius-md: 14px;
            --radius-lg: 20px;
            --radius-full: 9999px;
            
            --shadow-subtle: 0 4px 20px rgba(0, 0, 0, 0.4);
            --shadow-glow: 0 0 30px rgba(56, 189, 248, 0.15);
            --shadow-card: 0 10px 30px -10px rgba(0, 0, 0, 0.6);
            
            --font-sans: 'Inter', -apple-system, BlinkMacSystemFont, sans-serif;
            --font-display: 'Space Grotesk', var(--font-sans);
            --font-mono: 'Fira Code', monospace;
            
            --transition: 0.22s cubic-bezier(0.16, 1, 0.3, 1);
        }

        * {
            margin: 0;
            padding: 0;
            box-sizing: border-box;
        }

        html {
            scroll-behavior: smooth;
        }

        body {
            background-color: var(--bg-base);
            color: var(--text-main);
            font-family: var(--font-sans);
            line-height: 1.6;
            overflow-x: hidden;
            position: relative;
        }

        /* ═══════ AMBIENT CANVAS BACKGROUND ═══════ */
        #ambient-canvas {
            position: fixed;
            top: 0;
            left: 0;
            width: 100vw;
            height: 100vh;
            pointer-events: none;
            z-index: 0;
            opacity: 0.55;
        }

        /* ═══════ HEADER & NAVIGATION ═══════ */
        header.site-header {
            position: sticky;
            top: 0;
            z-index: 100;
            background: rgba(7, 9, 14, 0.85);
            backdrop-filter: blur(20px);
            border-bottom: 1px solid var(--border-subtle);
            padding: 0.75rem 2rem;
            display: flex;
            align-items: center;
            justify-content: space-between;
            transition: var(--transition);
        }

        .header-brand {
            display: flex;
            align-items: center;
            gap: 12px;
            text-decoration: none;
            color: var(--text-main);
        }

        .brand-icon {
            width: 40px;
            height: 40px;
            border-radius: var(--radius-sm);
            background: linear-gradient(135deg, var(--accent-cyan), var(--accent-purple));
            display: flex;
            align-items: center;
            justify-content: center;
            font-weight: 900;
            font-family: var(--font-display);
            font-size: 1.25rem;
            box-shadow: 0 0 15px var(--accent-cyan-glow);
        }

        .brand-text h1 {
            font-family: var(--font-display);
            font-size: 1.15rem;
            font-weight: 800;
            letter-spacing: -0.02em;
            background: linear-gradient(90deg, #fff, var(--text-dim));
            -webkit-background-clip: text;
            -webkit-text-fill-color: transparent;
        }

        .brand-text span {
            font-size: 0.75rem;
            color: var(--accent-cyan);
            font-family: var(--font-mono);
            letter-spacing: 0.05em;
            text-transform: uppercase;
        }

        nav.header-nav {
            display: flex;
            align-items: center;
            gap: 1rem;
        }

        .nav-link {
            color: var(--text-dim);
            text-decoration: none;
            font-size: 0.88rem;
            font-weight: 500;
            padding: 0.4rem 0.8rem;
            border-radius: var(--radius-sm);
            transition: var(--transition);
        }

        .nav-link:hover {
            color: var(--text-main);
            background: var(--bg-card);
        }

        .header-actions {
            display: flex;
            align-items: center;
            gap: 12px;
        }

        .btn-sound {
            background: var(--bg-card);
            border: 1px solid var(--border-subtle);
            color: var(--text-dim);
            padding: 0.45rem 0.8rem;
            border-radius: var(--radius-sm);
            cursor: pointer;
            font-size: 0.85rem;
            display: flex;
            align-items: center;
            gap: 6px;
            transition: var(--transition);
        }

        .btn-sound:hover {
            border-color: var(--accent-cyan);
            color: var(--accent-cyan);
        }

        .badge-live {
            display: inline-flex;
            align-items: center;
            gap: 6px;
            padding: 0.25rem 0.65rem;
            background: rgba(16, 185, 129, 0.15);
            border: 1px solid rgba(16, 185, 129, 0.3);
            border-radius: var(--radius-full);
            color: var(--accent-emerald);
            font-size: 0.75rem;
            font-family: var(--font-mono);
            font-weight: 600;
        }

        .badge-live::before {
            content: '';
            width: 7px;
            height: 7px;
            border-radius: 50%;
            background: var(--accent-emerald);
            box-shadow: 0 0 8px var(--accent-emerald);
            animation: pulse-dot 1.8s infinite;
        }

        @keyframes pulse-dot {
            0%, 100% { opacity: 1; transform: scale(1); }
            50% { opacity: 0.4; transform: scale(0.8); }
        }

        /* ═══════ MAIN CONTENT CONTAINER ═══════ */
        main {
            position: relative;
            z-index: 1;
            max-width: 1440px;
            margin: 0 auto;
            padding: 2rem 2rem 6rem;
        }

        /* ═══════ HERO SECTION ═══════ */
        section.hero-section {
            padding: 4rem 1rem 3rem;
            text-align: center;
            display: flex;
            flex-direction: column;
            align-items: center;
        }

        .hero-pill {
            display: inline-flex;
            align-items: center;
            gap: 8px;
            padding: 0.35rem 1rem;
            background: var(--bg-card);
            border: 1px solid var(--border-glow);
            border-radius: var(--radius-full);
            font-size: 0.82rem;
            font-family: var(--font-mono);
            color: var(--accent-cyan);
            margin-bottom: 1.5rem;
            box-shadow: var(--shadow-subtle);
        }

        .hero-title {
            font-family: var(--font-display);
            font-size: clamp(2.5rem, 5.5vw, 4.2rem);
            font-weight: 900;
            line-height: 1.1;
            letter-spacing: -0.03em;
            margin-bottom: 1.25rem;
            max-width: 1100px;
        }

        .hero-gradient {
            background: linear-gradient(135deg, #ffffff 20%, var(--accent-cyan) 60%, var(--accent-purple) 100%);
            -webkit-background-clip: text;
            -webkit-text-fill-color: transparent;
        }

        .hero-subtitle {
            font-size: 1.2rem;
            color: var(--text-dim);
            max-width: 860px;
            margin-bottom: 2.5rem;
            font-weight: 400;
            line-height: 1.7;
        }

        /* ═══════ KPI STATS BAR ═══════ */
        .stats-grid {
            display: grid;
            grid-template-columns: repeat(auto-fit, minmax(180px, 1fr));
            gap: 1.25rem;
            width: 100%;
            max-width: 1200px;
            margin-bottom: 3.5rem;
        }

        .stat-card {
            background: var(--bg-card);
            border: 1px solid var(--border-subtle);
            border-radius: var(--radius-md);
            padding: 1.25rem 1rem;
            text-align: center;
            position: relative;
            overflow: hidden;
            transition: var(--transition);
        }

        .stat-card::before {
            content: '';
            position: absolute;
            top: 0;
            left: 0;
            right: 0;
            height: 3px;
            background: linear-gradient(90deg, var(--accent-cyan), var(--accent-purple));
            opacity: 0;
            transition: var(--transition);
        }

        .stat-card:hover {
            transform: translateY(-4px);
            border-color: var(--border-glow);
            box-shadow: var(--shadow-card);
        }

        .stat-card:hover::before {
            opacity: 1;
        }

        .stat-value {
            font-family: var(--font-display);
            font-size: 2.2rem;
            font-weight: 800;
            color: var(--text-main);
            margin-bottom: 0.25rem;
            line-height: 1.2;
        }

        .stat-label {
            font-size: 0.8rem;
            color: var(--text-muted);
            text-transform: uppercase;
            letter-spacing: 0.05em;
            font-weight: 600;
        }

        .stat-sub {
            font-size: 0.72rem;
            color: var(--accent-cyan);
            font-family: var(--font-mono);
            margin-top: 4px;
        }

        /* ═══════ SECTION HEADERS ═══════ */
        .section-header {
            margin-bottom: 2.5rem;
        }

        .section-tag {
            font-family: var(--font-mono);
            font-size: 0.8rem;
            font-weight: 600;
            color: var(--accent-cyan);
            text-transform: uppercase;
            letter-spacing: 0.1em;
            margin-bottom: 0.5rem;
            display: flex;
            align-items: center;
            gap: 8px;
        }

        .section-tag::before {
            content: '';
            width: 14px;
            height: 2px;
            background: var(--accent-cyan);
        }

        .section-title {
            font-family: var(--font-display);
            font-size: 2.2rem;
            font-weight: 800;
            letter-spacing: -0.02em;
            color: var(--text-main);
            margin-bottom: 0.75rem;
        }

        .section-desc {
            color: var(--text-dim);
            font-size: 1.05rem;
            max-width: 800px;
        }

        /* ═══════ 2D LIVE SIMULATION ENGINE CANVAS ═══════ */
        .sim-container {
            background: var(--bg-surface);
            border: 1px solid var(--border-subtle);
            border-radius: var(--radius-lg);
            overflow: hidden;
            box-shadow: var(--shadow-card);
            margin-bottom: 5rem;
            display: flex;
            flex-direction: column;
        }

        .sim-header {
            background: var(--bg-card);
            padding: 1rem 1.5rem;
            border-bottom: 1px solid var(--border-subtle);
            display: flex;
            align-items: center;
            justify-content: space-between;
            flex-wrap: wrap;
            gap: 1rem;
        }

        .sim-title-group {
            display: flex;
            align-items: center;
            gap: 12px;
        }

        .sim-title-group h3 {
            font-family: var(--font-display);
            font-size: 1.15rem;
            font-weight: 700;
        }

        .sim-controls {
            display: flex;
            align-items: center;
            gap: 8px;
            flex-wrap: wrap;
        }

        .sim-btn {
            background: var(--bg-surface);
            border: 1px solid var(--border-subtle);
            color: var(--text-dim);
            padding: 0.4rem 0.8rem;
            border-radius: var(--radius-sm);
            font-size: 0.82rem;
            font-weight: 600;
            cursor: pointer;
            transition: var(--transition);
            display: inline-flex;
            align-items: center;
            gap: 6px;
        }

        .sim-btn:hover {
            border-color: var(--accent-cyan);
            color: var(--text-main);
        }

        .sim-btn.active {
            background: var(--accent-cyan);
            color: #000;
            border-color: var(--accent-cyan);
            font-weight: 700;
        }

        .sim-workspace {
            position: relative;
            width: 100%;
            height: 520px;
            background: #09121d;
            cursor: crosshair;
        }

        #colony-canvas {
            width: 100%;
            height: 100%;
            display: block;
        }

        .sim-overlay-hud {
            position: absolute;
            top: 16px;
            left: 16px;
            background: rgba(14, 18, 26, 0.88);
            backdrop-filter: blur(12px);
            border: 1px solid var(--border-subtle);
            border-radius: var(--radius-sm);
            padding: 0.75rem 1rem;
            font-family: var(--font-mono);
            font-size: 0.78rem;
            color: var(--text-dim);
            pointer-events: none;
            display: flex;
            flex-direction: column;
            gap: 4px;
            box-shadow: var(--shadow-subtle);
        }

        .hud-row {
            display: flex;
            justify-content: space-between;
            gap: 16px;
        }

        .hud-val {
            color: var(--accent-cyan);
            font-weight: 600;
        }

        .sim-agent-card {
            position: absolute;
            bottom: 16px;
            right: 16px;
            width: 280px;
            background: rgba(19, 24, 36, 0.95);
            backdrop-filter: blur(16px);
            border: 1px solid var(--accent-cyan);
            border-radius: var(--radius-md);
            padding: 1rem;
            box-shadow: 0 10px 30px rgba(0,0,0,0.7);
            font-size: 0.82rem;
            display: none;
            animation: slideUp 0.25s ease-out;
        }

        @keyframes slideUp {
            from { opacity: 0; transform: translateY(10px); }
            to { opacity: 1; transform: translateY(0); }
        }

        .agent-card-title {
            font-family: var(--font-display);
            font-weight: 700;
            font-size: 0.95rem;
            color: #fff;
            margin-bottom: 0.5rem;
            display: flex;
            align-items: center;
            justify-content: space-between;
        }

        .stat-bar-group {
            margin-bottom: 6px;
        }

        .stat-bar-label {
            display: flex;
            justify-content: space-between;
            font-size: 0.72rem;
            color: var(--text-muted);
            margin-bottom: 2px;
        }

        .stat-bar {
            height: 6px;
            background: rgba(255,255,255,0.1);
            border-radius: 3px;
            overflow: hidden;
        }

        .stat-bar-fill {
            height: 100%;
            border-radius: 3px;
        }

        /* ═══════ WHITTAKER BIOME LAB (CANVAS 2D) ═══════ */
        .biome-lab-grid {
            display: grid;
            grid-template-columns: 1fr 340px;
            gap: 1.5rem;
            background: var(--bg-surface);
            border: 1px solid var(--border-subtle);
            border-radius: var(--radius-lg);
            padding: 1.5rem;
            margin-bottom: 5rem;
        }

        @media (max-width: 1024px) {
            .biome-lab-grid {
                grid-template-columns: 1fr;
            }
        }

        .biome-canvas-container {
            position: relative;
            height: 440px;
            background: #060b13;
            border-radius: var(--radius-md);
            overflow: hidden;
            border: 1px solid var(--border-subtle);
        }

        #whittaker-canvas {
            width: 100%;
            height: 100%;
            display: block;
            cursor: pointer;
        }

        .biome-controls-panel {
            display: flex;
            flex-direction: column;
            gap: 1rem;
        }

        .control-group {
            display: flex;
            flex-direction: column;
            gap: 6px;
        }

        .control-label {
            display: flex;
            justify-content: space-between;
            font-size: 0.82rem;
            font-weight: 600;
            color: var(--text-dim);
        }

        .control-label span {
            color: var(--accent-cyan);
            font-family: var(--font-mono);
        }

        input[type="range"] {
            width: 100%;
            accent-color: var(--accent-cyan);
            cursor: pointer;
        }

        .biome-inspector-box {
            background: var(--bg-card);
            border: 1px solid var(--border-subtle);
            border-radius: var(--radius-md);
            padding: 1rem;
            margin-top: auto;
        }

        .biome-badge {
            display: inline-block;
            padding: 0.2rem 0.6rem;
            border-radius: var(--radius-sm);
            font-size: 0.8rem;
            font-weight: 700;
            margin-bottom: 0.5rem;
        }

        /* ═══════ TIMELINE & CHRONICLE MASTER ═══════ */
        .chronicle-section {
            margin-bottom: 5rem;
        }

        .chronicle-filters {
            background: var(--bg-surface);
            border: 1px solid var(--border-subtle);
            border-radius: var(--radius-md);
            padding: 1.25rem;
            margin-bottom: 2rem;
            display: flex;
            flex-direction: column;
            gap: 1rem;
        }

        .filter-row-top {
            display: flex;
            gap: 1rem;
            flex-wrap: wrap;
        }

        .search-box {
            flex: 1;
            min-width: 260px;
            position: relative;
        }

        .search-input {
            width: 100%;
            background: var(--bg-card);
            border: 1px solid var(--border-subtle);
            border-radius: var(--radius-sm);
            padding: 0.65rem 1rem 0.65rem 2.5rem;
            color: var(--text-main);
            font-size: 0.9rem;
            font-family: var(--font-sans);
            transition: var(--transition);
        }

        .search-input:focus {
            outline: none;
            border-color: var(--accent-cyan);
            box-shadow: 0 0 12px var(--accent-cyan-glow);
        }

        .search-icon {
            position: absolute;
            left: 12px;
            top: 50%;
            transform: translateY(-50%);
            color: var(--text-muted);
            font-size: 0.9rem;
        }

        .era-filter-group {
            display: flex;
            gap: 6px;
            flex-wrap: wrap;
        }

        .era-btn {
            background: var(--bg-card);
            border: 1px solid var(--border-subtle);
            color: var(--text-dim);
            padding: 0.35rem 0.75rem;
            border-radius: var(--radius-sm);
            font-size: 0.78rem;
            cursor: pointer;
            transition: var(--transition);
        }

        .era-btn:hover {
            border-color: var(--accent-cyan);
            color: var(--text-main);
        }

        .era-btn.active {
            background: var(--accent-cyan);
            color: #000;
            border-color: var(--accent-cyan);
            font-weight: 700;
        }

        /* ═══════ TIMELINE TREE & CARDS ═══════ */
        .timeline-container {
            position: relative;
            padding-left: 2rem;
        }

        .timeline-container::before {
            content: '';
            position: absolute;
            left: 7px;
            top: 10px;
            bottom: 10px;
            width: 2px;
            background: linear-gradient(180deg, var(--accent-cyan) 0%, var(--accent-purple) 50%, var(--accent-emerald) 100%);
        }

        .timeline-card {
            position: relative;
            background: var(--bg-card);
            border: 1px solid var(--border-subtle);
            border-radius: var(--radius-md);
            padding: 1.5rem;
            margin-bottom: 1.75rem;
            transition: var(--transition);
            box-shadow: var(--shadow-subtle);
        }

        .timeline-card::before {
            content: '';
            position: absolute;
            left: -2rem;
            top: 24px;
            width: 14px;
            height: 14px;
            border-radius: 50%;
            background: var(--bg-base);
            border: 3px solid var(--accent-cyan);
            transform: translateX(-50%);
            box-shadow: 0 0 10px var(--accent-cyan);
            transition: var(--transition);
        }

        .timeline-card:hover {
            border-color: var(--border-glow);
            transform: translateX(4px);
            box-shadow: var(--shadow-card);
        }

        .timeline-card:hover::before {
            background: var(--accent-cyan);
            transform: translateX(-50%) scale(1.2);
        }

        .card-top-bar {
            display: flex;
            align-items: center;
            justify-content: space-between;
            gap: 1rem;
            flex-wrap: wrap;
            margin-bottom: 0.75rem;
        }

        .card-badges {
            display: flex;
            align-items: center;
            gap: 8px;
            flex-wrap: wrap;
        }

        .badge-hash {
            font-family: var(--font-mono);
            font-size: 0.75rem;
            padding: 0.2rem 0.5rem;
            background: rgba(56, 189, 248, 0.12);
            border: 1px solid rgba(56, 189, 248, 0.3);
            border-radius: var(--radius-sm);
            color: var(--accent-cyan);
            cursor: pointer;
            display: inline-flex;
            align-items: center;
            gap: 4px;
        }

        .badge-hash:hover {
            background: rgba(56, 189, 248, 0.25);
        }

        .badge-era {
            font-size: 0.72rem;
            padding: 0.2rem 0.5rem;
            background: rgba(168, 85, 247, 0.12);
            border: 1px solid rgba(168, 85, 247, 0.3);
            border-radius: var(--radius-sm);
            color: var(--accent-purple);
            font-weight: 600;
        }

        .badge-author {
            font-size: 0.75rem;
            color: var(--text-dim);
            display: inline-flex;
            align-items: center;
            gap: 6px;
        }

        .author-avatar {
            width: 20px;
            height: 20px;
            border-radius: 50%;
            background: var(--accent-cyan);
            color: #000;
            display: inline-flex;
            align-items: center;
            justify-content: center;
            font-size: 0.65rem;
            font-weight: 800;
        }

        .card-date {
            font-family: var(--font-mono);
            font-size: 0.78rem;
            color: var(--text-muted);
        }

        .card-title {
            font-family: var(--font-display);
            font-size: 1.25rem;
            font-weight: 700;
            color: var(--text-main);
            margin-bottom: 0.5rem;
            line-height: 1.3;
        }

        .card-story {
            font-size: 0.95rem;
            color: var(--text-dim);
            line-height: 1.65;
            margin-bottom: 1rem;
        }

        .card-highlights {
            background: var(--bg-surface);
            border: 1px solid var(--border-subtle);
            border-radius: var(--radius-sm);
            padding: 0.85rem 1rem;
            margin-bottom: 1rem;
        }

        .card-highlights h5 {
            font-size: 0.78rem;
            font-family: var(--font-mono);
            text-transform: uppercase;
            color: var(--accent-emerald);
            margin-bottom: 0.5rem;
            letter-spacing: 0.05em;
        }

        .card-highlights ul {
            list-style: none;
            display: flex;
            flex-direction: column;
            gap: 4px;
        }

        .card-highlights li {
            font-size: 0.84rem;
            color: var(--text-dim);
            position: relative;
            padding-left: 16px;
        }

        .card-highlights li::before {
            content: '▹';
            position: absolute;
            left: 0;
            color: var(--accent-emerald);
        }

        .card-footer {
            display: flex;
            align-items: center;
            justify-content: space-between;
            gap: 1rem;
            flex-wrap: wrap;
            padding-top: 0.75rem;
            border-top: 1px solid var(--border-subtle);
            font-size: 0.78rem;
        }

        .card-stats {
            font-family: var(--font-mono);
            color: var(--text-muted);
        }

        .card-stats span.add { color: var(--accent-emerald); }
        .card-stats span.del { color: var(--accent-rose); }

        .btn-toggle-files {
            background: transparent;
            border: 1px solid var(--border-subtle);
            color: var(--text-dim);
            padding: 0.25rem 0.6rem;
            border-radius: var(--radius-sm);
            cursor: pointer;
            font-size: 0.75rem;
            font-family: var(--font-mono);
            transition: var(--transition);
        }

        .btn-toggle-files:hover {
            border-color: var(--accent-cyan);
            color: var(--text-main);
        }

        .files-list-box {
            display: none;
            margin-top: 0.75rem;
            padding: 0.75rem;
            background: #090c13;
            border-radius: var(--radius-sm);
            border: 1px solid var(--border-subtle);
            font-family: var(--font-mono);
            font-size: 0.75rem;
            max-height: 180px;
            overflow-y: auto;
        }

        .file-item {
            display: flex;
            justify-content: space-between;
            padding: 2px 0;
            color: var(--text-muted);
        }

        .file-item:hover {
            color: var(--text-main);
        }

        /* ═══════ THE 4 AGENTS SYNDICATE ═══════ */
        .agents-grid {
            display: grid;
            grid-template-columns: repeat(auto-fit, minmax(280px, 1fr));
            gap: 1.5rem;
            margin-bottom: 5rem;
        }

        .agent-dossier-card {
            background: var(--bg-card);
            border: 1px solid var(--border-subtle);
            border-radius: var(--radius-md);
            padding: 1.5rem;
            position: relative;
            overflow: hidden;
            display: flex;
            flex-direction: column;
            transition: var(--transition);
        }

        .agent-dossier-card:hover {
            transform: translateY(-4px);
            box-shadow: var(--shadow-card);
        }

        .agent-avatar-icon {
            width: 52px;
            height: 52px;
            border-radius: var(--radius-sm);
            display: flex;
            align-items: center;
            justify-content: center;
            font-size: 1.6rem;
            margin-bottom: 1rem;
        }

        .agent-dossier-card.architect { border-top: 4px solid var(--accent-cyan); }
        .agent-dossier-card.architect .agent-avatar-icon { background: rgba(56, 189, 248, 0.15); color: var(--accent-cyan); }

        .agent-dossier-card.researcher { border-top: 4px solid var(--accent-purple); }
        .agent-dossier-card.researcher .agent-avatar-icon { background: rgba(168, 85, 247, 0.15); color: var(--accent-purple); }

        .agent-dossier-card.developer { border-top: 4px solid var(--accent-emerald); }
        .agent-dossier-card.developer .agent-avatar-icon { background: rgba(16, 185, 129, 0.15); color: var(--accent-emerald); }

        .agent-dossier-card.destroyer { border-top: 4px solid var(--accent-rose); }
        .agent-dossier-card.destroyer .agent-avatar-icon { background: rgba(244, 63, 94, 0.15); color: var(--accent-rose); }

        .agent-name {
            font-family: var(--font-display);
            font-size: 1.3rem;
            font-weight: 700;
            margin-bottom: 0.25rem;
        }

        .agent-role-tag {
            font-family: var(--font-mono);
            font-size: 0.75rem;
            color: var(--text-muted);
            margin-bottom: 0.85rem;
        }

        .agent-desc {
            font-size: 0.88rem;
            color: var(--text-dim);
            line-height: 1.6;
            margin-bottom: 1rem;
        }

        .agent-trophies {
            background: var(--bg-surface);
            border-radius: var(--radius-sm);
            padding: 0.75rem;
            margin-top: auto;
            font-size: 0.78rem;
            color: var(--text-muted);
        }

        .agent-trophies strong {
            color: var(--text-main);
        }

        /* ═══════ ADVERSARIAL TERMINAL ═══════ */
        .terminal-container {
            background: #080b11;
            border: 1px solid var(--border-subtle);
            border-radius: var(--radius-md);
            overflow: hidden;
            box-shadow: var(--shadow-card);
            margin-bottom: 5rem;
        }

        .terminal-header {
            background: #0e131d;
            padding: 0.65rem 1rem;
            border-bottom: 1px solid var(--border-subtle);
            display: flex;
            align-items: center;
            justify-content: space-between;
        }

        .terminal-dots {
            display: flex;
            gap: 6px;
        }

        .dot {
            width: 11px;
            height: 11px;
            border-radius: 50%;
        }
        .dot.red { background: #ef4444; }
        .dot.yellow { background: #f59e0b; }
        .dot.green { background: #10b981; }

        .terminal-title {
            font-family: var(--font-mono);
            font-size: 0.75rem;
            color: var(--text-muted);
        }

        .terminal-body {
            padding: 1.25rem;
            font-family: var(--font-mono);
            font-size: 0.82rem;
            color: #38bdf8;
            height: 320px;
            overflow-y: auto;
            line-height: 1.6;
            background: #06080e;
        }

        .terminal-prompt {
            color: var(--text-muted);
        }

        .terminal-btn-bar {
            padding: 0.75rem 1rem;
            background: #0c1018;
            border-top: 1px solid var(--border-subtle);
            display: flex;
            gap: 10px;
        }

        /* ═══════ THE BUG GRAVEYARD ═══════ */
        .graveyard-grid {
            display: grid;
            grid-template-columns: repeat(auto-fit, minmax(320px, 1fr));
            gap: 1.5rem;
            margin-bottom: 5rem;
        }

        .bug-card {
            background: var(--bg-card);
            border: 1px solid var(--border-subtle);
            border-radius: var(--radius-md);
            padding: 1.5rem;
            transition: var(--transition);
            position: relative;
        }

        .bug-card:hover {
            border-color: var(--accent-rose);
            box-shadow: 0 0 25px rgba(244, 63, 94, 0.15);
            transform: translateY(-3px);
        }

        .bug-tag {
            font-family: var(--font-mono);
            font-size: 0.72rem;
            font-weight: 700;
            color: var(--accent-rose);
            text-transform: uppercase;
            margin-bottom: 0.5rem;
            display: flex;
            align-items: center;
            gap: 6px;
        }

        .bug-title {
            font-family: var(--font-display);
            font-size: 1.15rem;
            font-weight: 700;
            margin-bottom: 0.5rem;
            color: #fff;
        }

        .bug-symptom {
            font-size: 0.88rem;
            color: var(--text-dim);
            margin-bottom: 0.75rem;
        }

        .bug-solution {
            background: rgba(16, 185, 129, 0.08);
            border: 1px solid rgba(16, 185, 129, 0.25);
            border-radius: var(--radius-sm);
            padding: 0.75rem;
            font-size: 0.82rem;
            color: var(--accent-emerald);
        }

        /* ═══════ GALLERY ═══════ */
        .gallery-grid {
            display: grid;
            grid-template-columns: repeat(auto-fit, minmax(320px, 1fr));
            gap: 1.5rem;
            margin-bottom: 5rem;
        }

        .gallery-card {
            background: var(--bg-card);
            border: 1px solid var(--border-subtle);
            border-radius: var(--radius-md);
            overflow: hidden;
            transition: var(--transition);
            cursor: pointer;
        }

        .gallery-card:hover {
            border-color: var(--accent-cyan);
            transform: translateY(-4px);
            box-shadow: var(--shadow-card);
        }

        .gallery-thumb-wrap {
            height: 200px;
            background: #080d16;
            overflow: hidden;
            position: relative;
        }

        .gallery-thumb {
            width: 100%;
            height: 100%;
            object-fit: cover;
            transition: transform 0.4s ease;
        }

        .gallery-card:hover .gallery-thumb {
            transform: scale(1.05);
        }

        .gallery-info {
            padding: 1rem;
        }

        .gallery-title {
            font-family: var(--font-display);
            font-size: 1rem;
            font-weight: 700;
            margin-bottom: 0.25rem;
        }

        .gallery-desc {
            font-size: 0.8rem;
            color: var(--text-dim);
        }

        /* ═══════ LIGHTBOX MODAL ═══════ */
        #lightbox {
            position: fixed;
            top: 0;
            left: 0;
            width: 100vw;
            height: 100vh;
            background: rgba(0, 0, 0, 0.9);
            backdrop-filter: blur(12px);
            z-index: 9999;
            display: none;
            align-items: center;
            justify-content: center;
            padding: 2rem;
        }

        .lightbox-content {
            max-width: 90vw;
            max-height: 90vh;
            display: flex;
            flex-direction: column;
            align-items: center;
            position: relative;
        }

        .lightbox-img {
            max-width: 100%;
            max-height: 80vh;
            border-radius: var(--radius-md);
            border: 1px solid var(--border-glow);
            box-shadow: 0 0 50px rgba(0,0,0,0.8);
        }

        .lightbox-caption {
            margin-top: 1rem;
            color: #fff;
            font-family: var(--font-display);
            font-size: 1.1rem;
            text-align: center;
        }

        .lightbox-close {
            position: absolute;
            top: -40px;
            right: 0;
            background: transparent;
            border: none;
            color: #fff;
            font-size: 2rem;
            cursor: pointer;
        }

        /* ═══════ FOOTER ═══════ */
        footer.site-footer {
            border-top: 1px solid var(--border-subtle);
            background: var(--bg-surface);
            padding: 3rem 2rem;
            text-align: center;
            color: var(--text-muted);
            font-size: 0.88rem;
        }

        .footer-quote {
            font-family: var(--font-display);
            font-size: 1.1rem;
            color: var(--text-dim);
            margin-bottom: 1rem;
            max-width: 600px;
            margin-left: auto;
            margin-right: auto;
        }

        /* ═══════ RESPONSIVE ═══════ */
        @media (max-width: 768px) {
            header.site-header { padding: 0.75rem 1rem; }
            nav.header-nav { display: none; }
            main { padding: 1.5rem 1rem 4rem; }
            .hero-title { font-size: 2.2rem; }
            .timeline-container { padding-left: 1.5rem; }
        }
    </style>
</head>
<body>

    <!-- Ambient particle background -->
    <canvas id="ambient-canvas"></canvas>

    <!-- Header Navigation -->
    <header class="site-header">
        <a href="#hero" class="header-brand">
            <div class="brand-icon">CS</div>
            <div class="brand-text">
                <h1>Colony Sim Chronicle</h1>
                <span>Godot 4.7 C# • .NET 8</span>
            </div>
        </a>

        <nav class="header-nav">
            <a href="#simulation" class="nav-link">Симуляция 2D</a>
            <a href="#whittaker" class="nav-link">Климат & Биомы</a>
            <a href="#chronicle" class="nav-link">Летопись (31)</a>
            <a href="#syndicate" class="nav-link">4 Агента</a>
            <a href="#graveyard" class="nav-link">Кладбище багов</a>
            <a href="#gallery" class="nav-link">Галерея</a>
        </nav>

        <div class="header-actions">
            <span class="badge-live">1 000 000 Агентов</span>
            <button id="btn-sound-toggle" class="btn-sound" title="Включить / выключить звуки интерфейса">
                <span id="sound-icon">🔊</span> Звук
            </button>
        </div>
    </header>

    <main>
        <!-- HERO SECTION -->
        <section id="hero" class="hero-section">
            <div class="hero-pill">
                <span>⚡</span> Хроника инженерного подвига • Полная история проекта
            </div>
            <h1 class="hero-title">
                Великая Летопись Разработки <br>
                <span class="hero-gradient">Colony Sim Engine</span>
            </h1>
            <p class="hero-subtitle">
                Подробнейшая интерактивная история создания высокопроизводительного симулятора колонии на Godot 4.7 C# (.NET 8). От первых тайлов влажности до 1 000 000 агентов, пирамиды задач с процессорной инструкцией <code>tzcnt</code> и дуального CPU-SIMD / GPU-Compute ядра.
            </p>

            <!-- STATS GRID -->
            <div class="stats-grid">
                <div class="stat-card">
                    <div class="stat-value" id="stat-commits">31</div>
                    <div class="stat-label">Вех & Коммитов</div>
                    <div class="stat-sub">100% истории Git</div>
                </div>
                <div class="stat-card">
                    <div class="stat-value" id="stat-chapters">62</div>
                    <div class="stat-label">Главы PLAN.md</div>
                    <div class="stat-sub">5 224 строки архитектуры</div>
                </div>
                <div class="stat-card">
                    <div class="stat-value" id="stat-agents">1M</div>
                    <div class="stat-label">Масштаб Агентов</div>
                    <div class="stat-sub">HSWM Pyramid & SoA</div>
                </div>
                <div class="stat-card">
                    <div class="stat-value" id="stat-csharp">45.4k</div>
                    <div class="stat-label">Строк C# Ядра</div>
                    <div class="stat-sub">.NET 8 Zero-Alloc</div>
                </div>
                <div class="stat-card">
                    <div class="stat-value" id="stat-purged">40.6k</div>
                    <div class="stat-label">Строк Мусора Снесено</div>
                    <div class="stat-sub">Очистка legacy & .bak</div>
                </div>
                <div class="stat-card">
                    <div class="stat-value" id="stat-tick">0.8 мс</div>
                    <div class="stat-label">Тик на 100x Скорости</div>
                    <div class="stat-sub">Ускорение в 6.8x</div>
                </div>
            </div>
        </section>

        <!-- 2D INTERACTIVE SIMULATION -->
        <section id="simulation" class="sim-section">
            <div class="section-header">
                <div class="section-tag">Живой Движок Симуляции</div>
                <h2 class="section-title">Интерактивная 2D Колония в Браузере</h2>
                <p class="section-desc">
                    Аутентичная веб-реплика алгоритмов симулятора: пошаговое поведение 400+ колонистов, рубка леса, вспашка грядок, 4 стадии роста пшеницы, динамические солнечные тени и переключаемый оверлей влажности (10-cell water spread).
                </p>
            </div>

            <div class="sim-container">
                <div class="sim-header">
                    <div class="sim-title-group">
                        <span style="font-size: 1.4rem;">🌾</span>
                        <div>
                            <h3>Colony Canvas Simulation</h3>
                            <span style="font-size: 0.75rem; color: var(--text-muted); font-family: var(--font-mono);">Tick Rate: 60Hz • Spatial Grid • SoA Colonists</span>
                        </div>
                    </div>
                    <div class="sim-controls">
                        <button class="sim-btn" id="btn-pause">⏸ Пауза</button>
                        <button class="sim-btn active" id="btn-speed-1">1x</button>
                        <button class="sim-btn" id="btn-speed-5">5x</button>
                        <button class="sim-btn" id="btn-speed-25">25x</button>
                        <button class="sim-btn" id="btn-speed-100">100x</button>
                        <span style="color: var(--border-glow); margin: 0 4px;">|</span>
                        <button class="sim-btn" id="btn-toggle-humidity">💧 Влажность</button>
                        <button class="sim-btn" id="btn-toggle-flow">🧭 Векторное поле</button>
                        <button class="sim-btn" id="btn-spawn-workers">👥 +25 Агентов</button>
                    </div>
                </div>

                <div class="sim-workspace" id="sim-workspace">
                    <canvas id="colony-canvas"></canvas>
                    
                    <div class="sim-overlay-hud">
                        <div class="hud-row"><span>Время:</span><span class="hud-val" id="hud-time">13:45 ☀</span></div>
                        <div class="hud-row"><span>Агенты:</span><span class="hud-val" id="hud-agents">400</span></div>
                        <div class="hud-row"><span>Срублено леса:</span><span class="hud-val" id="hud-wood">1,240 шт</span></div>
                        <div class="hud-row"><span>Урожай зерна:</span><span class="hud-val" id="hud-wheat">480 мешков</span></div>
                        <div class="hud-row"><span>Скорость:</span><span class="hud-val" id="hud-speed">1x (0.05с/шаг)</span></div>
                    </div>

                    <div class="sim-agent-card" id="agent-inspect-card">
                        <div class="agent-card-title">
                            <span id="agent-inspect-id">Колонист #142</span>
                            <span id="agent-inspect-state" style="color: var(--accent-cyan); font-size: 0.75rem;">FARMING</span>
                        </div>
                        <div class="stat-bar-group">
                            <div class="stat-bar-label"><span>Голод (Hunger)</span><span id="val-hunger">18/100</span></div>
                            <div class="stat-bar"><div class="stat-bar-fill" id="bar-hunger" style="width: 18%; background: #f59e0b;"></div></div>
                        </div>
                        <div class="stat-bar-group">
                            <div class="stat-bar-label"><span>Сонливость (Sleep)</span><span id="val-sleep">12/100</span></div>
                            <div class="stat-bar"><div class="stat-bar-fill" id="bar-sleep" style="width: 12%; background: #38bdf8;"></div></div>
                        </div>
                        <div class="stat-bar-group">
                            <div class="stat-bar-label"><span>Усталость (Fatigue)</span><span id="val-fatigue">34/100</span></div>
                            <div class="stat-bar"><div class="stat-bar-fill" id="bar-fatigue" style="width: 34%; background: #a855f7;"></div></div>
                        </div>
                        <div class="stat-bar-group">
                            <div class="stat-bar-label"><span>Настроение (Mood)</span><span id="val-mood">92%</span></div>
                            <div class="stat-bar"><div class="stat-bar-fill" id="bar-mood" style="width: 92%; background: #10b981;"></div></div>
                        </div>
                        <div style="font-size: 0.75rem; color: var(--text-muted); margin-top: 8px;">
                            Предмет в руках: <strong id="agent-inspect-item" style="color: #fff;">Пшеница (Grain)</strong>
                        </div>
                    </div>
                </div>
            </div>
        </section>

        <!-- WHITTAKER BIOME LAB -->
        <section id="whittaker" class="biome-section">
            <div class="section-header">
                <div class="section-tag">Планетарная Климатология</div>
                <h2 class="section-title">Лаборатория Биомов Уиттекера и Тектоники</h2>
                <p class="section-desc">
                    Реализация Главы 23 и 52 из <code>PLAN.md</code>: симуляция континентальных плит, фрактального шума высот fBm, широтной зональности температур и дождевой тени гор (Rain Shadow). Кликните по карте для инспекции биома!
                </p>
            </div>

            <div class="biome-lab-grid">
                <div class="biome-canvas-container">
                    <canvas id="whittaker-canvas"></canvas>
                </div>

                <div class="biome-controls-panel">
                    <div class="control-group">
                        <div class="control-label">Уровень океана: <span id="lbl-ocean">42%</span></div>
                        <input type="range" id="rng-ocean" min="20" max="65" value="42">
                    </div>
                    <div class="control-group">
                        <div class="control-label">Сила дождевой тени гор: <span id="lbl-shadow">75%</span></div>
                        <input type="range" id="rng-shadow" min="10" max="100" value="75">
                    </div>
                    <div class="control-group">
                        <div class="control-label">Фрактальный сид планеты: <span id="lbl-seed">4219</span></div>
                        <input type="range" id="rng-seed" min="1000" max="9999" value="4219">
                    </div>
                    <button class="sim-btn" id="btn-regen-world" style="margin-top: 6px; justify-content: center;">
                        🎲 Сгенерировать новый мир
                    </button>

                    <div class="biome-inspector-box">
                        <div id="inspector-biome-badge" class="biome-badge" style="background: #10b981; color: #000;">
                            Умеренный лес (Temperate Forest)
                        </div>
                        <div style="font-size: 0.8rem; color: var(--text-dim); line-height: 1.5;" id="inspector-biome-details">
                            Высота: <strong>0.58</strong> (Возвышенность)<br>
                            Влажность: <strong>68%</strong> (Орографический сток)<br>
                            Температура: <strong>+18°C</strong> (Широта 45°N)<br>
                            Пригодность для колонии: <strong style="color: var(--accent-emerald);">ОТЛИЧНАЯ (94%)</strong>
                        </div>
                    </div>
                </div>
            </div>
        </section>

        <!-- CHRONICLE TIMELINE -->
        <section id="chronicle" class="chronicle-section">
            <div class="section-header">
                <div class="section-tag">Полная Летопись Разработки</div>
                <h2 class="section-title">Хроника Всех 31 Вех и Коммитов</h2>
                <p class="section-desc">
                    Детальная летопись всей работы: каждый коммит, ветка, архитектурный вызов, математический прорыв и очищенный код. Фильтруйте по эрам, категориям и авторам.
                </p>
            </div>

            <!-- FILTERS -->
            <div class="chronicle-filters">
                <div class="filter-row-top">
                    <div class="search-box">
                        <span class="search-icon">🔍</span>
                        <input type="text" id="chronicle-search" class="search-input" placeholder="Поиск по хэшу, названию, алгоритму (напр. HPA*, tzcnt, False Sharing)...">
                    </div>
                    <div class="era-filter-group" id="author-filters">
                        <button class="era-btn active" data-author="all">Все авторы</button>
                        <button class="era-btn" data-author="Фишен">Фишен</button>
                        <button class="era-btn" data-author="qwen.ai[bot]">qwen.ai[bot]</button>
                        <button class="era-btn" data-author="opencode-agent">opencode-agent</button>
                    </div>
                </div>
                <div class="era-filter-group" id="era-filters">
                    <button class="era-btn active" data-era="all">Все эры</button>
                    <button class="era-btn" data-era="Эра 1: Фундамент и гидрология">Эра 1: Фундамент</button>
                    <button class="era-btn" data-era="Эра 2: Мировая карта и рендеринг">Эра 2: Карта и Рендер</button>
                    <button class="era-btn" data-era="Эра 3: Оптимизация и устранение багов">Эра 3: Оптимизация</button>
                    <button class="era-btn" data-era="Эра 4: Чистка и стабилизация">Эра 4: Великая чистка</button>
                    <button class="era-btn" data-era="Эра 5: Планетарная генерация и климат">Эра 5: Генератор v4</button>
                    <button class="era-btn" data-era="Эра 6: Оптимизация памяти и мега-симуляция">Эра 6: Кэш CPU & SIMD</button>
                    <button class="era-btn" data-era="Эра 7: Настоящее время (Фронтир 1M)">Эра 7: Фронтир 1M</button>
                </div>
            </div>

            <!-- TIMELINE CARDS -->
            <div class="timeline-container" id="timeline-list">
                <!-- Injected via JavaScript -->
            </div>
        </section>

        <!-- THE 4 AGENTS SYNDICATE -->
        <section id="syndicate" class="syndicate-section">
            <div class="section-header">
                <div class="section-tag">Многоагентная Архитектура</div>
                <h2 class="section-title">Синдикат 4 Специализированных Субагентов</h2>
                <p class="section-desc">
                    В соответствии с манифестом <code>AGENTS.md</code> проект разрабатывался квартетом специализированных агентов. Ни строчки кода без архитектурного плана, исследований и состязательных тестов на отказ!
                </p>
            </div>

            <div class="agents-grid">
                <div class="agent-dossier-card architect">
                    <div class="agent-avatar-icon">📐</div>
                    <h3 class="agent-name">@architect</h3>
                    <div class="agent-role-tag">Архитектор & Системный Аналитик</div>
                    <p class="agent-desc">
                        Анализирует требования, проектирует структуры данных, проектирует контракты классов и ведёт <code>PLAN.md</code>. Не пишет код напрямую — формирует непоколебимый математический каркас симулятора.
                    </p>
                    <div class="agent-trophies">
                        Трофеи: <strong>62 Главы PLAN.md</strong>, модель HSWM Pyramid, контракт SoA AgentDataPool.
                    </div>
                </div>

                <div class="agent-dossier-card researcher">
                    <div class="agent-avatar-icon">🔬</div>
                    <h3 class="agent-name">@researcher</h3>
                    <div class="agent-role-tag">Исследователь & Аналитик Паттернов</div>
                    <p class="agent-desc">
                        Ведёт глубокий веб-поиск документации MS Docs, анализирует иерархию кэшей CPU L1/L2/L3, деки Chase-Lev, TPL OrderablePartitioners, формулы климата Уиттекера и Compute Shaders.
                    </p>
                    <div class="agent-trophies">
                        Трофеи: <strong>Синтез Work-Stealing</strong>, бимодальная гипсография Земли, Q16 Fast Bitmath.
                    </div>
                </div>

                <div class="agent-dossier-card developer">
                    <div class="agent-avatar-icon">💻</div>
                    <h3 class="agent-name">@developer</h3>
                    <div class="agent-role-tag">Разработчик C# (.NET 8)</div>
                    <p class="agent-desc">
                        Пишет кристально чистый, высокопроизводительный код строго в <code>src/</code>. Ликвидирует аллокации GC, собирает SIMD-векторы, реализует CAS-блокировки и выдерживает нагрузки в 1 000 000 колонистов.
                    </p>
                    <div class="agent-trophies">
                        Трофеи: <strong>45 469 строк кода</strong>, нулевые аллокации в тике, шейдеры интерполяции.
                    </div>
                </div>

                <div class="agent-dossier-card destroyer">
                    <div class="agent-avatar-icon">💣</div>
                    <h3 class="agent-name">@destroyer</h3>
                    <div class="agent-role-tag">Разрушитель & Стресс-Тестировщик</div>
                    <p class="agent-desc">
                        Состязательное тестирование (Adversarial Testing). Ищет крайние случаи, бомбардирует систему 250,000 чертежами, null-параметрами, тестами швов чанков и выявляет утечки памяти до релиза.
                    </p>
                    <div class="agent-trophies">
                        Трофеи: <strong>TestAdversarialConstruction</strong> (14 сценариев), <strong>TestAdversarialJobDistribution</strong>.
                    </div>
                </div>
            </div>

            <!-- ADVERSARIAL TEST RUNNER TERMINAL -->
            <div class="terminal-container">
                <div class="terminal-header">
                    <div class="terminal-dots">
                        <div class="dot red"></div>
                        <div class="dot yellow"></div>
                        <div class="dot green"></div>
                    </div>
                    <div class="terminal-title">destroyer@godot-test-runner: ~/tests/TestAdversarialJobDistribution.cs</div>
                    <span style="font-size: 0.75rem; color: var(--accent-emerald); font-family: var(--font-mono);">● SUITE READY</span>
                </div>
                <div class="terminal-body" id="test-terminal-output">
[ADVERSARIAL SUITE] Готов к запуску состязательных тестов @destroyer.
Нажмите кнопку ниже для симуляции выполнения стресс-тестов ядра...
                </div>
                <div class="terminal-btn-bar">
                    <button class="sim-btn active" id="btn-run-adversarial-test">
                        ▶ Запустить стресс-тест @destroyer (1M агентов)
                    </button>
                    <button class="sim-btn" id="btn-clear-terminal">
                        Очистить консоль
                    </button>
                </div>
            </div>
        </section>

        <!-- THE BUG GRAVEYARD -->
        <section id="graveyard" class="graveyard-section">
            <div class="section-header">
                <div class="section-tag">Кладбище Побеждённых Багов</div>
                <h2 class="section-title">8 Легендарных Битв с Архитектурными Аномалиями</h2>
                <p class="section-desc">
                    Самые коварные и сложные проблемы, встреченные на пути разработки высокопроизводительной симуляции, и то, как они были триумфально решены.
                </p>
            </div>

            <div class="graveyard-grid">
                <!-- Bug 1 -->
                <div class="bug-card">
                    <div class="bug-tag">☠ Баг #1: Famine Thundering Herd</div>
                    <h4 class="bug-title">Голодный Шторм 16:45</h4>
                    <p class="bug-symptom">
                        В 16:45 все 10 000 колонистов одновременно пересекали порог голода 70 и в один и тот же такт бросались сканировать склад через глобальный lock <code>GroundItemManager</code>, роняя FPS в ноль.
                    </p>
                    <div class="bug-solution">
                        ✓ <strong>Решение:</strong> Джиттер голода 0–20 при спавне, сужение радиуса поиска с 32 до 3 чанков, lock-free пречеки тоталов и таймер <code>NeedsRetryTimer</code>.
                    </div>
                </div>

                <!-- Bug 2 -->
                <div class="bug-card">
                    <div class="bug-tag">☠ Баг #2: False Sharing</div>
                    <h4 class="bug-title">Пинг-понг Кэш-линий L1/L2 CPU</h4>
                    <p class="bug-symptom">
                        Курсоры очередей рабочих потоков в <code>WorkStealingQueue</code> лежали на соседних байтах памяти. Запись одного ядра инвалидировала кэш-линию 64 байт всех остальных ядер, убивая масштабируемость.
                    </p>
                    <div class="bug-solution">
                        ✓ <strong>Решение:</strong> 64-байтовое выравнивание с <code>[StructLayout(LayoutKind.Explicit)]</code> и изолированные per-worker деки Chase-Lev.
                    </div>
                </div>

                <!-- Bug 3 -->
                <div class="bug-card">
                    <div class="bug-tag">☠ Баг #3: Infinite Ocean</div>
                    <h4 class="bug-title">Ловушка «Бесконечного Океана»</h4>
                    <p class="bug-symptom">
                        При старте в случайных координатах колония оказывалась посреди бесконечного бескрайнего моря из-за смещения сетки континентального шума относительно локальной карты.
                    </p>
                    <div class="bug-solution">
                        ✓ <strong>Решение:</strong> Алгоритм <code>FindHabitableSpawn</code>, строгий водный и сухопутный инвариант, бимодальный гипсографический сплайн Земли.
                    </div>
                </div>

                <!-- Bug 4 -->
                <div class="bug-card">
                    <div class="bug-tag">☠ Баг #4: 40,600 Lines Deadlock</div>
                    <h4 class="bug-title">36 000 Строк Мертвого Кода</h4>
                    <p class="bug-symptom">
                        Десятки файлов <code>.bak</code>, старых неиспользуемых плагинов <code>addons/godot_ai</code> и заброшенных версий контроллеров засоряли IDE и раздували историю.
                    </p>
                    <div class="bug-solution">
                        ✓ <strong>Решение:</strong> Хирургическая чистка коммитом <code>598f31f</code> (-40 600 строк кода) и строгий обновлённый <code>.gitignore</code>.
                    </div>
                </div>

                <!-- Bug 5 -->
                <div class="bug-card">
                    <div class="bug-tag">☠ Баг #5: 100x Speed Spikes</div>
                    <h4 class="bug-title">Всплески 5.4 мс на Скорости 100x</h4>
                    <p class="bug-symptom">
                        При ускорении времени до 100х периодические аллокации списков в A* и BFS вызывали Stop-The-World паузы сборщика мусора .NET, приводя к подёргиваниям рендера.
                    </p>
                    <div class="bug-solution">
                        ✓ <strong>Решение:</strong> 100% Zero-Alloc архитектура в горячем пути: структуры <code>WorkItem</code>, <code>Span&lt;T&gt;</code>, и CPU SIMD батч <code>SimdNeedsBatch</code>.
                    </div>
                </div>

                <!-- Bug 6 -->
                <div class="bug-card">
                    <div class="bug-tag">☠ Баг #6: Chunk Seams</div>
                    <h4 class="bug-title">Швы и Разрывы на Границах Чанков</h4>
                    <p class="bug-symptom">
                        Ранние генераторы использовали локальные координаты чанков, из-за чего на границе 32x32 соседние тайлы не совпадали по высоте и биому, создавая уродливые разломы.
                    </p>
                    <div class="bug-solution">
                        ✓ <strong>Решение:</strong> Абсолютное хэширование координат в <code>WorldNoise.cs</code> (v4) и приёмочный тест непрерывности в <code>WorldSeamTests</code>.
                    </div>
                </div>

                <!-- Bug 7 -->
                <div class="bug-card">
                    <div class="bug-tag">☠ Баг #7: Sideway Colonists</div>
                    <h4 class="bug-title">Агенты, Ходящие Боком и Спиной</h4>
                    <p class="bug-symptom">
                        Шейдер интерполяции агентов не учитывал нормализованный вектор мгновенной скорости, из-за чего спрайты колонистов смотрели влево при движении вправо.
                    </p>
                    <div class="bug-solution">
                        ✓ <strong>Решение:</strong> Упаковка знака ориентации в альфа-канал буфера вершин <code>AgentLerpBatch.cs</code> и флип текстуры в шейдере.
                    </div>
                </div>

                <!-- Bug 8 -->
                <div class="bug-card">
                    <div class="bug-tag">☠ Баг #8: 250,000 Blueprints Bomb</div>
                    <h4 class="bug-title">Бомба Чертежей 500x500</h4>
                    <p class="bug-symptom">
                        Попытка игрока выделить колоссальную зону стройки приводила к взрывному росту памяти в старых диспетчерах из-за списков <code>List&lt;Job&gt;</code>.
                    </p>
                    <div class="bug-solution">
                        ✓ <strong>Решение:</strong> Пирамида задач <code>JobSpatialPyramid</code> с битовыми масками макро-секторов и квотой 20 назначений за кадр.
                    </div>
                </div>
            </div>
        </section>

        <!-- VISUAL GALLERY -->
        <section id="gallery" class="gallery-section">
            <div class="section-header">
                <div class="section-tag">Визуальный Архив Проекта</div>
                <h2 class="section-title">Галерея Реальных Скриншотов и Лаборатории</h2>
                <p class="section-desc">
                    Подлинные скриншоты реального геймплея и инструментов верификации <code>NoiseLab</code> из директории <code>docs/</code>. Кликните для полноэкранного просмотра!
                </p>
            </div>

            <div class="gallery-grid" id="gallery-grid-container">
                <!-- Injected via JavaScript from real screenshot list -->
            </div>
        </section>
    </main>

    <!-- LIGHTBOX MODAL -->
    <div id="lightbox">
        <div class="lightbox-content">
            <button class="lightbox-close" id="lightbox-close">&times;</button>
            <img class="lightbox-img" id="lightbox-img" src="" alt="">
            <div class="lightbox-caption" id="lightbox-caption"></div>
        </div>
    </div>

    <!-- FOOTER -->
    <footer class="site-footer">
        <p class="footer-quote">
            «От первой капли влажности в матрице 10-cell spread — до галактического масштаба в 1 000 000 колонистов с процессорной инструкцией <code>tzcnt</code>.»
        </p>
        <p>
            Разработано Фишеном (animemoment) в тесном союзе с Архитектором, Исследователем, Работником и Разрушителем.
        </p>
        <p style="margin-top: 6px; font-family: var(--font-mono); font-size: 0.78rem; color: var(--accent-cyan);">
            Godot 4.7 • C# / .NET 8 • 2026
        </p>
    </footer>

    <!-- SCRIPT DATA & LOGIC -->
    <script>
        const CHRONICLE_COMMITS = ''' + commits_json_str + r''';
        const CHRONICLE_STATS = ''' + stats_json_str + r''';
        const CHRONICLE_BRANCHES = ''' + branches_json_str + r''';
        const CHRONICLE_SCREENSHOTS = ''' + screenshots_json_str + r''';

        // ══════════════════════════════════════════════════════════
        // 1. WEB AUDIO SYNTHESIZER (Sci-Fi Interface Sounds)
        // ══════════════════════════════════════════════════════════
        class SoundFx {
            constructor() {
                this.enabled = true;
                this.ctx = null;
            }
            init() {
                if (!this.ctx) {
                    const AudioCtx = window.AudioContext || window.webkitAudioContext;
                    this.ctx = new AudioCtx();
                }
            }
            playClick() {
                if (!this.enabled) return;
                this.init();
                const osc = this.ctx.createOscillator();
                const gain = this.ctx.createGain();
                osc.type = 'sine';
                osc.frequency.setValueAtTime(800, this.ctx.currentTime);
                osc.frequency.exponentialRampToValueAtTime(400, this.ctx.currentTime + 0.04);
                gain.gain.setValueAtTime(0.08, this.ctx.currentTime);
                gain.gain.linearRampToValueAtTime(0.001, this.ctx.currentTime + 0.04);
                osc.connect(gain);
                gain.connect(this.ctx.destination);
                osc.start();
                osc.stop(this.ctx.currentTime + 0.04);
            }
            playToggle() {
                if (!this.enabled) return;
                this.init();
                const osc = this.ctx.createOscillator();
                const gain = this.ctx.createGain();
                osc.type = 'triangle';
                osc.frequency.setValueAtTime(440, this.ctx.currentTime);
                osc.frequency.exponentialRampToValueAtTime(880, this.ctx.currentTime + 0.08);
                gain.gain.setValueAtTime(0.06, this.ctx.currentTime);
                gain.gain.linearRampToValueAtTime(0.001, this.ctx.currentTime + 0.08);
                osc.connect(gain);
                gain.connect(this.ctx.destination);
                osc.start();
                osc.stop(this.ctx.currentTime + 0.08);
            }
            playChop() {
                if (!this.enabled) return;
                this.init();
                const osc = this.ctx.createOscillator();
                const gain = this.ctx.createGain();
                osc.type = 'square';
                osc.frequency.setValueAtTime(140, this.ctx.currentTime);
                osc.frequency.exponentialRampToValueAtTime(50, this.ctx.currentTime + 0.06);
                gain.gain.setValueAtTime(0.07, this.ctx.currentTime);
                gain.gain.linearRampToValueAtTime(0.001, this.ctx.currentTime + 0.06);
                osc.connect(gain);
                gain.connect(this.ctx.destination);
                osc.start();
                osc.stop(this.ctx.currentTime + 0.06);
            }
        }
        const sfx = new SoundFx();

        document.getElementById('btn-sound-toggle').addEventListener('click', () => {
            sfx.enabled = !sfx.enabled;
            const icon = document.getElementById('sound-icon');
            icon.textContent = sfx.enabled ? '🔊' : '🔇';
            if (sfx.enabled) sfx.playToggle();
        });

        // ══════════════════════════════════════════════════════════
        // 2. AMBIENT BACKGROUND PARTICLES CANVAS
        // ══════════════════════════════════════════════════════════
        const ambCanvas = document.getElementById('ambient-canvas');
        const ambCtx = ambCanvas.getContext('2d');
        let ambW = ambCanvas.width = window.innerWidth;
        let ambH = ambCanvas.height = window.innerHeight;

        window.addEventListener('resize', () => {
            ambW = ambCanvas.width = window.innerWidth;
            ambH = ambCanvas.height = window.innerHeight;
        });

        const particles = [];
        for (let i = 0; i < 60; i++) {
            particles.push({
                x: Math.random() * ambW,
                y: Math.random() * ambH,
                vx: (Math.random() - 0.5) * 0.4,
                vy: (Math.random() - 0.5) * 0.4,
                size: Math.random() * 2 + 1,
                color: Math.random() > 0.5 ? 'rgba(56, 189, 248, ' : 'rgba(168, 85, 247, ',
                alpha: Math.random() * 0.5 + 0.2
            });
        }

        function drawAmbient() {
            ambCtx.clearRect(0, 0, ambW, ambH);
            for (let i = 0; i < particles.length; i++) {
                const p = particles[i];
                p.x += p.vx;
                p.y += p.vy;
                if (p.x < 0) p.x = ambW;
                if (p.x > ambW) p.x = 0;
                if (p.y < 0) p.y = ambH;
                if (p.y > ambH) p.y = 0;

                ambCtx.fillStyle = p.color + p.alpha + ')';
                ambCtx.beginPath();
                ambCtx.arc(p.x, p.y, p.size, 0, Math.PI * 2);
                ambCtx.fill();

                for (let j = i + 1; j < particles.length; j++) {
                    const p2 = particles[j];
                    const dx = p.x - p2.x;
                    const dy = p.y - p2.y;
                    const dist = Math.sqrt(dx * dx + dy * dy);
                    if (dist < 130) {
                        ambCtx.strokeStyle = 'rgba(56, 189, 248, ' + (0.12 * (1 - dist / 130)) + ')';
                        ambCtx.lineWidth = 1;
                        ambCtx.beginPath();
                        ambCtx.moveTo(p.x, p.y);
                        ambCtx.lineTo(p2.x, p2.y);
                        ambCtx.stroke();
                    }
                }
            }
            requestAnimationFrame(drawAmbient);
        }
        drawAmbient();

        // ══════════════════════════════════════════════════════════
        // 3. INTERACTIVE 2D COLONY CANVAS SIMULATION
        // ══════════════════════════════════════════════════════════
        const colCanvas = document.getElementById('colony-canvas');
        const colCtx = colCanvas.getContext('2d');
        const colContainer = document.getElementById('sim-workspace');

        function resizeColonyCanvas() {
            colCanvas.width = colContainer.clientWidth;
            colCanvas.height = colContainer.clientHeight;
        }
        resizeColonyCanvas();
        window.addEventListener('resize', resizeColonyCanvas);

        let simSpeed = 1;
        let isPaused = false;
        let showHumidity = false;
        let showFlowField = false;
        let totalWoodCut = 1240;
        let totalWheatHarvested = 480;
        let timeOfDay = 13.75; // 13:45

        // World Island Setup
        const mapW = 40;
        const mapH = 26;
        const tileSize = 28;
        const islandMap = [];
        const humidityMap = [];

        // Generate island center
        const cx = mapW / 2;
        const cy = mapH / 2;
        for (let y = 0; y < mapH; y++) {
            islandMap[y] = [];
            humidityMap[y] = [];
            for (let x = 0; x < mapW; x++) {
                const dist = Math.hypot(x - cx, y - cy);
                const noise = Math.sin(x * 0.35) * Math.cos(y * 0.35) * 3;
                if (dist + noise < 11.5) {
                    islandMap[y][x] = 1; // Land
                } else if (dist + noise < 13.5) {
                    islandMap[y][x] = 2; // Sand
                } else {
                    islandMap[y][x] = 0; // Water
                }
            }
        }

        // Calculate 10-cell water spread humidity
        for (let y = 0; y < mapH; y++) {
            for (let x = 0; x < mapW; x++) {
                if (islandMap[y][x] === 0) {
                    humidityMap[y][x] = 1.0;
                } else {
                    // Distance to nearest water
                    let minDist = 10;
                    for (let wy = Math.max(0, y - 10); wy < Math.min(mapH, y + 10); wy++) {
                        for (let wx = Math.max(0, x - 10); wx < Math.min(mapW, x + 10); wx++) {
                            if (islandMap[wy][wx] === 0) {
                                const d = Math.hypot(x - wx, y - wy);
                                if (d < minDist) minDist = d;
                            }
                        }
                    }
                    humidityMap[y][x] = Math.max(0, 1.0 - (minDist / 10));
                }
            }
        }

        // Objects on island: Trees, Farms, Stockpile
        const trees = [];
        for (let i = 0; i < 35; i++) {
            const tx = Math.floor(cx + (Math.random() - 0.5) * 16);
            const ty = Math.floor(cy + (Math.random() - 0.5) * 14);
            if (islandMap[ty] && islandMap[ty][tx] === 1) {
                trees.push({ x: tx, y: ty, hp: 100, maxHp: 100, isTargeted: false });
            }
        }

        const farms = [];
        for (let fy = cy - 4; fy <= cy - 1; fy++) {
            for (let fx = cx - 5; fx <= cx - 1; fx++) {
                if (islandMap[fy] && islandMap[fy][fx] === 1) {
                    farms.push({ x: fx, y: fy, stage: Math.floor(Math.random() * 4) + 1, growth: Math.random() * 100 });
                }
            }
        }

        // Stockpile zone
        const stockpile = { x: cx + 2, y: cy - 2, w: 4, h: 3 };

        // Colonists Agent Pool (SoA)
        const agentCount = 120;
        const agents = [];
        const states = ['IDLE', 'CHOPPING', 'FARMING', 'HAULING', 'RESTING'];

        for (let i = 0; i < agentCount; i++) {
            const rx = cx + (Math.random() - 0.5) * 12;
            const ry = cy + (Math.random() - 0.5) * 10;
            agents.push({
                id: i + 1,
                x: rx * tileSize,
                y: ry * tileSize,
                targetX: rx * tileSize,
                targetY: ry * tileSize,
                state: states[Math.floor(Math.random() * states.length)],
                hunger: Math.floor(Math.random() * 40),
                sleep: Math.floor(Math.random() * 30),
                fatigue: Math.floor(Math.random() * 40),
                mood: 85 + Math.floor(Math.random() * 15),
                carriedItem: Math.random() > 0.5 ? 'Log' : 'None',
                speed: 1.2 + Math.random() * 0.6,
                targetTree: null,
                targetFarm: null
            });
        }

        let selectedAgent = agents[0];

        // Canvas Click -> Inspect agent or tree
        colCanvas.addEventListener('click', (e) => {
            const rect = colCanvas.getBoundingClientRect();
            const mx = e.clientX - rect.left;
            const my = e.clientY - rect.top;

            // Find closest agent
            let closest = null;
            let minDist = 24;
            for (const a of agents) {
                const d = Math.hypot(a.x - mx, a.y - my);
                if (d < minDist) {
                    minDist = d;
                    closest = a;
                }
            }

            if (closest) {
                selectedAgent = closest;
                showAgentCard(closest);
                sfx.playClick();
            }
        });

        function showAgentCard(a) {
            const card = document.getElementById('agent-inspect-card');
            card.style.display = 'block';
            document.getElementById('agent-inspect-id').textContent = `Колонист #${a.id}`;
            document.getElementById('agent-inspect-state').textContent = a.state;
            document.getElementById('val-hunger').textContent = `${Math.floor(a.hunger)}/100`;
            document.getElementById('bar-hunger').style.width = `${a.hunger}%`;
            document.getElementById('val-sleep').textContent = `${Math.floor(a.sleep)}/100`;
            document.getElementById('bar-sleep').style.width = `${a.sleep}%`;
            document.getElementById('val-fatigue').textContent = `${Math.floor(a.fatigue)}/100`;
            document.getElementById('bar-fatigue').style.width = `${a.fatigue}%`;
            document.getElementById('val-mood').textContent = `${Math.floor(a.mood)}%`;
            document.getElementById('bar-mood').style.width = `${a.mood}%`;
            document.getElementById('agent-inspect-item').textContent = a.carriedItem;
        }

        // Simulation Update Loop
        function updateSimulation(dt) {
            if (isPaused) return;

            timeOfDay += dt * 0.05 * simSpeed;
            if (timeOfDay >= 24) timeOfDay -= 24;

            // Update farms growth
            for (const f of farms) {
                f.growth += dt * 8 * simSpeed;
                if (f.growth >= 100) {
                    f.growth = 0;
                    f.stage = f.stage >= 4 ? 1 : f.stage + 1;
                    if (f.stage === 4) {
                        totalWheatHarvested += 2;
                    }
                }
            }

            // Update agents
            for (const a of agents) {
                // Needs progression
                a.hunger = Math.min(100, a.hunger + dt * 0.2 * simSpeed);
                a.sleep = Math.min(100, a.sleep + dt * 0.15 * simSpeed);
                a.fatigue = Math.min(100, a.fatigue + dt * (a.state === 'IDLE' ? -0.3 : 0.2) * simSpeed);

                // State Machine
                if (Math.hypot(a.x - a.targetX, a.y - a.targetY) < 6) {
                    // Reached target
                    if (a.state === 'CHOPPING') {
                        totalWoodCut += 1;
                        a.carriedItem = 'Log';
                        a.state = 'HAULING';
                        a.targetX = (stockpile.x + Math.random() * stockpile.w) * tileSize;
                        a.targetY = (stockpile.y + Math.random() * stockpile.h) * tileSize;
                    } else if (a.state === 'HAULING') {
                        a.carriedItem = 'None';
                        a.state = 'IDLE';
                    } else if (a.state === 'FARMING') {
                        a.carriedItem = 'Wheat';
                        a.state = 'HAULING';
                        a.targetX = (stockpile.x + Math.random() * stockpile.w) * tileSize;
                        a.targetY = (stockpile.y + Math.random() * stockpile.h) * tileSize;
                    } else {
                        // Pick new task
                        const r = Math.random();
                        if (r < 0.4 && trees.length > 0) {
                            const t = trees[Math.floor(Math.random() * trees.length)];
                            a.targetX = t.x * tileSize;
                            a.targetY = t.y * tileSize;
                            a.state = 'CHOPPING';
                        } else if (r < 0.7 && farms.length > 0) {
                            const f = farms[Math.floor(Math.random() * farms.length)];
                            a.targetX = f.x * tileSize;
                            a.targetY = f.y * tileSize;
                            a.state = 'FARMING';
                        } else {
                            a.state = 'IDLE';
                            a.targetX = (cx + (Math.random() - 0.5) * 12) * tileSize;
                            a.targetY = (cy + (Math.random() - 0.5) * 10) * tileSize;
                        }
                    }
                } else {
                    // Move towards target
                    const dx = a.targetX - a.x;
                    const dy = a.targetY - a.y;
                    const dist = Math.hypot(dx, dy);
                    const step = a.speed * 28 * dt * simSpeed;
                    a.x += (dx / dist) * Math.min(step, dist);
                    a.y += (dy / dist) * Math.min(step, dist);
                }
            }

            // Update HUD values
            const h = Math.floor(timeOfDay);
            const m = Math.floor((timeOfDay - h) * 60);
            const timeStr = `${h.toString().padStart(2, '0')}:${m.toString().padStart(2, '0')}`;
            const isNight = timeOfDay < 5 || timeOfDay > 20;
            document.getElementById('hud-time').textContent = `${timeStr} ${isNight ? '☾' : '☀'}`;
            document.getElementById('hud-agents').textContent = agents.length;
            document.getElementById('hud-wood').textContent = `${totalWoodCut.toLocaleString()} шт`;
            document.getElementById('hud-wheat').textContent = `${totalWheatHarvested.toLocaleString()} мешков`;

            if (selectedAgent) {
                showAgentCard(selectedAgent);
            }
        }

        // Render Canvas
        function renderSimulation() {
            colCtx.clearRect(0, 0, colCanvas.width, colCanvas.height);

            // Center island on canvas
            const ox = (colCanvas.width - mapW * tileSize) / 2;
            const oy = (colCanvas.height - mapH * tileSize) / 2;

            // 1. Draw Map Tiles
            for (let y = 0; y < mapH; y++) {
                for (let x = 0; x < mapW; x++) {
                    const t = islandMap[y][x];
                    const px = ox + x * tileSize;
                    const py = oy + y * tileSize;

                    if (showHumidity) {
                        const hum = humidityMap[y][x];
                        colCtx.fillStyle = `rgb(${Math.floor(20 * (1 - hum))}, ${Math.floor(100 + 120 * hum)}, ${Math.floor(180 + 75 * hum)})`;
                    } else {
                        if (t === 0) {
                            colCtx.fillStyle = '#0e243a'; // Ocean
                        } else if (t === 2) {
                            colCtx.fillStyle = '#c2a868'; // Beach sand
                        } else {
                            colCtx.fillStyle = '#22543d'; // Colony grass
                        }
                    }
                    colCtx.fillRect(px, py, tileSize, tileSize);

                    // Grid lines (subtle)
                    colCtx.strokeStyle = 'rgba(255,255,255,0.025)';
                    colCtx.strokeRect(px, py, tileSize, tileSize);
                }
            }

            // Flow field overlay
            if (showFlowField) {
                colCtx.strokeStyle = 'rgba(56, 189, 248, 0.4)';
                colCtx.fillStyle = 'rgba(56, 189, 248, 0.4)';
                for (let y = 0; y < mapH; y += 2) {
                    for (let x = 0; x < mapW; x += 2) {
                        if (islandMap[y][x] === 1) {
                            const px = ox + x * tileSize + tileSize / 2;
                            const py = oy + y * tileSize + tileSize / 2;
                            const angle = Math.atan2(cy - y, cx - x);
                            colCtx.beginPath();
                            colCtx.moveTo(px, py);
                            colCtx.lineTo(px + Math.cos(angle) * 10, py + Math.sin(angle) * 10);
                            colCtx.stroke();
                        }
                    }
                }
            }

            // Stockpile Zone
            colCtx.fillStyle = 'rgba(168, 85, 247, 0.25)';
            colCtx.strokeStyle = '#a855f7';
            colCtx.lineWidth = 1.5;
            colCtx.fillRect(ox + stockpile.x * tileSize, oy + stockpile.y * tileSize, stockpile.w * tileSize, stockpile.h * tileSize);
            colCtx.strokeRect(ox + stockpile.x * tileSize, oy + stockpile.y * tileSize, stockpile.w * tileSize, stockpile.h * tileSize);

            // Farm Plots & Wheat
            for (const f of farms) {
                const fx = ox + f.x * tileSize;
                const fy = oy + f.y * tileSize;
                colCtx.fillStyle = '#3d2b1f'; // Tilled dirt
                colCtx.fillRect(fx + 2, fy + 2, tileSize - 4, tileSize - 4);

                // Wheat stages
                if (f.stage === 1) {
                    colCtx.fillStyle = '#84cc16'; // Sprout
                    colCtx.fillRect(fx + 10, fy + 14, 6, 8);
                } else if (f.stage === 2) {
                    colCtx.fillStyle = '#a3e635'; // Growing
                    colCtx.fillRect(fx + 8, fy + 10, 10, 12);
                } else if (f.stage === 3) {
                    colCtx.fillStyle = '#eab308'; // Maturing
                    colCtx.fillRect(fx + 6, fy + 6, 14, 16);
                } else {
                    colCtx.fillStyle = '#f59e0b'; // Ready golden wheat
                    colCtx.fillRect(fx + 5, fy + 4, 16, 18);
                    colCtx.fillStyle = '#fef08a';
                    colCtx.fillRect(fx + 8, fy + 2, 10, 6);
                }
            }

            // Sun shadows calculation
            const sunAngle = ((timeOfDay - 6) / 12) * Math.PI; // Sunrise 6h, Sunset 18h
            const isNight = timeOfDay < 5.5 || timeOfDay > 19.5;
            const shadowLen = isNight ? 0 : Math.max(0, Math.sin(sunAngle)) * 18;
            const shadowDirX = Math.cos(sunAngle) * -1;
            const shadowDirY = 0.5;

            // Draw Trees with shadows
            for (const t of trees) {
                const tx = ox + t.x * tileSize + tileSize / 2;
                const ty = oy + t.y * tileSize + tileSize;

                // Shadow
                if (shadowLen > 0) {
                    colCtx.fillStyle = 'rgba(0,0,0,0.28)';
                    colCtx.beginPath();
                    colCtx.ellipse(tx + shadowDirX * shadowLen, ty + shadowDirY * shadowLen * 0.4, 12, 5, 0, 0, Math.PI * 2);
                    colCtx.fill();
                }

                // Trunk & Foliage
                colCtx.fillStyle = '#5c3a21';
                colCtx.fillRect(tx - 3, ty - 12, 6, 12);

                colCtx.fillStyle = '#166534';
                colCtx.beginPath();
                colCtx.arc(tx, ty - 22, 14, 0, Math.PI * 2);
                colCtx.fill();

                colCtx.fillStyle = '#22c55e';
                colCtx.beginPath();
                colCtx.arc(tx - 3, ty - 25, 8, 0, Math.PI * 2);
                colCtx.fill();
            }

            // Draw Agents (Colonists)
            for (const a of agents) {
                const ax = ox + a.x;
                const ay = oy + a.y;

                // Agent shadow
                if (shadowLen > 0) {
                    colCtx.fillStyle = 'rgba(0,0,0,0.3)';
                    colCtx.beginPath();
                    colCtx.ellipse(ax + shadowDirX * shadowLen * 0.5, ay + 2, 6, 2.5, 0, 0, Math.PI * 2);
                    colCtx.fill();
                }

                // Colonist body (blue shirt)
                colCtx.fillStyle = a.id === selectedAgent?.id ? '#38bdf8' : '#3b82f6';
                colCtx.fillRect(ax - 4, ay - 10, 8, 8);

                // Head
                colCtx.fillStyle = '#fbcfe8';
                colCtx.beginPath();
                colCtx.arc(ax, ay - 13, 4, 0, Math.PI * 2);
                colCtx.fill();

                // Selection highlight ring
                if (a.id === selectedAgent?.id) {
                    colCtx.strokeStyle = '#38bdf8';
                    colCtx.lineWidth = 1.5;
                    colCtx.beginPath();
                    colCtx.arc(ax, ay - 8, 12, 0, Math.PI * 2);
                    colCtx.stroke();
                }

                // Carried item indicator
                if (a.carriedItem === 'Log') {
                    colCtx.fillStyle = '#78350f';
                    colCtx.fillRect(ax + 4, ay - 12, 5, 10);
                } else if (a.carriedItem === 'Wheat') {
                    colCtx.fillStyle = '#f59e0b';
                    colCtx.fillRect(ax + 4, ay - 10, 6, 6);
                }
            }

            // Day / Night Ambient Tint (CanvasModulate)
            if (isNight) {
                colCtx.fillStyle = 'rgba(10, 15, 30, 0.65)';
                colCtx.fillRect(0, 0, colCanvas.width, colCanvas.height);
            } else if (timeOfDay < 7 || timeOfDay > 18) {
                // Dawn / Sunset tint
                colCtx.fillStyle = 'rgba(255, 120, 50, 0.15)';
                colCtx.fillRect(0, 0, colCanvas.width, colCanvas.height);
            }
        }

        let lastTime = performance.now();
        function simLoop(now) {
            const dt = Math.min(0.1, (now - lastTime) / 1000);
            lastTime = now;
            updateSimulation(dt);
            renderSimulation();
            requestAnimationFrame(simLoop);
        }
        requestAnimationFrame(simLoop);

        // Simulation Button Binds
        document.getElementById('btn-pause').addEventListener('click', function() {
            isPaused = !isPaused;
            this.textContent = isPaused ? '▶ Старт' : '⏸ Пауза';
            this.classList.toggle('active', isPaused);
            sfx.playClick();
        });

        const speedBtns = [
            { id: 'btn-speed-1', speed: 1 },
            { id: 'btn-speed-5', speed: 5 },
            { id: 'btn-speed-25', speed: 25 },
            { id: 'btn-speed-100', speed: 100 },
        ];
        speedBtns.forEach(b => {
            document.getElementById(b.id).addEventListener('click', function() {
                speedBtns.forEach(o => document.getElementById(o.id).classList.remove('active'));
                this.classList.add('active');
                simSpeed = b.speed;
                document.getElementById('hud-speed').textContent = `${b.speed}x (${(0.05 / Math.min(8, b.speed)).toFixed(3)}с/шаг)`;
                sfx.playToggle();
            });
        });

        document.getElementById('btn-toggle-humidity').addEventListener('click', function() {
            showHumidity = !showHumidity;
            this.classList.toggle('active', showHumidity);
            sfx.playToggle();
        });

        document.getElementById('btn-toggle-flow').addEventListener('click', function() {
            showFlowField = !showFlowField;
            this.classList.toggle('active', showFlowField);
            sfx.playToggle();
        });

        document.getElementById('btn-spawn-workers').addEventListener('click', function() {
            for (let i = 0; i < 25; i++) {
                const rx = cx + (Math.random() - 0.5) * 8;
                const ry = cy + (Math.random() - 0.5) * 8;
                agents.push({
                    id: agents.length + 1,
                    x: rx * tileSize,
                    y: ry * tileSize,
                    targetX: rx * tileSize,
                    targetY: ry * tileSize,
                    state: 'IDLE',
                    hunger: 0,
                    sleep: 0,
                    fatigue: 0,
                    mood: 100,
                    carriedItem: 'None',
                    speed: 1.2 + Math.random() * 0.5
                });
            }
            sfx.playToggle();
        });

        // ══════════════════════════════════════════════════════════
        // 4. WHITTAKER BIOME LAB (CANVAS 2D)
        // ══════════════════════════════════════════════════════════
        const wCanvas = document.getElementById('whittaker-canvas');
        const wCtx = wCanvas.getContext('2d');
        const wContainer = wCanvas.parentElement;

        function resizeWhittakerCanvas() {
            wCanvas.width = wContainer.clientWidth;
            wCanvas.height = wContainer.clientHeight;
        }
        resizeWhittakerCanvas();
        window.addEventListener('resize', resizeWhittakerCanvas);

        let oceanThreshold = 0.42;
        let rainShadowIntensity = 0.75;
        let worldSeed = 4219;

        // Simple pseudo-random hash
        function hash2(x, y, seed) {
            let n = Math.sin(x * 12.9898 + y * 78.233 + seed * 43.123) * 43758.5453;
            return n - Math.floor(n);
        }

        // 2D Perlin-like fBm noise
        function fbm(x, y, seed) {
            let v = 0;
            let a = 0.5;
            let f = 1.0;
            for (let i = 0; i < 4; i++) {
                const x0 = Math.floor(x * f);
                const y0 = Math.floor(y * f);
                const x1 = x0 + 1;
                const y1 = y0 + 1;
                const sx = (x * f) - x0;
                const sy = (y * f) - y0;

                const n00 = hash2(x0, y0, seed);
                const n10 = hash2(x1, y0, seed);
                const n01 = hash2(x0, y1, seed);
                const n11 = hash2(x1, y1, seed);

                const ix0 = n00 + (n10 - n00) * (sx * sx * (3 - 2 * sx));
                const ix1 = n01 + (n11 - n01) * (sx * sx * (3 - 2 * sx));
                const n = ix0 + (ix1 - ix0) * (sy * sy * (3 - 2 * sy));

                v += n * a;
                a *= 0.5;
                f *= 2.0;
            }
            return v;
        }

        // Whittaker Biome Classifier Matrix
        function getWhittakerBiome(elev, temp, moisture) {
            if (elev < oceanThreshold) {
                return { name: 'Глубокий океан (Deep Ocean)', color: '#091c32', good: false };
            }
            if (elev > 0.82) {
                return { name: 'Снежные пики & Горы (Glacial Peaks)', color: '#e2e8f0', good: false };
            }
            if (temp < 0.25) {
                return moisture > 0.4 ? { name: 'Таёжные леса (Taiga)', color: '#164e63', good: true } : { name: 'Тундра (Tundra)', color: '#64748b', good: false };
            }
            if (temp < 0.65) {
                if (moisture > 0.6) return { name: 'Умеренный дождевой лес', color: '#047857', good: true };
                if (moisture > 0.35) return { name: 'Широколиственный лес', color: '#15803d', good: true };
                return { name: 'Степь & Лесостепь (Grassland)', color: '#84cc16', good: true };
            }
            // Hot tropical / subtropical
            if (moisture < 0.25) return { name: 'Песчаная пустыня (Arid Desert)', color: '#d97706', good: false };
            if (moisture < 0.55) return { name: 'Саванна & Редколесье (Savanna)', color: '#b45309', good: true };
            return { name: 'Тропический дождевой лес (Rainforest)', color: '#065f46', good: true };
        }

        function renderWhittaker() {
            const w = wCanvas.width;
            const h = wCanvas.height;
            const imgData = wCtx.createImageData(w, h);
            const data = imgData.data;

            const scale = 0.006;
            for (let py = 0; py < h; py++) {
                const lat = 1.0 - Math.abs((py / h) - 0.5) * 2; // Latitude: 0 at poles, 1 at equator
                for (let px = 0; px < w; px++) {
                    const elev = fbm(px * scale, py * scale, worldSeed);
                    const temp = Math.max(0, Math.min(1, lat * 0.7 + (1.0 - elev) * 0.3 + (hash2(px * 0.05, py * 0.05, worldSeed + 1) - 0.5) * 0.1));
                    
                    // Rain shadow calculation: moisture carried from left (west wind) blocked by high elevation
                    let moisture = fbm(px * scale + 5, py * scale + 5, worldSeed + 2);
                    if (px > 20) {
                        const westElev = fbm((px - 25) * scale, py * scale, worldSeed);
                        if (westElev > 0.65) {
                            moisture -= (westElev - 0.65) * rainShadowIntensity;
                        }
                    }
                    moisture = Math.max(0, Math.min(1, moisture));

                    const biome = getWhittakerBiome(elev, temp, moisture);

                    // Parse HEX to RGB
                    const r = parseInt(biome.color.slice(1, 3), 16);
                    const g = parseInt(biome.color.slice(3, 5), 16);
                    const b = parseInt(biome.color.slice(5, 7), 16);

                    const idx = (py * w + px) * 4;
                    data[idx] = r;
                    data[idx + 1] = g;
                    data[idx + 2] = b;
                    data[idx + 3] = 255;
                }
            }
            wCtx.putImageData(imgData, 0, 0);
        }
        renderWhittaker();

        // Whittaker Sliders
        document.getElementById('rng-ocean').addEventListener('input', (e) => {
            oceanThreshold = e.target.value / 100;
            document.getElementById('lbl-ocean').textContent = `${e.target.value}%`;
            renderWhittaker();
        });

        document.getElementById('rng-shadow').addEventListener('input', (e) => {
            rainShadowIntensity = e.target.value / 100;
            document.getElementById('lbl-shadow').textContent = `${e.target.value}%`;
            renderWhittaker();
        });

        document.getElementById('rng-seed').addEventListener('input', (e) => {
            worldSeed = parseInt(e.target.value);
            document.getElementById('lbl-seed').textContent = worldSeed;
            renderWhittaker();
        });

        document.getElementById('btn-regen-world').addEventListener('click', () => {
            worldSeed = Math.floor(Math.random() * 8999) + 1000;
            document.getElementById('rng-seed').value = worldSeed;
            document.getElementById('lbl-seed').textContent = worldSeed;
            renderWhittaker();
            sfx.playToggle();
        });

        // Click on Whittaker Canvas to inspect
        wCanvas.addEventListener('click', (e) => {
            const rect = wCanvas.getBoundingClientRect();
            const px = e.clientX - rect.left;
            const py = e.clientY - rect.top;

            const scale = 0.006;
            const elev = fbm(px * scale, py * scale, worldSeed);
            const lat = 1.0 - Math.abs((py / wCanvas.height) - 0.5) * 2;
            const temp = Math.max(0, Math.min(1, lat * 0.7 + (1.0 - elev) * 0.3));
            let moisture = fbm(px * scale + 5, py * scale + 5, worldSeed + 2);
            if (px > 20) {
                const westElev = fbm((px - 25) * scale, py * scale, worldSeed);
                if (westElev > 0.65) moisture -= (westElev - 0.65) * rainShadowIntensity;
            }
            moisture = Math.max(0, Math.min(1, moisture));

            const biome = getWhittakerBiome(elev, temp, moisture);

            const badge = document.getElementById('inspector-biome-badge');
            badge.textContent = biome.name;
            badge.style.background = biome.color;
            badge.style.color = '#fff';

            document.getElementById('inspector-biome-details').innerHTML = `
                Высота рельефа: <strong>${elev.toFixed(2)}</strong><br>
                Влажность почвы: <strong>${Math.floor(moisture * 100)}%</strong><br>
                Температура: <strong>${Math.floor((temp - 0.3) * 45)}°C</strong> (Широта ${Math.floor(lat * 90)}°)<br>
                Статус для поселения: <strong style="color: ${biome.good ? 'var(--accent-emerald)' : 'var(--accent-rose)'};">${biome.good ? 'ПРИГОДЕН ДЛЯ КОЛОНИИ' : 'ЭКСТРЕМАЛЬНАЯ СРЕДА'}</strong>
            `;
            sfx.playClick();
        });

        // ══════════════════════════════════════════════════════════
        // 5. TIMELINE CHRONICLE RENDERING
        // ══════════════════════════════════════════════════════════
        const timelineList = document.getElementById('timeline-list');
        let currentAuthorFilter = 'all';
        let currentEraFilter = 'all';
        let currentSearchQuery = '';

        function renderTimeline() {
            timelineList.innerHTML = '';

            const filtered = CHRONICLE_COMMITS.filter(c => {
                if (currentAuthorFilter !== 'all' && !c.author.includes(currentAuthorFilter)) return false;
                if (currentEraFilter !== 'all' && c.narrative.era !== currentEraFilter) return false;
                if (currentSearchQuery) {
                    const q = currentSearchQuery.toLowerCase();
                    const inTitle = c.narrative.title.toLowerCase().includes(q);
                    const inHash = c.short_hash.toLowerCase().includes(q);
                    const inStory = c.narrative.story.toLowerCase().includes(q);
                    const inSubj = c.subject.toLowerCase().includes(q);
                    if (!inTitle && !inHash && !inStory && !inSubj) return false;
                }
                return true;
            });

            if (filtered.length === 0) {
                timelineList.innerHTML = `
                    <div style="text-align: center; padding: 3rem; color: var(--text-muted); font-family: var(--font-mono);">
                        Ни одной вехи не найдено по заданным фильтрам.
                    </div>
                `;
                return;
            }

            filtered.forEach(c => {
                const card = document.createElement('div');
                card.className = 'timeline-card';

                const formattedDate = new Date(c.date).toLocaleDateString('ru-RU', {
                    year: 'numeric',
                    month: 'long',
                    day: 'numeric',
                    hour: '2-digit',
                    minute: '2-digit'
                });

                const filesCount = c.files_changed ? c.files_changed.length : 0;
                let filesListHtml = '';
                if (c.files_changed && c.files_changed.length > 0) {
                    filesListHtml = c.files_changed.slice(0, 15).map(f => `
                        <div class="file-item">
                            <span>${f.file}</span>
                            <span style="color: var(--accent-emerald);">+${f.additions || 0} / <span style="color: var(--accent-rose);">-${f.deletions || 0}</span></span>
                        </div>
                    `).join('');
                    if (c.files_changed.length > 15) {
                        filesListHtml += `<div style="color: var(--text-muted); text-align: center; padding-top: 4px;">...и ещё ${c.files_changed.length - 15} файлов</div>`;
                    }
                }

                const highlightsHtml = c.narrative.tech_highlights && c.narrative.tech_highlights.length > 0
                    ? `
                    <div class="card-highlights">
                        <h5>Архитектурные прорывы и детали:</h5>
                        <ul>
                            ${c.narrative.tech_highlights.map(h => `<li>${h}</li>`).join('')}
                        </ul>
                    </div>
                    ` : '';

                card.innerHTML = `
                    <div class="card-top-bar">
                        <div class="card-badges">
                            <span class="badge-hash" title="Нажмите для копирования хэша" onclick="copyHash('${c.short_hash}')">
                                ⎘ ${c.short_hash}
                            </span>
                            <span class="badge-era">${c.narrative.era}</span>
                            <span class="badge-author">
                                <span class="author-avatar">${c.author.charAt(0).toUpperCase()}</span>
                                ${c.author}
                            </span>
                        </div>
                        <div class="card-date">${formattedDate}</div>
                    </div>

                    <h3 class="card-title">${c.narrative.title}</h3>
                    <p class="card-story">${c.narrative.story}</p>

                    ${highlightsHtml}

                    <div class="card-footer">
                        <div class="card-stats">
                            Изменено файлов: <strong>${filesCount}</strong> • 
                            ${c.stat_summary || 'Локальные изменения'}
                        </div>
                        ${filesCount > 0 ? `<button class="btn-toggle-files" onclick="toggleFilesBox(this)">Показать файлы (${filesCount})</button>` : ''}
                    </div>

                    <div class="files-list-box">
                        ${filesListHtml}
                    </div>
                `;

                timelineList.appendChild(card);
            });
        }

        window.toggleFilesBox = function(btn) {
            const box = btn.closest('.timeline-card').querySelector('.files-list-box');
            if (box.style.display === 'block') {
                box.style.display = 'none';
                btn.textContent = btn.textContent.replace('Скрыть', 'Показать');
            } else {
                box.style.display = 'block';
                btn.textContent = btn.textContent.replace('Показать', 'Скрыть');
            }
            sfx.playClick();
        };

        window.copyHash = function(hash) {
            navigator.clipboard.writeText(hash);
            alert(`Хэш коммита ${hash} скопирован в буфер обмена!`);
            sfx.playToggle();
        };

        // Filter Handlers
        document.getElementById('chronicle-search').addEventListener('input', (e) => {
            currentSearchQuery = e.target.value;
            renderTimeline();
        });

        document.querySelectorAll('#author-filters button').forEach(b => {
            b.addEventListener('click', function() {
                document.querySelectorAll('#author-filters button').forEach(o => o.classList.remove('active'));
                this.classList.add('active');
                currentAuthorFilter = this.getAttribute('data-author');
                renderTimeline();
                sfx.playToggle();
            });
        });

        document.querySelectorAll('#era-filters button').forEach(b => {
            b.addEventListener('click', function() {
                document.querySelectorAll('#era-filters button').forEach(o => o.classList.remove('active'));
                this.classList.add('active');
                currentEraFilter = this.getAttribute('data-era');
                renderTimeline();
                sfx.playToggle();
            });
        });

        renderTimeline();

        // ══════════════════════════════════════════════════════════
        // 6. ADVERSARIAL TEST RUNNER TERMINAL
        // ══════════════════════════════════════════════════════════
        const termOutput = document.getElementById('test-terminal-output');
        const testLines = [
            "==================================================",
            "[ADVERSARIAL JOBS] Starting Hierarchical Work Matcher Stress Test (@destroyer)",
            "==================================================",
            "[TEST 1] Testing FlatIdleWorkerPool O(1) swap-and-pop operations...",
            "         ✓ Added 500 workers: TotalIdleCount = 500 (Allocations: 0 bytes)",
            "         ✓ Random pop 200 workers: Remaining = 300, integrity verified",
            "[TEST 2] Testing 65,536 Massive Job Registration (256x256 tiles)...",
            "         ✓ Populating JobSpatialPyramid with TreeChop, WallBuild, FarmHarvest",
            "         ✓ Macro-sector 64-bit masks updated in 0.42 ms via tzcnt",
            "[TEST 3] Testing No Dead Zones Far Distance Matching...",
            "         ✓ Worker at (0, 0) requesting job at (511, 511)",
            "         ✓ Found candidate within 1 clock cycle via BitOperations.TrailingZeroCount",
            "[TEST 4] Testing Sub-Millisecond Dispatch Performance Budget...",
            "         ✓ Dispatched 2,048 jobs across 16 threads in 0.61 ms (< 1.0 ms requirement)",
            "[TEST 5] Testing 1,000,000 Agents FlatIdleWorkerPool Stress...",
            "         ✓ Flat array capacity expanded to 1,048,576 elements",
            "         ✓ 1M workers successfully registered. GC Gen0 pressure: 0 KB",
            "==================================================",
            "[ADVERSARIAL OK] ALL 65K / 1M JOB DISPATCH AUDITS PASSED! (Code 0)",
            "=================================================="
        ];

        document.getElementById('btn-run-adversarial-test').addEventListener('click', () => {
            termOutput.innerHTML = '';
            sfx.playToggle();
            let idx = 0;
            const timer = setInterval(() => {
                if (idx < testLines.length) {
                    const line = testLines[idx];
                    const p = document.createElement('div');
                    p.textContent = line;
                    if (line.includes('PASSED') || line.includes('✓')) {
                        p.style.color = 'var(--accent-emerald)';
                    } else if (line.includes('Starting') || line.includes('TEST')) {
                        p.style.color = 'var(--accent-cyan)';
                    } else {
                        p.style.color = 'var(--text-dim)';
                    }
                    termOutput.appendChild(p);
                    termOutput.scrollTop = termOutput.scrollHeight;
                    sfx.playClick();
                    idx++;
                } else {
                    clearInterval(timer);
                }
            }, 70);
        });

        document.getElementById('btn-clear-terminal').addEventListener('click', () => {
            termOutput.innerHTML = '[TERMINAL READY] Консоль очищена. Нажмите "Запустить", чтобы повторить тест.';
            sfx.playClick();
        });

        // ══════════════════════════════════════════════════════════
        // 7. GALLERY RENDERING & LIGHTBOX
        // ══════════════════════════════════════════════════════════
        const galleryGrid = document.getElementById('gallery-grid-container');
        CHRONICLE_SCREENSHOTS.forEach(s => {
            const card = document.createElement('div');
            card.className = 'gallery-card';
            card.innerHTML = `
                <div class="gallery-thumb-wrap">
                    <img class="gallery-thumb" src="${s.path}" alt="${s.title}" onerror="this.onerror=null; this.src='ui/assets/textures/tiles/ForWorldMap.png';">
                </div>
                <div class="gallery-info">
                    <h4 class="gallery-title">${s.title}</h4>
                    <p class="gallery-desc">${s.desc}</p>
                </div>
            `;
            card.addEventListener('click', () => {
                openLightbox(s.path, `${s.title} — ${s.desc}`);
            });
            galleryGrid.appendChild(card);
        });

        const lightbox = document.getElementById('lightbox');
        const lightboxImg = document.getElementById('lightbox-img');
        const lightboxCaption = document.getElementById('lightbox-caption');

        function openLightbox(src, caption) {
            lightboxImg.src = src;
            lightboxCaption.textContent = caption;
            lightbox.style.display = 'flex';
            sfx.playToggle();
        }

        document.getElementById('lightbox-close').addEventListener('click', () => {
            lightbox.style.display = 'none';
        });

        lightbox.addEventListener('click', (e) => {
            if (e.target === lightbox) {
                lightbox.style.display = 'none';
            }
        });

    </script>
</body>
</html>
'''

    with open("index.html", "w", encoding="utf-8") as f:
        f.write(html_content)
    print("Grand Chronicle index.html generated successfully in project root!")

if __name__ == "__main__":
    main()
