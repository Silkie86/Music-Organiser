using System.IO;
using System.Windows;
using System.Windows.Controls;
using MusicOrganiser.App.ViewModels;

namespace MusicOrganiser.App.Views;

public partial class ShareCheckView : UserControl
{
    public ShareCheckView() => InitializeComponent();

    void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    async void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files && File.Exists(files[0]) &&
            DataContext is ShareCheckViewModel vm)
            await vm.Open(files[0]);
    }
}
