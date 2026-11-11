using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;

namespace MdReaderSetup;

public partial class UninstallWindow : Window
{
    private readonly SetupOptions _options;
    private bool _removed;

    public UninstallWindow(SetupOptions options)
    {
        InitializeComponent();

        _options = options;
        DirText.Text = options.InstallDir;
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (_removed)
        {
            Close();
            return;
        }

        RemoveButton.IsEnabled = false;
        CancelButton.IsEnabled = false;

        try
        {
            var directory = _options.InstallDir;
            await Task.Run(() => SetupService.Uninstall(directory));

            _removed = true;
            TitleText.Text = "حذف شد";
            DirText.Text = "MD Reader و تنظیمات مرتبط با آن پاک شد.";
            RemoveButton.Content = "بستن";
            RemoveButton.Background = new SolidColorBrush(Color.FromRgb(0x1D, 0x6F, 0xE0));
            RemoveButton.IsEnabled = true;
            CancelButton.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            TitleText.Text = "حذف ناموفق بود";
            DirText.Text = ex.Message;
            RemoveButton.IsEnabled = true;
            CancelButton.IsEnabled = true;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
