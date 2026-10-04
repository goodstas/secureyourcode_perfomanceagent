namespace SecureYourCode.Agent.Infrastructure;

/// <summary>The "SecureYourCode" configuration section (appsettings.json or SecureYourCode__* environment variables).</summary>
public sealed class SecureYourCodeOptions
{
    public const string SectionName = "SecureYourCode";

    /// <summary>Absolute path. Empty: SECUREYOURCODE_STATE_ROOT, otherwise &lt;user home&gt;/.secureyourcode.</summary>
    public string? StateRoot { get; set; }

    /// <summary>Absolute path of this repository. Empty: found by walking up from the host binaries to SecureYourCode.slnx.</summary>
    public string? AppWorkspace { get; set; }

    /// <summary>Copilot model for reviewer sessions in Copilot mode. Empty: "auto". Ignored in ApiKey mode (see Llm:Provider).</summary>
    public string? Model { get; set; }

    /// <summary>Which model backend the reviewer sessions use (H8).</summary>
    public LlmOptions Llm { get; set; } = new();

    /// <summary>
    /// NuGet source (a folder of .nupkg files or a feed URL) used for the Husky.Net tool install in the demo repo (H8,
    /// air-gapped). Empty: SECUREYOURCODE_NUGET_SOURCE, otherwise the machine's normal NuGet configuration.
    /// </summary>
    public string? NuGetSource { get; set; }

    /// <summary>Optional overrides for the Graphify interpreter and CLI (H8, air-gapped installs).</summary>
    public GraphifyOptions Graphify { get; set; } = new();

    /// <summary>
    /// Install the Husky.Net post-commit hook into the demo repo at startup (plan §4.2). Default true. False is the plan's
    /// H3 fallback for a machine whose NuGet feed cannot serve the Husky tool (H8): commits then do not trigger a graph
    /// refresh, and the graph is refreshed at startup and at /analyze time instead.
    /// </summary>
    public bool InstallGitHook { get; set; } = true;
}

/// <summary>SecureYourCode:Llm. Mode "Copilot" (GitHub sign-in, the H1 configuration) or "ApiKey" (a self-hosted, OpenAI-compatible endpoint).</summary>
public sealed class LlmOptions
{
    public const string CopilotMode = "Copilot";
    public const string ApiKeyMode = "ApiKey";

    public string? Mode { get; set; }

    public ProviderOptions Provider { get; set; } = new();
}

/// <summary>SecureYourCode:Llm:Provider, used only in ApiKey mode. The key itself never lives in appsettings.json.</summary>
public sealed class ProviderOptions
{
    public const string DefaultApiKeyEnvironmentVariable = "SECUREYOURCODE_LLM_API_KEY";

    /// <summary>Copilot SDK provider type: "openai" (default, any OpenAI-compatible server), "azure", "anthropic" or "ollama".</summary>
    public string? Type { get; set; }

    /// <summary>Base URL of the endpoint, for example http://models.internal:8000/v1.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>"chat-completions" (default) or "responses".</summary>
    public string? WireApi { get; set; }

    /// <summary>The model name sent to the endpoint (the deployment or internal model name). Required.</summary>
    public string? WireModel { get; set; }

    /// <summary>Optional well-known model id the runtime uses to pick prompting and token limits. Empty: WireModel.</summary>
    public string? ModelId { get; set; }

    /// <summary>Environment variable that holds the API key. Empty: SECUREYOURCODE_LLM_API_KEY.</summary>
    public string? ApiKeyEnvironmentVariable { get; set; }

    /// <summary>Absolute path of a file whose trimmed content is the API key (used when the environment variable is unset).</summary>
    public string? ApiKeyFile { get; set; }

    /// <summary>Send the key as a bearer token (Authorization header) instead of the provider's API-key header. Default: true for "openai".</summary>
    public bool? UseBearerToken { get; set; }

    /// <summary>Extra HTTP headers for every request to the endpoint.</summary>
    public Dictionary<string, string>? Headers { get; set; }

    /// <summary>Azure OpenAI API version (provider type "azure" only).</summary>
    public string? AzureApiVersion { get; set; }

    public int? MaxPromptTokens { get; set; }

    public int? MaxOutputTokens { get; set; }
}

/// <summary>SecureYourCode:Graphify. Empty values mean the venv under StateRoot that tools/setup.py creates.</summary>
public sealed class GraphifyOptions
{
    /// <summary>Absolute path of the Python interpreter that has graphifyy[mcp] installed.</summary>
    public string? Python { get; set; }

    /// <summary>Absolute path of the graphify CLI executable.</summary>
    public string? Cli { get; set; }
}
