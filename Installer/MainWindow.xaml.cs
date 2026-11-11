using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;

namespace MdReaderSetup;

public partial class MainWindow : Window
{
    private readonly SetupOptions _options;
    private string _installedDir = string.Empty;

    public MainWindow(SetupOptions options)
    {
        InitializeComponent();

        _options = options;
        DirBox.Text = options.InstallDir;
        AssociateCheck.IsChecked = options.Associate;
        StartMenuCheck.IsChecked = options.StartMenu;
        DesktopCheck.IsChecked = options.Desktop;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "انتخاب پوشه‌ی نصب",
            InitialDirectory = Directory.Exists(DirBox.Text) ? DirBox.Text : SetupService.DefaultInstallDir,
        };

        if (dialog.ShowDialog(this) == true)
        {
            DirBox.Text = dialog.FolderName;
        }
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        var directory = DirBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(directory))
        {
            StatusText.Text = "یک پوشه برای نصب انتخاب کنید.";
            return;
        }

        try
        {
            directory = Path.GetFullPath(directory);
        }
        catch (Exception)
        {
            StatusText.Text = "مسیر وارد شده معتبر نیست.";
            return;
        }

        SetBusy(true, "در حال نصب…");

        var extensions = _options.Extensions;
        var associate = AssociateCheck.IsChecked == true;
        var startMenu = StartMenuCheck.IsChecked == true;
        var desktop = DesktopCheck.IsChecked == true;

        try
        {
            await Task.Run(() => SetupService.Install(directory, extensions, associate, startMenu, desktop));
            ShowDone(directory);
        }
        catch (Exception ex)
        {
            SetBusy(false, $"نصب ناموفق بود: {ex.Message}");
        }
    }

    private void SetBusy(bool busy, string message)
    {
        Progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        InstallButton.IsEnabled = !busy;
        CancelButton.IsEnabled = !busy;
        DirBox.IsEnabled = !busy;
        AssociateCheck.IsEnabled = !busy;
        StartMenuCheck.IsEnabled = !busy;
        DesktopCheck.IsEnabled = !busy;
        StatusText.Text = message;
    }

    private void ShowDone(string directory)
    {
        _installedDir = directory;
        DonePath.Text = SetupService.ExePath(directory);
        InstallPanel.Visibility = Visibility.Collapsed;
        DonePanel.Visibility = Visibility.Visible;
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(SetupService.ExePath(_installedDir)) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(this, $"برنامه باز نشد:\n{ex.Message}", "MD Reader",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
