using SecureYourCode.Agent.Graph;
using SecureYourCode.Agent.Reporting;

namespace SecureYourCode.Agent.Orchestration;

/// <summary>One reviewer's Copilot session (one per specialist and one for the critic).</summary>
public interface IReviewerSession : IAsyncDisposable
{
    /// <summary>Sends a prompt and awaits the final reply.</summary>
    Task<string> SendAsync(string prompt, CancellationToken cancellationToken);

    IReadOnlyList<UsageRecord> Usage { get; }

    int GraphifyCalls { get; }

    /// <summary>Tool requests the strict permission handler denied, described as "kind target".</summary>
    IReadOnlyList<string> DeniedToolRequests { get; }
}

/// <summary>The Copilot client for one run: every session of the run is created from it.</summary>
public interface IReviewerClient : IAsyncDisposable
{
    Task StartAsync(CancellationToken cancellationToken);

    Task<IReviewerSession> CreateSessionAsync(ReviewerDefinition reviewer, PublishedGraph? graph, CancellationToken cancellationToken);
}

public interface IReviewerClientFactory
{
    IReviewerClient Create();
}

/// <summary>The agent prompts, one Markdown file per reviewer under Prompts/ next to the host binaries.</summary>
public sealed class PromptLibrary
{
    private readonly string _directory;

    public PromptLibrary(string? directory = null) => _directory = directory ?? Path.Combine(AppContext.BaseDirectory, "Prompts");

    public string Load(ReviewerDefinition reviewer) => File.ReadAllText(Path.Combine(_directory, reviewer.PromptFile));
}
