using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SpaceInspector.App.Media;
using SpaceInspector.App.Views;
using SpaceInspector.Core.Models;
using SpaceInspector.Infrastructure.FileSystem;
using Xunit;
namespace SpaceInspector.UnitTests;

public sealed class MediaPreviewTests
{
    private static IndexedFile Item(string extension) => new(1, "fixture" + extension, @"C:\fixture" + extension, 10, null, "Normal", null, "fixture-volume");
    private sealed class FakeSession : IMediaSession
    {
        public FrameworkElement View { get; } = new Border();
        public event Action? Opened;
        public event Action? Ended;
        public event Action<string>? Failed;
        public TimeSpan? Duration { get; set; } = TimeSpan.FromSeconds(20);
        public bool HasVideo { get; set; } = true;
        public bool Muted { get; set; }
        public int Plays { get; private set; }
        public int Pauses { get; private set; }
        public TimeSpan Position { get; private set; }
        public bool Disposed { get; private set; }
        public void Open(Uri source) { }
        public void Seek(TimeSpan position) => Position = position;
        public void Play() => Plays++;
        public void Pause() => Pauses++;
        public void Dispose() => Disposed = true;
        public void SignalOpened() => Opened?.Invoke();
        public void SignalEnded() => Ended?.Invoke();
        public void SignalFailed() => Failed?.Invoke("fixture decoder failure");
    }
    private static void PumpUntil(Func<bool> done, int seconds = 12)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!done() && DateTime.UtcNow < deadline) { Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background); Thread.Sleep(10); }
        Assert.True(done(), "Timed out waiting for media state");
    }
    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            try { action(); } catch (Exception ex) { failure = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(45)));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private static Window Show(FrameworkElement content)
    {
        var window = new Window { Content = content, Width = 600, Height = 400, Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
        window.Show(); Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background); return window;
    }

    [Fact]
    public void VideoUsesMutedRepresentativePositionsAndDisposesOnSelectionChange() => OnSta(() => {
        var sessions = new List<FakeSession>();
        var preview = new MediaPreview(() => { var s = new FakeSession(); sessions.Add(s); return s; }, _ => Task.CompletedTask) { Item = Item(".mp4") };
        var window = Show(preview);
        try {
            var session = sessions.Last(); session.SignalOpened();
            Assert.True(session.Muted); Assert.Equal(0, session.Plays); Assert.Equal(TimeSpan.FromSeconds(2), session.Position);
            preview.ShowFrame(0.5); Assert.Equal(TimeSpan.FromSeconds(10), session.Position);
            preview.ShowFrame(0.9); Assert.Equal(TimeSpan.FromSeconds(18), session.Position);
            preview.Item = Item(".wav"); Assert.True(session.Disposed); Assert.False(preview.HasSession);
            session.SignalOpened(); Assert.False(preview.HasSession); Assert.False(preview.IsPlaying);
        } finally { window.Close(); }
    });

    [Fact]
    public void AudioRequiresClickStopsAtFiveSecondsAndOnlyOnePanelPlays() => OnSta(() => {
        var sessions = new List<FakeSession>();
        MediaPreview Make() => new(() => { var s = new FakeSession { HasVideo = false }; sessions.Add(s); return s; }, _ => Task.CompletedTask) { Item = Item(".wav") };
        var first = Make(); var second = Make(); var panel = new StackPanel(); panel.Children.Add(first); panel.Children.Add(second);
        var window = Show(panel);
        try {
            Assert.Empty(sessions);
            first.PlayAudioAsync().GetAwaiter().GetResult(); var one = sessions.Last(); one.SignalOpened();
            Assert.True(first.IsPlaying); Assert.False(one.Muted);
            second.PlayAudioAsync().GetAwaiter().GetResult(); var two = sessions.Last(); two.SignalOpened();
            Assert.True(one.Disposed); Assert.False(first.IsPlaying); Assert.True(second.IsPlaying);
            PumpUntil(() => !second.IsPlaying, 7); Assert.True(two.Disposed);
            second.PlayAudioAsync().GetAwaiter().GetResult(); var three = sessions.Last(); three.SignalOpened();
            second.Visibility = Visibility.Collapsed; Assert.True(three.Disposed); Assert.False(second.IsPlaying);
        } finally { window.Close(); }
    });

    [Fact]
    public void FailedValidationAndLateOpenNeverStartPlayback() => OnSta(() => {
        var validated = new TaskCompletionSource(); int created = 0;
        var preview = new MediaPreview(() => { created++; return new FakeSession(); }, _ => validated.Task) { Item = Item(".mp4") };
        var window = Show(preview);
        try {
            preview.Item = Item(".fla"); validated.SetResult();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Assert.Equal(0, created); Assert.False(preview.HasSession);
        } finally { window.Close(); }
        var rejected = new MediaPreview(() => { created++; return new FakeSession(); }, _ => Task.FromException(new IOException("protected path"))) { Item = Item(".mp4") };
        window = Show(rejected);
        try { Assert.Equal(0, created); Assert.Contains("protected path", rejected.StatusText); }
        finally { window.Close(); }
    });

    [Fact]
    public void DecoderFailureAndShortAudioReleaseTheFile() => OnSta(() => {
        FakeSession? session = null;
        var preview = new MediaPreview(() => session = new FakeSession(), _ => Task.CompletedTask) { Item = Item(".mp4") };
        var window = Show(preview);
        try {
            session!.SignalFailed(); Assert.True(session.Disposed); Assert.Contains("미리보기 불가", preview.StatusText);
            preview.Item = Item(".wav"); preview.PlayAudioAsync().GetAwaiter().GetResult();
            session!.SignalOpened(); session.SignalEnded(); Assert.True(session.Disposed); Assert.False(preview.IsPlaying);
        } finally { window.Close(); }
    });

    [Fact]
    public void NativeWindowsDecoderPlaysSilentWaveAndReleasesIt() => OnSta(() => {
        var path = Path.Combine(Path.GetTempPath(), "SI-media-" + Guid.NewGuid().ToString("N") + ".wav");
        WriteWave(path);
        var volume = new WindowsVolumeIdentityService().GetIdentity(path);
        var preview = new MediaPreview { Item = new IndexedFile(1, Path.GetFileName(path), path, new FileInfo(path).Length, null, "Normal", null, volume) };
        var window = Show(preview);
        try {
            Assert.False(preview.HasSession);
            var opening = preview.PlayAudioAsync(); PumpUntil(() => opening.IsCompleted); opening.GetAwaiter().GetResult();
            PumpUntil(() => preview.IsPlaying || preview.StatusText.Contains("미리보기 불가"));
            Assert.True(preview.IsPlaying, preview.StatusText);
            PumpUntil(() => !preview.IsPlaying, 7); Assert.False(preview.HasSession);
        } finally { window.Close(); File.Delete(path); }
    });

    [Fact]
    public void PreviewGuardRejectsWrongDeviceMissingFileAndNetworkPath()
    {
        var path = Path.Combine(Path.GetTempPath(), "SI-guard-" + Guid.NewGuid().ToString("N") + ".wav");
        File.WriteAllBytes(path, [1]);
        try { Assert.Throws<IOException>(() => new PreviewFile(path, "wrong-device").Validate()); }
        finally { File.Delete(path); }
        Assert.ThrowsAny<IOException>(() => new PreviewFile(path, null).Validate());
        Assert.Throws<IOException>(() => new PreviewFile(@"\\server\share\audio.wav", null).Validate());
        Assert.Equal(PreviewKind.Unsupported, PreviewFile.Classify("project.fla"));
    }
    [Fact]
    public void NativeWindowsDecoderOpensAndSeeksSyntheticVideo() => OnSta(() => {
        var path = Path.Combine(Path.GetTempPath(), "SI-video-" + Guid.NewGuid().ToString("N") + ".avi");
        WriteAvi(path);
        var volume = new WindowsVolumeIdentityService().GetIdentity(path);
        WpfMediaSession? session = null;
        var preview = new MediaPreview(() => session = new WpfMediaSession(), file => Task.Run(file.Validate)) {
            Item = new IndexedFile(1, Path.GetFileName(path), path, new FileInfo(path).Length, null, "Normal", null, volume)
        };
        var window = Show(preview);
        try {
            PumpUntil(() => preview.StatusText.Contains("대표 화면 ·") || preview.StatusText.Contains("미리보기 불가"));
            Assert.True(preview.HasSession, preview.StatusText); Assert.True(session!.HasVideo);
            Assert.True(session.Muted); Assert.False(preview.IsPlaying);
            preview.ShowFrame(0.5); Assert.Contains("1.0초", preview.StatusText);
            preview.ShowFrame(0.9); Assert.Contains("1.8초", preview.StatusText);
            var renderUntil = DateTime.UtcNow.AddMilliseconds(300);
            while (DateTime.UtcNow < renderUntil) { Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background); Thread.Sleep(10); }
            if (Environment.GetEnvironmentVariable("SPACE_INSPECTOR_MEDIA_SCREENSHOT") is { } capture)
            {
                window.UpdateLayout();
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(600, 400, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(preview);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using var stream = File.Create(capture); encoder.Save(stream);
            }
            preview.Item = null; Assert.False(preview.HasSession);
        } finally { window.Close(); File.Delete(path); }
    });
    private static void WriteAvi(string path)
    {
        const int width = 32, height = 16, frames = 20, frameSize = width * height * 3;
        static byte[] Chunk(string id, Action<BinaryWriter> body)
        {
            using var content = new MemoryStream(); using (var writer = new BinaryWriter(content, System.Text.Encoding.ASCII, true)) body(writer);
            using var output = new MemoryStream(); using var chunk = new BinaryWriter(output);
            chunk.Write(System.Text.Encoding.ASCII.GetBytes(id)); chunk.Write((int)content.Length); chunk.Write(content.ToArray());
            if (content.Length % 2 != 0) chunk.Write((byte)0);
            return output.ToArray();
        }
        var header = Chunk("LIST", w => {
            w.Write("hdrl"u8);
            w.Write(Chunk("avih", h => {
                foreach (var value in new[] {100000, frameSize * 10, 0, 16, frames, 0, 1, frameSize, width, height, 0, 0, 0, 0}) h.Write(value);
            }));
            w.Write(Chunk("LIST", list => {
                list.Write("strl"u8);
                list.Write(Chunk("strh", h => {
                    h.Write("vidsDIB "u8); h.Write(0); h.Write((short)0); h.Write((short)0);
                    foreach (var value in new[] {0, 1, 10, 0, frames, frameSize, -1, 0}) h.Write(value);
                    h.Write((short)0); h.Write((short)0); h.Write((short)width); h.Write((short)height);
                }));
                list.Write(Chunk("strf", h => {
                    h.Write(40); h.Write(width); h.Write(height); h.Write((short)1); h.Write((short)24);
                    foreach (var value in new[] {0, frameSize, 0, 0, 0, 0}) h.Write(value);
                }));
            }));
        });
        File.WriteAllBytes(path, Chunk("RIFF", w => {
            w.Write("AVI "u8); w.Write(header);
            w.Write(Chunk("LIST", movi => {
                movi.Write("movi"u8);
                for (var i = 0; i < frames; i++) {
                    var color = (byte)(i * 10);
                    movi.Write(Chunk("00db", f => { for (var pixel = 0; pixel < width * height; pixel++) { f.Write(color); f.Write((byte)100); f.Write((byte)(200 - color)); } }));
                }
            }));
            w.Write(Chunk("idx1", index => {
                for (var i = 0; i < frames; i++) { index.Write("00db"u8); index.Write(16); index.Write(4 + i * (8 + frameSize)); index.Write(frameSize); }
            }));
        }));
    }
    private static void WriteWave(string path)
    {
        const int sampleRate = 8000, bytes = sampleRate * 2 * 8;
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write("RIFF"u8); writer.Write(36 + bytes); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(sampleRate);
        writer.Write(sampleRate * 2); writer.Write((short)2); writer.Write((short)16);
        writer.Write("data"u8); writer.Write(bytes); writer.Write(new byte[bytes]);
    }
}
