import os

search_files = ['JobData.cs', 'TileType.cs', 'ItemRegistry.cs', 'WorkItem.cs', 'ITool.cs', 'IJobHandler.cs', 'GameSpeed.cs', 'ToolRequirement.cs', 'DayNightCycle.cs', 'AgentRenderer.cs']
found = {}

for root, dirs, files in os.walk('src'):
    for f in files:
        if f in search_files:
            rel = os.path.relpath(os.path.join(root, f), '.')
            found[f] = rel

for k, v in found.items():
    print(f"{k} -> {v}")
