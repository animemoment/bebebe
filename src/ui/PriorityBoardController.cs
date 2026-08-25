using System;
using Godot;
using Game.Core;
using Game.Simulation;

namespace Game.UI;

public partial class PriorityBoardController : Control
{
    private Control _rootPanel;

    private Button _logPlusBtn;
    private Button _logMinusBtn;
    private Label _logNumLabel;

    private Button _lumbPlusBtn;
    private Button _lumbMinusBtn;
    private Label _lumbNumLabel;

    private Button _farmPlusBtn;
    private Button _farmMinusBtn;
    private Label _farmNumLabel;

    private Button _buildPlusBtn;
    private Button _buildMinusBtn;
    private Label _buildNumLabel;

    public event Action OnVisibilityToggled;

    public override void _Ready()
    {
        Initialize(this);
    }

    public void Initialize(Control targetNode)
    {
        _rootPanel = targetNode ?? this;
        _rootPanel.Visible = false;

        // 1. Логистика (LogisticsSpecialist -> NumLog)
        var logContainer = _rootPanel.FindChild("LogisticsSpecialist", true, false);
        if (logContainer != null)
        {
            _logPlusBtn = logContainer.FindChild("+", true, false) as Button;
            _logMinusBtn = logContainer.FindChild("-", true, false) as Button;
            _logNumLabel = logContainer.FindChild("NumLog", true, false) as Label;
        }

        // 2. Лесозаготовка (LumberjackPrioritetContainer -> NumLumb)
        var lumbContainer = _rootPanel.FindChild("LumberjackPrioritetContainer", true, false);
        if (lumbContainer != null)
        {
            _lumbPlusBtn = lumbContainer.FindChild("+", true, false) as Button;
            _lumbMinusBtn = lumbContainer.FindChild("-", true, false) as Button;
            _lumbNumLabel = lumbContainer.FindChild("NumLumb", true, false) as Label;
        }

        // 3. Фермерство (FarmerPrioritetContainer2 -> NumFarm)
        var farmContainer = _rootPanel.FindChild("FarmerPrioritetContainer2", true, false);
        if (farmContainer != null)
        {
            _farmPlusBtn = farmContainer.FindChild("+", true, false) as Button;
            _farmMinusBtn = farmContainer.FindChild("-", true, false) as Button;
            _farmNumLabel = farmContainer.FindChild("NumFarm", true, false) as Label;
        }

        // 4. Строительство (BuilderPrioritetContainer3 -> NumBuild)
        var buildContainer = _rootPanel.FindChild("BuilderPrioritetContainer3", true, false);
        if (buildContainer != null)
        {
            _buildPlusBtn = buildContainer.FindChild("+", true, false) as Button;
            _buildMinusBtn = buildContainer.FindChild("-", true, false) as Button;
            _buildNumLabel = buildContainer.FindChild("NumBuild", true, false) as Label;
        }

        if (_logPlusBtn != null) _logPlusBtn.Pressed += () => ChangePriority(JobCategory.Logistics, 1);
        if (_logMinusBtn != null) _logMinusBtn.Pressed += () => ChangePriority(JobCategory.Logistics, -1);

        if (_lumbPlusBtn != null) _lumbPlusBtn.Pressed += () => ChangePriority(JobCategory.Lumberjack, 1);
        if (_lumbMinusBtn != null) _lumbMinusBtn.Pressed += () => ChangePriority(JobCategory.Lumberjack, -1);

        if (_farmPlusBtn != null) _farmPlusBtn.Pressed += () => ChangePriority(JobCategory.Farming, 1);
        if (_farmMinusBtn != null) _farmMinusBtn.Pressed += () => ChangePriority(JobCategory.Farming, -1);

        if (_buildPlusBtn != null) _buildPlusBtn.Pressed += () => ChangePriority(JobCategory.Construction, 1);
        if (_buildMinusBtn != null) _buildMinusBtn.Pressed += () => ChangePriority(JobCategory.Construction, -1);

        RefreshLabels();
    }

    public void Open()
    {
        if (_rootPanel != null) _rootPanel.Visible = true;
        RefreshLabels();
        OnVisibilityToggled?.Invoke();
    }

    public void Close()
    {
        if (_rootPanel != null) _rootPanel.Visible = false;
        OnVisibilityToggled?.Invoke();
    }

    public void Toggle()
    {
        if (IsOpen)
            Close();
        else
            Open();
    }

    public bool IsOpen => _rootPanel != null && _rootPanel.Visible;

    private void ChangePriority(JobCategory category, int delta)
    {
        int current = JobPriorityManager.Instance.GetPriority(category);
        int next = Math.Max(0, current + delta);
        JobPriorityManager.Instance.SetPriority(category, next);
        RefreshLabels();
    }

    private void RefreshLabels()
    {
        if (_logNumLabel != null)
            _logNumLabel.Text = JobPriorityManager.Instance.GetPriority(JobCategory.Logistics).ToString();

        if (_lumbNumLabel != null)
            _lumbNumLabel.Text = JobPriorityManager.Instance.GetPriority(JobCategory.Lumberjack).ToString();

        if (_farmNumLabel != null)
            _farmNumLabel.Text = JobPriorityManager.Instance.GetPriority(JobCategory.Farming).ToString();

        if (_buildNumLabel != null)
            _buildNumLabel.Text = JobPriorityManager.Instance.GetPriority(JobCategory.Construction).ToString();
    }
}