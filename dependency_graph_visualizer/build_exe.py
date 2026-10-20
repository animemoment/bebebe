import os
import sys
import PyInstaller.__main__

proj_dir = os.path.dirname(os.path.abspath(__file__))
os.chdir(proj_dir)

web_dir = os.path.join(proj_dir, "web")
app_py = os.path.join(proj_dir, "app.py")
build_dir = os.path.join(proj_dir, "build")

PyInstaller.__main__.run([
    '--onefile',
    '--noconfirm',
    '--clean',
    '--add-data', f'{web_dir};web',
    '--name', 'DependencyGraphVisualizer',
    '--specpath', proj_dir,
    '--distpath', proj_dir,
    '--workpath', build_dir,
    app_py
])

import shutil
try:
    exe_src = os.path.join(proj_dir, 'DependencyGraphVisualizer.exe')
    exe_dst = os.path.join(proj_dir, 'app.exe')
    if os.path.exists(exe_src):
        shutil.copy2(exe_src, exe_dst)
        print(f"[+] Successfully copied {exe_src} to {exe_dst}")
except Exception as e:
    print(f"[!] Warning copying to app.exe: {e}")

