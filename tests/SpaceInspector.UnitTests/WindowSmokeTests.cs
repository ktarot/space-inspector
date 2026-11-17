using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SpaceInspector.App.ViewModels;
using SpaceInspector.App.Views;
using SpaceInspector.Core.Interfaces;
using SpaceInspector.Core.Models;
using SpaceInspector.Infrastructure.Database;
using Xunit;

namespace SpaceInspector.UnitTests;

public class WindowSmokeTests
{
    [Fact]
    public void MainWindowLoadsAndRendersWithInitializedSqlite()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "SpaceInspector.Tests", Guid.NewGuid().ToString("N"));
            MainWindow? window = null;
            try
            {
                var model = new MainViewModel(new FixtureDrives(), new SqliteIndexDatabase(Path.Combine(directory, "index.db")));
                window = new MainWindow(model) { ShowActivated = false, ShowInTaskbar = false, Left = -10000, Top = -10000 };
                window.Show();
                var deadline = DateTime.UtcNow.AddSeconds(15);
                while ((model.IsBusy || model.Status == "초기화 준비 중") && DateTime.UtcNow < deadline)
                {
                    window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
                    Thread.Sleep(10);
                }
                Assert.False(model.IsBusy);
                Assert.Contains("준비 완료", model.Status);
                Assert.Single(model.Drives);
                Assert.Equal("Space Inspector", window.Title);
                Assert.Contains(Descendants(window).OfType<Button>(), b =>
                    b.Content?.ToString() == "검사 시작 · 준비 중" && !b.IsEnabled);
                window.UpdateLayout();
                var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(window);
                Assert.True(bitmap.PixelWidth >= 920);
                var output = Environment.GetEnvironmentVariable("SPACE_INSPECTOR_TEST_SCREENSHOT");
                if (!string.IsNullOrWhiteSpace(output))
                {
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream = File.Create(output);
                    encoder.Save(stream);
                }
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                window?.Close();
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Window smoke test timed out.");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private sealed class FixtureDrives : IDriveService
    {
        public Task<IReadOnlyList<DriveSnapshot>> GetLocalDrivesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<DriveSnapshot>>([new("X:\\", "테스트 드라이브", "Fixed", 512L << 30, 180L << 30, true, null)]);
    }
}
