using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SpaceInspector.Core.Models;
using SpaceInspector.App.Media;
using SpaceInspector.Infrastructure.FileSystem;
namespace SpaceInspector.App.Views;

public sealed class ImagePreview : Border
{
    public static readonly DependencyProperty ItemProperty = DependencyProperty.Register(nameof(Item), typeof(object), typeof(ImagePreview), new PropertyMetadata(null, OnItemChanged));
    public object? Item { get => GetValue(ItemProperty); set => SetValue(ItemProperty, value); }
    private int _generation;
    private static readonly SemaphoreSlim Gate = new(2);
    public ImagePreview() { Background = new SolidColorBrush(Color.FromRgb(235,240,246)); Loaded += (_, _) => Refresh(); IsVisibleChanged += (_, _) => { if (IsVisible) Refresh(); else { _generation++; Child = null; } }; Unloaded += (_, _) => { _generation++; Child = null; }; }
    private static void OnItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) { if (((ImagePreview)d).IsLoaded) ((ImagePreview)d).Refresh(); }
    private async void Refresh()
    {
        if (!IsVisible) return;
        var generation = ++_generation;
        var item = Item;
        var (path, volume) = item switch {
            IndexedFile f => (f.FullPath, f.VolumeIdentifier),
            DuplicateMember f => (f.FullPath, f.VolumeIdentifier),
            _ => ((string?)null, (string?)null)
        };
        Child = new TextBlock { Text = path is null ? "파일을 선택하세요" : "미리보기 준비 중", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12) };
        if (path is null) return;
        if (PreviewFile.Classify(path) is PreviewKind.Video or PreviewKind.Audio)
        {
            Child = new TextBlock { Text = PreviewFile.Classify(path) == PreviewKind.Video ? "▶ 동영상\n선택하면 대표 화면 표시" : "♫ 오디오\n선택하면 5초 듣기", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12), VerticalAlignment = VerticalAlignment.Center };
            return;
        }
        try
        {
            var bitmap = await Task.Run(async () => {
                await Gate.WaitAsync();
                try {
                    if (generation != Volatile.Read(ref _generation)) throw new OperationCanceledException();
                    if (!new[] {".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff"}.Contains(Path.GetExtension(path).ToLowerInvariant())) throw new IOException("이 형식의 미리보기는 아직 지원하지 않습니다.");
                    new PreviewFile(path, volume).Validate();
                    var info = new FileInfo(path);
                    if (info.Length > 32 * 1024 * 1024) throw new IOException("32MB 초과 이미지: 미리보기 생략");
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                    var frame = decoder.Frames[0];
                    if ((long)frame.PixelWidth * frame.PixelHeight > 40000000) throw new IOException("4천만 화소 초과 이미지: 미리보기 생략");
                    stream.Position = 0;
                    var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; if (frame.PixelWidth >= frame.PixelHeight) image.DecodePixelWidth = Math.Min(640, frame.PixelWidth); else image.DecodePixelHeight = Math.Min(640, frame.PixelHeight); image.StreamSource = stream; image.EndInit(); image.Freeze(); return image;
                } finally { Gate.Release(); }
            });
            if (generation == _generation && IsLoaded) Child = new Image { Source = bitmap, Stretch = Stretch.Uniform, Margin = new Thickness(6) };
        }
        catch (Exception ex) { if (generation == _generation && IsLoaded) Child = new TextBlock { Text = "미리보기 없음 · " + ex.Message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12) }; }
    }
}
