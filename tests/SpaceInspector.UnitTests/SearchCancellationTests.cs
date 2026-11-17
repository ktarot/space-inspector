using SpaceInspector.App.ViewModels;
using SpaceInspector.Core.Interfaces;
using SpaceInspector.Core.Models;
using Xunit;

namespace SpaceInspector.UnitTests;

public class SearchCancellationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellingSearchKeepsPreviousResultsAndAllowsRetry(bool closingWindow)
    {
        var db = new BlockingDatabase();
        var model = new MainViewModel(new EmptyDrives(), db);
        await model.InitializeAsync();
        var old = Assert.Single(model.Files);
        db.Block = true;
        model.NameFilter = "slow";
        var search = model.SearchAsync();
        await db.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(model.IsSearching);
        Assert.True(model.IsWorking);
        Assert.True(model.CancelCommand.CanExecute(null));
        Assert.False(model.SearchCommand.CanExecute(null));
        if (closingWindow) await model.CancelAndWaitAsync();
        else model.CancelCommand.Execute(null);
        await search.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(old, Assert.Single(model.Files));
        Assert.Contains("취소", model.Status);
        Assert.False(model.IsBusy);
        Assert.False(model.IsWorking);
        Assert.False(model.CancelCommand.CanExecute(null));
        db.Block = false;
        await model.SearchAsync();
        Assert.Contains("완료", model.Status);
    }

    private sealed class EmptyDrives : IDriveService
    {
        public Task<IReadOnlyList<DriveSnapshot>> GetLocalDrivesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<DriveSnapshot>>([]);
    }
    private sealed class BlockingDatabase : IIndexDatabase, IScanRepository, IFileSearchRepository
    {
        public bool Block { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IndexSummary> GetSummaryAsync(CancellationToken cancellationToken = default) => Task.FromResult(new IndexSummary(1, null));
        public async Task<IndexPage> SearchAsync(FileSearch search, int pageNumber, int pageSize, CancellationToken cancellationToken = default)
        {
            if (Block) { Started.TrySetResult(); await Task.Delay(Timeout.Infinite, cancellationToken); }
            return new([new(1, "original.txt", @"C:\fixture\original.txt", 1, null, "Normal", null)], 1, pageNumber, pageSize);
        }
        public Task<ScanHistory?> GetLatestScanAsync(CancellationToken cancellationToken = default) => Task.FromResult<ScanHistory?>(null);
        public Task<IndexPage> GetPageAsync(int pageNumber, int pageSize, CancellationToken cancellationToken = default) => SearchAsync(new(), pageNumber, pageSize, cancellationToken);
        public Task<IReadOnlyList<ScanIssue>> GetIssuesAsync(long scanId, int limit = 100, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ScanIssue>>([]);
        public Task<long> BeginScanAsync(IReadOnlyList<string> roots, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task AppendAsync(long scanId, IReadOnlyList<ScanObservation> batch, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task FinishScanAsync(ScanResult result, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
