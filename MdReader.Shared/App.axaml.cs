using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform;

namespace MdReader;

public partial class App : Application
{
    private const int DefaultWidth = 1080;
    private const int DefaultHeight = 760;
    private const int MinimumWidth = 720;
    private const int MinimumHeight = 440;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        switch (ApplicationLifetime)
        {
            case IClassicDesktopStyleApplicationLifetime desktop:
                desktop.MainWindow = CreateDesktopWindow(desktop.Args ?? Array.Empty<string>());
                break;

            // Android, iOS and the browser host a single view instead of a window.
            case ISingleViewApplicationLifetime singleView:
                singleView.MainView = new MainView();
                break;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static Window CreateDesktopWindow(string[] args)
    {
        var view = new MainView();

        var window = new Window
        {
            Title = $"MD Reader {MainView.AppVersion}",
            Width = DefaultWidth,
            Height = DefaultHeight,
            MinWidth = MinimumWidth,
            MinHeight = MinimumHeight,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Background = new SolidColorBrush(Color.Parse("#FFFFFF")),
            FontSize = 13,
            Content = view,
        };

        TryApplyIcon(window);

        view.DocumentTitleChanged += (_, title) => window.Title = title;
        window.Opened += (_, _) => view.ApplyStartArguments(args);

        return window;
    }

    private static void TryApplyIcon(Window window)
    {
        try
        {
            var assembly = typeof(App).Assembly.GetName().Name;
            window.Icon = new WindowIcon(AssetLoader.Open(new Uri($"avares://{assembly}/assets/MdReader.png")));
        }
        catch (Exception)
        {
            // A missing icon file is not a reason to fail start-up.
        }
    }
}
