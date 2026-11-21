using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SpaceInspector.App.Media;
namespace SpaceInspector.App.Views;

public sealed class MediaPreview : Grid
{
    public static readonly DependencyProperty ItemProperty = DependencyProperty.Register(nameof(Item), typeof(object), typeof(MediaPreview),
        new PropertyMetadata(null, (d, _) => ((MediaPreview)d).Refresh()));
    public object? Item { get => GetValue(ItemProperty); set => SetValue(ItemProperty, value); }
    public ImagePreview ImageContent { get; } = new();
    private readonly Func<IMediaSession> _factory;
    private readonly Func<PreviewFile, Task> _validate;
    private readonly ContentControl _playerHost = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8) };
    private readonly WrapPanel _controls = new();
    private readonly Button _play = new() { Content = "처음부터 5초 듣기" };
    private readonly Button _stop = new() { Content = "중지" };
    private readonly List<Button> _frameButtons = [];
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly Stopwatch _elapsed = new();
    private IMediaSession? _session;
    private PreviewFile? _file;
    private int _generation;
    private bool _opening, _playing;
    private static WeakReference<MediaPreview>? _audioOwner;
    public string StatusText => _status.Text;
    public bool IsPlaying => _playing;
    public bool HasSession => _session is not null;
    public MediaPreview() : this(() => new WpfMediaSession(), file => Task.Run(file.Validate)) { }
    public MediaPreview(Func<IMediaSession> factory, Func<PreviewFile, Task> validate)
    {
        _factory = factory; _validate = validate;
        RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new() { Height = GridLength.Auto }); RowDefinitions.Add(new() { Height = GridLength.Auto });
        Children.Add(ImageContent); Children.Add(_playerHost);
        SetRow(_status, 1); Children.Add(_status); SetRow(_controls, 2); Children.Add(_controls);
        foreach (var (label, fraction) in new[] { ("초반", 0.1), ("중간", 0.5), ("후반", 0.9) })
        {
            var button = new Button { Content = label, IsEnabled = false, Margin = new Thickness(3) };
            button.Click += (_, _) => ShowFrame(fraction); _frameButtons.Add(button);
        }
        _play.Click += async (_, _) => await PlayAudioAsync(); _stop.Click += (_, _) => StopPlayback();
        _timer.Tick += (_, _) => Tick();
        Loaded += (_, _) => Refresh();
        IsVisibleChanged += (_, _) => { if (IsVisible && IsLoaded) Refresh(); else Release(); };
        Unloaded += (_, _) => Release();
    }
    private void Refresh()
    {
        Release(); _file = PreviewFile.From(Item); _controls.Children.Clear();
        ImageContent.Item = null; ImageContent.Visibility = Visibility.Collapsed; _playerHost.Visibility = Visibility.Collapsed;
        if (!IsLoaded || !IsVisible) return;
        if (_file?.Kind is not (PreviewKind.Video or PreviewKind.Audio))
        {
            ImageContent.Visibility = Visibility.Visible; ImageContent.Item = Item; _status.Text = ""; return;
        }
        if (_file.Kind == PreviewKind.Audio)
        {
            _controls.Children.Add(_play); _controls.Children.Add(_stop);
            _status.Text = "오디오 · 버튼을 누르면 처음부터 약 5초 재생합니다.";
        }
        else
        {
            foreach (var button in _frameButtons) _controls.Children.Add(button);
            _ = OpenAsync(false);
        }
    }
    public async Task PlayAudioAsync()
    {
        if (!IsLoaded || !IsVisible || _file?.Kind != PreviewKind.Audio) return;
        if (_audioOwner?.TryGetTarget(out var previous) == true && !ReferenceEquals(previous, this)) previous.StopPlayback();
        _audioOwner = new(this);
        await OpenAsync(true);
    }
    private async Task OpenAsync(bool playAudio)
    {
        Release();
        if (_file is not { } file || !IsVisible || !IsLoaded) return;
        if (playAudio) _audioOwner = new(this);
        var generation = _generation;
        _opening = true; _elapsed.Restart(); _timer.Start(); _play.IsEnabled = false; _stop.IsEnabled = true;
        _status.Text = playAudio ? "오디오 준비 중…" : "동영상 대표 화면 준비 중…";
        try
        {
            await _validate(file);
            if (generation != _generation || !IsLoaded || !IsVisible) return;
            var session = _factory(); _session = session;
            _playerHost.Content = session.View; _playerHost.Visibility = Visibility.Visible;
            session.View.Opacity = playAudio ? 0 : 1;
            session.Muted = true;
            session.Opened += () => {
                if (!Current()) return;
                try {
                    session.Pause(); _opening = false;
                    if (playAudio)
                    {
                        session.Seek(TimeSpan.Zero); session.Muted = false;
                        _playing = true; _elapsed.Restart(); session.Play();
                        _status.Text = "재생 중 · 약 5초 후 자동 중지";
                    }
                    else
                    {
                        _timer.Stop();
                        if (!session.HasVideo || session.Duration is not { } duration || duration <= TimeSpan.Zero)
                        { Fail("영상 또는 재생 시간을 확인할 수 없습니다."); return; }
                        foreach (var button in _frameButtons) button.IsEnabled = true;
                        ShowFrame(0.1);
                    }
                } catch (Exception ex) { Fail(ex.Message); }
            };
            session.Ended += () => { if (Current()) StopPlayback(); };
            session.Failed += reason => { if (Current()) Fail(reason); };
            session.Open(new Uri(file.Path, UriKind.Absolute));
            bool Current() => generation == _generation && ReferenceEquals(_session, session);
        }
        catch (Exception ex) { if (generation == _generation) Fail(ex.Message); }
    }
    public void ShowFrame(double fraction)
    {
        if (_opening || _file?.Kind != PreviewKind.Video || _session?.Duration is not { } duration) return;
        try
        {
            var seconds = Math.Clamp(duration.TotalSeconds * Math.Clamp(fraction, 0, 1), 0, Math.Max(0, duration.TotalSeconds - 0.05));
            _session.Muted = true; _session.Pause(); _session.Seek(TimeSpan.FromSeconds(seconds));
            _status.Text = $"대표 화면 · {seconds:0.0}초 / {duration.TotalSeconds:0.0}초 · 무음";
        }
        catch (Exception ex) { Fail(ex.Message); }
    }
    private void Tick()
    {
        if (_opening && _elapsed.Elapsed >= TimeSpan.FromSeconds(10)) Fail("10초 안에 열지 못했습니다. 파일 또는 코덱을 확인하세요.");
        else if (_playing && _elapsed.Elapsed >= TimeSpan.FromSeconds(5)) StopPlayback();
    }
    public void CloseSource() => Release();
    public void RefreshSource() => Refresh();
    public void StopPlayback()
    {
        if (_file?.Kind != PreviewKind.Audio || (!_playing && !_opening)) return;
        Release(); _status.Text = "재생 중지 · 다시 들으려면 재생 버튼을 누르세요.";
    }
    private void Fail(string reason)
    {
        Release(); _status.Text = "미리보기 불가 · Windows 코덱 또는 파일 상태를 확인하세요. " + reason;
    }
    private void Release()
    {
        _generation++; _timer.Stop(); _elapsed.Reset(); _opening = _playing = false;
        var session = _session; _session = null;
        if (session is not null) { try { session.Dispose(); } finally { _playerHost.Content = null; } }
        foreach (var button in _frameButtons) button.IsEnabled = false;
        _play.IsEnabled = true; _stop.IsEnabled = false;
        if (_audioOwner?.TryGetTarget(out var owner) == true && ReferenceEquals(owner, this)) _audioOwner = null;
    }
}
