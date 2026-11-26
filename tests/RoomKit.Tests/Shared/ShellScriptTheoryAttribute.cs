namespace RoomKit.Tests;

/// <summary>A <see cref="TheoryAttribute"/> that is skipped on Windows or without <c>jq</c>.</summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class ShellScriptTheoryAttribute : TheoryAttribute
{
    public ShellScriptTheoryAttribute()
    {
        Skip = ShellScriptSkip.SkipReason();
    }
}
