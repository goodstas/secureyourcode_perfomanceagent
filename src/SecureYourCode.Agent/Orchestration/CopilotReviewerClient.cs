using GitHub.Copilot;
using SecureYourCode.Agent.Graph;
using SecureYourCode.Agent.Infrastructure;
using SecureYourCode.Agent.Reporting;

namespace SecureYourCode.Agent.Orchestration;

public sealed class CopilotReviewerClientFactory(StatePaths paths, LlmSettings llm, PromptLibrary prompts) : IReviewerClientFactory
{
    public IReviewerClient Create() => new CopilotReviewerClient(paths, llm, prompts);
}

/// <summary>
/// The final configuration proven in H1: isolated (Empty) client mode with its own COPILOT_HOME, session working directory
/// RepoPath, the three Graphify tool-name lists, explicit isolation options, a host-written environment context with the
/// custom-instructions section removed, one explicitly selected custom agent, and the strict permission handler.
/// In ApiKey mode (H8) the same runtime and session configuration are used, but every session carries a whole-session
/// BYOK provider (the configured endpoint and key), and no GitHub sign-in is required or checked.
/// </summary>
public sealed class CopilotReviewerClient(StatePaths paths, LlmSettings llm, PromptLibrary prompts) : IReviewerClient
{
    private static readonly string[] ReadOnlyTools = ["view", "grep", "glob"];
    private CopilotClient? _client;

    private string NeutralDirectory => Path.Combine(paths.StateRoot, "copilot-workdir");

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(NeutralDirectory);
        _client = new CopilotClient(new CopilotClientOptions
        {
            Mode = CopilotClientMode.Empty,
            BaseDirectory = paths.CopilotDirectory,
            WorkingDirectory = NeutralDirectory,
        });
        await _client.StartAsync(cancellationToken);
        if (llm.Mode == LlmMode.Copilot)
        {
            var auth = await _client.GetAuthStatusAsync(cancellationToken);
            if (auth.IsAuthenticated != true)
            {
                throw new InvalidOperationException(
                    "Copilot is not signed in for the isolated client; run 'python tools/setup.py --login' (see README.md).");
            }
        }
    }

    /// <summary>The SDK provider configuration for ApiKey mode: the endpoint, the key, and the model names (H8).</summary>
    public static ProviderConfig ToProviderConfig(ResolvedProvider provider) => new()
    {
        Type = provider.Type,
        BaseUrl = provider.BaseUrl,
        WireApi = provider.WireApi,
        WireModel = provider.WireModel,
        ModelId = provider.ModelId,
        ApiKey = provider.UseBearerToken ? null : provider.ApiKey,
        BearerToken = provider.UseBearerToken ? provider.ApiKey : null,
        Headers = provider.Headers is null ? null : new Dictionary<string, string>(provider.Headers),
        Azure = provider.AzureApiVersion is null ? null : new AzureOptions { ApiVersion = provider.AzureApiVersion },
        MaxPromptTokens = provider.MaxPromptTokens,
        MaxOutputTokens = provider.MaxOutputTokens,
    };

    public async Task<IReviewerSession> CreateSessionAsync(ReviewerDefinition reviewer, PublishedGraph? graph, CancellationToken cancellationToken)
    {
        var client = _client ?? throw new InvalidOperationException("The reviewer client is not started.");
        var graphTools = graph is null ? [] : StrictPermissionHandler.GraphifyServerToolNames;
        var handler = new StrictPermissionHandler(paths.RepoPath);
        var session = new CopilotReviewerSession(handler);
        var config = new SessionConfig
        {
            Model = llm.Mode == LlmMode.ApiKey ? llm.Provider!.ModelId : llm.CopilotModel,
            Provider = llm.Mode == LlmMode.ApiKey ? ToProviderConfig(llm.Provider!) : null,
            WorkingDirectory = paths.RepoPath,
            AvailableTools = [.. ReadOnlyTools, .. graphTools.Select(t => $"mcp:{StrictPermissionHandler.GraphifyServerKey}-{t}")],
            McpServers = graph is null ? null : new Dictionary<string, McpServerConfig>
            {
                [StrictPermissionHandler.GraphifyServerKey] = new McpStdioServerConfig
                {
                    Command = paths.GraphifyPython,
                    Args = ["-m", "graphify.serve", graph.GraphJsonPath],
                    Tools = [.. graphTools],
                    WorkingDirectory = NeutralDirectory,
                },
            },
            CustomAgents =
            [
                new CustomAgentConfig
                {
                    Name = reviewer.Name,
                    Description = reviewer.Description,
                    Tools = [.. ReadOnlyTools, .. graphTools.Select(t => $"{StrictPermissionHandler.GraphifyServerKey}-{t}")],
                    Prompt = prompts.Load(reviewer),
                    Infer = false,
                },
            ],
            Agent = reviewer.Name,
            OnPermissionRequest = handler.HandleAsync,
            SkipCustomInstructions = true,
            EnableOnDemandInstructionDiscovery = false,
            EnableConfigDiscovery = false,
            CustomAgentsLocalOnly = true,
            EnableSkills = false,
            EnableFileHooks = false,
            EnableHostGitOperations = false,
            EnableSessionStore = false,
            Memory = new MemoryConfiguration { Enabled = false },
            SystemMessage = new SystemMessageConfig
            {
                Mode = SystemMessageMode.Customize,
                Sections = new Dictionary<SystemMessageSection, SectionOverride>
                {
                    [SystemMessageSection.EnvironmentContext] = new()
                    {
                        Action = SectionOverrideAction.Replace,
                        Content = $"The repository under review is {paths.RepoPath} (the current working directory). " +
                            "Always pass absolute paths inside this directory to the view tool.",
                    },
                    [SystemMessageSection.CustomInstructions] = new() { Action = SectionOverrideAction.Remove },
                },
            },
        };

        session.Attach(await client.CreateSessionAsync(config, cancellationToken));
        return session;
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is null)
        {
            return;
        }

        try
        {
            await _client.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (Exception)
        {
            await _client.ForceStopAsync();
        }
    }
}

