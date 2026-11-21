using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
namespace SpaceInspector.App.Media;

// A session owns one decoder. Disposing it releases the source file and native player.
public interface IMediaSession : IDisposable
{
    FrameworkElement View { get; }
    event Action? Opened;
    event Action? Ended;
    event Action<string>? Failed;
    TimeSpan? Duration { get; }
    bool HasVideo { get; }
    bool Muted { get; set; }
    void Open(Uri source);
    void Seek(TimeSpan position);
    void Play();
    void Pause();
}

public sealed class WpfMediaSession : IMediaSession
{
    private readonly MediaElement _media = new() {
        LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Close,
        ScrubbingEnabled = true, IsMuted = true, Volume = 0.5, Stretch = Stretch.Uniform
    };
    public FrameworkElement View => _media;
    public event Action? Opened;
    public event Action? Ended;
    public event Action<string>? Failed;
    public TimeSpan? Duration => _media.NaturalDuration.HasTimeSpan ? _media.NaturalDuration.TimeSpan : null;
    public bool HasVideo => _media.HasVideo;
    public bool Muted { get => _media.IsMuted; set => _media.IsMuted = value; }
    public WpfMediaSession()
    {
        _media.MediaOpened += (_, _) => Opened?.Invoke();
        _media.MediaEnded += (_, _) => Ended?.Invoke();
        _media.MediaFailed += (_, e) => Failed?.Invoke(e.ErrorException.Message);
    }
    public void Open(Uri source) { _media.Source = source; _media.Play(); }
    public void Seek(TimeSpan position) => _media.Position = position;
    public void Play() => _media.Play();
    public void Pause() => _media.Pause();
    public void Dispose()
    {
        Opened = null; Ended = null; Failed = null;
        _media.IsMuted = true; _media.Close(); _media.Source = null;
    }
}
