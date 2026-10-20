import sys
sys.path.insert(0, r'D:\dependency_graph_visualizer')
import app

root = r'D:\bebebe-main (1)\bebebe-main'
res = app.analyze(root)
comps = res.get('cycles') or []
if not comps:
    print('NO CYCLES')
    sys.exit(0)
comp = set(comps[0])
edges = [e for e in res['edges'] if e['source'] in comp and e['target'] in comp]
print('SCC size=%d inner_edges=%d' % (len(comp), len(edges)))

pair = {}
for e in edges:
    pair.setdefault((e['source'], e['target']), []).append(e)

seen = set()
mutual = []
for (a, b), es in pair.items():
    key = tuple(sorted([a, b]))
    if (b, a) in pair and key not in seen:
        seen.add(key)
        mutual.append((key, es[0], pair[(b, a)][0]))

print('MUTUAL_PAIRS=%d' % len(mutual))
for key, ab, ba in mutual:
    print('  %s\n    <-> %s\n    [%s/%s ; %s/%s]' % (key[0], key[1], ab['type'], ab['confidence'], ba['type'], ba['confidence']))

print('--- inner edges (high/medium only) ---')
for e in sorted(edges, key=lambda x: x['confidence'], reverse=True):
    if e['confidence'] in ('high', 'medium'):
        print('  %s %s -> %s (%s)' % (e['confidence'], e['source'], e['target'], e['type']))