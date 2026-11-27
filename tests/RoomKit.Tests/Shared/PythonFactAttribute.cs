namespace RoomKit.Tests;

/// <summary>A <see cref="FactAttribute"/> that is skipped on Windows or without <c>python3</c> on <c>PATH</c>.</summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class PythonFactAttribute : FactAttribute
{
    public PythonFactAttribute()
    {
        Skip = PythonSkip.SkipReason();
    }
}
