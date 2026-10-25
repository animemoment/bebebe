    /// <summary>Очистить все метки рубки при смене региона / перезапуске карты.</summary>
    public void ClearAll()
    {
        lock (_lock)
        {
            var oldTrees = new List<(int X, int Y)>(_markedTrees);
            _markedTrees.Clear();
            
            // Обновляем визуал всех деревьев на "обычный"
            foreach (var (x, y) in oldTrees)
            {
                if (IsValidTree(x, y))
                {
                    MapRenderer.Instance.UpdateToolVisual(x, y, ToolVisualSpec.WorldKinds.Tree);
                }
            }
            
            ChopProgress.ClearAll();
            JobBroker.Instance.ClearTreeJobs();
        }
    }