using System.Windows;
using System.Windows.Controls;

namespace IslandBar;

sealed partial class IslandWindow
{
    Button? _overflowButton;
    readonly List<(string Label, Action Open)> _overflowModules = [];

    void InitializeDockOverflow()
    {
        _overflowButton = MakeButton(Label("…", 16), "Ещё функции", 32);
        _overflowButton.Visibility = Visibility.Collapsed;
        _overflowButton.Click += (_, _) => {
            var column = CardColumn(270);
            foreach (var module in _overflowModules) {
                var button = PanelControls.Button(module.Label, module.Label);
                button.Click += (_, _) => module.Open();
                column.Children.Add(button);
            }
            OpenCard("overflow", column);
        };
        _featureTriggers["overflow"] = _overflowButton;
        _dock.Children.Insert(_dock.Children.IndexOf(_scroll), _overflowButton);
    }

    void FitDockModules()
    {
        if (_overflowButton is null) return;
        var candidates = new (string Key, string Label, Action Open)[] {
            ("network", "Скорость сети", OpenNetwork), ("music", "Музыка", OpenMusic),
            ("pc", "Нагрузка ПК", OpenPc), ("sets", "Наборы приложений", OpenSets),
            ("downloads", "Загрузки", OpenDownloads), ("layouts", "Раскладка окон", OpenLayouts),
            ("folders", "Быстрые папки", OpenFolders)
        };
        _overflowModules.Clear();
        _overflowButton.Visibility = Visibility.Collapsed;
        foreach (var item in ModuleSettings)
            _featureTriggers[item.Key].Visibility = _preferences.Modules.HasFlag(item.Module) ? Visibility.Visible : Visibility.Collapsed;
        var available = new Size(double.PositiveInfinity, 58);
        double fixedWidth = 0;
        foreach (UIElement child in _dock.Children) {
            if (ReferenceEquals(child, _scroll)) continue;
            child.Measure(available);
            fixedWidth += child.DesiredSize.Width;
        }
        double budget = Math.Max(0, Width - 42 - Math.Min(170, Width * .25));
        foreach (var item in candidates) {
            if (fixedWidth <= budget) break;
            var button = _featureTriggers[item.Key];
            if (button.Visibility == Visibility.Collapsed) continue;
            if (_overflowModules.Count == 0) fixedWidth += _overflowButton.Width;
            fixedWidth -= button.DesiredSize.Width;
            button.Visibility = Visibility.Collapsed;
            _overflowModules.Add((item.Label, item.Open));
        }
        _overflowButton.Visibility = _overflowModules.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }
}
