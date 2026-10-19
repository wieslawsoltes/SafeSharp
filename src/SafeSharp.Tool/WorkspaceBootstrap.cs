using System.Runtime.CompilerServices;
using Microsoft.Build.Locator;

namespace SafeSharp.Tool;

internal static class WorkspaceBootstrap
{
    // Keep MSBuild-dependent workspace code behind a separate call boundary: Microsoft.Build
    // must resolve to the installed SDK selected by Locator before any workspace types are used.
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Register()
    {
        if (MSBuildLocator.IsRegistered) return;
        try
        {
            MSBuildLocator.RegisterDefaults();
        }
        catch (InvalidOperationException exception)
        {
            throw new ToolException("Cannot locate an installed .NET SDK for project analysis. Install a compatible SDK and verify 'dotnet --list-sdks'. " + exception.Message, exception);
        }
    }
}
