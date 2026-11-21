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
                var fixture = new PngBitmapEncoder();
                fixture.Frames.Add(BitmapFrame.Create(BitmapSource.Create(32, 16, 96, 96, PixelFormats.Bgra32, null,
                    Enumerable.Repeat((byte)180, 32 * 16 * 4).ToArray(), 32 * 4)));
                using (var fixtureStream = File.Create(Path.Combine(files, "a.jpg"))) fixture.Save(fixtureStream);
                File.Copy(Path.Combine(files, "a.jpg"), Path.Combine(files, "b.jpg"));
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
                model.SelectedGroup = model.Groups[0]; Wait();
                Assert.Equal(2, model.Members.Count);
                model.SelectedMember = model.Members[0];
                var keptPath = model.SelectedMember.FullPath;
                Assert.True(model.CanMoveSelectedToRecycle);
                model.ToggleKeep(keptPath);
                Assert.False(model.CanMoveSelectedToRecycle);
                model.SelectedHistory = Assert.Single(model.History);
                model.OpenHistoryCommand.Execute(null); Wait();
                Assert.Equal(2, model.Files.Count); Assert.Single(model.Groups);
                var browser = FindBrowser(window);
                model.SelectedGroup = model.Groups[0]; Wait();
                Assert.True(model.IsKept(keptPath));
                model.SelectedMember = model.Members.First(m => m.FullPath == keptPath);
                Assert.False(model.CanMoveSelectedToRecycle);
                model.ToggleKeep(keptPath);
                Assert.True(model.CanMoveSelectedToRecycle);
                model.ComparisonFilter = 1; model.FilterCommand.Execute(null); Wait();
                Assert.Equal(2, model.Files.Count);
                model.SelectedFile = model.Files[0];
                Assert.True(model.CanMoveSelectedToRecycle);
                model.KeepSelectedCommand.Execute(null);
                Assert.False(model.CanMoveSelectedToRecycle);
                model.KeptOnly = true; model.FilterCommand.Execute(null); Wait();
                Assert.Single(model.Files);
                model.ViewName = "보관한 중복 이미지"; model.MediaFilter = 1; model.SaveViewCommand.Execute(null);
                Assert.Single(model.SavedViews);
                model.ResetFiltersCommand.Execute(null); Wait(); Assert.Equal(2, model.Files.Count);
                model.SelectedView = model.SavedViews[0]; model.ApplyViewCommand.Execute(null); Wait(); Assert.Single(model.Files);
                model.ResetFiltersCommand.Execute(null); Wait();
                browser.Selection = model.Files[0]; browser.PinSelection();
                browser.Selection = model.Files[1];
                Assert.Equal(model.Files[0], browser.PinnedSelection);
                Assert.Equal(model.Files[1], browser.Selection);
                ((ComboBox)browser.FindName("Mode")).SelectedIndex = 2;
                Assert.Equal(model.Files[1], browser.Selection);
                var previewDeadline = DateTime.UtcNow.AddSeconds(10);
                var pinned = ((MediaPreview)browser.FindName("PinnedPreview")).ImageContent;
                while (pinned.Child is not Image && DateTime.UtcNow < previewDeadline)
                { Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background); Thread.Sleep(10); }
                Assert.IsType<Image>(pinned.Child);
                Assert.True(((BitmapSource)((Image)pinned.Child).Source).PixelWidth <= 640);
                using (var reopenedModel = new WorkViewModel(Path.Combine(root, "jobs")))
                {
                    var initialization = reopenedModel.InitializeAsync();
                    var deadline = DateTime.UtcNow.AddSeconds(10);
                    while (!initialization.IsCompleted && DateTime.UtcNow < deadline)
                    { Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background); Thread.Sleep(10); }
                    Assert.True(initialization.IsCompletedSuccessfully);
                    Assert.Equal("보관한 중복 이미지", Assert.Single(reopenedModel.SavedViews).Label);
                }
                model.ResetFiltersCommand.Execute(null); Wait(); Assert.Null(browser.PinnedSelection);
                browser.Selection = model.Files[0]; browser.PinSelection(); browser.Selection = model.Files[1];
                previewDeadline = DateTime.UtcNow.AddSeconds(10);
                while (pinned.Child is not Image && DateTime.UtcNow < previewDeadline)
                { Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background); Thread.Sleep(10); }
                Assert.IsType<Image>(pinned.Child);
                // Multi-selection survives view mode changes, but never leaks through a new query.
                var mode = (ComboBox)browser.FindName("Mode");
                mode.SelectedIndex = 0;
                var list = (DataGrid)browser.FindName("FileList");
                list.SelectAll(); Assert.Equal(2, model.ChosenFiles.Count);
                mode.SelectedIndex = 2;
                Assert.Equal(2, ((ListBox)browser.FindName("ThumbList")).SelectedItems.Count);
                model.ResetFiltersCommand.Execute(null); Wait(); Assert.Single(model.ChosenFiles); Assert.Equal(model.SelectedFile, model.ChosenFiles[0]);
                mode.SelectedIndex = 0; list.SelectAll(); Assert.Equal(2, model.ChosenFiles.Count);
                IReadOnlyList<SpaceInspector.Core.Models.FileChangeRequest> batchRequests;
                var batchDialog = new BatchFileChangeDialog(model.ChosenFiles, true) { Owner = window };
                var dialogTop = (StackPanel)((DockPanel)batchDialog.Content).Children[0];
                var dialogRow = (WrapPanel)dialogTop.Children[1];
                ((TextBox)dialogRow.Children[1]).Text = "batch-";
                batchRequests = batchDialog.Requests!; Assert.Equal(2, batchRequests.Count);
                Assert.EndsWith("batch-a.jpg", batchRequests[0].TargetPath);
                ((ComboBox)dialogRow.Children[0]).SelectedIndex = 1;
                ((TextBox)dialogRow.Children[3]).Text = "7";
                Assert.EndsWith("batch-007.jpg", batchDialog.Requests![0].TargetPath);
                Assert.EndsWith("batch-008.jpg", batchDialog.Requests![1].TargetPath);
                ((ComboBox)dialogRow.Children[0]).SelectedIndex = 0;
                batchDialog.Show(); batchDialog.UpdateLayout();
                var batchScreenshot = Environment.GetEnvironmentVariable("SPACE_INSPECTOR_BATCH_SCREENSHOT");
                if (batchScreenshot is not null)
                {
                    var preview = new RenderTargetBitmap((int)batchDialog.ActualWidth, (int)batchDialog.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    preview.Render(batchDialog); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(preview));
                    using var outputStream = File.Create(batchScreenshot); png.Save(outputStream);
                }
                batchDialog.Close();
                model.ChooseBatchTargets = (_, _) => batchRequests;
                model.ConfirmBatchChanges = _ => false;
                model.RenameFileCommand.Execute(null); Wait();
                Assert.All(batchRequests, r => Assert.True(File.Exists(r.File.FullPath)));
                IReadOnlyList<SpaceInspector.Core.Models.FileChangeOutcome>? outcomes = null;
                model.ShowBatchResults = result => outcomes = result;
                model.ConfirmBatchChanges = _ => true;
                model.RenameFileCommand.Execute(null); Wait();
                Assert.NotNull(outcomes); Assert.Equal(2, outcomes.Count); Assert.All(outcomes, o => Assert.Equal("Completed", o.State));
                Assert.All(batchRequests, r => { Assert.True(File.Exists(r.TargetPath)); Assert.False(File.Exists(r.File.FullPath)); });
                Assert.True(model.IsKept(batchRequests.First(r => r.File.FullPath == keptPath).TargetPath));
                // Exercise the UI commands with explicit synthetic-file confirmation, never user files.
                model.SelectedFile = model.Files.First(f => f.FileName == "batch-a.jpg");
                var beforeRename = model.SelectedFile.FullPath;
                var renamed = Path.Combine(files, "renamed.jpg");
                model.ChooseFileTarget = (_, _) => renamed;
                model.ConfirmFileChange = _ => false;
                model.RenameFileCommand.Execute(null); Wait(); Assert.True(File.Exists(beforeRename)); Assert.False(File.Exists(renamed));
                model.ConfirmFileChange = _ => true;
                model.RenameFileCommand.Execute(null); Wait();
                Assert.False(File.Exists(beforeRename)); Assert.True(File.Exists(renamed));
                Assert.True(model.IsKept(renamed)); Assert.False(model.IsKept(beforeRename));
                Assert.Equal(renamed, model.SelectedFile?.FullPath);
                var archive = Path.Combine(root, "archive"); Directory.CreateDirectory(archive);
                var moved = Path.Combine(archive, "renamed.jpg");
                model.ChooseFileTarget = (_, _) => moved;
                model.MoveFileCommand.Execute(null); Wait();
                Assert.True(File.Exists(moved)); Assert.False(File.Exists(renamed)); Assert.True(model.IsKept(moved));
                Assert.Equal("MovedOutsideScope", model.SelectedFile?.ScanStatus);
                Assert.False(model.CanMoveSelectedToRecycle);
                model.ConfirmFileChange = _ => false;
                model.UndoFileChangeCommand.Execute(null); Wait(); Assert.True(File.Exists(moved));
                model.ConfirmFileChange = _ => true;
                model.UndoFileChangeCommand.Execute(null); Wait();
                Assert.True(File.Exists(renamed)); Assert.False(File.Exists(moved));
                Assert.Equal(renamed, model.SelectedFile?.FullPath); Assert.True(model.IsKept(renamed));
                Assert.False(model.CanMoveSelectedToRecycle);
                list.SelectAll(); Assert.Equal(2, model.ChosenFiles.Count);
                browser.SelectContextFile(model.Files[1]); Assert.Equal(2, model.ChosenFiles.Count);
                Assert.Equal(model.Files[1], model.SelectedFile);
                Assert.True(model.UndoFileChangeCommand.CanExecute(null));
                model.ConfirmBatchChanges = _ => false;
                model.UndoFileChangeCommand.Execute(null); Wait();
                Assert.True(File.Exists(renamed)); Assert.True(File.Exists(Path.Combine(files, "batch-b.jpg")));
                model.ConfirmBatchChanges = plans => { Assert.Equal(2, plans.Count); Assert.All(plans, p => Assert.NotNull(p.UndoOf)); return true; };
                outcomes = null; model.UndoFileChangeCommand.Execute(null); Wait();
                Assert.NotNull(outcomes); Assert.All(outcomes, o => Assert.Equal("Completed", o.State));
                Assert.True(File.Exists(Path.Combine(files, "batch-a.jpg"))); Assert.True(File.Exists(Path.Combine(files, "b.jpg")));
                Assert.False(File.Exists(renamed)); Assert.True(model.IsKept(Path.Combine(files, "batch-a.jpg")));
                Assert.Contains("일괄 되돌리기", model.Status);
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

    private static FileBrowser FindBrowser(System.Windows.DependencyObject root)
    {
        var queue = new Queue<System.Windows.DependencyObject>(); queue.Enqueue(root);
        while (queue.TryDequeue(out var item))
        {
            if (item is FileBrowser browser) return browser;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(item); i++) queue.Enqueue(VisualTreeHelper.GetChild(item, i));
        }
        throw new InvalidOperationException("Unified browser missing");
    }
}
