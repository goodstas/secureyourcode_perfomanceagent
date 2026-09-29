using System.Runtime.InteropServices;

namespace SecureYourCode.Agent.Infrastructure;

/// <summary>The OS detected at runtime (plan §2). It selects the demo repo's hook task form (plan §4.2).</summary>
public static class HostPlatform
{
    public static string Description => $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})";

    public static string HookTaskRunnerTemplate => OperatingSystem.IsWindows()
        ? "task-runner.windows.json"
        : "task-runner.unix.json";
}
