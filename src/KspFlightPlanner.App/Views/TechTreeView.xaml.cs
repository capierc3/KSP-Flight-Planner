using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using KspFlightPlanner.App.ViewModels;

namespace KspFlightPlanner.App.Views;

public partial class TechTreeView : UserControl
{
    public TechTreeView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is INotifyPropertyChanged old) old.PropertyChanged -= OnViewModelChanged;
            if (e.NewValue is INotifyPropertyChanged now) now.PropertyChanged += OnViewModelChanged;
        };
        IsVisibleChanged += (_, _) => { if (IsVisible) ScrollToSelected(); };
    }

    // A node picked from elsewhere (a part's "Show in tech tree", or the Parts pane) may be scrolled out of sight.
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TechTreeViewModel.Selected)) ScrollToSelected();
    }

    private void ScrollToSelected()
    {
        if (!IsVisible) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if ((DataContext as TechTreeViewModel)?.Selected is not { } node) return;
            if (NodeList.ItemContainerGenerator.ContainerFromItem(node) is FrameworkElement container)
                container.BringIntoView(new Rect(-40, -40, TechTreeViewModel.NodeWidth + 80, TechTreeViewModel.NodeHeight + 80));
        });
    }
}
