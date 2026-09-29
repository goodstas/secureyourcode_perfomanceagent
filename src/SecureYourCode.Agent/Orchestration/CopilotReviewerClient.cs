using GitHub.Copilot;
using SecureYourCode.Agent.Graph;
using SecureYourCode.Agent.Infrastructure;
using SecureYourCode.Agent.Reporting;

namespace SecureYourCode.Agent.Orchestration;

public sealed class CopilotReviewerClientFactory(StatePaths paths, SecureYourCodeOptions options, PromptLibrary prompts) : IReviewerClientFactory
{
    public IReviewerClient Create() => new CopilotReviewerClient(paths, options, prompts);
}

/// <summary>
/// The final configuration proven in H1: isolated (Empty) client mode with its own COPILOT_HOME, session working directory
/// RepoPath, the three Graphify tool-name lists, explicit isolation options, a host-written environment context with the
/// custom-instructions section removed, one explicitly selected custom agent, and the strict permission handler.
/// </summary>
public sealed class CopilotReviewerClient(StatePaths paths, SecureYourCodeOptions options, PromptLibrary prompts) : IReviewerClient
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
        var auth = await _client.GetAuthStatusAsync(cancellationToken);
        if (auth.IsAuthenticated != true)
        {
            throw new InvalidOperationException(
                "Copilot is not signed in for the isolated client; run 'python tools/setup.py --login' (see README.md).");
        }
    }

    public async Task<IReviewerSession> CreateSessionAsync(ReviewerDefinition reviewer, PublishedGraph? graph, CancellationToken cancellationToken)
    {
        var client = _client ?? throw new InvalidOperationException("The reviewer client is not started.");
        var graphTools = graph is null ? [] : StrictPermissionHandler.GraphifyServerToolNames;
        var handler = new StrictPermissionHandler(paths.RepoPath);
        var session = new CopilotReviewerSession(handler);
        var config = new SessionConfig
        {
            Model = string.IsNullOrWhiteSpace(options.Model) ? "auto" : options.Model,
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
