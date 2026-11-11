using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MdReader.Rendering;

namespace MdReader;

public partial class MainWindow : Window
{
    private static readonly double[] FontSizes = { 12, 13, 14, 15, 16, 18, 20, 22, 24, 28, 32, 36, 44, 56 };

    private static readonly string[] PreferredFonts =
    {
        "Vazirmatn", "Segoe UI", "Tahoma", "IRANSans", "Noto Naskh Arabic",
        "Noto Sans Arabic", "Noto Sans", "DejaVu Sans", "Helvetica Neue", "Arial",
    };

    private readonly string[] _startArgs;
    private readonly MarkdownRenderOptions _options = new();
    private string _markdown = string.Empty;
    private string? _filePath;
    private RenderedMarkdown? _rendered;
    private string? _emptyTitle;
    private bool _suppressEvents;

    static MainWindow()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public MainWindow() : this(Array.Empty<string>())
    {
    }

    public MainWindow(string[] args)
    {
        InitializeComponent();

        _startArgs = args;

        TryLoadWindowIcon();
        LoadFontFamilies();
        LoadFontSizes();
        ShowEmptyState();

        Opened += MainWindow_Opened;
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, Window_DragOver);
        AddHandler(DragDrop.DropEvent, Window_Drop);
        AddHandler(PointerWheelChangedEvent, Window_PointerWheelChanged, RoutingStrategies.Tunnel);
    }

    private void TryLoadWindowIcon()
    {
        try
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://MdReader/assets/MdReader.png")));
        }
        catch (Exception)
        {
            // A missing icon file is not a reason to fail start-up.
        }
    }

    private void MainWindow_Opened(object? sender, EventArgs e)
    {
        var wantsRtl = _startArgs.Any(a => a.Equals("--rtl", StringComparison.OrdinalIgnoreCase));
        if (wantsRtl)
        {
            _suppressEvents = true;
            RtlCheckBox.IsChecked = true;
            _suppressEvents = false;
            _options.Rtl = true;
        }

        var path = _startArgs.FirstOrDefault(a => !a.StartsWith('-') && File.Exists(a));
        if (path != null)
        {
            OpenFile(path, preserveScroll: false);
        }
    }

    // ---------- toolbar ----------

    private async void OpenButton_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "انتخاب فایل Markdown",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("فایل‌های Markdown")
                {
                    Patterns = new[] { "*.md", "*.markdown", "*.mdown", "*.mkd" },
                },
                new FilePickerFileType("فایل متنی")
                {
                    Patterns = new[] { "*.txt" },
                },
                FilePickerFileTypes.All,
            },
        });

        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
        {
            OpenFile(path, preserveScroll: false);
        }
    }

    private void ReloadButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_filePath != null)
        {
            OpenFile(_filePath, preserveScroll: true);
        }
    }

    private void RtlCheckBox_Changed(object? sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
        {
            return;
        }

        _options.Rtl = RtlCheckBox.IsChecked == true;
        Refresh();
    }

    private void FontCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents || FontCombo.SelectedItem is not FontFamily family)
        {
            return;
        }

        _options.FontFamilyName = family.Name;
        Refresh(preserveScroll: true);
    }

    private void SizeCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents || SizeCombo.SelectedItem is not double size)
        {
            return;
        }

        ApplyFontSize(size);
    }

    private void ZoomIn_Click(object? sender, RoutedEventArgs e) => StepFontSize(1);

    private void ZoomOut_Click(object? sender, RoutedEventArgs e) => StepFontSize(-1);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled)
        {
            return;
        }

        var ctrl = (e.KeyModifiers & KeyModifiers.Control) != 0;

        if (ctrl && e.Key == Key.O)
        {
            OpenButton_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.F5 && _filePath != null)
        {
            OpenFile(_filePath, preserveScroll: true);
            e.Handled = true;
        }
        else if (ctrl && e.Key is Key.OemPlus or Key.Add)
        {
            StepFontSize(1);
            e.Handled = true;
        }
        else if (ctrl && e.Key is Key.OemMinus or Key.Subtract)
        {
            StepFontSize(-1);
            e.Handled = true;
        }
        else if (ctrl && e.Key is Key.D0 or Key.NumPad0)
        {
            ApplyFontSize(16);
            e.Handled = true;
        }
    }

    private void Window_PointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if ((e.KeyModifiers & KeyModifiers.Control) == 0)
        {
            return;
        }

        StepFontSize(e.Delta.Y > 0 ? 1 : -1);
        e.Handled = true;
    }

    private void Window_DragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void Window_Drop(object? sender, DragEventArgs e)
    {
        var file = e.DataTransfer.TryGetFiles()?.FirstOrDefault();
        if (file?.TryGetLocalPath() is { } path)
        {
            OpenFile(path, preserveScroll: false);
        }
    }

    // ---------- document ----------

    private void OpenFile(string path, bool preserveScroll)
    {
        try
        {
            _markdown = ReadText(path);
            _filePath = path;
            Title = $"{Path.GetFileName(path)} — MD Reader";
            ReloadButton.IsEnabled = true;
            Refresh(preserveScroll);
            UpdateStatus();
        }
        catch (Exception ex)
        {
            FileText.Text = $"فایل باز نشد: {ex.Message}";
            FileText.Foreground = new SolidColorBrush(Color.Parse("#B42318"));
            CountText.Text = string.Empty;
        }
    }

    private void Refresh(bool preserveScroll = true)
    {
        if (_markdown.Trim().Length == 0)
        {
            _emptyTitle = _filePath != null ? "این فایل خالی است." : null;
            ShowEmptyState();
            return;
        }

        var offset = preserveScroll ? DocScroll.Offset.Y : 0;

        _rendered = MarkdownRenderer.Render(_markdown, _options);
        DocHost.Children.Clear();
        DocHost.Children.Add(_rendered.Root);

        if (offset > 0)
        {
            RestoreScroll(offset);
        }
    }

    private void ApplyFontSize(double size)
    {
        _options.BaseFontSize = size;

        if (_rendered != null)
        {
            foreach (var apply in _rendered.SizeAppliers)
            {
                apply(size);
            }
        }
        else
        {
            ShowEmptyState();
        }

        _suppressEvents = true;
        if (FontSizes.Contains(size))
        {
            SizeCombo.SelectedItem = size;
        }
        _suppressEvents = false;
    }

    private void StepFontSize(int direction)
    {
        var current = _options.BaseFontSize;
        var nearest = 0;
        var best = double.MaxValue;

        for (var i = 0; i < FontSizes.Length; i++)
        {
            var distance = Math.Abs(FontSizes[i] - current);
            if (distance < best)
            {
                best = distance;
                nearest = i;
            }
        }

        ApplyFontSize(FontSizes[Math.Clamp(nearest + direction, 0, FontSizes.Length - 1)]);
    }

    private void ShowEmptyState()
    {
        _rendered = null;
        DocHost.Children.Clear();

        var direction = _options.Rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        var align = _options.Rtl ? TextAlignment.Right : TextAlignment.Left;
        var panel = new StackPanel
        {
            Margin = new Thickness(30, 44, 30, 30),
            FlowDirection = direction,
        };

        panel.Children.Add(new TextBlock
        {
            Text = _emptyTitle ?? "فایلی باز نشده است",
            FontSize = _options.BaseFontSize * 1.5,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse("#24292F")),
            TextAlignment = align,
            Margin = new Thickness(0, 0, 0, 10),
        });

        panel.Children.Add(new TextBlock
        {
            Text = _emptyTitle == null
                ? "برای شروع، یک فایل Markdown را باز کنید یا آن را روی همین پنجره بکشید."
                : "فایل انتخاب‌شده هیچ محتوایی ندارد.",
            FontSize = _options.BaseFontSize,
            Foreground = new SolidColorBrush(Color.Parse("#6B7580")),
            TextAlignment = align,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 18),
        });

        foreach (var (keys, description) in new (string, string)[]
                 {
                     ("Ctrl + O", "باز کردن فایل"),
                     ("F5", "بازخوانی فایل فعلی"),
                     ("Ctrl + + / Ctrl + −", "بزرگ و کوچک کردن فونت"),
                     ("Ctrl + 0", "بازگشت فونت به اندازه پیش‌فرض"),
                 })
        {
            var row = new TextBlock
            {
                FontSize = _options.BaseFontSize * 0.85,
                Foreground = new SolidColorBrush(Color.Parse("#6B7580")),
                TextAlignment = align,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 4),
            };

            row.Inlines!.Add(new Avalonia.Controls.Documents.Run(keys)
            {
                FontFamily = new FontFamily("Consolas, Menlo, DejaVu Sans Mono, monospace"),
            });
            row.Inlines!.Add(new Avalonia.Controls.Documents.Run("   " + description));
            panel.Children.Add(row);
        }

        DocHost.Children.Add(panel);
    }

    private void LoadFontFamilies()
    {
        var families = FontManager.Current.SystemFonts
            .OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        FontCombo.ItemsSource = families;

        var preferred = PreferredFonts
                            .Select(name => families.FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                            .FirstOrDefault(match => match != null)
                        ?? families.FirstOrDefault();

        if (preferred != null)
        {
            _suppressEvents = true;
            FontCombo.SelectedItem = preferred;
            _suppressEvents = false;
            _options.FontFamilyName = preferred.Name;
        }
    }

    private void LoadFontSizes()
    {
        _suppressEvents = true;
        SizeCombo.ItemsSource = FontSizes;
        SizeCombo.SelectedItem = 16d;
        _suppressEvents = false;
        _options.BaseFontSize = 16;
    }

    private void UpdateStatus()
    {
        FileText.Foreground = new SolidColorBrush(Color.Parse("#4B5563"));

        if (_filePath == null)
        {
            FileText.Text = "فایلی باز نشده است";
            CountText.Text = string.Empty;
            return;
        }

        FileText.Text = _filePath;
        var words = Regex.Matches(_markdown, @"[\p{L}\p{N}]+").Count;
        CountText.Text = string.Format(CultureInfo.CurrentCulture, "{0:N0} کلمه · {1:N0} کاراکتر", words, _markdown.Length);
    }

    private static string ReadText(string path)
    {
        var bytes = File.ReadAllBytes(path);

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }

        try
        {
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            // Legacy Windows-1256 files (common for older Persian text).
            return Encoding.GetEncoding(1256).GetString(bytes);
        }
    }

    // ---------- scrolling helpers ----------

    private void RestoreScroll(double offset)
    {
        void Restore() => DocScroll.Offset = new Vector(DocScroll.Offset.X, offset);

        Dispatcher.UIThread.Post(Restore, DispatcherPriority.Loaded);
        Dispatcher.UIThread.Post(Restore, DispatcherPriority.Background);
    }
}
