using System.Runtime.InteropServices;
using System.Text;
using SpaceInspector.Core.Interfaces;

namespace SpaceInspector.Infrastructure.FileSystem;

public sealed class WindowsVolumeIdentityService : IVolumeIdentityService
{
    public string? GetIdentity(string rootPath)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var root = Path.GetPathRoot(rootPath);
        if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal)) return null;
        var guid = new StringBuilder(64);
        var fs = new StringBuilder(64);
        if (!GetVolumeNameForVolumeMountPoint(root, guid, guid.Capacity)
            || !GetVolumeInformation(root, null, 0, out var serial, out _, out _, fs, fs.Capacity)) return null;
        return $"{guid.ToString().TrimEnd('\\').ToUpperInvariant()}|{serial:X8}|{fs.ToString().ToUpperInvariant()}";
    }

    [DllImport("kernel32.dll", EntryPoint = "GetVolumeNameForVolumeMountPointW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPoint(string root, StringBuilder volume, int length);
    [DllImport("kernel32.dll", EntryPoint = "GetVolumeInformationW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformation(string root, StringBuilder? label, int labelLength,
        out uint serial, out uint maximumComponentLength, out uint flags, StringBuilder fileSystem, int fsLength);
}
