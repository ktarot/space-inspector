using System.Collections;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using SpaceInspector.Core.Models;
namespace SpaceInspector.App.Views;
public partial class FileBrowser : UserControl
{
    public static readonly DependencyProperty ItemsProperty = DependencyProperty.Register(nameof(Items), typeof(IEnumerable), typeof(FileBrowser), new PropertyMetadata(null, ItemsChanged));
    public static readonly DependencyProperty SelectionProperty = DependencyProperty.Register(nameof(Selection), typeof(object), typeof(FileBrowser), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnSelectionChanged));
    public static readonly DependencyProperty ChosenFilesProperty = DependencyProperty.Register(nameof(ChosenFiles), typeof(IReadOnlyList<IndexedFile>), typeof(FileBrowser), new FrameworkPropertyMetadata(Array.Empty<IndexedFile>(), FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public IReadOnlyList<IndexedFile> ChosenFiles { get => (IReadOnlyList<IndexedFile>)GetValue(ChosenFilesProperty); set => SetValue(ChosenFilesProperty, value); }
    private bool _syncSelection;
    private IList ActiveSelection => Mode.SelectedIndex == 2 ? ThumbList.SelectedItems : FileList.SelectedItems;
    public IEnumerable? Items { get => (IEnumerable?)GetValue(ItemsProperty); set => SetValue(ItemsProperty, value); }
    public object? Selection { get => GetValue(SelectionProperty); set => SetValue(SelectionProperty, value); }
    public object? PinnedSelection => PinnedPreview.Item;
    public FileBrowser()
    {
        InitializeComponent();
        PreviewToggle.Checked += (_, _) => PreviewColumn.Width = new GridLength(PinnedSelection is null ? 280 : 520);
        PreviewToggle.Unchecked += (_, _) => PreviewColumn.Width = new GridLength(0);
        Mode.SelectionChanged += (_, _) => {
            ThumbnailColumn.Visibility = Mode.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
            _syncSelection = true;
            try { ActiveSelection.Clear(); foreach (var file in ChosenFiles) ActiveSelection.Add(file); }
            finally { _syncSelection = false; }
        };
        FileList.SelectionChanged += (_, _) => { if (Mode.SelectedIndex != 2) UpdateSelection(); };
        ThumbList.SelectionChanged += (_, _) => { if (Mode.SelectedIndex == 2) UpdateSelection(); };
        PinButton.Click += (_, _) => PinSelection(); ClearComparisonButton.Click += (_, _) => ClearComparison();
    }
    private void UpdateSelection()
    {
        if (_syncSelection) return;
        _syncSelection = true;
        try
        {
            var files = ActiveSelection.Cast<object>().OfType<IndexedFile>().ToArray();
            SetCurrentValue(ChosenFilesProperty, files);
            SetCurrentValue(SelectionProperty, Mode.SelectedIndex == 2 ? ThumbList.SelectedItem : FileList.SelectedItem);
            SelectionHint.Text = $"선택 {files.Length}개 · Ctrl/Shift · 현재 페이지만";
        }
        finally { _syncSelection = false; }
    }
    public void SelectContextFile(IndexedFile file)
    {
        if (Items?.Cast<object>().Contains(file) != true) return;
        _syncSelection = true;
        try
        {
            // Right-click on an already selected row changes the preview, not the selection set.
            if (!ActiveSelection.Contains(file)) { ActiveSelection.Clear(); ActiveSelection.Add(file); }
            SetCurrentValue(SelectionProperty, file);
            var files = ActiveSelection.Cast<object>().OfType<IndexedFile>().ToArray();
            SetCurrentValue(ChosenFilesProperty, files);
            SelectionHint.Text = $"선택 {files.Length}개 · Ctrl/Shift · 현재 페이지만";
        }
        finally { _syncSelection = false; }
    }
    public void ReleasePreviews() { ClearComparison(); SelectedPreview.CloseSource(); }
    public void ResumePreviews() => SelectedPreview.RefreshSource();
    public void PinSelection()
    {
        if (Selection is not IndexedFile and not DuplicateMember) return;
        PinnedPreview.Item = Selection;
        PinnedPath.Text = Selection is IndexedFile file ? file.FullPath : ((DuplicateMember)Selection).FullPath;
        PinnedPanel.Visibility = Visibility.Visible; PinnedColumn.Width = new GridLength(1, GridUnitType.Star);
        PreviewToggle.IsChecked = true; PreviewColumn.Width = new GridLength(520);
        CompareHint.Text = "왼쪽: 선택한 파일 · 오른쪽: 비교 기준 — 목록에서 다른 파일을 선택하세요";
    }
    public void ClearComparison()
    {
        PinnedPreview.Item = null; PinnedPath.Text = ""; PinnedPanel.Visibility = Visibility.Collapsed;
        PinnedColumn.Width = new GridLength(0); CompareHint.Text = "선택한 파일";
        PreviewColumn.Width = new GridLength(PreviewToggle.IsChecked == true ? 280 : 0);
    }
    private static void OnSelectionChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        var browser = (FileBrowser)target;
        browser.SelectedPreview?.StopPlayback(); browser.PinnedPreview?.StopPlayback();
        if (!browser._syncSelection && browser.FileList is not null)
        {
            browser._syncSelection = true;
            try
            {
                browser.ActiveSelection.Clear();
                if (e.NewValue is not null && browser.Items?.Cast<object>().Contains(e.NewValue) == true) browser.ActiveSelection.Add(e.NewValue);
            }
            finally { browser._syncSelection = false; }
            browser.UpdateSelection();
        }
    }
    private static void ItemsChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        var browser = (FileBrowser)target;
        if (e.OldValue is INotifyCollectionChanged old) old.CollectionChanged -= browser.CollectionChanged;
        if (e.NewValue is INotifyCollectionChanged next) next.CollectionChanged += browser.CollectionChanged;
        browser.ClearComparison();
    }
    private void CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset) ClearComparison();
    }
}
