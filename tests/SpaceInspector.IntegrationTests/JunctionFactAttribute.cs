using Xunit;

namespace SpaceInspector.IntegrationTests;

public sealed class JunctionFactAttribute : FactAttribute
{
    public JunctionFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("SPACE_INSPECTOR_JUNCTION_TEST") != "1")
            Skip = "Set SPACE_INSPECTOR_JUNCTION_TEST=1 on an environment that permits NTFS junction creation. Enabled in Windows CI.";
    }
}
