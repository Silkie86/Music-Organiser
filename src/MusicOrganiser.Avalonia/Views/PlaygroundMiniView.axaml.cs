using Avalonia.Controls;

namespace MusicOrganiser.Avalonia.Views;

public partial class PlaygroundMiniView : UserControl
{
    public PlaygroundMiniView()
    {
        InitializeComponent();
        // A lane picked (the part, when it's loaded) scrolls the lanes to it, not the card it sits in: the card stays at
        // its top, where Play and the bars are.
        AddHandler(RequestBringIntoViewEvent, (_, e) => e.Handled = true);
    }
}
