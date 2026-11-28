using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MdReader.Export;
using MdReader.Rendering;

namespace MdReader;

/// <summary>One open document. Files picked through Android's document picker have no
/// filesystem path, so the storage item is kept and its stream re-read on reload.</summary>
public sealed class DocumentTab
{
    public DocumentTab(string header, string? path, IStorageFile? file, string markdown, string? key = null)
    {
        Header = header;
        Path = path;
        File = file;
        Markdown = markdown;
        Key = key;
    }

    /// <summary>Identity of a document that has no filesystem path, such as a <c>content://</c>
    /// URI on Android. Bookmarks and the reading position are stored under it.</summary>
    public string? Key { get; }

    public string Header { get; set; }

    public string? Path { get; }

    public IStorageFile? File { get; }

    public string Markdown { get; set; }

    public double ScrollOffset { get; set; }

    public RenderedMarkdown? Rendered { get; set; }

    public string? EmptyTitle { get; set; }

    /// <summary>Whether the saved reading position of this file was already applied in this session.</summary>
    public bool PositionRestored { get; set; }

    public string ToolTip => Path ?? Header;
}

/// <summary>One row of the quick open panel: a file that can be reached by typing part of
/// its name or path.</summary>
public sealed class QuickEntry
{
    public QuickEntry(string header, string detail, string? path, IStorageFile? file)
    {
        Header = header;
        Detail = detail;
        Path = path;
        File = file;
    }

    public string Header { get; }

    public string Detail { get; }

    public string? Path { get; }

    public IStorageFile? File { get; }

    public override string ToString() => Header;
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

/// <summary>One row of a toolbar picker: a stored value and its localized label.</summary>
public sealed class UiChoice
{
    public UiChoice(string value, string label)
    {
        Value = value;
        Label = label;
    }

    public string Value { get; }

    public string Label { get; }

    public override string ToString() => Label;
}

public partial class MainView : UserControl
{
    private static readonly double[] FontSizes = { 12, 13, 14, 15, 16, 18, 20, 22, 24, 28, 32, 36, 44, 56 };

    /// <summary>The font shipped inside the app, so Persian text looks the same everywhere.</summary>
    private const string EmbeddedFontName = "avares://MdReader.Shared/assets/fonts#Vazirmatn";

    private static readonly bool IsMobile = OperatingSystem.IsAndroid() || OperatingSystem.IsIOS();

    public static readonly string AppVersion = ResolveVersion();

    private static readonly FontFamily EmbeddedFont = new(EmbeddedFontName);

    private static readonly string[] PreferredFonts =
    {
        "Vazirmatn", "Segoe UI", "Tahoma", "IRANSans", "Noto Naskh Arabic",
        "Noto Sans Arabic", "Noto Sans", "DejaVu Sans", "Helvetica Neue", "Arial",
    };

    private readonly MarkdownRenderOptions _options = new();
    private readonly ObservableCollection<DocumentTab> _tabs = new();
    private readonly ObservableCollection<LibraryItem> _libraryItems = new();
    private readonly ObservableCollection<DocumentMark> _marks = new();
    private readonly ObservableCollection<QuickEntry> _quickItems = new();
    private readonly LibraryIndex _library = new();
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly AppSettings _settings = AppSettings.Load();

    private DocumentTab? _current;
    private bool _suppressEvents;
    private bool _suppressTabEvents;
    private bool _suppressSearch;
    private bool _updatingPosition;
    private bool _focusMode;
    private bool _remoteLibraryRestored;

    private string _searchQuery = string.Empty;
    private int _matchIndex;

    private DispatcherTimer? _libraryTimer;
    private DispatcherTimer? _reloadTimer;
    private DispatcherTimer? _stateTimer;
    private string _pendingReloadPath = string.Empty;

    public MainView()
    {
        // The language has to be settled before the markup resolves its DynamicResource strings.
        Lang.Apply(_settings.Language);
        AppTheme.Apply(_settings.Theme);
        InitializeComponent();

        DocTabs.ItemsSource = _tabs;
        LibraryList.ItemsSource = _libraryItems;
        MarksList.ItemsSource = _marks;
        QuickOpenList.ItemsSource = _quickItems;

        _options.NavigateToAnchor = NavigateToAnchor;
        _options.OpenDocument = OpenLinkedDocument;
        _options.BaseFontSize = FontSizes.Contains(_settings.BaseFontSize) ? _settings.BaseFontSize : 16;
        _options.MaxContentWidth = WidthFor(_settings.Width);
        _options.Palette = MarkdownPalette.For(AppTheme.IsDark);

        LoadFontFamilies();
        LoadFontSizes();
        LoadChoices();

        _suppressEvents = true;
        TocToggle.IsChecked = _settings.OutlineVisible;
        AutoReloadCheck.IsChecked = _settings.AutoReload;
        _suppressEvents = false;
        OutlinePanel.IsVisible = _settings.OutlineVisible;

        LibraryPanel.IsVisible = false;
        MarksPanel.IsVisible = false;

        ApplyUiDirection();
        ShowEmptyState();

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, View_DragOver);
        AddHandler(DragDrop.DropEvent, View_Drop);
        AddHandler(PointerWheelChangedEvent, View_PointerWheelChanged, RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, View_PointerPressed, RoutingStrategies.Tunnel);

        // Tab and Escape are handled on the way in, so a focused combo box cannot swallow them:
        // the card keeps the keyboard inside itself until it is closed.
        SettingsPanel.AddHandler(KeyDownEvent, SettingsPanel_KeyDown, RoutingStrategies.Tunnel);

        // The settings card is sized by the window: every layout pass re-fits it below the toolbars
        // and inside the available width, which keeps it usable on phones and in narrow windows.
        LayoutUpdated += (_, _) => UpdateSettingsPanelPlacement();

        DocScroll.ScrollChanged += (_, _) =>
        {
            UpdateReadingPosition();
            ScheduleStateSave();
        };

        ExternalOpen.PendingChanged += OnExternalOpen;
        Lang.Changed += (_, _) => ApplyLanguage();
        Application.Current!.ActualThemeVariantChanged += (_, _) => OnThemeVariantChanged();

        RestoreLibrary();
        ConsumeExternalOpen();

        // A folder that lives behind a storage URI can only be reopened once the view has a top
        // level to ask for its storage provider.
        AttachedToVisualTree += (_, _) => _ = RestoreRemoteLibraryAsync();
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

