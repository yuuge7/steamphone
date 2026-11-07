namespace Steamphone.Core;

/// <summary>Processes ordered UsageEvents. A late pause from A must not erase a resumed B.</summary>
public sealed class ForegroundState
{
    private string? activity;
    public string? PackageName { get; private set; }
    public void Resume(string packageName, string? className)
    {
        PackageName = packageName;
        activity = className;
    }
    public void Pause(string packageName, string? className)
    {
        if (PackageName == packageName && (activity is null || className is null || activity == className)) Clear();
    }
    public void Clear() { PackageName = null; activity = null; }
}
