namespace SecureYourCode.Agent.Orchestration;

/// <summary>A reviewer agent: its name, its prompt file under Prompts/, and (for specialists) its pillar.</summary>
public sealed record ReviewerDefinition(string Name, string PromptFile, string Description, string? Category, string? LlmRulePrefix);

public static class Reviewers
{
    public static readonly ReviewerDefinition Memory = new(
        "MemoryReviewer", "memory-reviewer.md", "Finds memory-retention risks. Read-only.", Categories.Memory, "LLM-MEM");

    public static readonly ReviewerDefinition Cpu = new(
        "CpuReviewer", "cpu-reviewer.md", "Finds CPU and call-amplification risks. Read-only.", Categories.Cpu, "LLM-CPU");

    public static readonly ReviewerDefinition Concurrency = new(
        "ConcurrencyReviewer", "concurrency-reviewer.md", "Finds unbounded-concurrency risks. Read-only.", Categories.Concurrency, "LLM-CONC");

    public static readonly ReviewerDefinition Critic = new(
        "VerificationReviewer", "verification-reviewer.md", "Challenges every candidate and proposes verification. Read-only.", null, null);

    /// <summary>The specialists, in the order they run (sequentially, one session each).</summary>
    public static readonly IReadOnlyList<ReviewerDefinition> Specialists = [Memory, Cpu, Concurrency];

    public static string? PillarPrefix(string category) => category switch
    {
        Categories.Memory => "LLM-MEM",
        Categories.Cpu => "LLM-CPU",
        Categories.Concurrency => "LLM-CONC",
        _ => null,
    };
}
