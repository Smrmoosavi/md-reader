using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MdReader.Rendering;

namespace MdReader;

/// <summary>One open document. Files picked through Android's document picker have no
/// filesystem path, so the storage item is kept and its stream re-read on reload.</summary>
public sealed class DocumentTab
{
    public DocumentTab(string header, string? path, IStorageFile? file, string markdown)
    {
        Header = header;
        Path = path;
        File = file;
        Markdown = markdown;
    }

    public string Header { get; set; }

    public string? Path { get; }

    public IStorageFile? File { get; }

    public string Markdown { get; set; }

    public double ScrollOffset { get; set; }

    public RenderedMarkdown? Rendered { get; set; }

    public string? EmptyTitle { get; set; }

    public string ToolTip => Path ?? Header;
}

public partial class MainView : UserControl
{
    private static readonly double[] FontSizes = { 12, 13, 14, 15, 16, 18, 20, 22, 24, 28, 32, 36, 44, 56 };

    public static readonly string AppVersion = ResolveVersion();

    private static readonly string[] PreferredFonts =
    {
        "Vazirmatn", "Segoe UI", "Tahoma", "IRANSans", "Noto Naskh Arabic",
        "Noto Sans Arabic", "Noto Sans", "DejaVu Sans", "Helvetica Neue", "Arial",
    };

    private readonly MarkdownRenderOptions _options = new();
    private readonly ObservableCollection<DocumentTab> _tabs = new();

    private DocumentTab? _current;
    private bool _suppressEvents;
    private bool _suppressTabEvents;

