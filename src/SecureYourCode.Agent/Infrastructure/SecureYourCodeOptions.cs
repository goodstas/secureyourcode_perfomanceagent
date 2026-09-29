namespace SecureYourCode.Agent.Infrastructure;

/// <summary>The "SecureYourCode" configuration section (appsettings.json or SecureYourCode__* environment variables).</summary>
public sealed class SecureYourCodeOptions
{
    public const string SectionName = "SecureYourCode";

    /// <summary>Absolute path. Empty: SECUREYOURCODE_STATE_ROOT, otherwise &lt;user home&gt;/.secureyourcode.</summary>
    public string? StateRoot { get; set; }

    /// <summary>Absolute path of this repository. Empty: found by walking up from the host binaries to SecureYourCode.slnx.</summary>
    public string? AppWorkspace { get; set; }

    /// <summary>Copilot model for reviewer sessions. Empty: "auto".</summary>
    public string? Model { get; set; }
}
