using SecureYourCode.Agent.Infrastructure;
using SecureYourCode.Agent.Orchestration;
using SecureYourCode.Agent.Reporting;

namespace SecureYourCode.Agent.Tests;

/// <summary>H8: the two model backends (Copilot sign-in, or an API key against a self-hosted endpoint) and their validation.</summary>
public sealed class LlmSettingsTests
{
    private static readonly Func<string, string> NoFiles = _ => throw new FileNotFoundException();

    private static LlmSettings Resolve(SecureYourCodeOptions options, Func<string, string?>? env = null, Func<string, string>? files = null) =>
        LlmSettings.Resolve(options, env ?? (_ => null), files ?? NoFiles);

    private static SecureYourCodeOptions ApiKeyOptions(Action<ProviderOptions>? configure = null)
    {
        var options = new SecureYourCodeOptions
        {
            Llm = new LlmOptions
            {
                Mode = "ApiKey",
                Provider = new ProviderOptions { BaseUrl = "http://models.internal:8000/v1", WireModel = "internal-coder-1" },
            },
        };
        configure?.Invoke(options.Llm.Provider);
        return options;
    }

    [Fact]
    public void DefaultIsCopilotModeWithAutoModel()
    {
        var settings = Resolve(new SecureYourCodeOptions());

        Assert.Equal(LlmMode.Copilot, settings.Mode);
        Assert.Equal("auto", settings.CopilotModel);
        Assert.Null(settings.Provider);
        Assert.Equal("Copilot (model auto)", settings.Description);
    }

    [Theory]
    [InlineData("copilot")]
    [InlineData(" Copilot ")]
    public void CopilotModeIsCaseInsensitive_AndUsesTheConfiguredModel(string mode)
    {
        var settings = Resolve(new SecureYourCodeOptions { Model = "gpt-5.6", Llm = new LlmOptions { Mode = mode } });

        Assert.Equal(LlmMode.Copilot, settings.Mode);
        Assert.Equal("gpt-5.6", settings.CopilotModel);
    }