    static MainView()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public MainView()
    {
        InitializeComponent();

        DocTabs.ItemsSource = _tabs;

        LoadFontFamilies();
        LoadFontSizes();
        ShowEmptyState();

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, View_DragOver);
        AddHandler(DragDrop.DropEvent, View_Drop);
        AddHandler(PointerWheelChangedEvent, View_PointerWheelChanged, RoutingStrategies.Tunnel);
    }

    /// <summary>Raised when the open document changes; the desktop window uses it for its title bar.</summary>
    public event EventHandler<string>? DocumentTitleChanged;

    private static string ResolveVersion()
    {
        var info = typeof(MainView).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (string.IsNullOrWhiteSpace(info))
        {
            return typeof(MainView).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        }

        // Source-link builds append "+<commit>"; the window title only needs the version.
        var plus = info.IndexOf('+');
        return plus > 0 ? info[..plus] : info;
    }

    /// <summary>Applies command line arguments (desktop only: <c>--rtl</c> and optional file paths).</summary>
    public async void ApplyStartArguments(string[] args)
    {
        if (args.Any(a => a.Equals("--rtl", StringComparison.OrdinalIgnoreCase)))
        {
            _suppressEvents = true;
            RtlCheckBox.IsChecked = true;
            _suppressEvents = false;
            _options.Rtl = true;
        }

        foreach (var path in args.Where(a => !a.StartsWith('-') && System.IO.File.Exists(a)))
        {
            await OpenPathAsync(path);
        }
    }

    // ---------- toolbar ----------

    private async void OpenButton_Click(object? sender, RoutedEventArgs e)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage == null)
        {
            return;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "انتخاب فایل Markdown",
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("فایل‌های Markdown")
                {
                    Patterns = new[] { "*.md", "*.markdown", "*.mdown", "*.mkd" },
                    AppleUniformTypeIdentifiers = new[] { "net.daringfireball.markdown", "public.plain-text" },
                    MimeTypes = new[] { "text/markdown", "text/x-markdown", "text/plain" },
                },
                new FilePickerFileType("فایل متنی")
                {
                    Patterns = new[] { "*.txt" },
                    MimeTypes = new[] { "text/plain" },
                },
                FilePickerFileTypes.All,
            },
        });

        foreach (var file in files)
        {
            await OpenFileAsync(file);
        }
    }

    private async void ReloadButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_current == null)
        {
            return;
        }

        if (_current.Path != null)
        {
            await OpenPathAsync(_current.Path, replaceCurrent: true);
        }
        else if (_current.File != null)
        {
            await OpenFileAsync(_current.File, replaceCurrent: true);
        }
    }

    private void CloseTab_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is DocumentTab tab)
        {
            CloseTab(tab);
        }

        e.Handled = true;
    }

    private void RtlCheckBox_Changed(object? sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
        {
            return;
        }

        _options.Rtl = RtlCheckBox.IsChecked == true;
        Refresh(preserveScroll: true);
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
        else if (ctrl && e.Key == Key.W && _current != null)
        {
            CloseTab(_current);
            e.Handled = true;
        }
        else if (e.Key == Key.F5 && _current != null)
        {
            ReloadButton_Click(this, new RoutedEventArgs());
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

    private void View_PointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if ((e.KeyModifiers & KeyModifiers.Control) == 0)
        {
            return;
        }

        StepFontSize(e.Delta.Y > 0 ? 1 : -1);
        e.Handled = true;
    }

    private void View_DragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void View_Drop(object? sender, DragEventArgs e)
    {
        var files = e.DataTransfer.TryGetFiles()?.OfType<IStorageFile>().ToArray();
        if (files is not { Length: > 0 })
        {
            return;
        }

        _ = OpenFilesAsync(files);
    }

    private async Task OpenFilesAsync(IStorageFile[] files)
    {
        foreach (var file in files)
        {
            await OpenFileAsync(file);
        }
    }

    // ---------- tabs ----------

    private void DocTabs_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressTabEvents || DocTabs.SelectedItem is not DocumentTab tab)
        {
            return;
        }

        ActivateTab(tab);
    }

    private void ActivateTab(DocumentTab tab)
    {
        if (_current != null && !ReferenceEquals(_current, tab))
        {
            _current.ScrollOffset = DocScroll.Offset.Y;
        }

        _current = tab;

        if (!ReferenceEquals(DocTabs.SelectedItem, tab))
        {
            _suppressTabEvents = true;
            DocTabs.SelectedItem = tab;
            _suppressTabEvents = false;
        }

        ReloadButton.IsEnabled = true;
        DocumentTitleChanged?.Invoke(this, $"{tab.Header} — MD Reader {AppVersion}");

        Refresh(preserveScroll: false);
        UpdateStatus();
    }

    private void CloseTab(DocumentTab tab)
    {
        var index = _tabs.IndexOf(tab);
        if (index < 0)
        {
            return;
        }

        if (ReferenceEquals(_current, tab))
        {
            _current = null;
        }

        _tabs.Remove(tab);
        DocTabs.IsVisible = _tabs.Count > 1;

        if (_tabs.Count == 0)
        {
            DocTabs.IsVisible = false;
            ReloadButton.IsEnabled = false;
            ShowEmptyState();
            UpdateStatus();
            DocumentTitleChanged?.Invoke(this, $"MD Reader {AppVersion}");
            return;
        }

        if (_current == null)
        {
            _suppressTabEvents = true;
            DocTabs.SelectedItem = _tabs[Math.Min(index, _tabs.Count - 1)];
            _suppressTabEvents = false;

            if (DocTabs.SelectedItem is DocumentTab next)
            {
                ActivateTab(next);
            }
        }
    }

    // ---------- document ----------

    private async Task OpenFileAsync(IStorageFile file, bool replaceCurrent = false)
    {
        try
        {
            var bytes = await ReadAllBytesAsync(file);
            CommitDocument(file.Name, file.TryGetLocalPath(), file, ReadText(bytes), replaceCurrent);
        }
        catch (Exception ex)
        {
            ShowOpenError(ex);
        }
    }

    private async Task OpenPathAsync(string path, bool replaceCurrent = false)
    {
        try
        {
            var bytes = System.IO.File.ReadAllBytes(path);
            var file = await TryGetStorageFileAsync(path);
            CommitDocument(System.IO.Path.GetFileName(path), path, file, ReadText(bytes), replaceCurrent);
        }
        catch (Exception ex)
        {
            ShowOpenError(ex);
        }
    }

    private static async Task<byte[]> ReadAllBytesAsync(IStorageFile file)
    {
        await using var stream = await file.OpenReadAsync();
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    private async Task<IStorageFile?> TryGetStorageFileAsync(string path)
    {
        try
        {
            var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
            return storage == null ? null : await storage.TryGetFileFromPathAsync(path);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void CommitDocument(string name, string? path, IStorageFile? file, string markdown, bool replaceCurrent)
    {
        var tab = replaceCurrent ? _current : null;

        if (tab == null)
        {
            tab = new DocumentTab(name, path, file, markdown);
            _tabs.Add(tab);
        }
        else
        {
            tab.Markdown = markdown;
            tab.ScrollOffset = DocScroll.Offset.Y;
        }

        DocTabs.IsVisible = _tabs.Count > 1;

        if (!ReferenceEquals(DocTabs.SelectedItem, tab))
        {
            _suppressTabEvents = true;
            DocTabs.SelectedItem = tab;
            _suppressTabEvents = false;
        }

        ActivateTab(tab);
    }

    private void ShowOpenError(Exception ex)
    {
        FileText.Text = $"فایل باز نشد: {ex.Message}";
        FileText.Foreground = new SolidColorBrush(Color.Parse("#B42318"));
        CountText.Text = string.Empty;
    }

    private void Refresh(bool preserveScroll = true)
    {
        if (_current == null)
        {
            ShowEmptyState();
            return;
        }

        if (_current.Markdown.Trim().Length == 0)
        {
            _current.EmptyTitle = "این فایل خالی است.";
            _current.Rendered = null;
            ShowEmptyState();
            return;
        }

        _current.EmptyTitle = null;

        var offset = preserveScroll ? DocScroll.Offset.Y : _current.ScrollOffset;

        _current.Rendered = MarkdownRenderer.Render(_current.Markdown, _options);
        DocHost.Children.Clear();
        DocHost.Children.Add(_current.Rendered.Root);

        if (offset > 0)
        {
            RestoreScroll(offset);
        }
    }

    private void ApplyFontSize(double size)
    {
        _options.BaseFontSize = size;

        if (_current?.Rendered is { } rendered)
        {
            foreach (var apply in rendered.SizeAppliers)
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
        if (_current != null)
        {
            _current.Rendered = null;
        }

        DocHost.Children.Clear();

        var direction = _options.Rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        var align = _options.Rtl ? TextAlignment.Right : TextAlignment.Left;
        var emptyTitle = _current?.EmptyTitle;
        var panel = new StackPanel
        {
            Margin = new Thickness(30, 44, 30, 30),
            FlowDirection = direction,
        };

        panel.Children.Add(new TextBlock
        {
            Text = emptyTitle ?? "فایلی باز نشده است",
            FontSize = _options.BaseFontSize * 1.5,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse("#24292F")),
            TextAlignment = align,
            Margin = new Thickness(0, 0, 0, 10),
        });

        panel.Children.Add(new TextBlock
        {
            Text = emptyTitle == null
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
                     ("Ctrl + O", "باز کردن فایل (تب جدید)"),
                     ("Ctrl + W", "بستن تب فعلی"),
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

        if (_current == null)
        {
            FileText.Text = "فایلی باز نشده است";
            CountText.Text = string.Empty;
            return;
        }

        FileText.Text = _current.Path ?? _current.Header;
        var words = Regex.Matches(_current.Markdown, @"[\p{L}\p{N}]+").Count;
        CountText.Text = string.Format(
            CultureInfo.CurrentCulture,
            "{0:N0} کلمه · {1:N0} کاراکتر",
            words,
            _current.Markdown.Length);
    }

    private static string ReadText(byte[] bytes)
    {
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
