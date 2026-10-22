using Avalonia.Controls;
using MusicOrganiser.ViewModels;

namespace MusicOrganiser.Avalonia.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        // Settings opens on the tab last used; a tab that's gone since leaves it on the first.
        DataContextChanged += (_, _) =>
        {
            if (DataContext is SettingsViewModel vm &&
                Tabs.Items.OfType<TabItem>().FirstOrDefault(t => Equals(t.Header, vm.Tab)) is { } last)
                Tabs.SelectedItem = last;
        };
        Tabs.SelectionChanged += (_, e) =>
        {
            if (e.Source == Tabs && DataContext is SettingsViewModel vm && Tabs.SelectedItem is TabItem { Header: string header })
                vm.Tab = header;
        };
    }
}
