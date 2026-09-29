using SecureYourCode.Agent.Graph;
using SecureYourCode.Agent.Infrastructure;
using SecureYourCode.Agent.StaticAnalysis;

// Content root = the binaries' folder, so appsettings.json (and its 127.0.0.1:9876 binding) loads from any working directory.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

var options = builder.Configuration.GetSection(SecureYourCodeOptions.SectionName).Get<SecureYourCodeOptions>()
    ?? new SecureYourCodeOptions();
var paths = StatePaths.Resolve(options);
var accessToken = LocalAccessToken.Ensure(paths.AccessTokenFile);

builder.Services.AddSingleton(options);
builder.Services.AddSingleton(paths);
builder.Services.AddSingleton(accessToken);
builder.Services.AddSingleton<DemoRepoMaterializer>();
builder.Services.AddSingleton<GitHookInstaller>();
builder.Services.AddSingleton<IGraphExtractor, GraphifyCliExtractor>();
builder.Services.AddSingleton<GraphifyUpdater>();
builder.Services.AddSingleton<GraphRefreshQueue>();
builder.Services.AddHostedService<GraphRefreshWorker>();
builder.Services.AddSingleton<StaticAnalysisRunner>();

var app = builder.Build();

app.Logger.LogInformation("OS: {OS}; hook task template: {HookTemplate}", HostPlatform.Description, HostPlatform.HookTaskRunnerTemplate);
app.Logger.LogInformation("AppWorkspace: {AppWorkspace}", paths.AppWorkspace);
app.Logger.LogInformation("StateRoot: {StateRoot}", paths.StateRoot);
var demoRepo = app.Services.GetRequiredService<DemoRepoMaterializer>();
await demoRepo.EnsureAsync(app.Lifetime.ApplicationStopping);
await demoRepo.EnsureAnalyzerReferenceAsync(app.Lifetime.ApplicationStopping);
await app.Services.GetRequiredService<GitHookInstaller>().EnsureHookInstalledAsync(app.Lifetime.ApplicationStopping);
app.Services.GetRequiredService<GraphRefreshQueue>().Request(); // startup refresh, run by the background worker

app.MapPost("/git-post-commit", (HttpRequest http, LocalAccessToken token, GraphRefreshQueue queue) =>
{
    if (!token.Matches(http.Headers[LocalAccessToken.HeaderName]))
    {
        return Results.Unauthorized();
    }

    queue.Request();
    return Results.Accepted();
});

app.Run();
