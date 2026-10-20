# -*- coding: utf-8 -*-
import os
import json

# Read modules
with open("tools/modules_data.json", "r", encoding="utf-8") as f:
    modules = json.load(f)

# Serialize modules to JS string
# We need to preserve functions in "check" fields if any
modules_js_str = json.dumps(modules, ensure_ascii=False, indent=2)

# Now let's assemble the HTML
html_content = r'''<!DOCTYPE html>
<html lang="ru">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>C# для Colony Sim — Интерактивный курс с нуля</title>
    <link rel="preconnect" href="https://fonts.googleapis.com">
    <link href="https://fonts.googleapis.com/css2?family=Fira+Code:wght@400;500;600&family=Inter:wght@400;500;600;700;800&display=swap" rel="stylesheet">
    <style>
        :root {
            --bg: #0d1117;
            --bg-card: #161b22;
            --bg-card-hover: #1c212c;
            --bg-code: #0b0e14;
            --bg-hover: #21262d;
            --text: #f0f6fc;
            --text-dim: #8b949e;
            --text-muted: #6e7681;
            --accent: #7c5cfc;
            --accent-light: #a371f7;
            --accent-dark: #5833b6;
            --green: #3fb950;
            --green-bg: rgba(63, 185, 80, 0.12);
            --red: #f85149;
            --red-bg: rgba(248, 81, 73, 0.12);
            --yellow: #d29922;
            --yellow-bg: rgba(210, 153, 34, 0.12);
            --blue: #58a6ff;
            --blue-bg: rgba(88, 166, 255, 0.12);
            --python: #ffc331;
            --python-bg: rgba(255, 195, 49, 0.12);
            --border: #30363d;
            --border-light: #3d444d;
            --sidebar-w: 310px;
            --header-h: 64px;
            --radius: 12px;
            --radius-sm: 8px;
            --shadow: 0 8px 32px rgba(0,0,0,0.4);
            --transition: 0.2s cubic-bezier(0.16, 1, 0.3, 1);
        }

        * { margin: 0; padding: 0; box-sizing: border-box; }

        body {
            font-family: 'Inter', -apple-system, BlinkMacSystemFont, sans-serif;
            background: var(--bg);
            color: var(--text);
            line-height: 1.6;
            overflow-x: hidden;
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
            background: linear-gradient(180deg, rgba(124, 92, 252, 0.08) 0%, transparent 100%);
        }

        .sidebar-header h2 {
            font-size: 17px;
            font-weight: 800;
            color: var(--text);
            display: flex;
            align-items: center;
            justify-content: center;
            gap: 8px;
            letter-spacing: -0.3px;
        }

        .sidebar-header h2 span {
            background: linear-gradient(135deg, var(--accent-light), #58a6ff);
            -webkit-background-clip: text;
            -webkit-text-fill-color: transparent;
        }

        .user-profile-badge {
            display: inline-flex;
            align-items: center;
            gap: 6px;
            background: rgba(124, 92, 252, 0.15);
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
            transform: scale(1.02);
        }

        .sidebar-stats {
            padding: 12px 20px;
            border-bottom: 1px solid var(--border);
            display: grid;
            grid-template-columns: 1fr 1fr;
            gap: 8px;
            background: rgba(0,0,0,0.15);
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
            padding: 14px 20px;
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
            padding: 14px;
            border-top: 1px solid var(--border);
            display: flex;
            gap: 8px;
            background: var(--bg-card);
        }

        /* ═══════ MAIN ═══════ */
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
            background: rgba(13, 17, 23, 0.85);
            backdrop-filter: blur(12px);
            z-index: 50;
        }

        .breadcrumb {
            font-size: 13px;
            color: var(--text-dim);
            display: flex;
            align-items: center;
            gap: 6px;
        }

        .breadcrumb span {
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
        }

        .btn-success {
            background: var(--green);
            border-color: var(--green);
            color: white;
            font-weight: 600;
        }

        .btn-sm {
            padding: 5px 10px;
            font-size: 11px;
        }

        .btn:disabled {
            opacity: 0.4;
            cursor: not-allowed;
            transform: none !important;
        }

        /* ═══════ CONTENT ═══════ */
        .content {
            max-width: 900px;
            margin: 0 auto;
            padding: 36px 32px;
            width: 100%;
            flex: 1;
        }

        .lesson-header {
            margin-bottom: 28px;
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
            margin-bottom: 12px;
            border: 1px solid rgba(124, 92, 252, 0.25);
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

        /* Python vs C# comparison box */
        .py-vs-cs {
            background: var(--bg-card);
            border: 1px solid var(--border);
            border-radius: var(--radius);
            overflow: hidden;
            margin: 20px 0;
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
            grid-template-columns: 1fr 1fr;
            gap: 1px;
            background: var(--border);
        }

        .py-col, .cs-col {
            background: var(--bg-card);
            padding: 16px;
        }

        .col-title {
            font-size: 12px;
            font-weight: 700;
            margin-bottom: 8px;
            display: flex;
            align-items: center;
            gap: 6px;
        }

        .py-col .col-title { color: var(--python); }
        .cs-col .col-title { color: var(--blue); }

        .py-col pre, .cs-col pre {
            font-family: 'Fira Code', monospace;
            font-size: 13px;
            line-height: 1.6;
            color: var(--text);
            overflow-x: auto;
        }

        /* Code blocks */
        .code-block {
            background: var(--bg-code);
            border: 1px solid var(--border);
            border-radius: var(--radius-sm);
            overflow: hidden;
            margin: 18px 0;
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

        .code-body {
            padding: 16px;
            overflow-x: auto;
        }

        .code-body pre {
            font-family: 'Fira Code', monospace;
            font-size: 13.5px;
            line-height: 1.7;
            color: var(--text);
        }

        /* Syntax highlighting */
        .kw { color: #ff7b72; font-weight: 600; }        /* keywords */
        .tp { color: #79c0ff; font-weight: 600; }        /* types */
        .str { color: #a5d6ff; }                         /* strings */
        .cm { color: #8b949e; font-style: italic; }      /* comments */
        .num { color: #ffa657; }                         /* numbers */
        .prop { color: #d2a5ff; }                        /* properties */
        .fn { color: #d2a5ff; }                          /* methods */
        .attr { color: #7ee787; }                        /* attributes */

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

        /* ═══════ BOB MINI-SIMULATOR ═══════ */
        .bob-simulator {
            background: linear-gradient(180deg, #161b22 0%, #10141b 100%);
            border: 1px solid var(--border);
            border-radius: var(--radius);
            padding: 20px;
            margin: 24px 0;
            position: relative;
            overflow: hidden;
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
            font-size: 14px;
            font-weight: 700;
            display: flex;
            align-items: center;
            gap: 8px;
            color: var(--accent-light);
        }

        .sim-body {
            display: grid;
            grid-template-columns: 140px 1fr;
            gap: 20px;
            align-items: center;
        }

        .bob-avatar-box {
            background: var(--bg);
            border: 1px solid var(--border);
            border-radius: var(--radius-sm);
            padding: 14px;
            text-align: center;
            position: relative;
        }

        .bob-emoji {
            font-size: 48px;
            display: inline-block;
            transition: transform 0.2s ease;
        }

        .bob-name {
            font-size: 13px;
            font-weight: 700;
            margin-top: 6px;
            color: var(--text);
        }

        .bob-status {
            font-size: 10px;
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
            padding: 8px 12px;
        }

        .sim-stat-lbl {
            font-size: 10px;
            color: var(--text-dim);
            text-transform: uppercase;
            font-weight: 600;
            margin-bottom: 4px;
            display: flex;
            justify-content: space-between;
        }

        .sim-stat-bar {
            height: 5px;
            background: var(--border);
            border-radius: 3px;
            overflow: hidden;
            margin-top: 4px;
        }

        .sim-stat-bar-fill {
            height: 100%;
            border-radius: 3px;
            transition: width 0.3s ease;
        }

        .sim-controls {
            margin-top: 14px;
            display: flex;
            gap: 8px;
            flex-wrap: wrap;
        }

        .sim-log {
            margin-top: 12px;
            background: var(--bg-code);
            border: 1px solid var(--border);
            border-radius: 6px;
            padding: 8px 12px;
            font-family: 'Fira Code', monospace;
            font-size: 11px;
            color: var(--green);
            min-height: 32px;
            display: flex;
            align-items: center;
        }

        /* ═══════ TASK AREA ═══════ */
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
            background: linear-gradient(180deg, var(--bg-card) 0%, rgba(63, 185, 80, 0.05) 100%);
        }

        .task-area.wrong {
            border-color: var(--red);
            border-style: solid;
            background: linear-gradient(180deg, var(--bg-card) 0%, rgba(248, 81, 73, 0.05) 100%);
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
            margin-top: 14px;
            line-height: 1.6;
            outline: none;
            transition: border-color var(--transition);
        }

        .task-input:focus {
            border-color: var(--accent);
            box-shadow: 0 0 0 3px rgba(124, 92, 252, 0.2);
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
            min-height: 70px;
            background: var(--bg-code);
            border: 1px solid var(--border);
            border-radius: var(--radius-sm);
            color: var(--text);
            font-size: 13px;
            padding: 10px;
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
            max-width: 580px;
            margin: 0 auto 32px;
            line-height: 1.7;
        }

        .features-grid {
            display: grid;
            grid-template-columns: repeat(auto-fit, minmax(240px, 1fr));
            gap: 16px;
            max-width: 800px;
            margin: 32px auto;
            text-align: left;
        }

        .feature-card {
            background: var(--bg-card);
            border: 1px solid var(--border);
            border-radius: var(--radius);
            padding: 20px;
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

        /* ═══════ ONBOARDING MODAL ═══════ */
        .modal-overlay {
            position: fixed;
            top: 0; left: 0; right: 0; bottom: 0;
            background: rgba(0,0,0,0.75);
            backdrop-filter: blur(8px);
            z-index: 500;
            display: none;
            align-items: center;
            justify-content: center;
            padding: 20px;
        }

        .modal-overlay.open {
            display: flex;
        }

        .modal-card {
            background: var(--bg-card);
            border: 1px solid var(--border);
            border-radius: var(--radius);
            max-width: 580px;
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

        .survey-q {
            margin-bottom: 20px;
        }

        .survey-label {
            font-size: 13px;
            font-weight: 700;
            margin-bottom: 10px;
            color: var(--text);
            display: block;
        }

        .survey-opts {
            display: grid;
            gap: 8px;
        }

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
            gap: 10px;
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

        /* ═══════ GLOSSARY MODAL ═══════ */
        .glossary-search {
            width: 100%;
            padding: 10px 14px;
            background: var(--bg);
            border: 1px solid var(--border);
            border-radius: var(--radius-sm);
            color: var(--text);
            font-size: 14px;
            margin-bottom: 16px;
            outline: none;
        }

        .glossary-item {
            padding: 12px 0;
            border-bottom: 1px solid var(--border);
        }

        .glossary-term {
            font-size: 14px;
            font-weight: 700;
            color: var(--accent-light);
            font-family: 'Fira Code', monospace;
            margin-bottom: 4px;
        }

        .glossary-def {
            font-size: 13px;
            color: var(--text-dim);
            line-height: 1.5;
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
            width: 50px; height: 50px;
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

<!-- Sidebar -->
<nav class="sidebar" id="sidebar">
    <div class="sidebar-header">
        <h2>🎮 <span>C# Colony Sim</span></h2>
        <div class="user-profile-badge" id="profile-badge" onclick="openOnboarding()">
            🎯 <span id="profile-text">Настроить под себя</span>
        </div>
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
        <button class="btn btn-sm" style="flex:1" onclick="openGlossary()">📖 Словарь</button>
        <button class="btn btn-sm" onclick="toggleAudio()" id="audio-btn" title="Звук">🔊</button>
        <button class="btn btn-sm" onclick="exportProgress()" title="Экспорт">💾</button>
    </div>
</nav>

<button class="menu-toggle" id="menu-toggle">☰</button>

<!-- Main -->
<main class="main">
    <div class="topbar">
        <div class="breadcrumb" id="breadcrumb">Добро пожаловать в курс</div>
        <div class="topbar-actions">
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
        <div class="modal-subtitle">Без банальных анкет: укажи свой опыт и то, что хочешь освоить в первую очередь, чтобы расставить правильные акценты.</div>

        <div class="survey-q">
            <span class="survey-label">1. Твой опыт с кодом на текущий момент:</span>
            <div class="survey-opts">
                <div class="survey-opt selected" data-q="exp" data-v="zero" onclick="selectSurvey(this)">
                    <span>🌱</span> <div><strong>Абсолютный ноль</strong><div style="font-size:11px;color:var(--text-dim)">Объясняй максимально наглядно и на житейских примерах</div></div>
                </div>
                <div class="survey-opt" data-q="exp" data-v="python" onclick="selectSurvey(this)">
                    <span>🐍</span> <div><strong>Пробовал Python когда-то</strong><div style="font-size:11px;color:var(--text-dim)">Делай мост между Python и C#, показывай различия</div></div>
                </div>
                <div class="survey-opt" data-q="exp" data-v="fast" onclick="selectSurvey(this)">
                    <span>⚡</span> <div><strong>Быстро схватываю</strong><div style="font-size:11px;color:var(--text-dim)">Меньше теории, сразу к архитектуре и коду Godot</div></div>
                </div>
            </div>
        </div>

        <div class="survey-q">
            <span class="survey-label">2. Что в твоем проекте тебе интереснее понять первым?</span>
            <div class="survey-opts">
                <div class="survey-opt selected" data-q="goal" data-v="colonists" onclick="selectSurvey(this)">
                    <span>🧑‍🌾</span> <div>Поведение колонистов: голод, работа, рубка деревьев, ИИ</div>
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
            <button class="btn btn-primary" onclick="saveOnboarding()">Применить и начать 🚀</button>
        </div>
    </div>
</div>

<!-- Glossary Modal -->
<div class="modal-overlay" id="glossary-modal">
    <div class="modal-card">
        <div class="modal-title">📖 Словарь терминов C# и проекта</div>
        <div class="modal-subtitle">Все сложные термины простыми словами колонии.</div>
        <input type="text" class="glossary-search" id="glossary-search" placeholder="Поиск по терминам (напр. Struct, GC, Node)..." oninput="filterGlossary()">
        <div id="glossary-list" style="max-height: 400px; overflow-y: auto;"></div>
        <div style="text-align: right; margin-top: 20px;">
            <button class="btn" onclick="closeGlossary()">Закрыть</button>
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
        osc.frequency.setValueAtTime(523.25, now);       // C5
        osc.frequency.setValueAtTime(659.25, now + 0.08); // E5
        osc.frequency.setValueAtTime(783.99, now + 0.16); // G5
        osc.frequency.setValueAtTime(1046.50, now + 0.24); // C6
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
// BOB MINI-SIMULATOR STATE
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

function bobChopTree() {
    playClickSound();
    if (bobSim.hunger >= 90) {
        logBob("⚠️ Боб слишком голоден, чтобы рубить сосны! Сначала покорми его.");
        bobSim.status = "Обессилел от голода";
        updateBobSimUI();
        return;
    }
    bobSim.wood += 5;
    bobSim.hunger = Math.min(100, bobSim.hunger + 15);
    bobSim.status = "Срубил сосну (+5 дерева)";
    logBob("🪓 Боб срубил сосну: wood += 5 (всего " + bobSim.wood + "), hunger += 15");
    updateBobSimUI();
}

function bobEat() {
    playClickSound();
    if (bobSim.hunger <= 0) {
        logBob("🍲 Боб уже сыт!");
        return;
    }
    bobSim.hunger = Math.max(0, bobSim.hunger - 35);
    bobSim.status = "Пообедал супом";
    logBob("🍲 Боб поел: hunger -= 35 (текущий голод: " + bobSim.hunger + "%)");
    updateBobSimUI();
}

function bobRest() {
    playClickSound();
    bobSim.health = Math.min(100, bobSim.health + 10);
    bobSim.status = "Отдохнул в палатке";
    logBob("⛺ Боб восстановил силы (+10% здоровья)");
    updateBobSimUI();
}

function logBob(msg) {
    const el = document.getElementById('bob-log');
    if (el) el.textContent = msg;
}

// ═══════════════════════════════════════════
// COURSE MODULES
// ═══════════════════════════════════════════
const MODULES = ''' + modules_js_str + r''';

// ═══════════════════════════════════════════
// STATE MANAGEMENT & LOCALSTORAGE
// ═══════════════════════════════════════════
let currentModuleIdx = -1; // -1 = welcome
let currentLessonIdx = 0;
let progress = {};
let userNotes = {};
let userProfile = { exp: 'zero', goal: 'colonists' };

function loadProgress() {
    try {
        const savedProg = localStorage.getItem('csharp_course_progress');
        if (savedProg) progress = JSON.parse(savedProg);

        const savedNotes = localStorage.getItem('csharp_course_notes');
        if (savedNotes) userNotes = JSON.parse(savedNotes);

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

function saveProfile() {
    try {
        localStorage.setItem('csharp_course_profile', JSON.stringify(userProfile));
    } catch(e) {}
}

function isLessonCompleted(moduleIdx, lessonIdx) {
    return !!progress[`${moduleIdx}_${lessonIdx}`];
}

function completeLesson(moduleIdx, lessonIdx) {
    progress[`${moduleIdx}_${lessonIdx}`] = true;
    saveProgress();
    updateProgressUI();
    renderSidebar();
}

function getTotalLessons() {
    let total = 0;
    MODULES.forEach(m => total += m.lessons.length);
    return total;
}

function getCompletedLessons() {
    return Object.keys(progress).filter(k => progress[k]).length;
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
    if (confirm('Сбросить весь прогресс прохождения курса? (Заметки останутся)')) {
        progress = {};
        saveProgress();
        updateProgressUI();
        renderSidebar();
        showWelcome();
    }
}

function exportProgress() {
    const data = {
        progress,
        userNotes,
        userProfile,
        date: new Date().toISOString()
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
    // Preselect based on userProfile
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
    showBadge('🎯 Профиль настроен!', 'Уроки адаптированы под твои цели');
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
// GLOSSARY
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
        <div class="glossary-item">
            <div class="glossary-term">${item.term}</div>
            <div class="glossary-def">${item.def}</div>
        </div>
    `).join('');
}

function filterGlossary() {
    const query = document.getElementById('glossary-search').value.toLowerCase().trim();
    const filtered = GLOSSARY.filter(i => i.term.toLowerCase().includes(query) || i.def.toLowerCase().includes(query));
    renderGlossaryList(filtered);
}

// ═══════════════════════════════════════════
// SYNTAX HIGHLIGHTER (C#)
// ═══════════════════════════════════════════
function highlightCSharp(code) {
    let html = code.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');

    // Comments
    html = html.replace(/(\/\/.*)/gm, '<span class="cm">$1</span>');

    // Strings
    html = html.replace(/"([^"\\]*(?:\\.[^"\\]*)*)"/g, '<span class="str">"$1"</span>');

    // Keywords
    const keywords = ['public', 'private', 'protected', 'internal', 'static', 'sealed', 'readonly',
                      'var', 'void', 'new', 'return', 'if', 'else', 'for', 'foreach', 'while', 'do',
                      'class', 'struct', 'enum', 'interface', 'record', 'namespace', 'using', 'where',
                      'true', 'false', 'null', 'this', 'throw', 'try', 'catch', 'finally', 'break',
                      'continue', 'default', 'switch', 'case', 'when', 'in', 'is', 'as', 'override',
                      'virtual', 'abstract', 'partial', 'get', 'set', 'async', 'await', 'ref', 'out'];

    keywords.forEach(kw => {
        const re = new RegExp(`\\b(${kw})\\b`, 'g');
        html = html.replace(re, '<span class="kw">$1</span>');
    });

    // Types
    const types = ['int', 'float', 'double', 'bool', 'string', 'byte', 'char', 'long',
                   'short', 'object', 'TileType', 'JobData', 'ItemId', 'ItemDefinition',
                   'WorkItem', 'GameSpeed', 'ToolRequirement', 'Node', 'SimulationContext',
                   'AgentDataPool', 'ITool', 'IJobHandler'];
    types.forEach(tp => {
        const re = new RegExp(`\\b(${tp})\\b`, 'g');
        html = html.replace(re, '<span class="tp">$1</span>');
    });

    // Attributes like [Flags]
    html = html.replace(/\[(\w+)\]/g, '<span class="attr">[$1]</span>');

    // Numbers
    html = html.replace(/\b(\d+\.?\d*[fFdD]?)\b/g, '<span class="num">$1</span>');

    // Methods
    html = html.replace(/\b([a-zA-Z_]\w*)(?=\s*\()/g, '<span class="fn">$1</span>');

    return html;
}

// ═══════════════════════════════════════════
// RENDERING VIEWS
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

    document.getElementById('breadcrumb').innerHTML = '🏠 <span>Главная</span> · Интерактивный курс';
    document.getElementById('content').innerHTML = `
        <div class="welcome fade-in">
            <div class="welcome-icon">🎮</div>
            <h1>Изучай C# через свою игру</h1>
            <p>Курс разработан специально под твою кодовую базу <strong>Colony Sim</strong> (Godot 4.7, .NET 8). Мы начнем с самых азов, разберем разницу с Python и дойдем до реального кода поселения!</p>
            
            <div class="features-grid">
                <div class="feature-card">
                    <div class="emoji">🌱</div>
                    <h3>С самых азов</h3>
                    <p>Никакого заумного академического языка. Всё объясняется через метафоры колонии, стройку и склад.</p>
                </div>
                <div class="feature-card">
                    <div class="emoji">🐍⚡</div>
                    <h3>Мост после Python</h3>
                    <p>Наглядные блоки сравнения: как привычные вещи из Python пишутся в C# и почему C# летает быстрее.</p>
                </div>
                <div class="feature-card">
                    <div class="emoji">🧑‍🌾</div>
                    <h3>Интерактивный симулятор</h3>
                    <p>Мини-симулятор колониста Боба прямо в браузере: руби сосны, корми поселенца и меняй статы кодом!</p>
                </div>
                <div class="feature-card">
                    <div class="emoji">🎯</div>
                    <h3>Практика без стресса</h3>
                    <p>Викторины и написание реальных строчек кода с умной и доброжелательной проверкой.</p>
                </div>
            </div>

            <div style="margin-top: 36px; display: flex; gap: 12px; justify-content: center;">
                <button class="btn btn-primary" onclick="openModule(0)" style="padding: 12px 28px; font-size: 15px;">
                    ▶ Начать с Модуля 1 (Абсолютный старт)
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
    renderSidebar();
    renderLesson();
}

function openLesson(lessonIdx) {
    playClickSound();
    currentLessonIdx = lessonIdx;
    renderSidebar();
    renderLesson();
}

function renderLesson() {
    const mod = MODULES[currentModuleIdx];
    const lesson = mod.lessons[currentLessonIdx];

    document.getElementById('breadcrumb').innerHTML = `
        <span onclick="openModule(${currentModuleIdx})" style="cursor:pointer">${mod.icon} ${mod.title}</span> 
        <span style="color:var(--text-muted)">/</span> 
        <span>Урок ${currentLessonIdx + 1} из ${mod.lessons.length}</span>
    `;

    let html = `<div class="fade-in">`;
    html += `
        <div class="lesson-header">
            <div class="lesson-badge">Модуль ${currentModuleIdx + 1}: ${mod.title}</div>
            <h1 class="lesson-title">${lesson.title}</h1>
            <p class="lesson-subtitle">${mod.desc}</p>
        </div>
    `;

    // Sections
    lesson.sections.forEach(section => {
        html += `<div class="section">`;

        if (section.type === 'text') {
            html += `<div class="text">${section.content}</div>`;
        } else if (section.type === 'code') {
            const code = section.source || '';
            const highlighted = highlightCSharp(code);
            html += `
                <div class="code-block">
                    <div class="code-header">
                        <span>${section.filename || 'C#'}</span>
                        <span style="color:var(--text-muted);font-size:10px;">C# .NET 8</span>
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
                            <pre>${section.py}</pre>
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
        } else if (section.type === 'bob_sim') {
            html += `
                <div class="bob-simulator">
                    <div class="sim-header">
                        <div class="sim-title">🧑‍🌾 Интерактивный симулятор «Боб в поселении»</div>
                        <div style="font-size:11px;color:var(--text-dim)">Живое изменение переменных в памяти</div>
                    </div>
                    <div class="sim-body">
                        <div class="bob-avatar-box">
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
                    <div class="sim-log" id="bob-log">✨ Попробуй нажать кнопки — это наглядно показывает, как код меняет значения в памяти!</div>
                </div>
            `;
        }

        html += `</div>`;
    });

    // Task section
    if (lesson.task) {
        const task = lesson.task;
        const isDone = isLessonCompleted(currentModuleIdx, currentLessonIdx);
        html += `
            <div class="task-area ${isDone ? 'correct' : ''}" id="task-area">
                <div class="section-title"><span class="icon">🎯</span> Практическое задание</div>
                <div class="text">${task.question}</div>
        `;

        if (task.type === 'quiz') {
            html += `<div class="task-options" id="task-options">`;
            const letters = ['A', 'B', 'C', 'D'];
            task.options.forEach((opt, idx) => {
                let optClass = 'task-option';
                if (isDone && idx === task.correct) optClass += ' correct-answer';
                html += `
                    <div class="${optClass}" onclick="selectOption(${idx})" data-idx="${idx}">
                        <span class="option-letter">${letters[idx]}</span>
                        <span>${opt}</span>
                    </div>
                `;
            });
            html += `</div>`;
        } else if (task.type === 'code') {
            html += `
                <div class="text" style="margin-top:10px;font-size:12px;color:var(--text-muted)">
                    💡 Подсказка: ${task.hint || ''}
                </div>
                <textarea class="task-input" id="task-input" placeholder="Напиши свой код на C# здесь..."></textarea>
            `;
        }

        html += `
            <div class="feedback ${isDone ? 'show success' : ''}" id="task-feedback">
                ${isDone ? '✅ Это задание уже выполнено!' : ''}
            </div>
            <button class="btn btn-primary" id="task-submit" style="margin-top:16px" onclick="submitTask()">
                ✋ Проверить ответ
            </button>
        </div>`;
    }

    // User Notes
    const noteKey = `${currentModuleIdx}_${currentLessonIdx}`;
    const noteContent = userNotes[noteKey] || '';
    html += `
        <div class="lesson-notes-area">
            <div class="notes-toggle" onclick="toggleNotes()">
                📝 <span>${noteContent ? 'Твоя заметка к уроку (сохранено)' : 'Добавить свою заметку к этому уроку'}</span>
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
        html += `<div style="color:var(--green);font-weight:700;padding:8px 0">🎉 Поздравляем! Ты прошел весь курс!</div>`;
    }

    html += `</div></div>`;

    document.getElementById('content').innerHTML = html;
    window.scrollTo({ top: 0, behavior: 'smooth' });
}

// ═══════════════════════════════════════════
// TASK EVALUATION SYSTEM
// ═══════════════════════════════════════════
let selectedOption = -1;

function selectOption(idx) {
    if (isLessonCompleted(currentModuleIdx, currentLessonIdx)) return;
    playClickSound();
    selectedOption = idx;
    document.querySelectorAll('.task-option').forEach(el => el.classList.remove('selected'));
    document.querySelector(`.task-option[data-idx="${idx}"]`).classList.add('selected');
}

function submitTask() {
    const mod = MODULES[currentModuleIdx];
    const lesson = mod.lessons[currentLessonIdx];
    const task = lesson.task;
    const feedbackEl = document.getElementById('task-feedback');
    const taskArea = document.getElementById('task-area');
    const nextBtn = document.getElementById('next-btn');

    if (!feedbackEl || !nextBtn) return;

    let isCorrect = false;
    let customError = null;

    if (task.type === 'quiz') {
        if (selectedOption < 0) {
            feedbackEl.className = 'feedback show error';
            feedbackEl.innerHTML = '⚠️ Пожалуйста, выбери один из вариантов ответа!';
            return;
        }
        isCorrect = selectedOption === task.correct;
    } else if (task.type === 'code') {
        const input = document.getElementById('task-input')?.value.trim();
        if (!input) {
            feedbackEl.className = 'feedback show error';
            feedbackEl.innerHTML = '⚠️ Поле ввода пустое! Напиши свой код на C#. Не бойся ошибиться.';
            return;
        }

        // Friendly smart diagnostics for beginners:
        if (!input.includes(';') && !input.includes('{') && !input.includes('=>')) {
            customError = 'Похоже, ты забыл точку с запятой <code>;</code> в конце команды! В C# это обязательное правило.';
        } else {
            try {
                // Execute check function
                let checkFn;
                if (typeof task.check === 'function') {
                    checkFn = task.check;
                } else {
                    checkFn = eval(task.check);
                }
                isCorrect = checkFn(input);
            } catch(e) {
                isCorrect = false;
            }
        }
    }

    if (isCorrect) {
        playSuccessSound();
        feedbackEl.className = 'feedback show success';
        feedbackEl.innerHTML = '✅ ' + (task.explanation || task.successMsg || 'Отлично! Задание решено верно!');
        taskArea.classList.add('correct');
        taskArea.classList.remove('wrong');

        completeLesson(currentModuleIdx, currentLessonIdx);

        if (task.type === 'quiz') {
            document.querySelectorAll('.task-option').forEach((el, idx) => {
                el.classList.remove('selected');
                if (idx === task.correct) el.classList.add('correct-answer');
                if (idx === selectedOption && idx !== task.correct) el.classList.add('wrong-answer');
            });
        }

        nextBtn.disabled = false;
        showBadge('🏅 Урок пройден!', `${mod.title} → ${lesson.title}`);

    } else {
        feedbackEl.className = 'feedback show error';
        const errText = customError || task.errorMsg || 'Не совсем точно. Проверь синтаксис или попробуй другой вариант!';
        feedbackEl.innerHTML = '❌ ' + errText;
        taskArea.classList.add('wrong');
        taskArea.classList.remove('correct');

        if (task.type === 'quiz') {
            document.querySelectorAll('.task-option').forEach((el, idx) => {
                if (idx === task.correct) el.classList.add('correct-answer');
                if (idx === selectedOption) el.classList.add('wrong-answer');
            });
        }
    }
}

function prevLesson() {
    playClickSound();
    selectedOption = -1;
    if (currentLessonIdx > 0) {
        currentLessonIdx--;
    } else if (currentModuleIdx > 0) {
        currentModuleIdx--;
        currentLessonIdx = MODULES[currentModuleIdx].lessons.length - 1;
    } else {
        showWelcome();
        return;
    }
    renderLesson();
}

function nextLesson() {
    playClickSound();
    selectedOption = -1;
    if (currentLessonIdx < MODULES[currentModuleIdx].lessons.length - 1) {
        currentLessonIdx++;
        renderLesson();
    } else {
        nextModule();
    }
}

function nextModule() {
    playClickSound();
    selectedOption = -1;
    if (currentModuleIdx < MODULES.length - 1) {
        currentModuleIdx++;
        currentLessonIdx = 0;
        renderSidebar();
        renderLesson();
    } else {
        showBadge('🎉 Грандиозно!', 'Ты завершил все модули курса C# для Colony Sim!');
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
// MOBILE MENU
// ═══════════════════════════════════════════
document.getElementById('menu-toggle')?.addEventListener('click', () => {
    document.getElementById('sidebar').classList.toggle('open');
});

// Close sidebar on click outside on mobile
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

# Target paths
target_path_1 = r"d:\bebebe-main (1)\csharp-course\index.html"
target_dir_2 = r"d:\bebebe-main (1)\bebebe-main\csharp-course"
target_path_2 = os.path.join(target_dir_2, "index.html")

os.makedirs(os.path.dirname(target_path_1), exist_ok=True)
os.makedirs(target_dir_2, exist_ok=True)

with open(target_path_1, "w", encoding="utf-8") as f:
    f.write(html_content)
print(f"Successfully generated: {target_path_1} ({len(html_content)} bytes)")

with open(target_path_2, "w", encoding="utf-8") as f:
    f.write(html_content)
print(f"Successfully generated: {target_path_2} ({len(html_content)} bytes)")