public sealed class CopilotReviewerSession(StrictPermissionHandler handler) : IReviewerSession
{
    private static readonly TimeSpan SendTimeout = TimeSpan.FromMinutes(10);
    private readonly List<UsageRecord> _usage = [];
    private readonly Dictionary<string, ToolExecutionStartData> _starts = [];
    private CopilotSession? _session;
    private int _graphifyCalls;

    public IReadOnlyList<UsageRecord> Usage
    {
        get
        {
            lock (_usage)
            {
                return [.. _usage];
            }
        }
    }

    public int GraphifyCalls => Volatile.Read(ref _graphifyCalls);

    public IReadOnlyList<string> DeniedToolRequests => handler.Denials;

    public void Attach(CopilotSession session)
    {
        _session = session;
        session.On<SessionEvent>(e =>
        {
            switch (e)
            {
                case AssistantUsageEvent usage:
                    lock (_usage)
                    {
                        _usage.Add(new UsageRecord(usage.Data.Model, usage.Data.InputTokens, usage.Data.OutputTokens, usage.Data.Cost));
                    }

                    break;
                case ToolExecutionStartEvent start:
                    lock (_starts)
                    {
                        _starts[start.Data.ToolCallId] = start.Data;
                    }

                    break;
                case ToolExecutionCompleteEvent complete when complete.Data.Success:
                    lock (_starts)
                    {
                        if (_starts.TryGetValue(complete.Data.ToolCallId, out var started)
                            && started.McpServerName == StrictPermissionHandler.GraphifyServerKey)
                        {
                            Interlocked.Increment(ref _graphifyCalls);
                        }
                    }

                    break;
            }
        });
    }

    public async Task<string> SendAsync(string prompt, CancellationToken cancellationToken)
    {
        var session = _session ?? throw new InvalidOperationException("The session is not attached.");
        var message = await session.SendAndWaitAsync(new MessageOptions { Prompt = prompt }, SendTimeout, cancellationToken);
        return message?.Data.Content ?? "";
    }

    public async ValueTask DisposeAsync()
    {
        if (_session is not null)
        {
            await _session.DisposeAsync();
        }
    }
}