    /// <summary>Applies command line arguments (desktop only: <c>--rtl</c>, <c>--watch</c> and
    /// optional file paths). Without file paths the tabs of the previous session are restored.</summary>
    public async void ApplyStartArguments(string[] args)
    {
        if (args.Any(a => a.Equals("--rtl", StringComparison.OrdinalIgnoreCase)))
        {
            _settings.Direction = "rtl";
            SaveSettings();
            SelectChoice(DirectionCombo, "rtl");
        }

        if (args.Any(a => a.Equals("--watch", StringComparison.OrdinalIgnoreCase)))
        {
            _suppressEvents = true;
            AutoReloadCheck.IsChecked = true;
            _suppressEvents = false;
            _settings.AutoReload = true;
            UpdateWatchers();
        }

        var paths = args.Where(a => !a.StartsWith('-') && System.IO.File.Exists(a)).ToList();

        if (paths.Count == 0)
        {
            await RestoreSessionAsync();
            return;
        }

        foreach (var path in paths)
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
            Title = Lang.T("FilePickerTitle"),
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType(Lang.T("MarkdownFiles"))
                {
                    Patterns = new[] { "*.md", "*.markdown", "*.mdown", "*.mkd" },
                    AppleUniformTypeIdentifiers = new[] { "net.daringfireball.markdown", "public.plain-text" },
                    MimeTypes = new[] { "text/markdown", "text/x-markdown", "text/plain" },
                },
                new FilePickerFileType(Lang.T("TextFiles"))
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

    private void DirectionCombo_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents || DirectionCombo.SelectedItem is not UiChoice choice)
        {
            return;
        }

        _settings.Direction = choice.Value;
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
        UpdateReadingPosition();
    }

    private void MarksToggle_Changed(object? sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
        {
            return;
        }

        MarksPanel.IsVisible = MarksToggle.IsChecked == true;
        RebuildMarks();
    }

