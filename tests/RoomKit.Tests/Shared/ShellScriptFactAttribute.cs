namespace RoomKit.Tests;

/// <summary>A <see cref="FactAttribute"/> that is skipped on Windows or without <c>jq</c>.</summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class ShellScriptFactAttribute : FactAttribute
{
    public ShellScriptFactAttribute()
    {
        Skip = ShellScriptSkip.SkipReason();
    }
}
