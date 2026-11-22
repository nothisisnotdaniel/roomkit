using System.Runtime.CompilerServices;

namespace RoomKit.Tests;

/// <summary>Configures RoomKit with the sample app's values before any test runs.</summary>
internal static class ModuleSetup
{
    [ModuleInitializer]
    internal static void Init()
    {
        RoomContext.Use(SampleRoom.Options, SampleRoom.Jobs);
    }
}
