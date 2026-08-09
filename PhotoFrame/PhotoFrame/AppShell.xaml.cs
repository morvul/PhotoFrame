using Microsoft.Maui.Controls;

namespace PhotoFrame
{
    public partial class AppShell : Shell
    {
        public AppShell()
        {
            InitializeComponent();

            // Экран настроек открывается поверх слайд-шоу и не должен появляться
            // во flyout, поэтому регистрируется маршрутом, а не ShellContent.
            Routing.RegisterRoute(nameof(SettingsPage), typeof(SettingsPage));
            Routing.RegisterRoute(nameof(FolderPickerPage), typeof(FolderPickerPage));
        }
    }
}
