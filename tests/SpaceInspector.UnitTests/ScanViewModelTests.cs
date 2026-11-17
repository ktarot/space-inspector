using SpaceInspector.App.ViewModels;
using SpaceInspector.Core.Interfaces;
using SpaceInspector.Core.Models;
using Xunit;

namespace SpaceInspector.UnitTests;

public class ScanViewModelTests
{
    [Fact]
    public async Task ActiveScanDisablesRefreshAndAcceptsCancellation()
    {
        var scanner = new BlockingScanner();
        var model = new MainViewModel(new EmptyDrives(), new EmptyDatabase(), scanner);
        await model.InitializeAsync();
        Assert.False(model.ScanCommand.CanExecute(null));
        model.Folders.Add(@"C:\fixture");
        Assert.True(model.ScanCommand.CanExecute(null));
        var scan = model.RunScanAsync();
        Assert.True(model.IsScanning);
        Assert.False(model.RefreshCommand.CanExecute(null));
        Assert.False(model.CanEditScope);
        Assert.True(model.CancelCommand.CanExecute(null));
        model.CancelCommand.Execute(null);
        await scan;
        Assert.False(model.IsBusy);
        Assert.Contains("취소", model.Status);
        Assert.False(model.CancelCommand.CanExecute(null));
        Assert.True(model.ScanCommand.CanExecute(null));
        Assert.Equal(@"C:\fixture", Assert.Single(scanner.Roots));
    }

    private sealed class BlockingScanner : IFileIndexingService
    {
        public string[] Roots { get; private set; } = [];
        public async Task<ScanResult> ScanAsync(IEnumerable<string> roots, IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            Roots = roots.ToArray();
            try { await Task.Delay(Timeout.Infinite, cancellationToken); }
            catch (OperationCanceledException) { }
            return new(1, "Cancelled", 0, 0, 0);
        }
    }
    private sealed class EmptyDrives : IDriveService
    {
        public Task<IReadOnlyList<DriveSnapshot>> GetLocalDrivesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<DriveSnapshot>>([]);
    }
    private sealed class EmptyDatabase : IIndexDatabase
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IndexSummary> GetSummaryAsync(CancellationToken cancellationToken = default) => Task.FromResult(new IndexSummary(0, null));
    }
}
