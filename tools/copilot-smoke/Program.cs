// Per-developer model-backend check. Usage: dotnet run --project tools/copilot-smoke -- auth | chat [--appsettings <file>]
//   auth: start the SDK's bundled runtime and report runtime version and, in Copilot mode, sign-in and quota. No model call.
//   chat: additionally send one short prompt in a session with no tools, where any tool call would be denied.
// Reads the same configuration as the host: the SecureYourCode section of appsettings.json (default:
// src/SecureYourCode.Agent/appsettings.json, override with --appsettings or SECUREYOURCODE_APPSETTINGS) plus
// SecureYourCode__* environment variables, so it checks exactly the mode the host will run in (H8):
//   Copilot mode: the isolated client mode (CopilotClientMode.Empty) reads credentials only from <StateRoot>/copilot.
//   ApiKey mode:  no sign-in; the session carries the configured endpoint and key, and `chat` proves the round trip.
// Exit codes: 0 ok, 2 not signed in (Copilot mode), 3 configuration error, 1 other failure.
using System.Text.Json;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Microsoft.Extensions.Configuration;
using SecureYourCode.Agent.Infrastructure;
using SecureYourCode.Agent.Orchestration;

var mode = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? "auth";
var appsettingsIndex = Array.IndexOf(args, "--appsettings");
var appsettings = appsettingsIndex >= 0 && appsettingsIndex + 1 < args.Length
    ? args[appsettingsIndex + 1]
    : Environment.GetEnvironmentVariable("SECUREYOURCODE_APPSETTINGS") ?? FindDefaultAppsettings();

SecureYourCodeOptions options;
StatePaths paths;
LlmSettings llm;
try
{
    var configuration = new ConfigurationBuilder()
        .AddJsonFile(appsettings, optional: false)
        .AddEnvironmentVariables()
        .Build();
    options = configuration.GetSection(SecureYourCodeOptions.SectionName).Get<SecureYourCodeOptions>() ?? new SecureYourCodeOptions();
    paths = StatePaths.Resolve(options);
    llm = LlmSettings.Resolve(options);
}
catch (Exception ex)
{
    Console.WriteLine($"configuration error ({appsettings}): {ex.Message}");
    return 3;
}

Console.WriteLine($"appsettings: {appsettings}");
Console.WriteLine($"llm backend: {llm.Description}" + (llm.Provider is null ? "" : $" (key from {llm.Provider.KeySource})"));
var neutralDir = Path.Combine(paths.StateRoot, "probe-empty");
Directory.CreateDirectory(neutralDir);

using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
await using var client = new CopilotClient(new CopilotClientOptions
{
    Mode = CopilotClientMode.Empty,
    BaseDirectory = paths.CopilotDirectory,
    WorkingDirectory = neutralDir,
});

await client.StartAsync(cts.Token);
var status = await client.GetStatusAsync(cts.Token);
Console.WriteLine($"runtime: {status.Version} (protocol {status.ProtocolVersion})");

if (llm.Mode == LlmMode.Copilot)
{
    var auth = await client.GetAuthStatusAsync(cts.Token);
    if (auth.IsAuthenticated != true)
    {
        Console.WriteLine($"signed in: no ({auth.StatusMessage})");
        return 2;
    }
    Console.WriteLine($"signed in: yes ({auth.AuthType} on {auth.Host})");

    try
    {
        var quota = await client.Rpc.Account.GetQuotaAsync(null!, null!, cts.Token);
        Console.WriteLine("quota: " + JsonSerializer.Serialize(quota));
    }
    catch (Exception ex)
    {
        Console.WriteLine($"quota: unavailable ({ex.Message})");
    }

    var models = await client.ListModelsAsync(cts.Token);
    Console.WriteLine("models: " + string.Join(", ", models.Select(m => m.Id)));
}
else
{
    // ApiKey mode: sign-in is neither required nor checked; the endpoint decides. Reported for information only.
    var auth = await client.GetAuthStatusAsync(cts.Token);
    Console.WriteLine($"github sign-in (not required in ApiKey mode): {(auth.IsAuthenticated == true ? "present" : "absent")}");
}

if (mode != "chat") return 0;

var toolRequests = 0;
await using var session = await client.CreateSessionAsync(new SessionConfig
{
    Model = llm.Mode == LlmMode.ApiKey ? llm.Provider!.ModelId : llm.CopilotModel,
    Provider = llm.Mode == LlmMode.ApiKey ? CopilotReviewerClient.ToProviderConfig(llm.Provider!) : null,
    WorkingDirectory = neutralDir,
    AvailableTools = [], // Empty mode requires an explicit allow-list; this check needs no tools.
    OnPermissionRequest = (request, invocation) =>
    {
        Interlocked.Increment(ref toolRequests);
        return Task.FromResult(PermissionDecision.Reject("environment check: no tools allowed"));
    },
}, cts.Token);
session.On<AssistantUsageEvent>(e => Console.WriteLine(
    $"usage: model={e.Data.Model} input={e.Data.InputTokens} output={e.Data.OutputTokens} " +
    $"cost={e.Data.Cost}"));
session.On<SessionErrorEvent>(e => Console.WriteLine($"session error: {e.Data.Message}"));

try
{
    var reply = await session.SendAndWaitAsync(
        new MessageOptions { Prompt = "Reply with exactly the word OK and nothing else. Do not use any tools." },
        cancellationToken: cts.Token);
    Console.WriteLine($"reply: {reply?.Data.Content}");
    Console.WriteLine($"denied tool requests: {toolRequests}");
    return reply?.Data.Content?.Trim() == "OK" ? 0 : 1;
}
catch (Exception ex) when (ex is not OperationCanceledException)
{
    // ApiKey mode: a wrong key or URL surfaces here as a session error naming the endpoint and the HTTP status.
    Console.WriteLine($"model call failed: {ex.Message}");
    return 1;
}

static string FindDefaultAppsettings()
{
    for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
    {
        var candidate = Path.Combine(directory.FullName, "src", "SecureYourCode.Agent", "appsettings.json");
        if (File.Exists(Path.Combine(directory.FullName, "SecureYourCode.slnx")) && File.Exists(candidate))
        {
            return candidate;
        }
    }

    return Path.Combine(AppContext.BaseDirectory, "appsettings.json");
}
