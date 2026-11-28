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

    /// <summary>The view of the running app. A platform that leaves from outside the view (the
    /// Android back button) uses it to ask about unsaved edits before the app goes away.</summary>
    public static MainView? ActiveView { get; private set; }

    public override void OnFrameworkInitializationCompleted()
    {
        var settings = AppSettings.Load();

        // The theme and the language are applied before the first view is built, so the window
        // never flashes the wrong palette.
        AppTheme.Apply(settings.Theme);
        Lang.Apply(settings.Language);

        switch (ApplicationLifetime)
        {
            case IClassicDesktopStyleApplicationLifetime desktop:
                desktop.MainWindow = CreateDesktopWindow(desktop.Args ?? Array.Empty<string>());
                break;

            // Android can host several activities, so it asks for a view factory instead.
            case IActivityApplicationLifetime activity:
                activity.MainViewFactory = () =>
                {
                    var view = new MainView();
                    ActiveView = view;
                    return view;
                };
                break;

            // iOS and the browser host a single view instead of a window.
            case ISingleViewApplicationLifetime singleView:
            {
                var view = new MainView();
                singleView.MainView = view;
                ActiveView = view;
                break;
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>The window background for the current theme, used by the chrome around the view.</summary>
    public static IBrush WindowBackground() =>
        Current is { } app && app.TryFindResource("AppBackground", out var value) && value is IBrush brush
            ? brush
            : Brushes.White;

    private static Window CreateDesktopWindow(string[] args)
    {
        var view = new MainView();
        ActiveView = view;

        var window = new Window
        {
            Title = $"MD Reader {MainView.AppVersion}",
            Width = DefaultWidth,
            Height = DefaultHeight,
            MinWidth = MinimumWidth,
            MinHeight = MinimumHeight,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Background = WindowBackground(),
            FontSize = 13,
            Content = view,
        };

        TryApplyIcon(window);

        view.DocumentTitleChanged += (_, title) => window.Title = title;
        window.Opened += (_, _) => view.ApplyStartArguments(args);

        // Closing the window exits the app, so it goes through the same prompt as closing a tab:
        // the window only closes once the view has no unsaved edits left.
        var closeConfirmed = false;
        window.Closing += async (_, e) =>
        {
            if (closeConfirmed || !view.HasUnsavedTabs)
            {
                return;
            }

            e.Cancel = true;

            if (await view.ConfirmExitAsync())
            {
                closeConfirmed = true;
                window.Close();
            }
        };

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