    private void WidthCombo_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents || WidthCombo.SelectedItem is not UiChoice choice)
        {
            return;
        }

        _settings.Width = choice.Value;
        _options.MaxContentWidth = WidthFor(choice.Value);
        SaveSettings();
        Refresh(preserveScroll: true);
    }

    private void ThemeCombo_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents || ThemeCombo.SelectedItem is not UiChoice choice)
        {
            return;
        }

        _settings.Theme = choice.Value;
        SaveSettings();
        AppTheme.Apply(choice.Value);
        ApplyPalette();
    }

    private void LanguageCombo_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents || LanguageCombo.SelectedItem is not UiChoice choice)
        {
            return;
        }

        _settings.Language = choice.Value;
        SaveSettings();
        Lang.Apply(choice.Value);
    }

    private void FocusToggle_Click(object? sender, RoutedEventArgs e) => ApplyFocusMode(FocusToggle.IsChecked == true);

    private void SettingsButton_Click(object? sender, RoutedEventArgs e) =>
        SetSettingsVisible(!SettingsPanel.IsVisible);

    private void SettingsClose_Click(object? sender, RoutedEventArgs e) =>
        SetSettingsVisible(false);

    private void SetSettingsVisible(bool visible)
    {
        if (visible == SettingsPanel.IsVisible)
        {
            return;
        }

        if (visible)
        {
            UpdateSettingsPanelPlacement();
            SettingsPanel.IsVisible = true;

            // The card only owns a visual tree once it is visible, so its first setting takes the
            // focus after the layout pass that placed it.
            Dispatcher.UIThread.Post(FocusFirstSetting, DispatcherPriority.Loaded);
            return;
        }

        // Closing gives the focus back to the gear instead of leaving it on a hidden control,
        // so Tab keeps working from the toolbar (and the gear stays reachable on Android).
        var hadFocus = IsFocusInsideSettings();
        SettingsPanel.IsVisible = false;

        if (hadFocus && SettingsButton.IsEffectivelyVisible)
        {
            SettingsButton.Focus();
        }
    }

    /// <summary>Keeps the keyboard inside the settings card: Tab and Shift+Tab loop between its
    /// controls, and Escape closes it from any of them.</summary>
    private void SettingsPanel_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            SetSettingsVisible(false);
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Tab)
        {
            return;
        }

        var stops = SettingsFocusStops();
        if (stops.Count == 0)
        {
            return;
        }

        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Control;
        var index = focused == null ? -1 : stops.IndexOf(focused);
        var step = (e.KeyModifiers & KeyModifiers.Shift) != 0 ? -1 : 1;

        var next = index < 0
            ? (step > 0 ? 0 : stops.Count - 1)
            : ((index + step) % stops.Count + stops.Count) % stops.Count;

        stops[next].Focus();
        e.Handled = true;
    }

    /// <summary>The focus stops of the card in visual order, starting at the close button. Scroll
    /// bars are skipped so Tab only lands on something that visibly shows the focus.</summary>
    private List<Control> SettingsFocusStops() =>
        SettingsPanel.GetVisualDescendants()
            .OfType<Control>()
            .Where(control => control.Focusable
                              && control.IsEffectivelyVisible
                              && control.IsEffectivelyEnabled
                              && control is not ScrollViewer
                              && control is not ScrollBar)
            .ToList();

    /// <summary>True when the keyboard focus sits somewhere inside the settings card.</summary>
    private bool IsFocusInsideSettings() =>
        TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is Visual focused &&
        IsInside(focused, SettingsPanel);

    /// <summary>Moves the focus to the first control of the settings list, the natural entry
    /// point for a keyboard or a D-pad.</summary>
    private void FocusFirstSetting()
    {
        if (!SettingsPanel.IsVisible)
        {
            return;
        }

        SettingsStack.GetVisualDescendants()
            .OfType<Control>()
            .FirstOrDefault(control => control.Focusable && control.IsEffectivelyVisible && control.IsEffectivelyEnabled)
            ?.Focus();
    }

    /// <summary>Fits the settings card into the current window: it sits just below the toolbar and
    /// never covers the table of contents (or the side panels at its own edge), and it can never be
    /// taller or wider than the space that is actually left.</summary>
    private void UpdateSettingsPanelPlacement()
    {
        var width = Bounds.Width;
        var height = Bounds.Height;

        if (width < 1 || height < 1)
        {
            return;
        }

        const double gap = 12;
        const double minWidth = 180;
        const double maxWidth = 300;

        var onLeft = Lang.IsRtl;

        var reserved = onLeft
            ? (MarksPanel.IsVisible ? SidePanelWidth(MarksPanel) : 0) +
              (LibraryPanel.IsVisible ? SidePanelWidth(LibraryPanel) : 0)
            : OutlinePanel.IsVisible ? SidePanelWidth(OutlinePanel) : 0;

        // On a phone the side panel can be wider than the window; the card keeps its minimum
        // width and the side panel simply shows through underneath instead of being cut off.
        reserved = Math.Clamp(reserved, 0, Math.Max(0, width - minWidth - gap));

        var top = 8 + (ToolbarBorder.IsVisible ? ToolbarBorder.Bounds.Height : 0);
        if (DocTabs.IsVisible)
        {
            top += DocTabs.Bounds.Height;
        }

        if (SearchBar.IsVisible)
        {
            top += SearchBar.Bounds.Height;
        }

        var panelWidth = Math.Clamp(Math.Min(maxWidth, width - reserved - gap), minWidth, maxWidth);
        var panelHeight = Math.Max(90, height - top - gap);

        if (Math.Abs(SettingsPanel.Width - panelWidth) > 0.5)
        {
            SettingsPanel.Width = panelWidth;
        }

        if (Math.Abs(SettingsPanel.MaxHeight - panelHeight) > 0.5)
        {
            SettingsPanel.MaxHeight = panelHeight;
        }

        var margin = onLeft
            ? new Thickness(reserved + gap, top, gap, gap)
            : new Thickness(gap, top, reserved + gap, gap);

        if (SettingsPanel.Margin != margin)
        {
            SettingsPanel.Margin = margin;
        }
    }

    private static double SidePanelWidth(Control panel) =>
        double.IsNaN(panel.Width) || panel.Width <= 0 ? panel.Bounds.Width : panel.Width;

    /// <summary>A press anywhere outside the open settings card closes it, like a light-dismiss
    /// popup. The gear is left alone so its own click still toggles the card.</summary>
    private void View_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is not Visual source)
        {
            return;
        }

        if (QuickOpenPanel.IsVisible && !IsInside(source, QuickOpenPanel))
        {
            CloseQuickOpen();
        }

        if (!SettingsPanel.IsVisible)
        {
            return;
        }

        if (IsInside(source, SettingsPanel) || IsInside(source, SettingsButton))
        {
            return;
        }

        SettingsPanel.IsVisible = false;
    }

    private static bool IsInside(Visual source, Visual container)
    {
        for (Visual? visual = source; visual != null; visual = visual.GetVisualParent())
        {
            if (ReferenceEquals(visual, container))
            {
                return true;
            }
        }

        return false;
    }

    private void OutlineItem_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is OutlineEntry entry)
        {
            entry.Target.BringIntoView();

            if (_current?.Rendered?.Outline is { } outline)
            {
                foreach (var item in outline)
                {
                    item.IsActive = ReferenceEquals(item, entry);
                }
            }
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
        else if (ctrl && e.Key == Key.E)
        {
            ShowQuickOpen();
            e.Handled = true;
        }
        else if (e.Key == Key.F6)
        {
            CyclePane();
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.P && _current != null)
        {
            ExportPdf_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.C && (e.KeyModifiers & KeyModifiers.Shift) != 0 && _current != null)
        {
            CopyRichText_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.D && _current != null)
        {
            AddBookmark_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.F11)
        {
            ApplyFocusMode(!_focusMode);
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.OemComma)
        {
            SetSettingsVisible(!SettingsPanel.IsVisible);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && QuickOpenPanel.IsVisible)
        {
            CloseQuickOpen();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && SettingsPanel.IsVisible)
        {
            SetSettingsVisible(false);
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

    // ---------- marks ----------

    private DocumentState CurrentState =>
        ReadingStateStore.Get(StateKeyOf(_current));

    /// <summary>The identity the reading state is stored under: the path when there is one,
    /// otherwise the storage URI of the document (or the name of a shared document).</summary>
    private static string? StateKeyOf(DocumentTab? tab) =>
        tab == null
            ? null
            : tab.Path is { Length: > 0 } path
                ? path
                : tab.File?.Path?.ToString() ?? tab.Key;

    private void AddBookmark_Click(object? sender, RoutedEventArgs e)
    {
        if (_current == null)
        {
            return;
        }

        var offset = DocScroll.Offset.Y;
        var label = ActiveHeadingTitle() ?? Lang.F("BookmarkLabel", ReadPercent());

        CurrentState.Marks.Add(new DocumentMark("bookmark", offset, label));
        SaveCurrentState();
        RebuildMarks();
        FileText.Text = Lang.T("MarkAdded");
    }

    private void AddNote_Click(object? sender, RoutedEventArgs e)
    {
        if (_current == null)
        {
            return;
        }

        var text = (NoteBox.Text ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return;
        }

        CurrentState.Marks.Add(new DocumentMark("note", DocScroll.Offset.Y, text));
        SaveCurrentState();
        NoteBox.Text = string.Empty;
        RebuildMarks();
        FileText.Text = Lang.T("NoteAdded");
    }

    private void MarkItem_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not DocumentMark mark)
        {
            return;
        }

        RestoreScroll(mark.Offset);
        e.Handled = true;
    }

    private void MarkDelete_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not DocumentMark mark)
        {
            return;
        }

        CurrentState.Marks.Remove(mark);
        SaveCurrentState();
        RebuildMarks();
        e.Handled = true;
    }

    private void RebuildMarks()
    {
        _marks.Clear();

        if (_current != null)
        {
            foreach (var mark in CurrentState.Marks.OrderBy(m => m.Offset))
            {
                _marks.Add(mark);
            }
        }

        MarksEmptyText.IsVisible = _marks.Count == 0;
        MarksList.IsVisible = _marks.Count > 0;
    }

    private string? ActiveHeadingTitle() =>
        _current?.Rendered?.Outline.FirstOrDefault(entry => entry.IsActive)?.Title;

    private int ReadPercent()
    {
        var extent = DocScroll.Extent.Height;
        var viewport = DocScroll.Viewport.Height;

        if (extent <= viewport + 1 || viewport <= 0)
        {
            return 100;
        }

        var maximum = Math.Max(1, extent - viewport);
        return (int)Math.Round(Math.Clamp(DocScroll.Offset.Y / maximum, 0, 1) * 100);
    }

    /// <summary>Saves the scroll position shortly after scrolling stops, so a crash or a closed
    /// window still leaves the reader where they were.</summary>
    private void ScheduleStateSave()
    {
        if (_current == null)
        {
            return;
        }

        _stateTimer?.Stop();
        _stateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _stateTimer.Tick += (_, _) =>
        {
            _stateTimer?.Stop();
            SaveCurrentState();
        };
        _stateTimer.Start();
    }

    private void SaveCurrentState()
    {
        if (StateKeyOf(_current) is not { Length: > 0 } key)
        {
            return;
        }

        var state = ReadingStateStore.Get(key);
        state.Position = DocScroll.Offset.Y;
        ReadingStateStore.Save(key, state);
    }

    // ---------- library ----------

    private async void ChooseLibraryFolder_Click(object? sender, RoutedEventArgs e)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage == null || !storage.CanPickFolder)
        {
            LibraryStatusText.Text = Lang.T("FolderUnsupported");
            return;
        }

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Lang.T("FolderPickerTitle"),
            AllowMultiple = false,
        });

        var folder = folders.FirstOrDefault();
        if (folder == null)
        {
            return;
        }

        LibraryPanel.IsVisible = true;
        _suppressEvents = true;
        LibraryToggle.IsChecked = true;
        _suppressEvents = false;

        var path = folder.TryGetLocalPath();

        if (!string.IsNullOrEmpty(path))
        {
            _settings.LibraryPath = path;
            SaveSettings();
            LoadLibrary(path);
            return;
        }

        // A folder without a filesystem path (Android's storage access framework) is read through
        // the storage API. Its URI is kept, and the platform is asked to keep the read grant, so
        // the folder can be reopened in the next session when the provider allows it.
        _settings.LibraryPath = null;
        _settings.LibraryUri = folder.Path?.ToString();

        if (Uri.TryCreate(_settings.LibraryUri, UriKind.Absolute, out var folderUri))
        {
            RemoteAccess.PersistFolder?.Invoke(folderUri);
        }
        else
        {
            _settings.LibraryUri = null;
        }

        SaveSettings();
        LibraryPathText.Text = folder.Name;
        LibraryStatusText.Text = Lang.T("Indexing").Replace("{0}", "…", StringComparison.Ordinal);

        try
        {
            await _library.LoadFolderAsync(folder, count =>
                Dispatcher.UIThread.Post(() =>
                    LibraryStatusText.Text = Lang.F("Indexing", count)));
            RefreshLibraryList();
            LibraryStatusText.Text = _settings.LibraryUri == null
                ? Lang.T("LibraryAndroidHint")
                : Lang.T("LibraryRemembered");
        }
        catch (Exception ex)
        {
            LibraryStatusText.Text = Lang.F("FolderReadError", ex.Message);
        }
    }

    private void LibraryToggle_Changed(object? sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
        {
            return;
        }

        LibraryPanel.IsVisible = LibraryToggle.IsChecked == true;

        if (LibraryPanel.IsVisible && _library.Count == 0 && !string.IsNullOrEmpty(_settings.LibraryPath))
        {
            LoadLibrary(_settings.LibraryPath!);
        }
    }

    private void RestoreLibrary()
    {
        if (!string.IsNullOrEmpty(_settings.LibraryPath) && Directory.Exists(_settings.LibraryPath))
        {
            LoadLibrary(_settings.LibraryPath!);
        }
    }

    /// <summary>Reopens the library folder of the last session when it lives behind a storage URI
    /// (Android). A grant that the provider did not keep simply leaves the library empty.</summary>
    private async Task RestoreRemoteLibraryAsync()
    {
        if (_remoteLibraryRestored)
        {
            return;
        }

        _remoteLibraryRestored = true;

        if (_library.Count > 0 || _settings.LibraryUri is not { Length: > 0 } uri)
        {
            return;
        }

        if (!Uri.TryCreate(uri, UriKind.Absolute, out var folderUri))
        {
            return;
        }

        try
        {
            var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (storage == null)
            {
                return;
            }

            var folder = await storage.TryGetFolderFromPathAsync(folderUri);
            if (folder == null)
            {
                // The permission is gone, so the next session starts without a folder.
                _settings.LibraryUri = null;
                SaveSettings();
                return;
            }

            await _library.LoadFolderAsync(folder, count =>
                Dispatcher.UIThread.Post(() =>
                    LibraryStatusText.Text = Lang.F("Indexing", count)));

            LibraryPathText.Text = folder.Name;
            RefreshLibraryList();
            LibraryStatusText.Text = Lang.T("LibraryRemembered");
        }
        catch (Exception)
        {
            // A revoked or unreadable grant leaves the library as it was.
        }
    }

    private void LoadLibrary(string root)
    {
        try
        {
            _library.LoadPath(root);
            LibraryPathText.Text = root;
            LibraryPathText.Foreground = ResourceBrush("AppTextMuted");
            RefreshLibraryList();
        }
        catch (Exception ex)
        {
            LibraryStatusText.Text = Lang.F("FolderReadError", ex.Message);
        }
    }

    private void LibrarySearchBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_library.Count == 0)
        {
            return;
        }

        // Searching file contents touches the disk, so it waits for a short pause in typing.
        _libraryTimer?.Stop();
        _libraryTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _libraryTimer.Tick += (_, _) =>
        {
            _libraryTimer?.Stop();
            RefreshLibraryList();
        };
        _libraryTimer.Start();
    }

    private void RefreshLibraryList()
    {
        _libraryItems.Clear();

        if (_library.Count == 0)
        {
            LibraryStatusText.Text = Lang.T("NoFolder");
            return;
        }

        var query = LibrarySearchBox.Text ?? string.Empty;

        try
        {
            // Only files that changed on disk are read again, so typing stays fast.
            if (query.Trim().Length > 0)
            {
                _library.RefreshChanged();
            }

            foreach (var item in _library.Search(query))
            {
                _libraryItems.Add(item);
            }

            LibraryStatusText.Text = query.Trim().Length == 0
                ? Lang.F("FileCount", _library.Count)
                : Lang.F("ResultCount", _libraryItems.Count);
        }
        catch (Exception ex)
        {
            LibraryStatusText.Text = Lang.F("SearchFailed", ex.Message);
        }
    }

    private async void LibraryItem_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not LibraryItem item)
        {
            return;
        }

        e.Handled = true;

        if (item.Path is { Length: > 0 } path)
        {
            await OpenPathAsync(path);
        }
        else if (item.File is { } file)
        {
            await OpenFileAsync(file);
        }

        if (_current == null || item.Query is not { Length: > 0 } query)
        {
            return;
        }

        // Re-run the panel's query inside the opened document and jump to the same occurrence.
        _suppressSearch = true;
        SearchBox.Text = query;
        _suppressSearch = false;

        _searchQuery = query;
        _matchIndex = 0;
        SearchBar.IsVisible = true;
        Refresh(preserveScroll: true);

        var hosts = _current.Rendered?.MatchHosts;
        if (hosts is { Count: > 0 })
        {
            _matchIndex = Math.Clamp(item.MatchOrdinal, 0, hosts.Count - 1);
            hosts[_matchIndex].BringIntoView();
            UpdateSearchStatus();
        }
    }

    // ---------- export ----------

    private async void CopyRichText_Click(object? sender, RoutedEventArgs e)
    {
        if (_current == null)
        {
            return;
        }

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null)
        {
            return;
        }

        try
        {
            var html = MarkdownRenderer.ToHtml(_current.Markdown);

            // Avalonia 12 replaced DataObject with DataTransfer/DataTransferItem; both HTML
            // flavours are set so pasting works on Windows and on X11/Wayland.
            var item = new DataTransferItem();
            item.Set(DataFormat.Text, MarkdownRenderer.ToPlainText(_current.Markdown));
            item.Set(DataFormat.CreateStringPlatformFormat("text/html"), html);
            item.Set(DataFormat.CreateStringPlatformFormat("HTML Format"), RichText.ToCfHtml(html));

            var data = new DataTransfer();
            data.Add(item);
            await clipboard.SetDataAsync(data);
            FileText.Text = Lang.T("Copied");
        }
        catch (Exception ex)
        {
            ShowStatusError(Lang.F("CopyFailed", ex.Message));
        }
    }

    private async void ExportPdf_Click(object? sender, RoutedEventArgs e)
    {
        if (_current == null)
        {
            return;
        }

        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is not { CanSave: true })
        {
            FileText.Text = Lang.T("SaveUnsupported");
            return;
        }

        var name = Path.GetFileNameWithoutExtension(_current.Path ?? _current.Header);
        if (string.IsNullOrWhiteSpace(name))
        {
            name = "document";
        }

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Lang.T("PdfTitle"),
            SuggestedFileName = name + ".pdf",
            DefaultExtension = "pdf",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("PDF")
                {
                    Patterns = new[] { "*.pdf" },
                    MimeTypes = new[] { "application/pdf" },
                },
            },
        });

        if (file == null)
        {
            return;
        }

        try
        {
            await using var stream = await file.OpenWriteAsync();
            var pages = PdfExporter.Export(_current.Markdown, _options, stream);
            FileText.Text = Lang.F("PdfSaved", pages);
        }
        catch (Exception ex)
        {
            ShowStatusError(Lang.F("PdfFailed", ex.Message));
        }
    }

    // ---------- auto reload ----------

    private void AutoReloadCheck_Changed(object? sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
        {
            return;
        }

        _settings.AutoReload = AutoReloadCheck.IsChecked == true;
        SaveSettings();
        UpdateWatchers();
    }

    private void UpdateWatchers()
    {
        var enabled = AutoReloadCheck.IsChecked == true && !IsMobile;
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (enabled)
        {
            foreach (var tab in _tabs)
            {
                if (tab.Path is { } path && Path.GetDirectoryName(path) is { Length: > 0 } directory)
                {
                    wanted.Add(directory);
                }
            }
        }

        foreach (var directory in _watchers.Keys.ToList())
        {
            if (!wanted.Contains(directory))
            {
                _watchers[directory].Dispose();
                _watchers.Remove(directory);
            }
        }

        if (!enabled)
        {
            return;
        }

        foreach (var directory in wanted)
        {
            if (_watchers.ContainsKey(directory))
            {
                continue;
            }

            try
            {
                var watcher = new FileSystemWatcher(directory)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                    EnableRaisingEvents = true,
                };

                watcher.Changed += OnWatchedFileChanged;
                watcher.Created += OnWatchedFileChanged;
                watcher.Renamed += (_, args) => OnWatchedFileChanged(watcher, args);
                _watchers[directory] = watcher;
            }
            catch (Exception)
            {
                // A folder we cannot watch simply does not get auto reload.
            }
        }
    }

    private void OnWatchedFileChanged(object? sender, FileSystemEventArgs e) =>
        Dispatcher.UIThread.Post(() => ScheduleReload(e.FullPath));

    private void ScheduleReload(string path)
    {
        _pendingReloadPath = path;
        _reloadTimer?.Stop();

        _reloadTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _reloadTimer.Tick += (_, _) =>
        {
            _reloadTimer?.Stop();
            ReloadWatchedFile(_pendingReloadPath);
        };
        _reloadTimer.Start();
    }

    private void ReloadWatchedFile(string path)
    {
        foreach (var tab in _tabs.Where(t => t.Path != null && t.Path.Equals(path, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            try
            {
                var text = TextLoader.Read(System.IO.File.ReadAllBytes(path));
                if (text == tab.Markdown)
                {
                    continue;
                }

                tab.Markdown = text;

                if (ReferenceEquals(tab, _current))
                {
                    Refresh(preserveScroll: true);
                    UpdateStatus();
                }
                else
                {
                    tab.Rendered = null;
                }
            }
            catch (Exception)
            {
                // The file may be mid-save; the next change notification tries again.
            }
        }
    }

    // ---------- external files ----------

    private void OnExternalOpen(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(ConsumeExternalOpen);

    private void ConsumeExternalOpen()
    {
        if (ExternalOpen.Take() is not { } pending)
        {
            return;
        }

        CommitDocument(pending.Name, null, null, TextLoader.Read(pending.Bytes), false, pending.Key);
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
            SearchCountText.Text = Lang.T("NoResults");
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
        UpdateWatchers();
        RebuildMarks();

        // A file is reopened where the reader stopped, but only the first time in a session so a
        // deliberate jump is not undone by switching tabs.
        if (!tab.PositionRestored)
        {
            tab.PositionRestored = true;

            var key = StateKeyOf(tab);
            if (ReadingStateStore.HasPosition(ReadingStateStore.Get(key)))
            {
                RestoreScroll(ReadingStateStore.Get(key).Position);
            }
        }
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
            SaveCurrentState();
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
            UpdateSession();
            UpdateWatchers();
            RebuildMarks();
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
            CommitDocument(file.Name, file.TryGetLocalPath(), file, TextLoader.Read(bytes), replaceCurrent,
                file.Path?.ToString());
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
            CommitDocument(System.IO.Path.GetFileName(path), path, file, TextLoader.Read(bytes), replaceCurrent);
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

    private void CommitDocument(string name, string? path, IStorageFile? file, string markdown, bool replaceCurrent,
        string? key = null)
    {
        var tab = replaceCurrent ? _current : null;

        if (tab == null)
        {
            tab = new DocumentTab(name, path, file, markdown, key);
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

        if (path != null)
        {
            _settings.AddRecent(path);
        }

        UpdateSession();
        SaveSettings();
        UpdateWatchers();
    }

    private void UpdateSession()
    {
        _settings.Session.Clear();

        foreach (var tab in _tabs)
        {
            if (tab.Path != null && _settings.Session.Count < AppSettings.MaxSession)
            {
                _settings.Session.Add(tab.Path);
            }
        }

        // Files opened from a picker have no path, so a session of only such tabs is empty.
        SaveSettings();
    }

    private async Task RestoreSessionAsync()
    {
        var paths = _settings.Session
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Where(System.IO.File.Exists)
            .Take(AppSettings.MaxSession)
            .ToList();

        foreach (var path in paths)
        {
            await OpenPathAsync(path);
        }

        if (paths.Count > 0)
        {
            // Reopening a session must not look like a fresh open in the title bar.
            DocumentTitleChanged?.Invoke(this, _current is null
                ? $"MD Reader {AppVersion}"
                : $"{_current.Header} — MD Reader {AppVersion}");
        }
    }

    private void ShowOpenError(Exception ex)
    {
        ShowStatusError(Lang.F("OpenFailed", ex.Message));
        CountText.Text = string.Empty;
        ProgressText.Text = string.Empty;
    }

    private void ShowStatusError(string message)
    {
        FileText.Text = message;
        FileText.Foreground = ResourceBrush("AppDanger");
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
            _current.EmptyTitle = Lang.T("EmptyFileTitle");
            _current.Rendered = null;
            ShowEmptyState();
            UpdateOutline();
            UpdateSearchStatus();
            return;
        }

        _current.EmptyTitle = null;

        var offset = preserveScroll ? DocScroll.Offset.Y : _current.ScrollOffset;

        _options.DocumentDirectory = _current.Path is { Length: > 0 } path
            ? System.IO.Path.GetDirectoryName(path)
            : null;

        _options.Rtl = EffectiveRtl(_current.Markdown);
        _options.Palette = MarkdownPalette.For(AppTheme.IsDark);

        var viewportWidth = DocScroll.Viewport.Width;
        _options.MaxImageWidth = viewportWidth > 240 ? Math.Min(760, viewportWidth - 80) : 560;

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

        // Layout has to settle before headings know where they are on screen.
        Dispatcher.UIThread.Post(UpdateReadingPosition, DispatcherPriority.Loaded);
    }

    /// <summary>Whether this document is drawn right to left: the preference may be a fixed
    /// direction or follow the script of the document itself.</summary>
    private bool EffectiveRtl(string markdown) => _settings.Direction switch
    {
        "rtl" => true,
        "ltr" => false,
        _ => TextDirection.IsRtl(markdown) || (markdown.Trim().Length == 0 && Lang.IsRtl),
    };

    private void UpdateOutline()
    {
        var outline = _current?.Rendered?.Outline;
        OutlineList.ItemsSource = outline;

        var empty = outline == null || outline.Count == 0;
        OutlineEmpty.IsVisible = empty;
        OutlineList.IsVisible = !empty;
    }

    // ---------- reading position ----------

    /// <summary>Highlights the heading of the section at the top of the viewport and updates the
    /// reading percentage in the status bar.</summary>
    private void UpdateReadingPosition()
    {
        if (_updatingPosition)
        {
            return;
        }

        _updatingPosition = true;

        try
        {
            if (_current?.Rendered?.Outline is { Count: > 0 } outline)
            {
                OutlineEntry? active = null;

                foreach (var entry in outline)
                {
                    var point = entry.Target.TranslatePoint(new Point(0, 0), DocHost);
                    if (point is { } position && position.Y <= 48)
                    {
                        active = entry;
                    }
                }

                foreach (var entry in outline)
                {
                    entry.IsActive = ReferenceEquals(entry, active);
                }
            }

            UpdateProgress();
        }
        finally
        {
            _updatingPosition = false;
        }
    }

    private void UpdateProgress()
    {
        if (_current == null)
        {
            ProgressText.Text = string.Empty;
            return;
        }

        ProgressText.Text = $"{ReadPercent().ToString(CultureInfo.CurrentCulture)}٪";
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

        UpdateReadingPosition();
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
        ProgressText.Text = string.Empty;

        var rtl = _settings.Direction switch
        {
            "rtl" => true,
            "ltr" => false,
            _ => Lang.IsRtl,
        };

        var direction = rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        var align = rtl ? TextAlignment.Right : TextAlignment.Left;
        var emptyTitle = _current?.EmptyTitle;
        var panel = new StackPanel
        {
            Margin = new Thickness(30, 44, 30, 30),
            FlowDirection = direction,
        };

        panel.Children.Add(new TextBlock
        {
            Text = emptyTitle ?? Lang.T("NoFileTitle"),
            FontSize = _options.BaseFontSize * 1.5,
            FontWeight = FontWeight.SemiBold,
            Foreground = ResourceBrush("AppText"),
            TextAlignment = align,
            Margin = new Thickness(0, 0, 0, 10),
        });

        panel.Children.Add(new TextBlock
        {
            Text = emptyTitle == null ? Lang.T("NoFileHint") : Lang.T("EmptyFileHint"),
            FontSize = _options.BaseFontSize,
            Foreground = ResourceBrush("AppTextMuted"),
            TextAlignment = align,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 18),
        });

        foreach (var (keys, description) in new (string, string)[]
                 {
                     ("Ctrl + O", Lang.T("ShortcutOpen")),
                     ("Ctrl + W", Lang.T("ShortcutClose")),
                     ("Ctrl + F", Lang.T("ShortcutSearch")),
                     ("Ctrl + E", Lang.T("ShortcutQuickOpen")),
                     ("F6", Lang.T("ShortcutPanes")),
                     ("Ctrl + ,", Lang.T("ShortcutSettings")),
                     ("Ctrl + D", Lang.T("AddBookmark")),
                     ("F11", Lang.T("ShortcutFocus")),
                     ("F5", Lang.T("ShortcutReload")),
                     ("Ctrl + P", Lang.T("ShortcutPdf")),
                     ("Ctrl + Shift + C", Lang.T("ShortcutCopy")),
                     ("Ctrl + + / Ctrl + −", Lang.T("ShortcutZoom")),
                     ("Ctrl + 0", Lang.T("ShortcutReset")),
                 })
        {
            var row = new TextBlock
            {
                FontSize = _options.BaseFontSize * 0.85,
                Foreground = ResourceBrush("AppTextMuted"),
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

        AddRecentFiles(panel, align);

        DocHost.Children.Add(panel);
    }

    private void AddRecentFiles(StackPanel panel, TextAlignment align)
    {
        var recent = _settings.Recent
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Take(AppSettings.MaxRecent)
            .ToList();

        var existing = recent.Where(System.IO.File.Exists).ToList();

        // Files that disappeared are dropped from the list the next time it is shown.
        foreach (var missing in recent.Except(existing).ToList())
        {
            _settings.RemoveRecent(missing);
        }

        if (existing.Count == 0)
        {
            return;
        }

        panel.Children.Add(new TextBlock
        {
            Text = Lang.T("Recent"),
            FontSize = _options.BaseFontSize * 0.95,
            FontWeight = FontWeight.SemiBold,
            Foreground = ResourceBrush("AppText"),
            TextAlignment = align,
            Margin = new Thickness(0, 16, 0, 6),
        });

        foreach (var path in existing)
        {
            var button = new Button
            {
                Content = System.IO.Path.GetFileName(path),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                Padding = new Thickness(8, 3),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                FontSize = _options.BaseFontSize * 0.9,
                Foreground = ResourceBrush("AppLink"),
                Cursor = new Cursor(StandardCursorType.Hand),
            };

            ToolTip.SetTip(button, path);
            var target = path;
            button.Click += async (_, _) => await OpenPathAsync(target);
            panel.Children.Add(button);
        }
    }

    // ---------- quick open ----------

    private void ShowQuickOpen()
    {
        SetSettingsVisible(false);
        CloseSearch();

        QuickOpenPanel.IsVisible = true;
        QuickOpenBox.Text = string.Empty;
        FillQuickOpen(string.Empty);
        QuickOpenBox.Focus();
    }

    private void CloseQuickOpen()
    {
        if (!QuickOpenPanel.IsVisible)
        {
            return;
        }

        QuickOpenPanel.IsVisible = false;
        QuickOpenBox.Text = string.Empty;
        _quickItems.Clear();
    }

    private void QuickOpenBox_TextChanged(object? sender, TextChangedEventArgs e) =>
        FillQuickOpen(QuickOpenBox.Text ?? string.Empty);

    private void QuickOpenBox_KeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                OpenQuickEntry(_quickItems.FirstOrDefault());
                e.Handled = true;
                break;
            case Key.Escape:
                CloseQuickOpen();
                e.Handled = true;
                break;
        }
    }

    private void QuickOpenClose_Click(object? sender, RoutedEventArgs e) => CloseQuickOpen();

    private void QuickItem_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is QuickEntry entry)
        {
            _ = OpenQuickEntryAsync(entry);
        }

        e.Handled = true;
    }

    /// <summary>Enter opens the best match; rows are sorted by score, so the first one is it.</summary>
    private void OpenQuickEntry(QuickEntry? entry)
    {
        if (entry != null)
        {
            _ = OpenQuickEntryAsync(entry);
        }
    }

    private async Task OpenQuickEntryAsync(QuickEntry entry)
    {
        CloseQuickOpen();

        if (entry.Path is { Length: > 0 } path)
        {
            await OpenPathAsync(path);
        }
        else if (entry.File is { } file)
        {
            await OpenFileAsync(file);
        }
    }

    /// <summary>Lists the recently opened files and the library, filtered by what is typed. The
    /// query matches letters in order, so "rdme" still finds "README.md".</summary>
    private void FillQuickOpen(string query)
    {
        _quickItems.Clear();
        query = query.Trim();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var found = new List<(QuickEntry Entry, int Score)>();

        void Consider(QuickEntry entry, string text)
        {
            var score = QuickScore(text, query);
            if (score >= 0)
            {
                found.Add((entry, score));
            }
        }

        foreach (var path in _settings.Recent)
        {
            if (!System.IO.File.Exists(path) || !seen.Add(path))
            {
                continue;
            }

            Consider(new QuickEntry(System.IO.Path.GetFileName(path), path, path, null),
                System.IO.Path.GetFileName(path) + " " + path);
        }

        foreach (var file in _library.Files)
        {
            var key = file.Path ?? file.File?.Path?.ToString() ?? file.Relative;
            if (!seen.Add(key))
            {
                continue;
            }

            Consider(new QuickEntry(file.Name, file.Relative, file.Path, file.File), file.Relative);
        }

        foreach (var (entry, _) in found.OrderByDescending(pair => pair.Score).Take(60))
        {
            _quickItems.Add(entry);
        }

        QuickOpenEmpty.IsVisible = _quickItems.Count == 0;
    }

    /// <summary>0 for an empty query; -1 when the query is not a subsequence of the text.</summary>
    private static int QuickScore(string text, string query)
    {
        if (query.Length == 0)
        {
            return 0;
        }

        var score = 0;
        var matched = 0;
        var previous = -2;

        for (var i = 0; i < text.Length && matched < query.Length; i++)
        {
            if (char.ToUpperInvariant(text[i]) != char.ToUpperInvariant(query[matched]))
            {
                continue;
            }

            score += 2;
            if (i == previous + 1)
            {
                score += 5;
            }

            if (i == 0 || text[i - 1] is ' ' or '/' or '\\' or '-' or '_' or '.')
            {
                score += 8;
            }

            previous = i;
            matched++;
        }

        if (matched < query.Length)
        {
            return -1;
        }

        if (text.StartsWith(query, StringComparison.CurrentCultureIgnoreCase))
        {
            score += 30;
        }

        return score - Math.Min(text.Length, 50);
    }

    // ---------- panes ----------

    /// <summary>F6 walks the visible panes: bookmarks, library, the document and the outline,
    /// ending on the toolbar, so every region is reachable without a mouse.</summary>
    private void CyclePane()
    {
        var panes = new List<Control>();
        if (MarksPanel.IsVisible) panes.Add(MarksPanel);
        if (LibraryPanel.IsVisible) panes.Add(LibraryPanel);
        panes.Add(DocScroll);
        if (OutlinePanel.IsVisible) panes.Add(OutlinePanel);
        if (ToolbarBorder.IsVisible) panes.Add(ToolbarBorder);

        if (panes.Count == 0)
        {
            return;
        }

        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Visual;
        var index = -1;

        if (focused != null)
        {
            for (var i = 0; i < panes.Count; i++)
            {
                if (IsInside(focused, panes[i]))
                {
                    index = i;
                    break;
                }
            }
        }

        var next = panes[(index + 1) % panes.Count];
        var target = next.GetVisualDescendants()
            .OfType<Control>()
            .FirstOrDefault(control => control.Focusable
                                       && control.IsEffectivelyVisible
                                       && control.IsEffectivelyEnabled
                                       && control is not ScrollBar);

        if (target != null)
        {
            target.Focus();
        }
        else
        {
            next.Focus();
        }
    }

    // ---------- linked documents ----------

    /// <summary>Opens a relative link such as <c>next.md</c> or <c>notes/idea.md#part</c>. The
    /// file is resolved against the current document first, then against the library (a document
    /// opened from a storage URI has no folder of its own).</summary>
    private async void OpenLinkedDocument(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        var link = url.Trim();
        string? anchor = null;

        var hash = link.IndexOf('#');
        if (hash >= 0)
        {
            anchor = link[(hash + 1)..];
            link = link[..hash];
        }

        link = link.Split('?')[0];

        if (link.Length == 0)
        {
            if (!string.IsNullOrEmpty(anchor))
            {
                NavigateToAnchor(anchor);
            }

            return;
        }

        if (_current?.Path is { Length: > 0 } currentPath)
        {
            try
            {
                var resolved = System.IO.Path.GetFullPath(
                    System.IO.Path.Combine(System.IO.Path.GetDirectoryName(currentPath) ?? string.Empty,
                        Uri.UnescapeDataString(link)));

                if (System.IO.File.Exists(resolved))
                {
                    await OpenPathAsync(resolved);
                    JumpToAnchor(anchor);
                    return;
                }
            }
            catch (Exception)
            {
                // A malformed relative path falls through to the library lookup.
            }
        }

        if (_library.Find(link) is { } file)
        {
            if (file.Path is { Length: > 0 } path)
            {
                await OpenPathAsync(path);
            }
            else if (file.File is { } storageFile)
            {
                await OpenFileAsync(storageFile);
            }

            JumpToAnchor(anchor);
            return;
        }

        ShowStatusError(Lang.F("LinkedMissing", link));
    }

    private void JumpToAnchor(string? anchor)
    {
        if (string.IsNullOrWhiteSpace(anchor))
        {
            return;
        }

        Dispatcher.UIThread.Post(() => NavigateToAnchor(anchor), DispatcherPriority.Loaded);
    }

    // ---------- pickers and appearance ----------

    private void LoadChoices()
    {
        _suppressEvents = true;

        FillChoices(DirectionCombo, new[]
        {
            new UiChoice("auto", Lang.T("DirectionAuto")),
            new UiChoice("rtl", Lang.T("DirectionRtl")),
            new UiChoice("ltr", Lang.T("DirectionLtr")),
        }, _settings.Direction);

        FillChoices(WidthCombo, new[]
        {
            new UiChoice("narrow", Lang.T("WidthNarrow")),
            new UiChoice("normal", Lang.T("WidthNormal")),
            new UiChoice("wide", Lang.T("WidthWide")),
        }, _settings.Width);

        FillChoices(ThemeCombo, new[]
        {
            new UiChoice(AppTheme.System, Lang.T("ThemeSystem")),
            new UiChoice(AppTheme.Light, Lang.T("ThemeLight")),
            new UiChoice(AppTheme.Dark, Lang.T("ThemeDark")),
        }, AppTheme.Normalize(_settings.Theme));

        FillChoices(LanguageCombo,
            Lang.Supported.Select(code => new UiChoice(code, Lang.DisplayName(code))).ToArray(),
            _settings.Language);

        _suppressEvents = false;
    }

    private static void FillChoices(ComboBox combo, IReadOnlyList<UiChoice> choices, string selected)
    {
        combo.ItemsSource = choices;
        combo.SelectedItem = choices.FirstOrDefault(c => c.Value.Equals(selected, StringComparison.OrdinalIgnoreCase))
                             ?? choices.FirstOrDefault();
    }

    private void SelectChoice(ComboBox combo, string value)
    {
        if (combo.ItemsSource is IEnumerable<UiChoice> choices)
        {
            _suppressEvents = true;
            combo.SelectedItem = choices.FirstOrDefault(c => c.Value == value) ?? combo.SelectedItem;
            _suppressEvents = false;
        }
    }

    private static double WidthFor(string? width) => width switch
    {
        "narrow" => 660,
        "wide" => 0,
        _ => 880,
    };

    /// <summary>Mirrors the toolbar and the panels for the interface language.</summary>
    private void ApplyUiDirection()
    {
        var direction = Lang.IsRtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

        foreach (var control in new Control[]
                 {
                     ToolbarPanel, DocTabs, SearchBarGrid, MarksDock, LibraryDock, OutlineDock,
                     StatusPanel, OutlineList, SettingsStack, QuickOpenStack,
                 })
        {
            control.FlowDirection = direction;
        }

        // The gear sits at the opposite end in a right-to-left toolbar, so the panel follows it.
        SettingsPanel.HorizontalAlignment = Lang.IsRtl
            ? Avalonia.Layout.HorizontalAlignment.Left
            : Avalonia.Layout.HorizontalAlignment.Right;

        UpdateSettingsPanelPlacement();
    }

    /// <summary>Re-renders the document with the palette that matches the current theme.</summary>
    private void ApplyPalette()
    {
        _options.Palette = MarkdownPalette.For(AppTheme.IsDark);

        if (TopLevel.GetTopLevel(this) is Window window)
        {
            window.Background = App.WindowBackground();
        }

        if (_current != null)
        {
            Refresh(preserveScroll: true);
        }
        else
        {
            ShowEmptyState();
        }
    }

    private void OnThemeVariantChanged() =>
        Dispatcher.UIThread.Post(() =>
        {
            if (AppTheme.Normalize(_settings.Theme) == AppTheme.System)
            {
                ApplyPalette();
            }
        });

    private void ApplyLanguage()
    {
        ApplyUiDirection();
        LoadChoices();

        // Strings inside the toolbar are DynamicResources and follow automatically; the document
        // and the status bar are built in code, so they are rebuilt.
        FileText.Text = _current?.Path ?? Lang.T("NoFile");
        RebuildMarks();
        UpdateSearchStatus();
        UpdateStatus();

        if (_current != null)
        {
            Refresh(preserveScroll: true);
        }
        else
        {
            ShowEmptyState();
        }
    }

    private void ApplyFocusMode(bool focus)
    {
        _focusMode = focus;

        _suppressEvents = true;
        FocusToggle.IsChecked = focus;
        _suppressEvents = false;

        ToolbarBorder.IsVisible = !focus;
        StatusBorder.IsVisible = !focus;
        SetSettingsVisible(false);

        if (!IsMobile && TopLevel.GetTopLevel(this) is Window window)
        {
            window.WindowState = focus ? WindowState.FullScreen : WindowState.Normal;
        }
    }

    private IBrush ResourceBrush(string key) =>
        this.TryFindResource(key, out var value) && value is IBrush brush ? brush : Brushes.Gray;

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
        _settings.Save();
    }

    private void UpdateStatus()
    {
        FileText.Foreground = ResourceBrush("AppTextMuted");

        if (_current == null)
        {
            FileText.Text = Lang.T("NoFile");
            CountText.Text = string.Empty;
            ProgressText.Text = string.Empty;
            return;
        }

        FileText.Text = _current.Path ?? _current.Header;
        var words = Regex.Matches(_current.Markdown, @"[\p{L}\p{N}]+").Count;
        var minutes = Math.Max(1, (int)Math.Round(words / 200.0));

        CountText.Text = Lang.F("WordsFormat", words, _current.Markdown.Length) + " · " +
                         Lang.F("MinutesFormat", minutes);
    }

    // ---------- scrolling helpers ----------

    private void RestoreScroll(double offset)
    {
        void Restore() => DocScroll.Offset = new Vector(DocScroll.Offset.X, offset);

        Dispatcher.UIThread.Post(Restore, DispatcherPriority.Loaded);
        Dispatcher.UIThread.Post(Restore, DispatcherPriority.Background);
    }
}
