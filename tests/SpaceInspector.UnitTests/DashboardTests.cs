using System.IO;
using SpaceInspector.App.ViewModels;
using SpaceInspector.Core.Interfaces;
using SpaceInspector.Core.Models;
using Xunit;

namespace SpaceInspector.UnitTests;

public class DashboardTests
{
    [Fact]
    public async Task InitializationLoadsDrivesAndEmptyIndex()
    {
        var model = new MainViewModel(new Drives(), new Database());
        await model.InitializeAsync();
        Assert.Single(model.Drives);
        Assert.Contains("파일 0개", model.IndexStatus);
        Assert.Contains("준비 완료", model.Status);
        Assert.False(model.IsBusy);
    }

    [Fact]
    public async Task RefreshPreservesUserSelection()
    {
        var model = new MainViewModel(new Drives(), new Database());
        await model.InitializeAsync();
        model.Drives[0].IsSelected = true;
        await model.InitializeAsync();
        Assert.True(model.Drives[0].IsSelected);
    }

    [Fact]
    public async Task DatabaseFailureIsDifferentFromEmptyIndex()
    {
        var model = new MainViewModel(new Drives(), new Database(true));
        await model.InitializeAsync();
        Assert.Equal("정보 확인 실패", model.IndexStatus);
        Assert.Contains("초기화 오류", model.Status);
        Assert.False(model.IsBusy);
    }

    [Fact]
    public void UnavailableDriveDoesNotInventCapacity()
    {
        var drive = new DriveSnapshot("X:\\", "", "Removable", null, null, false, "연결 해제");
        Assert.Null(drive.UsedBytes);
        Assert.Null(drive.UsedPercent);
        Assert.Equal("연결 해제", drive.CapacityText);
    }

    [Fact]
    public void ZeroCapacityDoesNotDivideByZero()
        => Assert.Null(new DriveSnapshot("X:\\", "", "Fixed", 0, 0, true, null).UsedPercent);

    private sealed class Drives : IDriveService
    {
        public Task<IReadOnlyList<DriveSnapshot>> GetLocalDrivesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<DriveSnapshot>>([new("X:\\", "Test", "Fixed", 100, 40, true, null)]);
    }

    private sealed class Database(bool fail = false) : IIndexDatabase
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default)
            => fail ? Task.FromException(new IOException("테스트 오류")) : Task.CompletedTask;
        public Task<IndexSummary> GetSummaryAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new IndexSummary(0, null));
    }
}
