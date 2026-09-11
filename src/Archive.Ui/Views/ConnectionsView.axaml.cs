using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Archive.Ui.Views;

public partial class ConnectionsView : UserControl
{
    public ConnectionsView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
