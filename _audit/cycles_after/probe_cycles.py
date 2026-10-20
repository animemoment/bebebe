import sys, json
sys.path.insert(0, r'D:\dependency_graph_visualizer')
import app

root = r'D:\bebebe-main (1)\bebebe-main'
res = app.analyze(root)
s = res['stats']
print('STATS files=%s edges=%s cycles=%s health=%s cs=%s gd=%s scenes=%s'
      % (s['files'], s['edges'], s['cycles'], res['health_score'], s['csharp'], s['gdscript'], s['scenes']))
print('CYCLE_DETAILS=%d' % len(res.get('cycle_details') or []))
for i, cd in enumerate(res.get('cycle_details') or []):
    w = cd.get('weakest_link') or {}
    print('CYCLE#%d size=%d weakest=%s -> %s (%s/%s)' % (
        i + 1, cd['size'], w.get('source'), w.get('target'), w.get('type'), w.get('confidence')))
    for n in cd['nodes']:
        print('     ', n)
print('--- truly_dead ---')
for d in (res.get('orphans_summary') or {}).get('truly_dead') or []:
    print('    ', d)