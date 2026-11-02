using System.Windows;
using PureClean.Infrastructure;

namespace PureClean;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
    }
}
