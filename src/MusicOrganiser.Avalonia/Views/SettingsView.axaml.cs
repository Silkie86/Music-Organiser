using Avalonia.Controls;
using MusicOrganiser.ViewModels;

namespace MusicOrganiser.Avalonia.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        // Settings opens on the tab last used; a tab that's gone since leaves it on the first. Each time it opens, the app's
        // own files are measured in the background (Settings › General shows them).
        SettingsViewModel? shown = null;
        DataContextChanged += (_, _) =>
        {
            if (shown is not null) shown.PropertyChanged -= TabChanged;
            shown = DataContext as SettingsViewModel;
            if (shown is null) return;
            ShowTab(shown.Tab);
            shown.PropertyChanged += TabChanged;
            _ = shown.AppFiles.MeasureAsync();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            if (shown is not null) shown.PropertyChanged -= TabChanged;
        };
        Tabs.SelectionChanged += (_, e) =>
        {
            if (e.Source == Tabs && DataContext is SettingsViewModel vm && Tabs.SelectedItem is TabItem { Header: string header })
                vm.Tab = header;
        };
    }

    // A tab picked from elsewhere while Settings shows (the left bar's low-space sign picks General).
    void TabChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.Tab) && sender is SettingsViewModel vm) ShowTab(vm.Tab);
    }

    void ShowTab(string header)
    {
        if (Tabs.Items.OfType<TabItem>().FirstOrDefault(t => Equals(t.Header, header)) is { } tab && Tabs.SelectedItem != tab)
            Tabs.SelectedItem = tab;
    }
}
