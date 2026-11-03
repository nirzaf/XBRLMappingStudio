using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using XBRLMappingStudio.Desktop.ViewModels;

namespace XBRLMappingStudio.Desktop;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);

        // UI04: double-clicking a diagnostic row focuses and highlights its related node.
        // Wired at the window level so it is present regardless of template trimming.
        AddHandler(InputElement.DoubleTappedEvent, OnDiagnosticDoubleTapped, handledEventsToo: true);
    }

    private void OnDiagnosticDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (e.Source is not Avalonia.Visual source) return;
        var item = source.FindAncestorOfType<ListBoxItem>();
        if (item?.DataContext is DiagnosticRow { RelatedNodeId: not null } row)
        {
            vm.Session.Select(row.RelatedNodeId!, additive: false);
            vm.SelectedNode = vm.Nodes.FirstOrDefault(n => n.NodeId == row.RelatedNodeId);
        }
    }
}