    [Fact]
    public void UnknownModeFailsFast_NeverFallsBackToCopilot()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Resolve(new SecureYourCodeOptions { Llm = new LlmOptions { Mode = "Offline" } }));

        Assert.Contains("SecureYourCode:Llm:Mode", error.Message);
        Assert.Contains("Offline", error.Message);
    }

    [Fact]
    public void ApiKeyMode_ReadsTheKeyFromTheDefaultEnvironmentVariable()
    {
        var settings = Resolve(ApiKeyOptions(), env: name => name == ProviderOptions.DefaultApiKeyEnvironmentVariable ? " sk-secret " : null);

        Assert.Equal(LlmMode.ApiKey, settings.Mode);
        var provider = Assert.IsType<ResolvedProvider>(settings.Provider);
        Assert.Equal("openai", provider.Type);
        Assert.Equal("http://models.internal:8000/v1", provider.BaseUrl);
        Assert.Equal("chat-completions", provider.WireApi);
        Assert.Equal("internal-coder-1", provider.WireModel);
        Assert.Equal("internal-coder-1", provider.ModelId);
        Assert.Equal("sk-secret", provider.ApiKey);
        Assert.True(provider.UseBearerToken);
        Assert.Equal($"environment variable {ProviderOptions.DefaultApiKeyEnvironmentVariable}", provider.KeySource);
    }

    [Fact]
    public void ApiKeyMode_DescriptionNeverContainsTheKey()
    {
        var settings = Resolve(ApiKeyOptions(), env: _ => "sk-secret");

        Assert.Equal("ApiKey (openai endpoint http://models.internal:8000/v1, model internal-coder-1)", settings.Description);
        Assert.DoesNotContain("sk-secret", settings.Description);
        Assert.DoesNotContain("sk-secret", settings.Provider!.KeySource);
    }

    [Fact]
    public void ApiKeyMode_CustomVariableNameAndModelId()
    {
        var settings = Resolve(
            ApiKeyOptions(p =>
            {
                p.ApiKeyEnvironmentVariable = "MY_KEY";
                p.ModelId = "gpt-4o";
                p.WireApi = "Responses";
                p.Type = "OpenAI";
            }),
            env: name => name == "MY_KEY" ? "k" : null);

        Assert.Equal("gpt-4o", settings.Provider!.ModelId);
        Assert.Equal("responses", settings.Provider.WireApi);
        Assert.Equal("openai", settings.Provider.Type);
        Assert.Equal("environment variable MY_KEY", settings.Provider.KeySource);
    }

    [Fact]
    public void ApiKeyMode_FallsBackToTheKeyFile_WhenTheVariableIsUnset()
    {
        var file = Path.Combine(Path.GetTempPath(), "syc-key-" + Guid.NewGuid().ToString("N"));
        var settings = Resolve(ApiKeyOptions(p => p.ApiKeyFile = file), files: path => path == file ? "from-file\n" : throw new FileNotFoundException());

        Assert.Equal("from-file", settings.Provider!.ApiKey);
        Assert.Equal($"file {file}", settings.Provider.KeySource);
    }

    [Fact]
    public void ApiKeyMode_EnvironmentVariableWinsOverTheFile()
    {
        var settings = Resolve(ApiKeyOptions(p => p.ApiKeyFile = "/keys/llm"), env: _ => "from-env", files: _ => "from-file");

        Assert.Equal("from-env", settings.Provider!.ApiKey);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("models.internal/v1")]
    [InlineData("ftp://models.internal/v1")]
    public void ApiKeyMode_RequiresAnAbsoluteHttpBaseUrl(string? baseUrl)
    {
        var error = Assert.Throws<InvalidOperationException>(() => Resolve(ApiKeyOptions(p => p.BaseUrl = baseUrl), env: _ => "k"));

        Assert.Contains("SecureYourCode:Llm:Provider:BaseUrl", error.Message);
    }

    [Fact]
    public void ApiKeyMode_RequiresAWireModel()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Resolve(ApiKeyOptions(p => p.WireModel = " "), env: _ => "k"));

        Assert.Contains("WireModel", error.Message);
    }

    [Fact]
    public void ApiKeyMode_RequiresAKey_AndNamesBothSources()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Resolve(ApiKeyOptions()));

        Assert.Contains(ProviderOptions.DefaultApiKeyEnvironmentVariable, error.Message);
        Assert.Contains("ApiKeyFile", error.Message);
    }

    [Fact]
    public void ApiKeyMode_EmptyOrUnreadableKeyFileFails()
    {
        Assert.Contains("empty", Assert.Throws<InvalidOperationException>(
            () => Resolve(ApiKeyOptions(p => p.ApiKeyFile = "/keys/llm"), files: _ => "  \n")).Message);
        Assert.Contains("could not be read", Assert.Throws<InvalidOperationException>(
            () => Resolve(ApiKeyOptions(p => p.ApiKeyFile = "/keys/llm"), files: _ => throw new IOException("no such file"))).Message);
        Assert.Contains("absolute", Assert.Throws<InvalidOperationException>(
            () => Resolve(ApiKeyOptions(p => p.ApiKeyFile = "relative/llm"), files: _ => "k")).Message);
    }

    [Fact]
    public void ApiKeyMode_OllamaNeedsNoKey_OtherTypesValidated()
    {
        var ollama = Resolve(ApiKeyOptions(p => p.Type = "ollama"));
        Assert.Equal("", ollama.Provider!.ApiKey);
        Assert.False(ollama.Provider.UseBearerToken);

        Assert.Contains("Type", Assert.Throws<InvalidOperationException>(() => Resolve(ApiKeyOptions(p => p.Type = "gemini"), env: _ => "k")).Message);
        Assert.Contains("WireApi", Assert.Throws<InvalidOperationException>(() => Resolve(ApiKeyOptions(p => p.WireApi = "grpc"), env: _ => "k")).Message);
    }

    [Fact]
    public void ProviderConfig_CarriesEndpointKeyAndModelNames_BearerByDefault()
    {
        var settings = Resolve(
            ApiKeyOptions(p =>
            {
                p.ModelId = "gpt-4o";
                p.Headers = new Dictionary<string, string> { ["X-Team"] = "perf" };
                p.MaxPromptTokens = 100000;
            }),
            env: _ => "sk-secret");

        var config = CopilotReviewerClient.ToProviderConfig(settings.Provider!);

        Assert.Equal("openai", config.Type);
        Assert.Equal("http://models.internal:8000/v1", config.BaseUrl);
        Assert.Equal("chat-completions", config.WireApi);
        Assert.Equal("internal-coder-1", config.WireModel);
        Assert.Equal("gpt-4o", config.ModelId);
        Assert.Equal("sk-secret", config.BearerToken);
        Assert.Null(config.ApiKey);
        Assert.Equal("perf", config.Headers!["X-Team"]);
        Assert.Equal(100000, config.MaxPromptTokens);
        Assert.Null(config.Azure);
    }

    [Fact]
    public void ProviderConfig_ApiKeyHeaderWhenBearerIsOff_AndAzureVersion()
    {
        var settings = Resolve(
            ApiKeyOptions(p =>
            {
                p.Type = "azure";
                p.AzureApiVersion = "2025-04-01-preview";
            }),
            env: _ => "sk-secret");

        var config = CopilotReviewerClient.ToProviderConfig(settings.Provider!);

        Assert.Equal("sk-secret", config.ApiKey);
        Assert.Null(config.BearerToken);
        Assert.Equal("2025-04-01-preview", config.Azure!.ApiVersion);
    }

    [Fact]
    public void TokenUsage_CostIsNotApplicableInApiKeyMode()
    {
        var usage = TokenUsageReport.From([("MemoryReviewer", [new UsageRecord("internal-coder-1", 100, 10, 0)])], costApplicable: false);

        Assert.Equal(SessionUsage.CostNotApplicable, usage.Sessions[0].Cost.Display);
        Assert.Equal(SessionUsage.CostNotApplicable, usage.Total.Cost.Display);
        Assert.Null(usage.Total.Cost.Value);
        Assert.Equal("100", usage.Total.InputTokens.Display);

        var copilot = TokenUsageReport.From([("MemoryReviewer", [new UsageRecord("m", 100, 10, 1)])]);
        Assert.Equal("1 " + SessionUsage.CostUnit, copilot.Total.Cost.Display);
    }

    [Fact]
    public void GraphifyOverrides_MustBeAbsoluteAndSetTogether()
    {
        var root = Path.Combine(Path.GetTempPath(), "syc-llm-" + Guid.NewGuid().ToString("N")[..8]);
        var python = Path.Combine(root, "tools", "python");
        var cli = Path.Combine(root, "tools", "graphify");

        var paths = StatePaths.Resolve(new SecureYourCodeOptions { StateRoot = root, Graphify = new GraphifyOptions { Python = python, Cli = cli } });
        Assert.Equal(python, paths.GraphifyPython);
        Assert.Equal(cli, paths.GraphifyCli);

        var defaults = StatePaths.Resolve(new SecureYourCodeOptions { StateRoot = root });
        Assert.StartsWith(Path.Combine(root, "graphify-venv"), defaults.GraphifyPython);
        Assert.StartsWith(Path.Combine(root, "graphify-venv"), defaults.GraphifyCli);

        Assert.Contains("together", Assert.Throws<InvalidOperationException>(
            () => StatePaths.Resolve(new SecureYourCodeOptions { StateRoot = root, Graphify = new GraphifyOptions { Python = python } })).Message);
        Assert.Contains("absolute", Assert.Throws<InvalidOperationException>(
            () => StatePaths.Resolve(new SecureYourCodeOptions { StateRoot = root, Graphify = new GraphifyOptions { Python = "python", Cli = cli } })).Message);
    }

    [Fact]
    public void NuGetSource_OptionWinsOverEnvironment_MustBeAbsolute()
    {
        var root = Path.Combine(Path.GetTempPath(), "syc-nuget-" + Guid.NewGuid().ToString("N")[..8]);
        var bundle = Path.Combine(root, "bundle", "nuget");

        // Without the option, the environment variable decides (tools/setup.py sets it for air-gapped test runs).
        var fromEnvironment = Environment.GetEnvironmentVariable(StatePaths.NuGetSourceEnvironmentVariable);
        Assert.Equal(string.IsNullOrWhiteSpace(fromEnvironment) ? null : fromEnvironment.Trim(),
            StatePaths.Resolve(new SecureYourCodeOptions { StateRoot = root }).NuGetSource);
        var paths = StatePaths.Resolve(new SecureYourCodeOptions { StateRoot = root, NuGetSource = bundle });
        Assert.Equal(bundle, paths.NuGetSource);
        Assert.Equal(Path.Combine(root, "nuget.config"), paths.NuGetConfigFile);
        Assert.Equal("https://nuget.internal/v3/index.json",
            StatePaths.Resolve(new SecureYourCodeOptions { StateRoot = root, NuGetSource = " https://nuget.internal/v3/index.json " }).NuGetSource);

        Assert.Contains("NuGetSource", Assert.Throws<InvalidOperationException>(
            () => StatePaths.Resolve(new SecureYourCodeOptions { StateRoot = root, NuGetSource = "bundle/nuget" })).Message);
    }
}
