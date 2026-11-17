using SpaceInspector.Core.Models;

namespace SpaceInspector.App.ViewModels;

public sealed class DriveViewModel(DriveSnapshot snapshot) : ObservableObject
{
    private bool _isSelected;
    public DriveSnapshot Snapshot { get; } = snapshot;
    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected == value) return; _isSelected = value; Changed(); }
    }
}
