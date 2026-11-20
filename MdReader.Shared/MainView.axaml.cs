using System;
using System.Collections.Generic;
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

/// <summary>A font entry of the toolbar picker. <see cref="Name"/> is what gets saved in the
/// settings file: a system family name, or an <c>avares://</c> collection for the bundled font.</summary>
public sealed class FontChoice
{
    public FontChoice(string label, FontFamily family, string name)
    {
        Label = label;
        Family = family;
        Name = name;
    }

    public string Label { get; }

    public FontFamily Family { get; }

    public string Name { get; }
}

public partial class MainView : UserControl
{
    private static readonly double[] FontSizes = { 12, 13, 14, 15, 16, 18, 20, 22, 24, 28, 32, 36, 44, 56 };

    /// <summary>The font shipped inside the app, so Persian text looks the same everywhere.</summary>
    private const string EmbeddedFontName = "avares://MdReader.Shared/assets/fonts#Vazirmatn";

    public static readonly string AppVersion = ResolveVersion();

    private static readonly FontFamily EmbeddedFont = new(EmbeddedFontName);

    private static readonly string[] PreferredFonts =
    {
        "Vazirmatn", "Segoe UI", "Tahoma", "IRANSans", "Noto Naskh Arabic",
        "Noto Sans Arabic", "Noto Sans", "DejaVu Sans", "Helvetica Neue", "Arial",
    };

    private readonly MarkdownRenderOptions _options = new();
    private readonly ObservableCollection<DocumentTab> _tabs = new();
    private readonly AppSettings _settings = AppSettings.Load();

    private DocumentTab? _current;
    private bool _suppressEvents;
    private bool _suppressTabEvents;
    private bool _suppressSearch;

    private string _searchQuery = string.Empty;
    private int _matchIndex;

    static MainView()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public MainView()
    {
        InitializeComponent();

        DocTabs.ItemsSource = _tabs;

        _options.NavigateToAnchor = NavigateToAnchor;
        _options.BaseFontSize = FontSizes.Contains(_settings.BaseFontSize) ? _settings.BaseFontSize : 16;
        _options.Rtl = _settings.Rtl;

        LoadFontFamilies();
        LoadFontSizes();

        _suppressEvents = true;
        RtlCheckBox.IsChecked = _settings.Rtl;
        TocToggle.IsChecked = _settings.OutlineVisible;
        _suppressEvents = false;
        OutlinePanel.IsVisible = _settings.OutlineVisible;

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
        _settings.Rtl = _options.Rtl;
        SaveSettings();
        Refresh(preserveScroll: true);
    }

