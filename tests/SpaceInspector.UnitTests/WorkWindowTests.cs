using System.IO;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SpaceInspector.App.ViewModels;
using SpaceInspector.App.Views;
using Xunit;

namespace SpaceInspector.UnitTests;

public sealed class WorkWindowTests
{
    [Fact]
    public void ScopedWorkflowRendersResultsAndReloadsSavedJob()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            var root = Path.Combine(Path.GetTempPath(), "SI-UI-" + Guid.NewGuid().ToString("N"));
            WorkWindow? window = null; WorkViewModel? model = null;
            try
            {
                var files = Path.Combine(root, "files"); Directory.CreateDirectory(files);
                File.WriteAllBytes(Path.Combine(files, "a.jpg"), [1, 2, 3]);
                File.WriteAllBytes(Path.Combine(files, "b.jpg"), [1, 2, 3]);
                File.WriteAllBytes(Path.Combine(files, "ignored.mp3"), [1, 2, 3]);
                model = new(Path.Combine(root, "jobs"));
                window = new(model) { ShowActivated = false, ShowInTaskbar = false, Left = -10000, Top = -10000 };
                window.Show();
                void Wait()
                {
                    var deadline = DateTime.UtcNow.AddSeconds(15);
                    do { Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background); Thread.Sleep(10); }
                    while (model.Active && DateTime.UtcNow < deadline);
                    Assert.False(model.Active);
                }
                Wait(); Assert.Contains("준비 완료", model.Status);
                model.Folders.Add(files); model.Recursive = false;
                model.StartCommand.Execute(null); Wait();
                Assert.Equal(2, model.Files.Count); Assert.Single(model.Groups);
                Assert.Contains("완료", model.Status);
                model.SelectedGroup = model.Groups[0]; model.LoadMembersCommand.Execute(null); Wait();
                Assert.Equal(2, model.Members.Count);
                model.SelectedHistory = Assert.Single(model.History);
                model.OpenHistoryCommand.Execute(null); Wait();
                Assert.Equal(2, model.Files.Count); Assert.Single(model.Groups);
                var tabs = FindTabs(window); tabs.SelectedIndex = 1;
                model.SelectedGroup = model.Groups[0]; model.LoadMembersCommand.Execute(null); Wait();
                window.UpdateLayout();
                var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(window);
                var output = Environment.GetEnvironmentVariable("SPACE_INSPECTOR_V2_SCREENSHOT");
                if (output is not null)
                {
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream = File.Create(output); encoder.Save(stream);
                }
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                window?.Close(); Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
                model?.Dispose();
                if (Directory.Exists(root)) Directory.Delete(root, true);
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "Workflow UI timed out");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static TabControl FindTabs(System.Windows.DependencyObject root)
    {
        var queue = new Queue<System.Windows.DependencyObject>(); queue.Enqueue(root);
        while (queue.TryDequeue(out var item))
        {
            if (item is TabControl tabs) return tabs;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(item); i++) queue.Enqueue(VisualTreeHelper.GetChild(item, i));
        }
        throw new InvalidOperationException("Tabs missing");
    }
}
