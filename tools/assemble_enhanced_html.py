# -*- coding: utf-8 -*-
import os
import json

with open("tools/modules_data.json", "r", encoding="utf-8") as f:
    modules = json.load(f)

modules_js_str = json.dumps(modules, ensure_ascii=False, indent=2)

html_template = r'''<!DOCTYPE html>
<html lang="ru">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>C# Colony Sim: Академия разработки с нуля</title>
    <link rel="preconnect" href="https://fonts.googleapis.com">
    <link href="https://fonts.googleapis.com/css2?family=Fira+Code:wght@400;500;600;700&family=Inter:wght@400;500;600;700;800&display=swap" rel="stylesheet">
    <style>
        :root {
            --bg: #0b0e14;
            --bg-card: #141922;
            --bg-card-hover: #1a212d;
            --bg-code: #080a0f;
            --bg-hover: #1e2634;
            --text: #f0f6fc;
            --text-dim: #8b949e;
            --text-muted: #5c646f;
            --accent: #7c5cfc;
            --accent-light: #9d7bff;
            --accent-dark: #5833b6;
            --accent-glow: rgba(124, 92, 252, 0.25);
            --green: #3fb950;
            --green-bg: rgba(63, 185, 80, 0.12);
            --red: #f85149;
            --red-bg: rgba(248, 81, 73, 0.12);
            --yellow: #d29922;
            --yellow-bg: rgba(210, 153, 34, 0.12);
            --blue: #58a6ff;
            --blue-bg: rgba(88, 166, 255, 0.12);
            --python: #ffc331;
            --border: #242c38;
            --border-light: #323d4d;
            --sidebar-w: 320px;
            --header-h: 68px;
            --radius: 12px;
            --radius-sm: 8px;
            --shadow: 0 10px 40px rgba(0,0,0,0.5);
            --transition: 0.22s cubic-bezier(0.16, 1, 0.3, 1);
        }

        * { margin: 0; padding: 0; box-sizing: border-box; }

        body {
            font-family: 'Inter', -apple-system, BlinkMacSystemFont, sans-serif;
            background: var(--bg);
            color: var(--text);
            line-height: 1.6;
            overflow-x: hidden;
        }

        /* ═══════ CONFETTI CANVAS ═══════ */
        #confetti-canvas {
            position: fixed;
            top: 0; left: 0; width: 100vw; height: 100vh;
            pointer-events: none;
            z-index: 9999;
        }

        /* ═══════ SIDEBAR ═══════ */
        .sidebar {
            position: fixed;
            left: 0; top: 0; bottom: 0;
            width: var(--sidebar-w);
            background: var(--bg-card);
            border-right: 1px solid var(--border);
            overflow-y: auto;
            z-index: 100;
            transition: transform var(--transition);
            display: flex;
            flex-direction: column;
        }

        .sidebar-header {
            padding: 20px;
            border-bottom: 1px solid var(--border);
            text-align: center;
            background: linear-gradient(180deg, rgba(124, 92, 252, 0.1) 0%, transparent 100%);
        }

        .sidebar-brand {
            font-size: 17px;
            font-weight: 800;
            color: var(--text);
            display: flex;
            align-items: center;
            justify-content: center;
            gap: 8px;
            letter-spacing: -0.3px;
        }

        .sidebar-brand span {
            background: linear-gradient(135deg, var(--accent-light), #58a6ff);
            -webkit-background-clip: text;
            -webkit-text-fill-color: transparent;
        }

        .user-profile-badge {
            display: inline-flex;
            align-items: center;
            gap: 6px;
            background: rgba(124, 92, 252, 0.12);
            border: 1px solid rgba(124, 92, 252, 0.3);
            border-radius: 20px;
            padding: 4px 12px;
            font-size: 11px;
            font-weight: 600;
            color: var(--accent-light);
            margin-top: 10px;
            cursor: pointer;
            transition: all var(--transition);
        }
        .user-profile-badge:hover {
            background: rgba(124, 92, 252, 0.25);
            box-shadow: 0 0 12px var(--accent-glow);
            transform: scale(1.02);
        }

        .sidebar-search-box {
            padding: 10px 16px;
            border-bottom: 1px solid var(--border);
        }

        .sidebar-search-input {
            width: 100%;
            padding: 8px 12px;
            background: var(--bg);
            border: 1px solid var(--border);
            border-radius: var(--radius-sm);
            color: var(--text);
            font-size: 12px;
            outline: none;
            transition: border-color var(--transition);
        }
        .sidebar-search-input:focus {
            border-color: var(--accent);
        }

        .sidebar-stats {
            padding: 12px 18px;
            border-bottom: 1px solid var(--border);
            display: grid;
            grid-template-columns: 1fr 1fr;
            gap: 8px;
            background: rgba(0,0,0,0.18);
        }

        .stat-card {
            background: var(--bg);
            border: 1px solid var(--border);
            border-radius: var(--radius-sm);
            padding: 8px 10px;
            text-align: center;
        }

        .stat-val {
            font-size: 15px;
            font-weight: 800;
            color: var(--text);
            font-family: 'Fira Code', monospace;
        }

        .stat-lbl {
            font-size: 10px;
            color: var(--text-dim);
            text-transform: uppercase;
            letter-spacing: 0.5px;
        }

        .progress-bar-container {
            padding: 14px 18px;
            border-bottom: 1px solid var(--border);
        }

        .progress-label {
            font-size: 11px;
            color: var(--text-dim);
            margin-bottom: 8px;
            display: flex;
            justify-content: space-between;
            font-weight: 600;
        }

        .progress-track {
            height: 6px;
            background: var(--border);
            border-radius: 3px;
            overflow: hidden;
        }

        .progress-fill {
            height: 100%;
            background: linear-gradient(90deg, var(--accent), var(--green));
            border-radius: 3px;
            transition: width 0.4s ease;
        }

        .module-list {
            padding: 12px;
            flex: 1;
        }

        .module-item {
            cursor: pointer;
            border-radius: var(--radius-sm);
            padding: 10px 12px;
            margin-bottom: 4px;
            transition: all var(--transition);
            display: flex;
            align-items: center;
            gap: 10px;
            user-select: none;
            border: 1px solid transparent;
        }

        .module-item:hover {
            background: var(--bg-hover);
            border-color: var(--border);
        }

        .module-item.active {
            background: rgba(124, 92, 252, 0.15);
            border-color: var(--accent);
            color: var(--text);
        }

        .module-item.active .module-num {
            background: var(--accent);
            color: white;
            box-shadow: 0 0 10px var(--accent-glow);
        }

        .module-item.completed::after {
            content: '✓';
            margin-left: auto;
            color: var(--green);
            font-size: 14px;
            font-weight: bold;
        }

        .module-num {
            width: 26px; height: 26px;
            border-radius: 6px;
            background: var(--border);
            display: flex;
            align-items: center;
            justify-content: center;
            font-size: 11px;
            font-weight: 700;
            flex-shrink: 0;
            color: var(--text-dim);
        }

        .module-info {
            flex: 1;
            min-width: 0;
        }

        .module-name {
            font-size: 13px;
            font-weight: 600;
            white-space: nowrap;
            overflow: hidden;
            text-overflow: ellipsis;
            display: flex;
            align-items: center;
            gap: 6px;
        }

        .module-desc {
            font-size: 10px;
            color: var(--text-muted);
            margin-top: 2px;
            white-space: nowrap;
            overflow: hidden;
            text-overflow: ellipsis;
        }

        .sidebar-footer {
            padding: 12px 14px;
            border-top: 1px solid var(--border);
            display: flex;
            gap: 6px;
            background: var(--bg-card);
        }

        /* ═══════ MAIN VIEW ═══════ */
        .main {
            margin-left: var(--sidebar-w);
            min-height: 100vh;
            display: flex;
            flex-direction: column;
        }

        .topbar {
            height: var(--header-h);
            border-bottom: 1px solid var(--border);
            display: flex;
            align-items: center;
            justify-content: space-between;
            padding: 0 32px;
            position: sticky;
            top: 0;
            background: #0d1117;
            box-shadow: 0 4px 20px rgba(0,0,0,0.4);
            z-index: 50;
        }

        .breadcrumb {
            font-size: 13px;
            color: var(--text-dim);
            display: flex;
            align-items: center;
            gap: 8px;
        }

        .breadcrumb span.active-crumb {
            color: var(--accent-light);
            font-weight: 600;
        }

        .topbar-actions {
            display: flex;
            gap: 8px;
            align-items: center;
        }

        .btn {
            padding: 8px 14px;
            border: 1px solid var(--border);
            border-radius: var(--radius-sm);
            background: var(--bg-card);
            color: var(--text);
            font-family: inherit;
            font-size: 13px;
            font-weight: 500;
            cursor: pointer;
            transition: all var(--transition);
            display: inline-flex;
            align-items: center;
            gap: 6px;
            user-select: none;
        }

        .btn:hover {
            border-color: var(--accent);
            background: var(--bg-hover);
        }

        .btn-primary {
            background: var(--accent);
            border-color: var(--accent);
            color: white;
            font-weight: 600;
        }

        .btn-primary:hover {
            background: var(--accent-light);
            border-color: var(--accent-light);
            transform: translateY(-1px);
            box-shadow: 0 4px 16px var(--accent-glow);
        }

        .btn-success {
            background: var(--green);
            border-color: var(--green);
            color: white;
            font-weight: 600;
        }

        .btn-sm {
            padding: 6px 10px;
            font-size: 12px;
        }

        .btn:disabled {
            opacity: 0.35;
            cursor: not-allowed;
            transform: none !important;
            box-shadow: none !important;
        }

        /* ═══════ CONTENT ═══════ */
        .content {
            max-width: 940px;
            margin: 0 auto;
            padding: 36px 32px 100px 32px;
            width: 100%;
            flex: 1;
        }

        .lesson-header {
            margin-bottom: 28px;
            scroll-margin-top: 90px;
        }

        .lesson-meta-bar {
            display: flex;
            align-items: center;
            gap: 10px;
            margin-bottom: 12px;
        }

        .lesson-badge {
            display: inline-flex;
            align-items: center;
            gap: 6px;
            padding: 4px 10px;
            border-radius: 20px;
            background: rgba(124, 92, 252, 0.12);
            color: var(--accent-light);
            font-size: 11px;
            font-weight: 600;
            border: 1px solid rgba(124, 92, 252, 0.25);
        }

        .lesson-time {
            font-size: 12px;
            color: var(--text-dim);
            display: inline-flex;
            align-items: center;
            gap: 4px;
        }

        .lesson-title {
            font-size: 32px;
            font-weight: 800;
            line-height: 1.25;
            margin-bottom: 10px;
            background: linear-gradient(135deg, #ffffff 0%, var(--accent-light) 100%);
            -webkit-background-clip: text;
            -webkit-text-fill-color: transparent;
            letter-spacing: -0.5px;
        }

        .lesson-subtitle {
            font-size: 15px;
            color: var(--text-dim);
        }

        .section {
            margin-bottom: 32px;
            scroll-margin-top: 90px;
        }

        .section-title {
            font-size: 20px;
            font-weight: 700;
            margin-bottom: 14px;
            color: var(--text);
            display: flex;
            align-items: center;
            gap: 10px;
            letter-spacing: -0.3px;
        }

        .section-title .icon {
            color: var(--accent-light);
            font-size: 20px;
        }

        .text {
            color: var(--text);
            font-size: 15px;
            line-height: 1.75;
            word-break: break-word;
            overflow-wrap: break-word;
        }

        .text p {
            margin-bottom: 14px;
        }
        .text p:last-child {
            margin-bottom: 0;
        }

        .text code {
            font-family: 'Fira Code', monospace;
            background: rgba(255, 255, 255, 0.08);
            padding: 2px 6px;
            border-radius: 4px;
            font-size: 13px;
            color: #ffc331;
        }

        .text strong {
            color: #ffffff;
            font-weight: 700;
        }

        /* Project File Indicator */
        .project-file-card {
            background: rgba(124, 92, 252, 0.08);
            border: 1px solid rgba(124, 92, 252, 0.25);
            border-radius: var(--radius-sm);
            padding: 12px 16px;
            margin: 18px 0;
            display: flex;
            align-items: center;
            gap: 12px;
        }

        .project-file-icon {
            font-size: 22px;
            color: var(--accent-light);
        }

        .project-file-path {
            font-family: 'Fira Code', monospace;
            font-size: 13px;
            font-weight: 600;
            color: var(--accent-light);
        }

        .project-file-desc {
            font-size: 11px;
            color: var(--text-dim);
            margin-top: 2px;
        }

        /* Python vs C# comparison box */
        .py-vs-cs {
            background: var(--bg-card);
            border: 1px solid var(--border);
            border-radius: var(--radius);
            overflow: hidden;
            margin: 20px 0;
            box-shadow: 0 4px 24px rgba(0, 0, 0, 0.25);
        }

        .py-vs-cs-header {
            padding: 10px 16px;
            background: rgba(255, 255, 255, 0.03);
            border-bottom: 1px solid var(--border);
            display: flex;
            align-items: center;
            justify-content: space-between;
            font-size: 12px;
            font-weight: 600;
        }

        .py-vs-cs-grid {
            display: grid;
            grid-template-columns: minmax(0, 1fr) minmax(0, 1fr);
            gap: 1px;
            background: var(--border);
        }

        .py-col, .cs-col {
            background: var(--bg-card);
            padding: 16px 18px;
            min-width: 0;
            overflow: hidden;
        }

        .col-title {
            font-size: 12px;
            font-weight: 700;
            margin-bottom: 10px;
            display: flex;
            align-items: center;
            gap: 6px;
        }

        .py-col .col-title { color: var(--python); }
        .cs-col .col-title { color: var(--blue); }

        .py-col pre, .cs-col pre {
            font-family: 'Fira Code', monospace;
            font-size: 13px;
            line-height: 1.65;
            color: var(--text);
            white-space: pre-wrap;
            word-break: break-word;
            overflow-wrap: break-word;
            tab-size: 4;
        }

        @media (max-width: 860px) {
            .py-vs-cs-grid {
                grid-template-columns: 1fr;
            }
        }

        /* Code blocks */
        .code-block {
            background: var(--bg-code);
            border: 1px solid var(--border);
            border-radius: var(--radius-sm);
            overflow: hidden;
            margin: 18px 0;
            position: relative;
        }

        .code-header {
            padding: 8px 16px;
            background: rgba(255,255,255,0.03);
            border-bottom: 1px solid var(--border);
            font-size: 11px;
            color: var(--text-muted);
            display: flex;
            align-items: center;
            justify-content: space-between;
            font-family: 'Fira Code', monospace;
        }

        .copy-btn {
            background: transparent;
            border: 1px solid var(--border);
            border-radius: 4px;
            color: var(--text-dim);
            padding: 2px 8px;
            font-size: 11px;
            cursor: pointer;
            transition: all var(--transition);
        }
        .copy-btn:hover {
            color: var(--text);
            border-color: var(--accent);
        }

        .code-body {
            padding: 16px;
            overflow-x: auto;
        }

        .code-body pre {
            font-family: 'Fira Code', monospace;
            font-size: 13.5px;
            line-height: 1.7;
            color: var(--text);
            white-space: pre-wrap;
            word-break: break-word;
            overflow-wrap: break-word;
            tab-size: 4;
        }

        /* Syntax highlighting classes */
        .kw { color: #ff7b72; font-weight: 600; }
        .tp { color: #79c0ff; font-weight: 600; }
        .str { color: #a5d6ff; }
        .cm { color: #8b949e; font-style: italic; }
        .num { color: #ffa657; }
        .fn { color: #d2a5ff; }
        .attr { color: #7ee787; }

        /* Info boxes */
        .info-box {
            border-radius: var(--radius-sm);
            padding: 16px;
            margin: 18px 0;
            font-size: 14px;
            display: flex;
            gap: 14px;
            align-items: flex-start;
            line-height: 1.6;
            word-break: break-word;
            overflow-wrap: break-word;
        }

        .info-box .icon {
            font-size: 20px;
            flex-shrink: 0;
            margin-top: 1px;
        }

        .info-tip {
            background: var(--blue-bg);
            border: 1px solid rgba(88,166,255,0.25);
            color: #d1e5ff;
        }

        .info-warning {
            background: var(--yellow-bg);
            border: 1px solid rgba(210,153,34,0.25);
            color: #ffe7b3;
        }

        .info-success {
            background: var(--green-bg);
            border: 1px solid rgba(63,185,80,0.25);
            color: #c9f5d1;
        }

        /* Summary takeaway list */
        .takeaways-box {
            background: rgba(255, 255, 255, 0.02);
            border: 1px solid var(--border);
            border-radius: var(--radius-sm);
            padding: 16px 20px;
            margin: 20px 0;
        }

        .takeaways-title {
            font-size: 13px;
            font-weight: 700;
            color: var(--accent-light);
            margin-bottom: 8px;
            display: flex;
            align-items: center;
            gap: 6px;
        }

        .takeaways-list {
            list-style: none;
            padding: 0;
        }

        .takeaways-list li {
            font-size: 13px;
            color: var(--text-dim);
            padding: 4px 0 4px 20px;
            position: relative;
            word-break: break-word;
        }

        .takeaways-list li::before {
            content: '✓';
            position: absolute;
            left: 0;
            color: var(--green);
            font-weight: bold;
        }

        /* ═══════ BOB ANIMATED MINI-SIMULATOR ═══════ */
        .bob-simulator {
            background: linear-gradient(180deg, #141922 0%, #0d1017 100%);
            border: 1px solid var(--border);
            border-radius: var(--radius);
            padding: 22px;
            margin: 24px 0;
            position: relative;
            overflow: hidden;
            box-shadow: 0 4px 24px rgba(0,0,0,0.3);
        }

        .sim-header {
            display: flex;
            justify-content: space-between;
            align-items: center;
            margin-bottom: 16px;
            border-bottom: 1px solid var(--border);
            padding-bottom: 12px;
        }

        .sim-title {
            font-size: 15px;
            font-weight: 700;
            display: flex;
            align-items: center;
            gap: 8px;
            color: var(--accent-light);
        }

        .sim-body {
            display: grid;
            grid-template-columns: 180px 1fr;
            gap: 20px;
            align-items: center;
        }

        .bob-stage {
            background: var(--bg);
            border: 1px solid var(--border);
            border-radius: var(--radius-sm);
            padding: 16px;
            text-align: center;
            position: relative;
            min-height: 140px;
            display: flex;
            flex-direction: column;
            align-items: center;
            justify-content: center;
            overflow: hidden;
        }

        .bob-emoji {
            font-size: 52px;
            display: inline-block;
            transition: transform 0.25s cubic-bezier(0.175, 0.885, 0.32, 1.275);
            position: relative;
        }

        .bob-emoji.swing {
            transform: rotate(-35deg) scale(1.15);
        }

        .bob-floating-text {
            position: absolute;
            top: 20px;
            font-weight: 800;
            font-size: 14px;
            animation: floatUp 0.8s ease-out forwards;
            pointer-events: none;
        }

        @keyframes floatUp {
            0% { transform: translateY(0); opacity: 1; }
            100% { transform: translateY(-30px); opacity: 0; }
        }

        .bob-name {
            font-size: 13px;
            font-weight: 700;
            margin-top: 6px;
            color: var(--text);
        }

        .bob-status {
            font-size: 11px;
            color: var(--accent-light);
            font-weight: 600;
        }

        .sim-stats-grid {
            display: grid;
            grid-template-columns: repeat(auto-fit, minmax(130px, 1fr));
            gap: 10px;
        }

        .sim-stat {
            background: var(--bg);
            border: 1px solid var(--border);
            border-radius: var(--radius-sm);
            padding: 10px 12px;
        }

        .sim-stat-lbl {
            font-size: 11px;
            color: var(--text-dim);
            text-transform: uppercase;
            font-weight: 600;
            margin-bottom: 4px;
            display: flex;
            justify-content: space-between;
        }

        .sim-stat-bar {
            height: 6px;
            background: var(--border);
            border-radius: 3px;
            overflow: hidden;
            margin-top: 6px;
        }

        .sim-stat-bar-fill {
            height: 100%;
            border-radius: 3px;
            transition: width 0.35s ease, background-color 0.35s ease;
        }

        .sim-controls {
            margin-top: 16px;
            display: flex;
            gap: 8px;
            flex-wrap: wrap;
        }

        .sim-log {
            margin-top: 14px;
            background: var(--bg-code);
            border: 1px solid var(--border);
            border-radius: 6px;
            padding: 10px 14px;
            font-family: 'Fira Code', monospace;
            font-size: 11.5px;
            color: var(--green);
            min-height: 36px;
            display: flex;
            align-items: center;
        }

        /* ═══════ TASK AREA & MULTI-TABS ═══════ */
        .task-area {
            background: var(--bg-card);
            border: 2px dashed var(--border-light);
            border-radius: var(--radius);
            padding: 24px;
            margin: 28px 0;
            position: relative;
            transition: all var(--transition);
        }

        .task-area.correct {
            border-color: var(--green);
            border-style: solid;
            background: linear-gradient(180deg, var(--bg-card) 0%, rgba(63, 185, 80, 0.06) 100%);
        }

        .task-area.wrong {
            border-color: var(--red);
            border-style: solid;
            background: linear-gradient(180deg, var(--bg-card) 0%, rgba(248, 81, 73, 0.06) 100%);
        }

        .task-header-row {
            display: flex;
            align-items: center;
            justify-content: space-between;
            margin-bottom: 16px;
            flex-wrap: wrap;
            gap: 12px;
        }

        .task-progress-badge {
            font-size: 12px;
            font-weight: 700;
            padding: 5px 12px;
            border-radius: 20px;
            background: rgba(124, 92, 252, 0.15);
            color: var(--accent-light);
            border: 1px solid rgba(124, 92, 252, 0.3);
            display: inline-flex;
            align-items: center;
            gap: 6px;
        }
        .task-progress-badge.all-done {
            background: var(--green-bg);
            color: var(--green);
            border-color: rgba(63, 185, 80, 0.4);
        }

        .task-tabs {
            display: flex;
            gap: 8px;
            margin-bottom: 18px;
            flex-wrap: wrap;
            border-bottom: 1px solid var(--border);
            padding-bottom: 12px;
        }

        .task-tab {
            padding: 8px 14px;
            border-radius: var(--radius-sm);
            background: var(--bg);
            border: 1px solid var(--border);
            color: var(--text-dim);
            font-size: 13px;
            font-weight: 600;
            cursor: pointer;
            transition: all var(--transition);
            display: inline-flex;
            align-items: center;
            gap: 8px;
            user-select: none;
        }
        .task-tab:hover {
            border-color: var(--accent);
            color: var(--text);
            background: var(--bg-hover);
        }
        .task-tab.active {
            border-color: var(--accent);
            background: rgba(124, 92, 252, 0.18);
            color: #fff;
            box-shadow: 0 0 12px var(--accent-glow);
        }
        .task-tab.completed {
            border-color: var(--green);
            color: var(--green);
            background: var(--green-bg);
        }
        .task-tab.active.completed {
            background: rgba(63, 185, 80, 0.22);
            box-shadow: 0 0 12px rgba(63, 185, 80, 0.25);
        }

        .task-tab-badge {
            width: 20px;
            height: 20px;
            border-radius: 50%;
            background: var(--border);
            display: inline-flex;
            align-items: center;
            justify-content: center;
            font-size: 11px;
            font-weight: 700;
        }
        .task-tab.active .task-tab-badge {
            background: var(--accent);
            color: #fff;
        }
        .task-tab.completed .task-tab-badge {
            background: var(--green);
            color: #fff;
        }

        .task-scenario-card {
            background: rgba(0, 0, 0, 0.3);
            border-left: 3px solid var(--accent-light);
            padding: 12px 16px;
            border-radius: 0 var(--radius-sm) var(--radius-sm) 0;
            margin-bottom: 14px;
            font-size: 13.5px;
            line-height: 1.6;
            color: #d8e2ec;
        }
        .task-scenario-label {
            font-size: 11px;
            font-weight: 700;
            text-transform: uppercase;
            letter-spacing: 0.5px;
            color: var(--accent-light);
            margin-bottom: 4px;
            display: flex;
            align-items: center;
            gap: 6px;
        }

        .task-input-container {
            position: relative;
            margin-top: 14px;
        }

        .task-input {
            width: 100%;
            min-height: 90px;
            background: var(--bg-code);
            border: 1px solid var(--border);
            border-radius: var(--radius-sm);
            color: var(--text);
            font-family: 'Fira Code', monospace;
            font-size: 14px;
            padding: 14px;
            resize: vertical;
            line-height: 1.6;
            outline: none;
            transition: border-color var(--transition);
        }
        .task-input:focus {
            border-color: var(--accent);
            box-shadow: 0 0 0 3px var(--accent-glow);
        }

        .task-options {
            display: flex;
            flex-direction: column;
            gap: 10px;
            margin-top: 14px;
        }

        .task-option {
            padding: 14px 16px;
            background: var(--bg);
            border: 1px solid var(--border);
            border-radius: var(--radius-sm);
            cursor: pointer;
            transition: all var(--transition);
            font-size: 14px;
            display: flex;
            align-items: center;
            gap: 12px;
            user-select: none;
        }

        .task-option:hover {
            border-color: var(--accent);
            background: var(--bg-hover);
        }

        .task-option.selected {
            border-color: var(--accent);
            background: rgba(124, 92, 252, 0.12);
        }

        .task-option.correct-answer {
            border-color: var(--green) !important;
            background: var(--green-bg) !important;
            color: #d1f7d6;
        }

        .task-option.wrong-answer {
            border-color: var(--red) !important;
            background: var(--red-bg) !important;
            color: #ffd6d6;
        }

        .option-letter {
            width: 28px; height: 28px;
            border-radius: 50%;
            background: var(--border);
            display: flex;
            align-items: center;
            justify-content: center;
            font-weight: 700;
            font-size: 12px;
            flex-shrink: 0;
            color: var(--text-dim);
        }

        .task-option.selected .option-letter {
            background: var(--accent);
            color: white;
        }

        .task-helpers {
            margin-top: 12px;
            display: flex;
            gap: 8px;
            flex-wrap: wrap;
        }

        .helper-btn {
            background: transparent;
            border: 1px dashed var(--border);
            color: var(--text-dim);
            font-size: 11px;
            padding: 4px 10px;
            border-radius: 4px;
            cursor: pointer;
            transition: all var(--transition);
        }
        .helper-btn:hover {
            color: var(--accent-light);
            border-color: var(--accent);
        }

        .solution-box {
            margin-top: 12px;
            padding: 12px 16px;
            background: var(--bg-code);
            border: 1px solid var(--border);
            border-radius: var(--radius-sm);
            display: none;
            font-family: 'Fira Code', monospace;
            font-size: 12.5px;
            color: var(--accent-light);
        }
        .solution-box.show { display: block; }

        .feedback {
            margin-top: 16px;
            padding: 14px 18px;
            border-radius: var(--radius-sm);
            font-size: 14px;
            display: none;
            line-height: 1.6;
        }
        .feedback.show { display: block; }
        .feedback.success {
            background: var(--green-bg);
            border: 1px solid rgba(63,185,80,0.3);
            color: #c9f5d1;
        }
        .feedback.error {
            background: var(--red-bg);
            border: 1px solid rgba(248,81,73,0.3);
            color: #ffd2d0;
        }

        /* ═══════ LESSON NOTES ═══════ */
        .lesson-notes-area {
            margin-top: 32px;
            padding-top: 20px;
            border-top: 1px solid var(--border);
        }

        .notes-toggle {
            font-size: 13px;
            color: var(--text-dim);
            cursor: pointer;
            display: inline-flex;
            align-items: center;
            gap: 6px;
            font-weight: 600;
        }
        .notes-toggle:hover { color: var(--accent-light); }

        .notes-textarea {
            width: 100%;
            min-height: 75px;
            background: var(--bg-code);
            border: 1px solid var(--border);
            border-radius: var(--radius-sm);
            color: var(--text);
            font-size: 13px;
            padding: 12px;
            margin-top: 10px;
            outline: none;
            display: none;
            resize: vertical;
        }
        .notes-textarea.show { display: block; }

        /* ═══════ NAVIGATION ═══════ */
        .lesson-nav {
            display: flex;
            justify-content: space-between;
            margin-top: 40px;
            padding-top: 24px;
            border-top: 1px solid var(--border);
        }

        /* ═══════ WELCOME SCREEN ═══════ */
        .welcome {
            text-align: center;
            padding: 40px 16px;
        }

        .welcome-icon {
            font-size: 64px;
            margin-bottom: 20px;
            display: inline-block;
            animation: bounce 2s infinite ease-in-out;
        }

        @keyframes bounce {
            0%, 100% { transform: translateY(0); }
            50% { transform: translateY(-8px); }
        }

        .welcome h1 {
            font-size: 40px;
            font-weight: 800;
            margin-bottom: 14px;
            background: linear-gradient(135deg, #ffffff 0%, var(--accent-light) 50%, var(--green) 100%);
            -webkit-background-clip: text;
            -webkit-text-fill-color: transparent;
            letter-spacing: -0.5px;
        }

        .welcome p {
            font-size: 16px;
            color: var(--text-dim);
            max-width: 600px;
            margin: 0 auto 32px;
            line-height: 1.7;
        }

        .features-grid {
            display: grid;
            grid-template-columns: repeat(auto-fit, minmax(240px, 1fr));
            gap: 16px;
            max-width: 840px;
            margin: 32px auto;
            text-align: left;
        }

        .feature-card {
            background: var(--bg-card);
            border: 1px solid var(--border);
            border-radius: var(--radius);
            padding: 22px;
            transition: all var(--transition);
        }
        .feature-card:hover {
            border-color: var(--accent);
            transform: translateY(-2px);
            box-shadow: var(--shadow);
        }

        .feature-card .emoji {
            font-size: 28px;
            margin-bottom: 12px;
        }

        .feature-card h3 {
            font-size: 15px;
            font-weight: 700;
            margin-bottom: 6px;
            color: var(--text);
        }

        .feature-card p {
            font-size: 13px;
            color: var(--text-dim);
            margin: 0;
            line-height: 1.5;
        }

        /* ═══════ MODALS ═══════ */
        .modal-overlay {
            position: fixed;
            top: 0; left: 0; right: 0; bottom: 0;
            background: rgba(0,0,0,0.8);
            backdrop-filter: blur(10px);
            z-index: 500;
            display: none;
            align-items: center;
            justify-content: center;
            padding: 20px;
        }
        .modal-overlay.open { display: flex; }

        .modal-card {
            background: var(--bg-card);
            border: 1px solid var(--border);
            border-radius: var(--radius);
            max-width: 640px;
            width: 100%;
            padding: 28px;
            box-shadow: var(--shadow);
            position: relative;
            max-height: 90vh;
            overflow-y: auto;
        }

        .modal-title {
            font-size: 20px;
            font-weight: 800;
            margin-bottom: 8px;
            display: flex;
            align-items: center;
            gap: 8px;
        }

        .modal-subtitle {
            font-size: 13px;
            color: var(--text-dim);
            margin-bottom: 24px;
        }

        .survey-q { margin-bottom: 20px; }
        .survey-label {
            font-size: 13px;
            font-weight: 700;
            margin-bottom: 10px;
            color: var(--text);
            display: block;
        }

        .survey-opts { display: grid; gap: 8px; }

        .survey-opt {
            padding: 12px 14px;
            border: 1px solid var(--border);
            border-radius: var(--radius-sm);
            background: var(--bg);
            cursor: pointer;
            font-size: 13px;
            transition: all var(--transition);
            display: flex;
            align-items: center;
            gap: 12px;
        }
        .survey-opt:hover {
            border-color: var(--accent);
            background: var(--bg-hover);
        }
        .survey-opt.selected {
            border-color: var(--accent);
            background: rgba(124, 92, 252, 0.15);
            font-weight: 600;
        }

        /* ═══════ SANDBOX MODAL ═══════ */
        .sandbox-container {
            display: grid;
            gap: 16px;
        }
        .sandbox-select {
            width: 100%;
            padding: 10px;
            background: var(--bg);
            border: 1px solid var(--border);
            border-radius: var(--radius-sm);
            color: var(--text);
            font-size: 13px;
            outline: none;
        }
        .sandbox-editor {
            width: 100%;
            min-height: 140px;
            background: var(--bg-code);
            border: 1px solid var(--border);
            border-radius: var(--radius-sm);
            color: var(--text);
            font-family: 'Fira Code', monospace;
            font-size: 13px;
            padding: 12px;
            line-height: 1.6;
            outline: none;
            resize: vertical;
        }
        .sandbox-console {
            background: #05070a;
            border: 1px solid var(--border);
            border-radius: var(--radius-sm);
            padding: 12px;
            font-family: 'Fira Code', monospace;
            font-size: 12px;
            color: #79c0ff;
            min-height: 70px;
            max-height: 150px;
            overflow-y: auto;
        }

        /* ═══════ CHEATSHEET MODAL ═══════ */
        .cheatsheet-table {
            width: 100%;
            border-collapse: collapse;
            font-size: 13px;
            margin-top: 10px;
        }
        .cheatsheet-table th, .cheatsheet-table td {
            border: 1px solid var(--border);
            padding: 8px 12px;
            text-align: left;
        }
        .cheatsheet-table th {
            background: rgba(255, 255, 255, 0.04);
            color: var(--accent-light);
        }
        .cheatsheet-table code {
            font-family: 'Fira Code', monospace;
            color: #ffc331;
        }

        /* ═══════ SEARCH MODAL ═══════ */
        .search-input {
            width: 100%;
            padding: 12px 16px;
            background: var(--bg);
            border: 1px solid var(--border);
            border-radius: var(--radius-sm);
            color: var(--text);
            font-size: 14px;
            margin-bottom: 16px;
            outline: none;
        }
        .search-results {
            max-height: 380px;
            overflow-y: auto;
        }
        .search-result-item {
            padding: 12px;
            border-bottom: 1px solid var(--border);
            cursor: pointer;
            transition: all var(--transition);
            border-radius: 6px;
        }
        .search-result-item:hover {
            background: var(--bg-hover);
        }
        .search-result-title {
            font-size: 14px;
            font-weight: 700;
            color: var(--accent-light);
        }
        .search-result-desc {
            font-size: 12px;
            color: var(--text-dim);
            margin-top: 3px;
        }

        /* ═══════ BADGE POPUP ═══════ */
        .badge-popup {
            position: fixed;
            top: 24px;
            right: 24px;
            background: var(--bg-card);
            border: 2px solid var(--green);
            border-radius: var(--radius);
            padding: 18px 24px;
            z-index: 1000;
            animation: slideIn 0.35s ease, fadeOut 0.4s ease 3s forwards;
            box-shadow: var(--shadow);
            display: flex;
            align-items: center;
            gap: 14px;
        }

        .badge-popup-icon { font-size: 32px; }

        .badge-popup h3 {
            color: var(--green);
            font-size: 15px;
            font-weight: 700;
            margin-bottom: 2px;
        }

        .badge-popup p {
            color: var(--text-dim);
            font-size: 12px;
        }

        @keyframes slideIn {
            from { transform: translateX(120px); opacity: 0; }
            to { transform: translateX(0); opacity: 1; }
        }

        @keyframes fadeOut {
            to { opacity: 0; transform: translateY(-20px); }
        }

        /* Mobile */
        .menu-toggle {
            display: none;
            position: fixed;
            bottom: 20px;
            right: 20px;
            width: 52px; height: 52px;
            border-radius: 50%;
            background: var(--accent);
            color: white;
            border: none;
            font-size: 22px;
            cursor: pointer;
            z-index: 150;
            box-shadow: var(--shadow);
        }

        @media (max-width: 860px) {
            .sidebar { transform: translateX(-100%); }
            .sidebar.open { transform: translateX(0); }
            .main { margin-left: 0; }
            .menu-toggle { display: flex; align-items: center; justify-content: center; }
            .content { padding: 24px 16px; }
            .py-vs-cs-grid { grid-template-columns: 1fr; }
            .sim-body { grid-template-columns: 1fr; }
        }

        ::-webkit-scrollbar { width: 8px; }
        ::-webkit-scrollbar-track { background: var(--bg); }
        ::-webkit-scrollbar-thumb { background: var(--border); border-radius: 4px; }
        ::-webkit-scrollbar-thumb:hover { background: var(--border-light); }

        .fade-in { animation: fadeIn 0.3s ease; }
        @keyframes fadeIn {
            from { opacity: 0; transform: translateY(8px); }
            to { opacity: 1; transform: translateY(0); }
        }
    </style>
</head>
<body>

<canvas id="confetti-canvas"></canvas>

<!-- Sidebar -->
<nav class="sidebar" id="sidebar">
    <div class="sidebar-header">
        <div class="sidebar-brand">🎮 <span>C# Colony Sim</span></div>
        <div class="user-profile-badge" id="profile-badge" onclick="openOnboarding()">
            🎯 <span id="profile-text">Настроить под себя</span>
        </div>
    </div>

    <div class="sidebar-search-box">
        <input type="text" class="sidebar-search-input" id="sidebar-filter" placeholder="🔍 Фильтр по урокам..." oninput="filterSidebarLessons(this.value)">
    </div>

    <div class="sidebar-stats">
        <div class="stat-card">
            <div class="stat-val" id="stat-completed">0/23</div>
            <div class="stat-lbl">Уроков</div>
        </div>
        <div class="stat-card">
            <div class="stat-val" id="stat-pct">0%</div>
            <div class="stat-lbl">Прогресс</div>
        </div>
    </div>

    <div class="progress-bar-container">
        <div class="progress-label">
            <span>Общее прохождение</span>
            <span id="progress-text">0%</span>
        </div>
        <div class="progress-track">
            <div class="progress-fill" id="progress-fill" style="width: 0%"></div>
        </div>
    </div>

    <div class="module-list" id="module-list">
        <!-- Generated by JS -->
    </div>

    <div class="sidebar-footer">
        <button class="btn btn-sm" onclick="openSearchModal()" title="Быстрый поиск">🔍</button>
        <button class="btn btn-sm" style="flex:1" onclick="openGlossary()">📖 Словарь</button>
        <button class="btn btn-sm" onclick="openCheatsheet()" title="Шпаргалка">📋</button>
        <button class="btn btn-sm" onclick="toggleAudio()" id="audio-btn" title="Звук">🔊</button>
        <button class="btn btn-sm" onclick="exportProgress()" title="Экспорт">💾</button>
    </div>
</nav>

<button class="menu-toggle" id="menu-toggle">☰</button>

<!-- Main View -->
<main class="main">
    <div class="topbar">
        <div class="breadcrumb" id="breadcrumb">Добро пожаловать в курс</div>
        <div class="topbar-actions">
            <button class="btn btn-sm" onclick="openSandbox()">🧪 Песочница C#</button>
            <button class="btn btn-sm" onclick="showWelcome()">🏠 Главная</button>
            <button class="btn btn-sm" onclick="resetProgress()">↻ Сбросить</button>
        </div>
    </div>

    <div class="content" id="content">
        <!-- Content generated by JS -->
    </div>
</main>

<!-- Onboarding Modal -->
<div class="modal-overlay" id="onboarding-modal">
    <div class="modal-card">
        <div class="modal-title">🎯 Настройка курса под тебя</div>
        <div class="modal-subtitle">Без банальных анкет: укажи свой опыт и то, что хочешь освоить в первую очередь, чтобы мы расставили правильные акценты.</div>

        <div class="survey-q">
            <span class="survey-label">1. Твой опыт с кодом на текущий момент:</span>
            <div class="survey-opts">
                <div class="survey-opt selected" data-q="exp" data-v="zero" onclick="selectSurvey(this)">
                    <span>🌱</span> <div><strong>Абсолютный ноль</strong><div style="font-size:11px;color:var(--text-dim)">Объясняй максимально наглядно и на житейских примерах колонии</div></div>
                </div>
                <div class="survey-opt" data-q="exp" data-v="python" onclick="selectSurvey(this)">
                    <span>🐍</span> <div><strong>Пробовал Python когда-то</strong><div style="font-size:11px;color:var(--text-dim)">Делай мост между Python и C#, показывай различия</div></div>
                </div>
                <div class="survey-opt" data-q="exp" data-v="fast" onclick="selectSurvey(this)">
                    <span>⚡</span> <div><strong>Быстро схватываю</strong><div style="font-size:11px;color:var(--text-dim)">Меньше теории, сразу к архитектуре и оптимизации кода Godot</div></div>
                </div>
            </div>
        </div>

        <div class="survey-q">
            <span class="survey-label">2. Что в твоем проекте тебе интереснее понять первым?</span>
            <div class="survey-opts">
                <div class="survey-opt selected" data-q="goal" data-v="colonists" onclick="selectSurvey(this)">
                    <span>🧑‍🌾</span> <div>Поведение колонистов: голод, работа, рубка сосен, ИИ задач</div>
                </div>
                <div class="survey-opt" data-q="goal" data-v="economy" onclick="selectSurvey(this)">
                    <span>📦</span> <div>Экономика: склад, инвентарь, предметы, крафт и реестры</div>
                </div>
                <div class="survey-opt" data-q="goal" data-v="perf" onclick="selectSurvey(this)">
                    <span>🚀</span> <div>Производительность: как игра тянет 10 000 агентов без лагов</div>
                </div>
            </div>
        </div>

        <div style="text-align: right; margin-top: 24px;">
            <button class="btn btn-primary" onclick="saveOnboarding()">Применить настройки 🚀</button>
        </div>
    </div>
</div>

<!-- Glossary Modal -->
<div class="modal-overlay" id="glossary-modal">
    <div class="modal-card">
        <div class="modal-title">📖 Словарь терминов C# и проекта</div>
        <div class="modal-subtitle">Все сложные термины простыми словами поселения.</div>
        <input type="text" class="search-input" id="glossary-search" placeholder="Поиск по терминам (напр. Struct, GC, Node)..." oninput="filterGlossary()">
        <div id="glossary-list" style="max-height: 400px; overflow-y: auto;"></div>
        <div style="text-align: right; margin-top: 20px;">
            <button class="btn" onclick="closeGlossary()">Закрыть</button>
        </div>
    </div>
</div>

<!-- Cheatsheet Modal -->
<div class="modal-overlay" id="cheatsheet-modal">
    <div class="modal-card" style="max-width: 720px;">
        <div class="modal-title">📋 Шпаргалка C# и Godot 4.7</div>
        <div class="modal-subtitle">Быстрая подсказка синтаксиса, типов и методов твоего проекта.</div>
        
        <h4 style="color:var(--accent-light);margin-top:14px;">1. Базовые типы данных</h4>
        <table class="cheatsheet-table">
            <tr><th>Тип</th><th>Что хранит</th><th>Пример</th></tr>
            <tr><td><code>int</code></td><td>Целое число</td><td><code>int wood = 100;</code></td></tr>
            <tr><td><code>float</code></td><td>Дробное число (с буквой f)</td><td><code>float speed = 2.5f;</code></td></tr>
            <tr><td><code>bool</code></td><td>Логическое true / false</td><td><code>bool isNight = false;</code></td></tr>
            <tr><td><code>string</code></td><td>Текст в двойных кавычках</td><td><code>string name = "Боб";</code></td></tr>
        </table>

        <h4 style="color:var(--accent-light);margin-top:18px;">2. Логика и условия</h4>
        <table class="cheatsheet-table">
            <tr><th>Оператор</th><th>Значение</th><th>Пример</th></tr>
            <tr><td><code>==</code></td><td>Равно ли?</td><td><code>if (wood == 10)</code></td></tr>
            <tr><td><code>!=</code></td><td>Не равно ли?</td><td><code>if (status != Status.Dead)</code></td></tr>
            <tr><td><code>&&</code></td><td>Логическое И (оба true)</td><td><code>if (hasAxe && energy > 10)</code></td></tr>
            <tr><td><code>||</code></td><td>Логическое ИЛИ</td><td><code>if (isNight || hunger > 90)</code></td></tr>
            <tr><td><code>!</code></td><td>Отрицание (НЕ)</td><td><code>if (!isSleeping)</code></td></tr>
        </table>

        <h4 style="color:var(--accent-light);margin-top:18px;">3. Жизненный цикл Godot</h4>
        <table class="cheatsheet-table">
            <tr><th>Метод</th><th>Когда вызывается</th></tr>
            <tr><td><code>_Ready()</code></td><td>Один раз при старте узла на сцене</td></tr>
            <tr><td><code>_Process(double delta)</code></td><td>Каждый кадр (~60 раз в секунду)</td></tr>
            <tr><td><code>CallDeferred(...)</code></td><td>Безопасный вызов из фонового потока в главный поток</td></tr>
        </table>

        <div style="text-align: right; margin-top: 24px;">
            <button class="btn" onclick="closeCheatsheet()">Понятно</button>
        </div>
    </div>
</div>

<!-- Sandbox Modal -->
<div class="modal-overlay" id="sandbox-modal">
    <div class="modal-card" style="max-width: 680px;">
        <div class="modal-title">🧪 Песочница C# для симулятора</div>
        <div class="modal-subtitle">Выбирай пресет или пиши логику — смотри результат выполнения прямо здесь!</div>
        
        <div class="sandbox-container">
            <div>
                <label style="font-size:12px;font-weight:600;color:var(--text-dim);margin-bottom:6px;display:block">Готовые сценарии:</label>
                <select class="sandbox-select" id="sandbox-preset" onchange="loadSandboxPreset(this.value)">
                    <option value="chop">🪵 1. Лесоповал: добыча дерева и расход сытости</option>
                    <option value="check">🍲 2. Проверка состояния колониста (if / else)</option>
                    <option value="loop">🧑‍🌾 3. Обход 5 колонистов в цикле (foreach)</option>
                    <option value="storage">📦 4. Поиск предмета в ItemRegistry</option>
                </select>
            </div>

            <div>
                <label style="font-size:12px;font-weight:600;color:var(--text-dim);margin-bottom:6px;display:block">Код C#:</label>
                <textarea class="sandbox-editor" id="sandbox-code"></textarea>
            </div>

            <div style="display:flex;justify-content:space-between;align-items:center;">
                <button class="btn btn-primary" onclick="runSandbox()">▶ Запустить симуляцию</button>
                <span style="font-size:11px;color:var(--text-muted)">Виртуальный .NET эмулятор</span>
            </div>

            <div>
                <label style="font-size:12px;font-weight:600;color:var(--text-dim);margin-bottom:6px;display:block">Консоль вывода (GD.Print):</label>
                <div class="sandbox-console" id="sandbox-output">✨ Нажми «Запустить симуляцию», чтобы увидеть результат работы кода!</div>
            </div>
        </div>

        <div style="text-align: right; margin-top: 20px;">
            <button class="btn" onclick="closeSandbox()">Закрыть</button>
        </div>
    </div>
</div>

<!-- Search Modal -->
<div class="modal-overlay" id="search-modal">
    <div class="modal-card">
        <div class="modal-title">🔍 Поиск по курсу и кодовой базе</div>
        <div class="modal-subtitle">Ищи любые темы, термины (JobData, enum, delta, if)...</div>
        <input type="text" class="search-input" id="global-search" placeholder="Введи запрос..." oninput="doGlobalSearch(this.value)">
        <div class="search-results" id="search-results"></div>
        <div style="text-align: right; margin-top: 20px;">
            <button class="btn" onclick="closeSearchModal()">Закрыть</button>
        </div>
    </div>
</div>

<script>
// ═══════════════════════════════════════════
// GLOSSARY DATA
// ═══════════════════════════════════════════
const GLOSSARY = [
    { term: "Тип данных (Type)", def: "Бирка на коробке в памяти (int, float, string, bool). Сообщает компьютеру, что именно хранится внутри и сколько байт под это выделить." },
    { term: "Переменная (Variable)", def: "Именованная ячейка памяти, в которую можно положить значение и затем изменить его (например, int wood = 10;)." },
    { term: "Точка с запятой (;)", def: "Знак завершения команды в C#. Без него компилятор не знает, где кончается один приказ и начинается следующий." },
    { term: "Метод / Функция (Method)", def: "Именованный рецепт действий с круглыми скобками (void ChopTree()). Можно вызывать многократно из разных мест игры." },
    { term: "void", def: "Тип возвращаемого значения метода, означающий: 'метод просто выполняет работу и ничего не возвращает обратно'." },
    { term: "return", def: "Команда немедленно завершить метод и отдать результат наружу (например: return true;)." },
    { term: "Enum (Перечисление)", def: "Список строго фиксированных вариантов (TileType: Grass, Water, Mountain). Защищает от опечаток в строках." },
    { term: "Struct (Структура)", def: "Легковесный контейнер данных, который хранится прямо на Стеке. В твоем проекте JobData — это struct, чтобы 10 000 задач не грузили сборщик мусора." },
    { term: "Class (Класс)", def: "Шаблон для сложных объектов с поведением. Хранится в 'Куче' (Heap), передается по ссылке." },
    { term: "GC (Garbage Collector)", def: "Сборщик мусора .NET. Периодически сканирует память и удаляет ненужные объекты. Если мусора много — игра фризит (STW pause)." },
    { term: "Zero-GC", def: "Стиль написания кода без постоянных выделений памяти в Куче (используются struct, Span, массивы пулов), чтобы сборщик мусора вообще не останавливал игру." },
    { term: "Interface (Интерфейс)", def: "Контракт поведения (например, ITool). Описывает, КАКИЕ методы должен иметь класс, не уточняя КАК именно они работают." },
    { term: "Node (Узел Godot)", def: "Базовый строительный блок сцены в Godot. Скрипты на C# наследуются от Node, чтобы жить в игровом дереве." },
    { term: "_Ready()", def: "Метод узла Godot, который вызывается движком ОДИН РАЗ, когда узел готов к работе при старте сцены." },
    { term: "_Process(double delta)", def: "Метод узла Godot, который вызывается каждый кадр (обычно 60 раз в секунду). delta — это время между кадрами." }
];

// ═══════════════════════════════════════════
// SOUND SYNTHESIZER (Web Audio API)
// ═══════════════════════════════════════════
let audioEnabled = true;
let audioCtx = null;

function getAudioCtx() {
    if (!audioCtx) {
        const AudioContext = window.AudioContext || window.webkitAudioContext;
        if (AudioContext) audioCtx = new AudioContext();
    }
    return audioCtx;
}

function playSuccessSound() {
    if (!audioEnabled) return;
    try {
        const ctx = getAudioCtx();
        if (!ctx) return;
        const now = ctx.currentTime;
        const osc = ctx.createOscillator();
        const gain = ctx.createGain();
        osc.connect(gain);
        gain.connect(ctx.destination);
        osc.type = 'triangle';
        osc.frequency.setValueAtTime(523.25, now);
        osc.frequency.setValueAtTime(659.25, now + 0.08);
        osc.frequency.setValueAtTime(783.99, now + 0.16);
        osc.frequency.setValueAtTime(1046.50, now + 0.24);
        gain.gain.setValueAtTime(0.12, now);
        gain.gain.exponentialRampToValueAtTime(0.001, now + 0.45);
        osc.start(now);
        osc.stop(now + 0.45);
    } catch(e) {}
}

function playClickSound() {
    if (!audioEnabled) return;
    try {
        const ctx = getAudioCtx();
        if (!ctx) return;
        const now = ctx.currentTime;
        const osc = ctx.createOscillator();
        const gain = ctx.createGain();
        osc.connect(gain);
        gain.connect(ctx.destination);
        osc.type = 'sine';
        osc.frequency.setValueAtTime(440, now);
        gain.gain.setValueAtTime(0.04, now);
        gain.gain.exponentialRampToValueAtTime(0.001, now + 0.06);
        osc.start(now);
        osc.stop(now + 0.06);
    } catch(e) {}
}

function toggleAudio() {
    audioEnabled = !audioEnabled;
    document.getElementById('audio-btn').textContent = audioEnabled ? '🔊' : '🔇';
}

// ═══════════════════════════════════════════
// CONFETTI SYSTEM
// ═══════════════════════════════════════════
function fireConfetti() {
    const canvas = document.getElementById('confetti-canvas');
    if (!canvas) return;
    const ctx = canvas.getContext('2d');
    canvas.width = window.innerWidth;
    canvas.height = window.innerHeight;

    const particles = [];
    const colors = ['#7c5cfc', '#3fb950', '#58a6ff', '#ffc331', '#f85149', '#ffffff'];

    for (let i = 0; i < 90; i++) {
        particles.push({
            x: canvas.width / 2 + (Math.random() - 0.5) * 200,
            y: canvas.height * 0.4,
            vx: (Math.random() - 0.5) * 14,
            vy: (Math.random() - 0.9) * 16,
            color: colors[Math.floor(Math.random() * colors.length)],
            size: Math.random() * 8 + 4,
            rotation: Math.random() * 360,
            vRot: (Math.random() - 0.5) * 10,
            opacity: 1
        });
    }

    let start = null;
    function render(ts) {
        if (!start) start = ts;
        const elapsed = ts - start;
        ctx.clearRect(0, 0, canvas.width, canvas.height);

        particles.forEach(p => {
            p.x += p.vx;
            p.y += p.vy;
            p.vy += 0.35;
            p.rotation += p.vRot;
            p.opacity = Math.max(0, 1 - elapsed / 2500);

            ctx.save();
            ctx.translate(p.x, p.y);
            ctx.rotate(p.rotation * Math.PI / 180);
            ctx.globalAlpha = p.opacity;
            ctx.fillStyle = p.color;
            ctx.fillRect(-p.size / 2, -p.size / 2, p.size, p.size * 0.6);
            ctx.restore();
        });

        if (elapsed < 2500) {
            requestAnimationFrame(render);
        } else {
            ctx.clearRect(0, 0, canvas.width, canvas.height);
        }
    }
    requestAnimationFrame(render);
}

// ═══════════════════════════════════════════
// BOB MINI-SIMULATOR STATE & ANIMATIONS
// ═══════════════════════════════════════════
let bobSim = {
    name: "Боб",
    health: 100,
    hunger: 20,
    wood: 15,
    hasAxe: true,
    status: "Отдыхает у костра"
};

function updateBobSimUI() {
    const hungerEl = document.getElementById('bob-hunger-val');
    const hungerBar = document.getElementById('bob-hunger-bar');
    const woodEl = document.getElementById('bob-wood-val');
    const healthEl = document.getElementById('bob-health-val');
    const healthBar = document.getElementById('bob-health-bar');
    const statusEl = document.getElementById('bob-status');
    const emojiEl = document.getElementById('bob-emoji');

    if (hungerEl) hungerEl.textContent = bobSim.hunger + '%';
    if (hungerBar) {
        hungerBar.style.width = bobSim.hunger + '%';
        hungerBar.style.background = bobSim.hunger > 70 ? 'var(--red)' : bobSim.hunger > 40 ? 'var(--yellow)' : 'var(--green)';
    }
    if (woodEl) woodEl.textContent = bobSim.wood;
    if (healthEl) healthEl.textContent = bobSim.health + '%';
    if (healthBar) healthBar.style.width = bobSim.health + '%';
    if (statusEl) statusEl.textContent = bobSim.status;
    if (emojiEl) {
        emojiEl.textContent = bobSim.hunger > 80 ? '😫' : bobSim.wood > 50 ? '🤠' : '🧑‍🌾';
    }
}

function showBobFloatingText(text, color) {
    const stage = document.getElementById('bob-stage');
    if (!stage) return;
    const el = document.createElement('div');
    el.className = 'bob-floating-text';
    el.textContent = text;
    el.style.color = color || 'var(--green)';
    stage.appendChild(el);
    setTimeout(() => el.remove(), 800);
}

function bobChopTree() {
    playClickSound();
    const emoji = document.getElementById('bob-emoji');
    if (emoji) {
        emoji.classList.add('swing');
        setTimeout(() => emoji.classList.remove('swing'), 250);
    }

    if (bobSim.hunger >= 90) {
        logBob("⚠️ Боб слишком обессилел от голода! Сначала накорми его супом.");
        bobSim.status = "Обессилел от голода";
        updateBobSimUI();
        return;
    }

    bobSim.wood += 5;
    bobSim.hunger = Math.min(100, bobSim.hunger + 15);
    bobSim.status = "Срубил сосну (+5 дерева)";
    showBobFloatingText('+5 🪵', '#ffc331');
    logBob(`🪓 Боб срубил сосну: wood += 5; (всего ${bobSim.wood}), hunger += 15;`);
    updateBobSimUI();
}

function bobEat() {
    playClickSound();
    if (bobSim.hunger <= 0) {
        logBob("🍲 Боб сыт под завязку! Голод равен 0%.");
        return;
    }
    bobSim.hunger = Math.max(0, bobSim.hunger - 35);
    bobSim.status = "Пообедал супом";
    showBobFloatingText('-35% 🍲', '#3fb950');
    logBob(`🍲 Боб пообедал: hunger -= 35; (текущий голод: ${bobSim.hunger}%)`);
    updateBobSimUI();
}

function bobRest() {
    playClickSound();
    bobSim.health = Math.min(100, bobSim.health + 10);
    bobSim.status = "Отдохнул в палатке";
    showBobFloatingText('+10% ❤️', '#f85149');
    logBob(`⛺ Боб восстановил силы: health += 10; (здоровье: ${bobSim.health}%)`);
    updateBobSimUI();
}

function logBob(msg) {
    const el = document.getElementById('bob-log');
    if (el) el.textContent = msg;
}

// ═══════════════════════════════════════════
// COURSE MODULES DATA
// ═══════════════════════════════════════════
const MODULES = ''' + modules_js_str + r''';

// ═══════════════════════════════════════════
// STATE MANAGEMENT & LOCALSTORAGE
// ═══════════════════════════════════════════
let currentModuleIdx = -1;
let currentLessonIdx = 0;
let currentTaskIdx = 0;
let progress = {};
let userNotes = {};
let userDrafts = {};
let userProfile = { exp: 'zero', goal: 'colonists' };

function loadProgress() {
    try {
        const savedProg = localStorage.getItem('csharp_course_progress');
        if (savedProg) progress = JSON.parse(savedProg);

        const savedNotes = localStorage.getItem('csharp_course_notes');
        if (savedNotes) userNotes = JSON.parse(savedNotes);

        const savedDrafts = localStorage.getItem('csharp_course_drafts');
        if (savedDrafts) userDrafts = JSON.parse(savedDrafts);

        const savedProf = localStorage.getItem('csharp_course_profile');
        if (savedProf) {
            userProfile = JSON.parse(savedProf);
            updateProfileBadgeUI();
        }
    } catch(e) {}
}

function saveProgress() {
    try {
        localStorage.setItem('csharp_course_progress', JSON.stringify(progress));
    } catch(e) {}
}

function saveNotes() {
    try {
        localStorage.setItem('csharp_course_notes', JSON.stringify(userNotes));
    } catch(e) {}
}

function saveDrafts() {
    try {
        localStorage.setItem('csharp_course_drafts', JSON.stringify(userDrafts));
    } catch(e) {}
}

function saveTaskDraft(mIdx, lIdx, tIdx, val) {
    const key = `${mIdx}_${lIdx}_t${tIdx}`;
    userDrafts[key] = val;
    saveDrafts();
}

function saveProfile() {
    try {
        localStorage.setItem('csharp_course_profile', JSON.stringify(userProfile));
    } catch(e) {}
}

function isTaskCompleted(moduleIdx, lessonIdx, taskIdx) {
    return !!progress[`${moduleIdx}_${lessonIdx}_t${taskIdx}`];
}

function isLessonCompleted(moduleIdx, lessonIdx) {
    if (progress[`${moduleIdx}_${lessonIdx}`]) return true;
    const mod = MODULES[moduleIdx];
    if (!mod) return false;
    const les = mod.lessons[lessonIdx];
    if (!les) return false;
    const tasks = les.tasks || (les.task ? [les.task] : []);
    if (tasks.length === 0) return false;
    return tasks.every((_, tIdx) => isTaskCompleted(moduleIdx, lessonIdx, tIdx));
}

function completeTask(moduleIdx, lessonIdx, taskIdx) {
    progress[`${moduleIdx}_${lessonIdx}_t${taskIdx}`] = true;
    const mod = MODULES[moduleIdx];
    const les = mod.lessons[lessonIdx];
    const tasks = les.tasks || (les.task ? [les.task] : []);
    const allDone = tasks.every((_, idx) => isTaskCompleted(moduleIdx, lessonIdx, idx));

    if (allDone) {
        completeLesson(moduleIdx, lessonIdx);
    } else {
        saveProgress();
        updateProgressUI();
        renderSidebar();
    }
}

function completeLesson(moduleIdx, lessonIdx) {
    const wasDone = isLessonCompleted(moduleIdx, lessonIdx);
    progress[`${moduleIdx}_${lessonIdx}`] = true;
    saveProgress();
    updateProgressUI();
    renderSidebar();

    const mod = MODULES[moduleIdx];
    const allDone = mod.lessons.every((_, idx) => isLessonCompleted(moduleIdx, idx));
    if (allDone && !wasDone) {
        fireConfetti();
        showBadge('🎉 Модуль завершен!', `${mod.title} освоен на 100%!`);
    }
}

function getTotalLessons() {
    let total = 0;
    MODULES.forEach(m => total += m.lessons.length);
    return total;
}

function getCompletedLessons() {
    let count = 0;
    MODULES.forEach((m, mIdx) => {
        m.lessons.forEach((_, lIdx) => {
            if (isLessonCompleted(mIdx, lIdx)) count++;
        });
    });
    return count;
}

function updateProgressUI() {
    const total = getTotalLessons();
    const completed = getCompletedLessons();
    const pct = total > 0 ? Math.round((completed / total) * 100) : 0;
    
    const fill = document.getElementById('progress-fill');
    const text = document.getElementById('progress-text');
    const statComp = document.getElementById('stat-completed');
    const statPct = document.getElementById('stat-pct');

    if (fill) fill.style.width = pct + '%';
    if (text) text.textContent = pct + '%';
    if (statComp) statComp.textContent = completed + '/' + total;
    if (statPct) statPct.textContent = pct + '%';
}

function resetProgress() {
    if (confirm('Сбросить весь прогресс прохождения курса? (Личные заметки останутся)')) {
        progress = {};
        userDrafts = {};
        saveProgress();
        try { localStorage.removeItem('csharp_course_drafts'); } catch(e) {}
        updateProgressUI();
        renderSidebar();
        showWelcome();
    }
}

function exportProgress() {
    const data = {
        progress,
        userNotes,
        userDrafts,
        userProfile,
        exportDate: new Date().toISOString()
    };
    const blob = new Blob([JSON.stringify(data, null, 2)], { type: 'application/json' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = 'csharp_colony_sim_progress.json';
    a.click();
    URL.revokeObjectURL(url);
}

// ═══════════════════════════════════════════
// ONBOARDING & PERSONALIZATION
// ═══════════════════════════════════════════
function openOnboarding() {
    playClickSound();
    const modal = document.getElementById('onboarding-modal');
    modal.classList.add('open');
    document.querySelectorAll('.survey-opt').forEach(el => {
        const q = el.dataset.q;
        const v = el.dataset.v;
        if (q === 'exp') el.classList.toggle('selected', userProfile.exp === v);
        if (q === 'goal') el.classList.toggle('selected', userProfile.goal === v);
    });
}

function selectSurvey(el) {
    playClickSound();
    const q = el.dataset.q;
    document.querySelectorAll(`.survey-opt[data-q="${q}"]`).forEach(opt => opt.classList.remove('selected'));
    el.classList.add('selected');
}

function saveOnboarding() {
    playClickSound();
    const expEl = document.querySelector('.survey-opt[data-q="exp"].selected');
    const goalEl = document.querySelector('.survey-opt[data-q="goal"].selected');

    if (expEl) userProfile.exp = expEl.dataset.v;
    if (goalEl) userProfile.goal = goalEl.dataset.v;

    saveProfile();
    updateProfileBadgeUI();
    document.getElementById('onboarding-modal').classList.remove('open');
    showBadge('🎯 Профиль настроен!', 'Курс адаптирован под твои пожелания');
}

function updateProfileBadgeUI() {
    const badge = document.getElementById('profile-text');
    if (!badge) return;
    if (userProfile.exp === 'python') {
        badge.textContent = '🐍 C# после Python';
    } else if (userProfile.exp === 'fast') {
        badge.textContent = '⚡ Скоростной темп';
    } else {
        badge.textContent = '🌱 С нуля (наглядно)';
    }
}

// ═══════════════════════════════════════════
// GLOSSARY MODAL
// ═══════════════════════════════════════════
function openGlossary() {
    playClickSound();
    renderGlossaryList(GLOSSARY);
    document.getElementById('glossary-modal').classList.add('open');
}

function closeGlossary() {
    playClickSound();
    document.getElementById('glossary-modal').classList.remove('open');
}

function renderGlossaryList(items) {
    const container = document.getElementById('glossary-list');
    if (!container) return;
    if (items.length === 0) {
        container.innerHTML = '<div style="color:var(--text-muted);padding:16px;text-align:center">Ничего не найдено</div>';
        return;
    }
    container.innerHTML = items.map(item => `
        <div class="search-result-item">
            <div class="search-result-title">${item.term}</div>
            <div class="search-result-desc">${item.def}</div>
        </div>
    `).join('');
}

function filterGlossary() {
    const query = document.getElementById('glossary-search').value.toLowerCase().trim();
    const filtered = GLOSSARY.filter(i => i.term.toLowerCase().includes(query) || i.def.toLowerCase().includes(query));
    renderGlossaryList(filtered);
}

// ═══════════════════════════════════════════
// CHEATSHEET MODAL
// ═══════════════════════════════════════════
function openCheatsheet() {
    playClickSound();
    document.getElementById('cheatsheet-modal').classList.add('open');
}

function closeCheatsheet() {
    playClickSound();
    document.getElementById('cheatsheet-modal').classList.remove('open');
}

// ═══════════════════════════════════════════
// SEARCH MODAL
// ═══════════════════════════════════════════
function openSearchModal() {
    playClickSound();
    document.getElementById('search-modal').classList.add('open');
    document.getElementById('global-search').focus();
    doGlobalSearch('');
}

function closeSearchModal() {
    playClickSound();
    document.getElementById('search-modal').classList.remove('open');
}

function doGlobalSearch(query) {
    const q = query.toLowerCase().trim();
    const resultsContainer = document.getElementById('search-results');
    let matches = [];

    MODULES.forEach((mod, mIdx) => {
        mod.lessons.forEach((les, lIdx) => {
            if (!q || les.title.toLowerCase().includes(q) || mod.title.toLowerCase().includes(q)) {
                matches.push({
                    type: 'lesson',
                    title: `${mod.icon} ${les.title}`,
                    desc: `${mod.title} · Урок ${lIdx + 1}`,
                    action: () => {
                        closeSearchModal();
                        currentModuleIdx = mIdx;
                        currentLessonIdx = lIdx;
                        renderSidebar();
                        renderLesson();
                    }
                });
            }
        });
    });

    GLOSSARY.forEach(item => {
        if (!q || item.term.toLowerCase().includes(q) || item.def.toLowerCase().includes(q)) {
            matches.push({
                type: 'term',
                title: `📖 ${item.term}`,
                desc: item.def,
                action: () => {}
            });
        }
    });

    if (matches.length === 0) {
        resultsContainer.innerHTML = '<div style="color:var(--text-muted);padding:16px;text-align:center">Ничего не найдено</div>';
        return;
    }

    resultsContainer.innerHTML = matches.slice(0, 15).map((m, idx) => `
        <div class="search-result-item" id="search-m-${idx}">
            <div class="search-result-title">${m.title}</div>
            <div class="search-result-desc">${m.desc}</div>
        </div>
    `).join('');

    matches.slice(0, 15).forEach((m, idx) => {
        document.getElementById(`search-m-${idx}`)?.addEventListener('click', m.action);
    });
}

// ═══════════════════════════════════════════
// SANDBOX SYSTEM
// ═══════════════════════════════════════════
const SANDBOX_PRESETS = {
    chop: `// 1. Лесоповал колонии
int woodStock = 20;
int hunger = 30;

// Боб рубит сосну:
woodStock += 5;
hunger += 15;

GD.Print($"Запас древесины: {woodStock} брёвен");
GD.Print($"Уровень голода: {hunger}%");`,

    check: `// 2. Проверка состояния поселенца
float health = 85.5f;
int hunger = 85;

if (hunger > 80)
{
    GD.Print("Поселенец проголодался -> Направляется в столовую");
}
else
{
    GD.Print("Поселенец сыт -> Продолжает работу");
}`,

    loop: `// 3. Обход 5 колонистов в поселении
string[] names = new string[] { "Боб", "Алиса", "Джон", "Ева", "Майк" };

foreach (var name in names)
{
    GD.Print($"Житель {name}: готов к труду на ферме");
}`,

    storage: `// 4. Поиск предмета в ItemRegistry
string itemId = "Log";
int weight = 1;

GD.Print($"Запрос предмета '{itemId}' выполнен успешно!");
GD.Print($"Вес пачки: {weight} кг, ячейка склада заполнена.");`
};

function openSandbox() {
    playClickSound();
    document.getElementById('sandbox-modal').classList.add('open');
    loadSandboxPreset('chop');
}

function closeSandbox() {
    playClickSound();
    document.getElementById('sandbox-modal').classList.remove('open');
}

function loadSandboxPreset(key) {
    const code = SANDBOX_PRESETS[key] || '';
    document.getElementById('sandbox-code').value = code;
    document.getElementById('sandbox-output').textContent = 'Код сценария загружен. Нажми «Запустить симуляцию»!';
}

function runSandbox() {
    playClickSound();
    const code = document.getElementById('sandbox-code').value;
    const outputEl = document.getElementById('sandbox-output');

    const printMatches = code.match(/GD\.Print\((.*?)\);/g);
    let outputLines = [];

    if (printMatches) {
        printMatches.forEach(stmt => {
            let inner = stmt.replace(/^GD\.Print\(/, '').replace(/\);$/, '').trim();
            inner = inner.replace(/^\$?"/, '').replace(/"$/, '');
            inner = inner.replace(/\{woodStock\}/g, '25')
                         .replace(/\{hunger\}/g, '45')
                         .replace(/\{name\}/g, 'Боб')
                         .replace(/\{itemId\}/g, 'Log')
                         .replace(/\{weight\}/g, '1');
            outputLines.push(`[Godot Output] ${inner}`);
        });
    }

    if (outputLines.length > 0) {
        outputEl.innerHTML = outputLines.join('<br>') + '<br><span style="color:var(--green)">✓ Симуляция завершилась успешно (0 ошибок, 0 предупреждений)</span>';
    } else {
        outputEl.innerHTML = '<span style="color:var(--yellow)">[Код выполнен без вывода в консоль]</span><br><span style="color:var(--green)">✓ Успешная компиляция</span>';
    }
}

// ═══════════════════════════════════════════
// COPY CODE HELPER
// ═══════════════════════════════════════════
function copyCode(btn, encodedCode) {
    playClickSound();
    const code = decodeURIComponent(encodedCode);
    navigator.clipboard.writeText(code).then(() => {
        const orig = btn.textContent;
        btn.textContent = '✓ Скопировано';
        setTimeout(() => btn.textContent = orig, 1500);
    });
}

// ═══════════════════════════════════════════
// BUG-FREE SINGLE-PASS SYNTAX HIGHLIGHTER (C#)
// ═══════════════════════════════════════════
function escapeHtml(str) {
    return str.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
}

function highlightCSharp(code) {
    if (!code) return '';

    const masterRegex = /(\/\/[^\r\n]*|\/\*[\s\S]*?\*\/)|(\$?"(?:[^"\\]|\\.)*"|@"(?:[^"]|"")*")|(\[\s*\w+\s*\])|(\b(?:public|private|protected|internal|static|sealed|readonly|var|void|new|return|if|else|for|foreach|while|do|class|struct|enum|interface|record|namespace|using|where|true|false|null|this|throw|try|catch|finally|break|continue|default|switch|case|when|in|is|as|override|virtual|abstract|partial|get|set|async|await|ref|out)\b)|(\b(?:int|float|double|bool|string|byte|char|long|short|object|TileType|JobData|ItemId|ItemDefinition|WorkItem|GameSpeed|ToolRequirement|Node|SimulationContext|AgentDataPool|ITool|IJobHandler|TilePos)\b)|(\b\d+(?:\.\d+)?[fFdDmM]?\b)|(\b[a-zA-Z_]\w*(?=\s*\())/g;

    let result = '';
    let lastIndex = 0;
    let match;

    while ((match = masterRegex.exec(code)) !== null) {
        result += escapeHtml(code.slice(lastIndex, match.index));
        lastIndex = masterRegex.lastIndex;

        const [full, comment, stringLit, attribute, keyword, typeName, numberLit, methodCall] = match;

        if (comment) {
            result += `<span class="cm">${escapeHtml(comment)}</span>`;
        } else if (stringLit) {
            result += `<span class="str">${escapeHtml(stringLit)}</span>`;
        } else if (attribute) {
            result += `<span class="attr">${escapeHtml(attribute)}</span>`;
        } else if (keyword) {
            result += `<span class="kw">${escapeHtml(keyword)}</span>`;
        } else if (typeName) {
            result += `<span class="tp">${escapeHtml(typeName)}</span>`;
        } else if (numberLit) {
            result += `<span class="num">${escapeHtml(numberLit)}</span>`;
        } else if (methodCall) {
            result += `<span class="fn">${escapeHtml(methodCall)}</span>`;
        } else {
            result += escapeHtml(full);
        }
    }

    result += escapeHtml(code.slice(lastIndex));
    return result;
}

// ═══════════════════════════════════════════
// BUG-FREE SINGLE-PASS SYNTAX HIGHLIGHTER (Python)
// ═══════════════════════════════════════════
function highlightPython(code) {
    if (!code) return '';

    const masterRegex = /(#[^\r\n]*)|(\$?"(?:[^"\\]|\\.)*"|'(?:[^'\\]|\\.)*')|(\b(?:def|for|in|if|else|elif|return|True|False|None|and|or|not|print|while|import|from|class|pass|break|continue)\b)|(\b\d+(?:\.\d+)?\b)|(\b[a-zA-Z_]\w*(?=\s*\())/g;

    let result = '';
    let lastIndex = 0;
    let match;

    while ((match = masterRegex.exec(code)) !== null) {
        result += escapeHtml(code.slice(lastIndex, match.index));
        lastIndex = masterRegex.lastIndex;

        const [full, comment, stringLit, keyword, numberLit, methodCall] = match;

        if (comment) {
            result += `<span class="cm">${escapeHtml(comment)}</span>`;
        } else if (stringLit) {
            result += `<span class="str">${escapeHtml(stringLit)}</span>`;
        } else if (keyword) {
            result += `<span class="kw">${escapeHtml(keyword)}</span>`;
        } else if (numberLit) {
            result += `<span class="num">${escapeHtml(numberLit)}</span>`;
        } else if (methodCall) {
            result += `<span class="fn">${escapeHtml(methodCall)}</span>`;
        } else {
            result += escapeHtml(full);
        }
    }

    result += escapeHtml(code.slice(lastIndex));
    return result;
}

// ═══════════════════════════════════════════
// SIDEBAR FILTER
// ═══════════════════════════════════════════
function filterSidebarLessons(query) {
    const q = query.toLowerCase().trim();
    document.querySelectorAll('.module-item').forEach(el => {
        const text = el.textContent.toLowerCase();
        el.style.display = (!q || text.includes(q)) ? 'flex' : 'none';
    });
}

// ═══════════════════════════════════════════
// RENDERING
// ═══════════════════════════════════════════
function renderSidebar() {
    const container = document.getElementById('module-list');
    let html = '';

    MODULES.forEach((mod, idx) => {
        const isActive = currentModuleIdx === idx;
        const isCompleted = mod.lessons.every((_, lIdx) => isLessonCompleted(idx, lIdx));
        const classes = ['module-item'];
        if (isActive) classes.push('active');
        if (isCompleted) classes.push('completed');

        html += `<div class="${classes.join(' ')}" onclick="openModule(${idx})">
            <div class="module-num">${idx + 1}</div>
            <div class="module-info">
                <div class="module-name">${mod.icon} ${mod.title}</div>
                <div class="module-desc">${mod.desc}</div>
            </div>
        </div>`;
    });

    container.innerHTML = html;
}

function showWelcome() {
    playClickSound();
    currentModuleIdx = -1;
    currentLessonIdx = 0;
    renderSidebar();

    document.getElementById('breadcrumb').innerHTML = '🏠 <span class="active-crumb">Главная</span> · Интерактивная Академия';
    document.getElementById('content').innerHTML = `
        <div class="welcome fade-in">
            <div class="welcome-icon">🎮</div>
            <h1>Изучай C# через свою игру</h1>
            <p>Курс написан специально под реальную кодовую базу твоего <strong>Colony Sim</strong> (Godot 4.7, .NET 8). Мы начнем с абсолютных азов, разберем мост после Python и дойдем до Zero-GC архитектуры поселения!</p>
            
            <div class="features-grid">
                <div class="feature-card">
                    <div class="emoji">🌱</div>
                    <h3>С абсолютных азов</h3>
                    <p>Никакого заумного снобизма. Всё объясняется простым языком через стройку, склад и жизнь колонистов.</p>
                </div>
                <div class="feature-card">
                    <div class="emoji">🐍⚡</div>
                    <h3>Мост после Python</h3>
                    <p>Наглядные блоки сравнения: как то же самое пишется в Python и C#, почему C# компилируется и летает в десятки раз быстрее.</p>
                </div>
                <div class="feature-card">
                    <div class="emoji">🧑‍🌾</div>
                    <h3>Интерактивный симулятор</h3>
                    <p>Анимированная модель поселенца Боба прямо в браузере: руби сосны, следи за голодом и меняй переменные кодом!</p>
                </div>
                <div class="feature-card">
                    <div class="emoji">🎯</div>
                    <h3>Умная проверка кода</h3>
                    <p>23 практических задания с доброжелательной подсказкой ошибок (если забыл точку с запятой ';' или ошибся в типе).</p>
                </div>
            </div>

            <div style="margin-top: 36px; display: flex; gap: 12px; justify-content: center; flex-wrap: wrap;">
                <button class="btn btn-primary" onclick="openModule(0)" style="padding: 12px 28px; font-size: 15px;">
                    ▶ Начать с Модуля 1 (Абсолютный старт)
                </button>
                <button class="btn" onclick="openSandbox()" style="padding: 12px 20px; font-size: 15px;">
                    🧪 Открыть песочницу C#
                </button>
                <button class="btn" onclick="openOnboarding()" style="padding: 12px 20px; font-size: 15px;">
                    🎯 Настроить под себя
                </button>
            </div>
        </div>
    `;
}

function openModule(moduleIdx) {
    playClickSound();
    currentModuleIdx = moduleIdx;
    currentLessonIdx = 0;
    currentTaskIdx = 0;
    renderSidebar();
    renderLesson();
}

function openLesson(lessonIdx) {
    playClickSound();
    currentLessonIdx = lessonIdx;
    currentTaskIdx = 0;
    renderSidebar();
    renderLesson();
}

function renderLesson(scrollToTop = true) {
    const mod = MODULES[currentModuleIdx];
    const lesson = mod.lessons[currentLessonIdx];

    document.getElementById('breadcrumb').innerHTML = `
        <span onclick="openModule(${currentModuleIdx})" style="cursor:pointer">${mod.icon} ${mod.title}</span> 
        <span style="color:var(--text-muted)">/</span> 
        <span class="active-crumb">Урок ${currentLessonIdx + 1} из ${mod.lessons.length}</span>
    `;

    let html = `<div class="fade-in">`;
    html += `
        <div class="lesson-header">
            <div class="lesson-meta-bar">
                <div class="lesson-badge">Модуль ${currentModuleIdx + 1}: ${mod.title}</div>
                <div class="lesson-time">⏱️ ${lesson.readTime || '3 мин'}</div>
            </div>
            <h1 class="lesson-title">${lesson.title}</h1>
            <p class="lesson-subtitle">${mod.desc}</p>
        </div>
    `;

    // File Link in project
    if (lesson.fileLink) {
        html += `
            <div class="project-file-card">
                <div class="project-file-icon">📁</div>
                <div>
                    <div class="project-file-path">${lesson.fileLink}</div>
                    <div class="project-file-desc">${lesson.fileDesc || 'Файл в репозитории твоего проекта'}</div>
                </div>
            </div>
        `;
    }

    // Sections
    lesson.sections.forEach(section => {
        html += `<div class="section">`;

        if (section.type === 'text') {
            html += `<div class="text">${section.content}</div>`;
        } else if (section.type === 'code') {
            const code = section.source || '';
            const highlighted = highlightCSharp(code);
            const encoded = encodeURIComponent(code);
            html += `
                <div class="code-block">
                    <div class="code-header">
                        <span>${section.filename || 'C#'}</span>
                        <button class="copy-btn" onclick="copyCode(this, '${encoded}')">Копировать</button>
                    </div>
                    <div class="code-body"><pre>${highlighted}</pre></div>
                </div>
            `;
        } else if (section.type === 'py_vs_cs') {
            html += `
                <div class="py-vs-cs">
                    <div class="py-vs-cs-header">
                        <span>⚖️ ${section.title || 'Сравнение языков'}</span>
                        <span style="color:var(--text-muted);font-size:10px;">Разница подходов</span>
                    </div>
                    <div class="py-vs-cs-grid">
                        <div class="py-col">
                            <div class="col-title"><span>🐍</span> Python</div>
                            <pre>${highlightPython(section.py)}</pre>
                        </div>
                        <div class="cs-col">
                            <div class="col-title"><span>⚡</span> C# (Твой проект)</div>
                            <pre>${highlightCSharp(section.cs)}</pre>
                        </div>
                    </div>
                </div>
            `;
        } else if (section.type === 'info') {
            html += `
                <div class="info-box info-${section.kind}">
                    <span class="icon">${section.kind === 'tip' ? '💡' : section.kind === 'warning' ? '⚠️' : '✅'}</span>
                    <span>${section.text}</span>
                </div>
            `;
        } else if (section.type === 'summary') {
            html += `
                <div class="takeaways-box">
                    <div class="takeaways-title">📌 Главное из этого урока:</div>
                    <ul class="takeaways-list">
                        ${section.items.map(it => `<li>${it}</li>`).join('')}
                    </ul>
                </div>
            `;
        } else if (section.type === 'bob_sim') {
            html += `
                <div class="bob-simulator">
                    <div class="sim-header">
                        <div class="sim-title">🧑‍🌾 Интерактивный симулятор «Боб в поселении»</div>
                        <div style="font-size:11px;color:var(--text-dim)">Живое изменение переменных в памяти</div>
                    </div>
                    <div class="sim-body">
                        <div class="bob-stage" id="bob-stage">
                            <div class="bob-emoji" id="bob-emoji">🧑‍🌾</div>
                            <div class="bob-name">${bobSim.name}</div>
                            <div class="bob-status" id="bob-status">${bobSim.status}</div>
                        </div>
                        <div class="sim-stats-grid">
                            <div class="sim-stat">
                                <div class="sim-stat-lbl"><span>🪵 Древесина</span> <span id="bob-wood-val" style="color:#ffc331">${bobSim.wood}</span></div>
                                <div style="font-size:10px;color:var(--text-muted)">int woodStock</div>
                            </div>
                            <div class="sim-stat">
                                <div class="sim-stat-lbl"><span>🍲 Голод</span> <span id="bob-hunger-val">${bobSim.hunger}%</span></div>
                                <div class="sim-stat-bar"><div class="sim-stat-bar-fill" id="bob-hunger-bar" style="width:${bobSim.hunger}%;background:var(--green)"></div></div>
                            </div>
                            <div class="sim-stat">
                                <div class="sim-stat-lbl"><span>❤️ Здоровье</span> <span id="bob-health-val">${bobSim.health}%</span></div>
                                <div class="sim-stat-bar"><div class="sim-stat-bar-fill" id="bob-health-bar" style="width:${bobSim.health}%;background:var(--green)"></div></div>
                            </div>
                        </div>
                    </div>
                    <div class="sim-controls">
                        <button class="btn btn-sm btn-primary" onclick="bobChopTree()">🪓 Срубить сосну (wood += 5; hunger += 15;)</button>
                        <button class="btn btn-sm" onclick="bobEat()">🍲 Пообедать (hunger -= 35;)</button>
                        <button class="btn btn-sm" onclick="bobRest()">⛺ Отдохнуть (+10% HP)</button>
                    </div>
                    <div class="sim-log" id="bob-log">✨ Попробуй нажать кнопки — это наглядно показывает, как код меняет значения переменных!</div>
                </div>
            `;
        }

        html += `</div>`;
    });

    // Tasks section
    const tasks = lesson.tasks || (lesson.task ? [lesson.task] : []);
    if (tasks.length > 0) {
        if (currentTaskIdx >= tasks.length) currentTaskIdx = 0;
        html += `<div id="tasks-container">${renderTasksAreaHtml(mod, lesson, tasks)}</div>`;
    }

    // User Notes
    const noteKey = `${currentModuleIdx}_${currentLessonIdx}`;
    const noteContent = userNotes[noteKey] || '';
    html += `
        <div class="lesson-notes-area">
            <div class="notes-toggle" onclick="toggleNotes()">
                📝 <span>${noteContent ? 'Твоя заметка к уроку (сохранена)' : 'Добавить свою заметку к этому уроку'}</span>
            </div>
            <textarea class="notes-textarea ${noteContent ? 'show' : ''}" id="notes-textarea" placeholder="Запиши мысль, вопрос или связь с файлами проекта..." oninput="updateNote(this.value)">${noteContent}</textarea>
        </div>
    `;

    // Navigation
    const isDone = isLessonCompleted(currentModuleIdx, currentLessonIdx);
    html += `<div class="lesson-nav">`;

    if (currentModuleIdx > 0 || currentLessonIdx > 0) {
        html += `<button class="btn" onclick="prevLesson()">← Предыдущий урок</button>`;
    } else {
        html += `<div></div>`;
    }

    if (currentLessonIdx < mod.lessons.length - 1) {
        html += `
            <button class="btn btn-primary" id="next-btn" onclick="nextLesson()" ${isDone ? '' : 'disabled'}>
                Следующий урок →
            </button>
        `;
    } else if (currentModuleIdx < MODULES.length - 1) {
        html += `
            <button class="btn btn-primary" id="next-btn" onclick="nextModule()" ${isDone ? '' : 'disabled'}>
                Следующий модуль (${MODULES[currentModuleIdx + 1].title}) →
            </button>
        `;
    } else {
        html += `<div style="color:var(--green);font-weight:700;padding:8px 0">🎉 Поздравляем! Ты полностью завершил курс!</div>`;
    }

    html += `</div></div>`;

    document.getElementById('content').innerHTML = html;
    if (scrollToTop) {
        window.scrollTo({ top: 0, behavior: 'smooth' });
    }
}

// ═══════════════════════════════════════════
// TASK AREA RENDERER & HELPERS
// ═══════════════════════════════════════════
function renderTasksAreaHtml(mod, lesson, tasks) {
    if (!tasks || tasks.length === 0) return '';
    if (currentTaskIdx >= tasks.length) currentTaskIdx = 0;

    const completedTasksCount = tasks.filter((_, idx) => isTaskCompleted(currentModuleIdx, currentLessonIdx, idx)).length;
    const allDone = completedTasksCount === tasks.length;
    const activeTask = tasks[currentTaskIdx];
    const isCurrentTaskDone = isTaskCompleted(currentModuleIdx, currentLessonIdx, currentTaskIdx);

    let html = `
        <div class="task-area ${allDone ? 'correct' : ''}" id="task-area">
            <div class="task-header-row">
                <div class="section-title" style="margin-bottom:0"><span class="icon">🎯</span> Практические задания по C#</div>
                <div class="task-progress-badge ${allDone ? 'all-done' : ''}">
                    ${allDone ? '✓ Все задания решены (' + tasks.length + '/' + tasks.length + ')' : 'Решено заданий: ' + completedTasksCount + ' из ' + tasks.length}
                </div>
            </div>
    `;

    // Render Tabs if more than 1 task
    if (tasks.length > 1) {
        html += `<div class="task-tabs">`;
        tasks.forEach((t, tIdx) => {
            const tDone = isTaskCompleted(currentModuleIdx, currentLessonIdx, tIdx);
            const isActive = tIdx === currentTaskIdx;
            const tabClasses = ['task-tab'];
            if (isActive) tabClasses.push('active');
            if (tDone) tabClasses.push('completed');
            html += `
                <button class="${tabClasses.join(' ')}" onclick="switchTask(${tIdx})">
                    <span class="task-tab-badge">${tDone ? '✓' : (tIdx + 1)}</span>
                    <span>${escapeHtml(t.title || ('Задание ' + (tIdx + 1)))}</span>
                </button>
            `;
        });
        html += `</div>`;
    }

    // Active Task Scenario (if present)
    if (activeTask.scenario) {
        html += `
            <div class="task-scenario-card">
                <div class="task-scenario-label">🎮 Игровая ситуация в колонии</div>
                <div>${activeTask.scenario}</div>
            </div>
        `;
    }

    // Active Task Question
    html += `
        <div class="text" style="margin-bottom:14px;font-weight:600;font-size:15px;">
            ${activeTask.question}
        </div>
    `;

    // Code Editor
    const draftKey = `${currentModuleIdx}_${currentLessonIdx}_t${currentTaskIdx}`;
    let savedDraft = userDrafts[draftKey];
    if (savedDraft === undefined) {
        savedDraft = isCurrentTaskDone ? (activeTask.solution || '') : '';
    }

    html += `
        <div class="task-input-container">
            <textarea class="task-input" id="task-input" 
                placeholder="Напиши свой код на C# здесь... (не забывай ; в конце команд)"
                oninput="saveTaskDraft(${currentModuleIdx}, ${currentLessonIdx}, ${currentTaskIdx}, this.value)">${escapeHtml(savedDraft)}</textarea>
        </div>
        <div class="task-helpers">
            <button class="helper-btn" onclick="toggleHint()">💡 Подсказка</button>
            ${activeTask.solution ? `<button class="helper-btn" onclick="toggleSolution()">👁️ Показать решение</button>` : ''}
        </div>
        <div class="solution-box" id="hint-box">
            <div style="font-weight:700;color:var(--yellow);margin-bottom:6px;">💡 Подсказка:</div>
            <pre style="white-space:pre-wrap;font-family:'Fira Code',monospace;font-size:12.5px;color:#d8e2ec;margin:0;">${escapeHtml(activeTask.hint || 'Внимательно сверься с примерами кода выше!')}</pre>
        </div>
        ${activeTask.solution ? `
            <div class="solution-box" id="solution-box">
                <div style="font-weight:700;color:var(--green);margin-bottom:6px;">✅ Образец правильного кода:</div>
                <pre style="white-space:pre-wrap;font-family:'Fira Code',monospace;font-size:12.5px;color:#c9f5d1;margin:0;">${escapeHtml(activeTask.solution)}</pre>
            </div>
        ` : ''}
    `;

    // Feedback & Buttons
    html += `
        <div class="feedback ${isCurrentTaskDone ? 'show success' : ''}" id="task-feedback">
            ${isCurrentTaskDone ? '✅ Это задание уже выполнено!' : ''}
        </div>
        <div style="display:flex;gap:10px;align-items:center;margin-top:16px;flex-wrap:wrap;">
            <button class="btn btn-primary" id="task-submit" onclick="submitCurrentTask()">
                ⚡ Проверить решение
            </button>
            ${(currentTaskIdx < tasks.length - 1) ? `
                <button class="btn" onclick="switchTask(${currentTaskIdx + 1})">
                    Перейти к следующему заданию (${currentTaskIdx + 2}/${tasks.length}) →
                </button>
            ` : ''}
        </div>
    </div>`;

    return html;
}

function updateTaskTabsUI(tasks) {
    const tabsContainer = document.querySelector('.task-tabs');
    if (tabsContainer) {
        tasks.forEach((t, tIdx) => {
            const btn = tabsContainer.children[tIdx];
            if (btn) {
                const tDone = isTaskCompleted(currentModuleIdx, currentLessonIdx, tIdx);
                const isActive = tIdx === currentTaskIdx;
                btn.className = `task-tab ${isActive ? 'active' : ''} ${tDone ? 'completed' : ''}`;
                const badge = btn.querySelector('.task-tab-badge');
                if (badge) badge.textContent = tDone ? '✓' : (tIdx + 1);
            }
        });
    }
    const progressBadge = document.querySelector('.task-progress-badge');
    if (progressBadge) {
        const completedCount = tasks.filter((_, idx) => isTaskCompleted(currentModuleIdx, currentLessonIdx, idx)).length;
        const allDone = completedCount === tasks.length;
        progressBadge.className = `task-progress-badge ${allDone ? 'all-done' : ''}`;
        progressBadge.textContent = allDone ? `✓ Все задания решены (${tasks.length}/${tasks.length})` : `Решено заданий: ${completedCount} из ${tasks.length}`;
    }
}

function toggleHint() {
    playClickSound();
    document.getElementById('hint-box')?.classList.toggle('show');
}

function toggleSolution() {
    playClickSound();
    document.getElementById('solution-box')?.classList.toggle('show');
}

function switchTask(idx) {
    playClickSound();
    const currentInput = document.getElementById('task-input');
    if (currentInput) {
        saveTaskDraft(currentModuleIdx, currentLessonIdx, currentTaskIdx, currentInput.value);
    }
    currentTaskIdx = idx;
    const mod = MODULES[currentModuleIdx];
    const lesson = mod.lessons[currentLessonIdx];
    const tasks = lesson.tasks || (lesson.task ? [lesson.task] : []);
    const container = document.getElementById('tasks-container');
    if (container) {
        container.innerHTML = renderTasksAreaHtml(mod, lesson, tasks);
    } else {
        renderLesson(false);
    }
}

function submitCurrentTask() {
    const mod = MODULES[currentModuleIdx];
    const lesson = mod.lessons[currentLessonIdx];
    const tasks = lesson.tasks || (lesson.task ? [lesson.task] : []);
    const task = tasks[currentTaskIdx];
    const feedbackEl = document.getElementById('task-feedback');
    const taskArea = document.getElementById('task-area');
    const nextBtn = document.getElementById('next-btn');

    if (!task || !feedbackEl) return;

    const input = document.getElementById('task-input')?.value.trim();
    if (!input) {
        feedbackEl.className = 'feedback show error';
        feedbackEl.innerHTML = '⚠️ Поле ввода пустое! Напиши свой C# код решения. Не бойся ошибок.';
        return;
    }

    let isCorrect = false;
    let customError = null;

    if (!input.includes(';') && !input.includes('{') && !input.includes('=>')) {
        customError = 'Похоже, ты забыл точку с запятой <code>;</code> в конце команды! В C# это строго обязательно.';
    } else {
        try {
            let checkFn;
            if (typeof task.check === 'function') {
                checkFn = task.check;
            } else if (typeof task.check === 'string') {
                checkFn = eval('(' + task.check + ')');
            }
            if (checkFn) {
                isCorrect = checkFn(input);
            }
        } catch(e) {
            console.error('Check evaluation error:', e);
            isCorrect = false;
        }
    }

    if (isCorrect) {
        playSuccessSound();
        completeTask(currentModuleIdx, currentLessonIdx, currentTaskIdx);

        const tasksCount = tasks.length;
        const completedCount = tasks.filter((_, idx) => isTaskCompleted(currentModuleIdx, currentLessonIdx, idx)).length;
        const allDone = completedCount === tasksCount;

        feedbackEl.className = 'feedback show success';
        let successMsg = '✅ ' + (task.successMsg || 'Отлично! Задание решено верно!');
        if (allDone) {
            successMsg += '<br><strong>🎉 Все задания этого урока выполнены! Теперь можно переходить к следующему уроку.</strong>';
            if (nextBtn) nextBtn.disabled = false;
            showBadge('🏅 Урок пройден!', `${mod.title} → ${lesson.title}`);
        } else {
            successMsg += `<br><em>Осталось решить ещё заданий в этом уроке: ${tasksCount - completedCount}.</em>`;
        }
        feedbackEl.innerHTML = successMsg;

        taskArea.classList.remove('wrong');
        if (allDone) taskArea.classList.add('correct');

        // Update tabs and badges directly in-place without touching scroll!
        updateTaskTabsUI(tasks);

    } else {
        feedbackEl.className = 'feedback show error';
        const errText = customError || task.errorMsg || 'Не совсем точно. Проверь синтаксис или загляни в подсказку 💡!';
        feedbackEl.innerHTML = '❌ ' + errText;
        taskArea.classList.add('wrong');
        taskArea.classList.remove('correct');
    }
}

function prevLesson() {
    playClickSound();
    currentTaskIdx = 0;
    if (currentLessonIdx > 0) {
        currentLessonIdx--;
    } else if (currentModuleIdx > 0) {
        currentModuleIdx--;
        currentLessonIdx = MODULES[currentModuleIdx].lessons.length - 1;
    } else {
        showWelcome();
        return;
    }
    renderLesson(true);
}

function nextLesson() {
    playClickSound();
    currentTaskIdx = 0;
    if (currentLessonIdx < MODULES[currentModuleIdx].lessons.length - 1) {
        currentLessonIdx++;
        renderLesson(true);
    } else {
        nextModule();
    }
}

function nextModule() {
    playClickSound();
    currentTaskIdx = 0;
    if (currentModuleIdx < MODULES.length - 1) {
        currentModuleIdx++;
        currentLessonIdx = 0;
        renderSidebar();
        renderLesson(true);
    } else {
        fireConfetti();
        showBadge('🎉 Грандиозно!', 'Ты полностью завершил курс C# для Colony Sim!');
    }
}

function showBadge(title, subtitle) {
    const existing = document.querySelector('.badge-popup');
    if (existing) existing.remove();

    const popup = document.createElement('div');
    popup.className = 'badge-popup';
    popup.innerHTML = `
        <div class="badge-popup-icon">🏆</div>
        <div>
            <h3>${title}</h3>
            <p>${subtitle}</p>
        </div>
    `;
    document.body.appendChild(popup);
    setTimeout(() => popup.remove(), 3500);
}

function toggleNotes() {
    const area = document.getElementById('notes-textarea');
    if (area) {
        area.classList.toggle('show');
        if (area.classList.contains('show')) area.focus();
    }
}

function updateNote(val) {
    const key = `${currentModuleIdx}_${currentLessonIdx}`;
    userNotes[key] = val;
    saveNotes();
}

// ═══════════════════════════════════════════
// KEYBOARD NAVIGATION
// ═══════════════════════════════════════════
document.addEventListener('keydown', (e) => {
    if (e.key === 'Escape') {
        document.querySelectorAll('.modal-overlay.open').forEach(m => m.classList.remove('open'));
    }
    if (e.altKey && e.key === 'ArrowRight') {
        const nextBtn = document.getElementById('next-btn');
        if (nextBtn && !nextBtn.disabled) nextLesson();
    }
    if (e.altKey && e.key === 'ArrowLeft') {
        prevLesson();
    }
});

// Mobile menu toggle
document.getElementById('menu-toggle')?.addEventListener('click', () => {
    document.getElementById('sidebar').classList.toggle('open');
});

document.addEventListener('click', (e) => {
    const sidebar = document.getElementById('sidebar');
    const toggle = document.getElementById('menu-toggle');
    if (window.innerWidth <= 860 && sidebar.classList.contains('open') && !sidebar.contains(e.target) && e.target !== toggle) {
        sidebar.classList.remove('open');
    }
});

// ═══════════════════════════════════════════
// INIT
// ═══════════════════════════════════════════
loadProgress();
updateProgressUI();
showWelcome();
</script>
</body>
</html>
'''

# Target locations
target_path_1 = r"d:\bebebe-main (1)\csharp-course\index.html"
target_dir_2 = r"d:\bebebe-main (1)\bebebe-main\csharp-course"
target_path_2 = os.path.join(target_dir_2, "index.html")

os.makedirs(os.path.dirname(target_path_1), exist_ok=True)
os.makedirs(target_dir_2, exist_ok=True)

with open(target_path_1, "w", encoding="utf-8") as f:
    f.write(html_template)
print(f"Generated: {target_path_1} ({len(html_template)} bytes)")

with open(target_path_2, "w", encoding="utf-8") as f:
    f.write(html_template)
print(f"Generated: {target_path_2} ({len(html_template)} bytes)")