    private void FontCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents || FontCombo.SelectedItem is not FontChoice choice)
        {
            return;
        }

        _options.FontFamilyName = choice.Name;
        _settings.FontFamilyName = choice.Name;
        SaveSettings();
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

    private void SearchButton_Click(object? sender, RoutedEventArgs e) => ShowSearch();

    private void SearchClose_Click(object? sender, RoutedEventArgs e) => CloseSearch();

    private void SearchNext_Click(object? sender, RoutedEventArgs e) => StepSearch(1);

    private void SearchPrev_Click(object? sender, RoutedEventArgs e) => StepSearch(-1);

    private void TocToggle_Changed(object? sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
        {
            return;
        }

        _settings.OutlineVisible = TocToggle.IsChecked == true;
        OutlinePanel.IsVisible = _settings.OutlineVisible;
        SaveSettings();
    }

    private void OutlineItem_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is OutlineEntry entry)
        {
            entry.Target.BringIntoView();
        }

        e.Handled = true;
    }

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
        else if (ctrl && e.Key == Key.F)
        {
            ShowSearch();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && SearchBar.IsVisible)
        {
            CloseSearch();
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

    // ---------- search ----------

    private void ShowSearch()
    {
        if (_current == null || _current.Markdown.Length == 0)
        {
            return;
        }

        SearchBar.IsVisible = true;
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void CloseSearch()
    {
        if (!SearchBar.IsVisible)
        {
            return;
        }

        SearchBar.IsVisible = false;
        _matchIndex = 0;

        if (_searchQuery.Length == 0)
        {
            return;
        }

        _suppressSearch = true;
        SearchBox.Text = string.Empty;
        _suppressSearch = false;
        _searchQuery = string.Empty;
        Refresh(preserveScroll: true);
    }

    private void SearchBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            StepSearch((e.KeyModifiers & KeyModifiers.Shift) != 0 ? -1 : 1);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CloseSearch();
            e.Handled = true;
        }
    }

    private void SearchBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_suppressSearch)
        {
            return;
        }

        var query = SearchBox.Text ?? string.Empty;
        if (query == _searchQuery)
        {
            return;
        }

        _searchQuery = query;
        _matchIndex = 0;
        Refresh(preserveScroll: true);
    }

    private void StepSearch(int direction)
    {
        var hosts = _current?.Rendered?.MatchHosts;
        if (hosts is not { Count: > 0 })
        {
            UpdateSearchStatus();
            return;
        }

        _matchIndex = (_matchIndex + direction + hosts.Count) % hosts.Count;
        hosts[_matchIndex].BringIntoView();
        UpdateSearchStatus();
    }

    private void UpdateSearchStatus()
    {
        var query = _searchQuery.Trim();

        if (query.Length == 0)
        {
            SearchCountText.Text = string.Empty;
            return;
        }

        var count = _current?.Rendered?.MatchHosts.Count ?? 0;

        if (count == 0)
        {
            SearchCountText.Text = "نتیجه‌ای نیست";
            return;
        }

        if (_matchIndex >= count)
        {
            _matchIndex = 0;
        }

        SearchCountText.Text = $"{(_matchIndex + 1).ToString(CultureInfo.CurrentCulture)} / {count.ToString(CultureInfo.CurrentCulture)}";
    }

    // ---------- anchors ----------

    private void NavigateToAnchor(string anchor)
    {
        var anchors = _current?.Rendered?.Anchors;
        if (anchors is not { Count: > 0 } || string.IsNullOrWhiteSpace(anchor))
        {
            return;
        }

        var key = anchor.Trim().TrimStart('#');

        try
        {
            key = Uri.UnescapeDataString(key);
        }
        catch (UriFormatException)
        {
            // Keep the raw text when the anchor is not percent-encoded.
        }

        if (anchors.TryGetValue(key, out var exact))
        {
            exact.BringIntoView();
            return;
        }

        var normalized = key.ToLowerInvariant();

        foreach (var pair in anchors)
        {
            if (pair.Key.Equals(normalized, StringComparison.OrdinalIgnoreCase))
            {
                pair.Value.BringIntoView();
                return;
            }
        }

        // "## بخش" style anchors written by hand often only match the start of the slug.
        foreach (var pair in anchors)
        {
            if (normalized.Length > 0 && pair.Key.StartsWith(normalized, StringComparison.OrdinalIgnoreCase))
            {
                pair.Value.BringIntoView();
                return;
            }
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
            CloseSearch();
            ShowEmptyState();
            UpdateOutline();
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
            UpdateOutline();
            UpdateSearchStatus();
            return;
        }

        if (_current.Markdown.Trim().Length == 0)
        {
            _current.EmptyTitle = "این فایل خالی است.";
            _current.Rendered = null;
            ShowEmptyState();
            UpdateOutline();
            UpdateSearchStatus();
            return;
        }

        _current.EmptyTitle = null;

        var offset = preserveScroll ? DocScroll.Offset.Y : _current.ScrollOffset;

        var query = _searchQuery.Trim();
        _options.SearchQuery = query.Length > 0 ? query : null;

        _current.Rendered = MarkdownRenderer.Render(_current.Markdown, _options);
        DocHost.Children.Clear();
        DocHost.Children.Add(_current.Rendered.Root);

        if (offset > 0)
        {
            RestoreScroll(offset);
        }

        UpdateOutline();
        UpdateSearchStatus();
    }

    private void UpdateOutline()
    {
        var outline = _current?.Rendered?.Outline;
        OutlineList.ItemsSource = outline;

        var empty = outline == null || outline.Count == 0;
        OutlineEmpty.IsVisible = empty;
        OutlineList.IsVisible = !empty;
    }

    private void ApplyFontSize(double size)
    {
        _options.BaseFontSize = size;
        _settings.BaseFontSize = size;
        SaveSettings();

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
                     ("Ctrl + F", "جست‌وجو در سند (Enter = نتیجه بعدی)"),
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
        var choices = new List<FontChoice>();
        var families = FontManager.Current.SystemFonts.ToList();

        var systemHasEmbedded = families.Any(f => f.Name.Equals("Vazirmatn", StringComparison.OrdinalIgnoreCase));
        if (!systemHasEmbedded)
        {
            choices.Add(new FontChoice("Vazirmatn (همراه برنامه)", EmbeddedFont, EmbeddedFontName));
        }

        choices.AddRange(families
            .OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(f => new FontChoice(f.Name, f, f.Name)));

        FontCombo.ItemsSource = choices;

        var saved = _settings.FontFamilyName;
        var selected = !string.IsNullOrWhiteSpace(saved)
            ? choices.FirstOrDefault(c => c.Name.Equals(saved, StringComparison.OrdinalIgnoreCase))
            : null;

        // The bundled Vazirmatn is the default: it is the only family guaranteed to have proper
        // Persian shaping on every platform (Android in particular has no Vazirmatn installed).
        selected ??= choices.FirstOrDefault(c => c.Name == EmbeddedFontName)
                     ?? PreferredFonts
                         .Select(name => choices.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                         .FirstOrDefault(match => match != null)
                     ?? choices.FirstOrDefault();

        if (selected != null)
        {
            _suppressEvents = true;
            FontCombo.SelectedItem = selected;
            _suppressEvents = false;
            _options.FontFamilyName = selected.Name;
        }
    }

    private void LoadFontSizes()
    {
        _suppressEvents = true;
        SizeCombo.ItemsSource = FontSizes;
        SizeCombo.SelectedItem = _options.BaseFontSize;
        _suppressEvents = false;
    }

    private void SaveSettings()
    {
        _settings.Rtl = _options.Rtl;
        _settings.Save();
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
