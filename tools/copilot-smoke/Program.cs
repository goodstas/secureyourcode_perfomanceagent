// Per-developer Copilot environment check. Usage: dotnet run --project tools/copilot-smoke -- auth | chat
//   auth: start the SDK's bundled runtime and report runtime version, sign-in and quota. No model call.
//   chat: additionally send one short prompt in a session where every tool call is denied.
// Exit codes: 0 ok, 2 not signed in, 1 other failure.
using System.Text.Json;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;

var mode = args.FirstOrDefault() ?? "auth";
var overrideRoot = Environment.GetEnvironmentVariable("SECUREYOURCODE_STATE_ROOT");
var stateRoot = string.IsNullOrWhiteSpace(overrideRoot)
    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".secureyourcode")
    : Path.GetFullPath(overrideRoot);
var neutralDir = Path.Combine(stateRoot, "probe-empty");
Directory.CreateDirectory(neutralDir);

using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
await using var client = new CopilotClient(new CopilotClientOptions
{
    BaseDirectory = Path.Combine(stateRoot, "copilot"),
    WorkingDirectory = neutralDir,
});

await client.StartAsync(cts.Token);
var status = await client.GetStatusAsync(cts.Token);
Console.WriteLine($"runtime: {status.Version} (protocol {status.ProtocolVersion})");

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
if (mode != "chat") return 0;

var toolRequests = 0;
await using var session = await client.CreateSessionAsync(new SessionConfig
{
    WorkingDirectory = neutralDir,
    OnPermissionRequest = (request, invocation) =>
    {
        Interlocked.Increment(ref toolRequests);
        return Task.FromResult(PermissionDecision.Reject("environment check: no tools allowed"));
    },
}, cts.Token);
session.On<AssistantUsageEvent>(e => Console.WriteLine(
    $"usage: model={e.Data.Model} input={e.Data.InputTokens} output={e.Data.OutputTokens} " +
    $"cost={e.Data.Cost}"));

var reply = await session.SendAndWaitAsync(
    new MessageOptions { Prompt = "Reply with exactly the word OK and nothing else. Do not use any tools." },
    cancellationToken: cts.Token);
Console.WriteLine($"reply: {reply?.Data.Content}");
Console.WriteLine($"denied tool requests: {toolRequests}");
return reply?.Data.Content?.Trim() == "OK" ? 0 : 1;
