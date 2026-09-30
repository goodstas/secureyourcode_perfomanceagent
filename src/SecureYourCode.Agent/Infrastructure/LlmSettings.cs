namespace SecureYourCode.Agent.Infrastructure;

public enum LlmMode
{
    /// <summary>GitHub Copilot with the developer's own sign-in (the H1 configuration).</summary>
    Copilot,

    /// <summary>A self-hosted, OpenAI-compatible endpoint reached with an API key; no GitHub sign-in (H8, air-gapped).</summary>
    ApiKey,
}

/// <summary>
/// The resolved model backend for reviewer sessions (H8). Validated once at startup so that a misconfigured mode fails
/// before any analysis runs, and so that the two modes never fall back to each other silently.
/// </summary>
public sealed class LlmSettings
{
    private LlmSettings(LlmMode mode, string copilotModel, ResolvedProvider? provider)
    {
        Mode = mode;
        CopilotModel = copilotModel;
        Provider = provider;
    }

    public LlmMode Mode { get; }

    /// <summary>Copilot mode: the session model ("auto" by default).</summary>
    public string CopilotModel { get; }

    /// <summary>ApiKey mode: the endpoint and key. Null in Copilot mode.</summary>
    public ResolvedProvider? Provider { get; }

    /// <summary>What the report shows: the mode, and in ApiKey mode the endpoint and model, never the key.</summary>
    public string Description => Mode == LlmMode.Copilot
        ? $"Copilot (model {CopilotModel})"
        : $"ApiKey ({Provider!.Type} endpoint {Provider.BaseUrl}, model {Provider.WireModel})";

    public static LlmSettings Resolve(SecureYourCodeOptions options) =>
        Resolve(options, Environment.GetEnvironmentVariable, File.ReadAllText);

    public static LlmSettings Resolve(
        SecureYourCodeOptions options,
        Func<string, string?> environment,
        Func<string, string> readFile)
    {
        var llm = options.Llm ?? new LlmOptions();
        var modeText = string.IsNullOrWhiteSpace(llm.Mode) ? LlmOptions.CopilotMode : llm.Mode.Trim();
        var copilotModel = string.IsNullOrWhiteSpace(options.Model) ? "auto" : options.Model.Trim();

        if (modeText.Equals(LlmOptions.CopilotMode, StringComparison.OrdinalIgnoreCase))
        {
            return new LlmSettings(LlmMode.Copilot, copilotModel, null);
        }

        if (!modeText.Equals(LlmOptions.ApiKeyMode, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"SecureYourCode:Llm:Mode must be '{LlmOptions.CopilotMode}' or '{LlmOptions.ApiKeyMode}', but was '{modeText}'.");
        }

        return new LlmSettings(LlmMode.ApiKey, copilotModel, ResolvedProvider.From(llm.Provider ?? new ProviderOptions(), environment, readFile));
    }
}

/// <summary>ApiKey mode, validated: endpoint, wire model and the key (kept in memory only; never logged or reported).</summary>
public sealed class ResolvedProvider
{
    private ResolvedProvider(
        string type, string baseUrl, string wireApi, string wireModel, string modelId, string apiKey, bool useBearerToken,
        IReadOnlyDictionary<string, string>? headers, string? azureApiVersion, int? maxPromptTokens, int? maxOutputTokens, string keySource)
    {
        Type = type;
        BaseUrl = baseUrl;
        WireApi = wireApi;
        WireModel = wireModel;
        ModelId = modelId;
        ApiKey = apiKey;
        UseBearerToken = useBearerToken;
        Headers = headers;
        AzureApiVersion = azureApiVersion;
        MaxPromptTokens = maxPromptTokens;
        MaxOutputTokens = maxOutputTokens;
        KeySource = keySource;
    }

    public string Type { get; }

    public string BaseUrl { get; }

    public string WireApi { get; }

    public string WireModel { get; }

    public string ModelId { get; }

    /// <summary>The secret. Only the reviewer client reads it, to pass it to the runtime.</summary>
    public string ApiKey { get; }

    public bool UseBearerToken { get; }

    public IReadOnlyDictionary<string, string>? Headers { get; }

    public string? AzureApiVersion { get; }

    public int? MaxPromptTokens { get; }

    public int? MaxOutputTokens { get; }

    /// <summary>Where the key came from ("environment variable X" or "file Y"), for startup logs.</summary>
    public string KeySource { get; }

    public static ResolvedProvider From(ProviderOptions provider, Func<string, string?> environment, Func<string, string> readFile)
    {
        var type = string.IsNullOrWhiteSpace(provider.Type) ? "openai" : provider.Type.Trim().ToLowerInvariant();
        if (type is not ("openai" or "azure" or "anthropic" or "ollama"))
        {
            throw new InvalidOperationException(
                $"SecureYourCode:Llm:Provider:Type must be openai, azure, anthropic or ollama, but was '{provider.Type}'.");
        }

        if (string.IsNullOrWhiteSpace(provider.BaseUrl)
            || !Uri.TryCreate(provider.BaseUrl.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException(
                "SecureYourCode:Llm:Provider:BaseUrl must be an absolute http(s) URL in ApiKey mode (for example http://models.internal:8000/v1).");
        }

        if (string.IsNullOrWhiteSpace(provider.WireModel))
        {
            throw new InvalidOperationException("SecureYourCode:Llm:Provider:WireModel (the model name the endpoint expects) is required in ApiKey mode.");
        }

        var wireApi = string.IsNullOrWhiteSpace(provider.WireApi) ? "chat-completions" : provider.WireApi.Trim().ToLowerInvariant();
        if (wireApi is not ("chat-completions" or "responses"))
        {
            throw new InvalidOperationException(
                $"SecureYourCode:Llm:Provider:WireApi must be chat-completions or responses, but was '{provider.WireApi}'.");
        }

        var (apiKey, keySource) = ReadKey(provider, type, environment, readFile);
        var wireModel = provider.WireModel.Trim();
        var modelId = string.IsNullOrWhiteSpace(provider.ModelId) ? wireModel : provider.ModelId.Trim();
        var headers = provider.Headers is { Count: > 0 } ? new Dictionary<string, string>(provider.Headers, StringComparer.OrdinalIgnoreCase) : null;
        return new ResolvedProvider(
            type, uri.ToString().TrimEnd('/'), wireApi, wireModel, modelId, apiKey, provider.UseBearerToken ?? type == "openai",
            headers, string.IsNullOrWhiteSpace(provider.AzureApiVersion) ? null : provider.AzureApiVersion.Trim(),
            provider.MaxPromptTokens, provider.MaxOutputTokens, keySource);
    }

    private static (string Key, string Source) ReadKey(
        ProviderOptions provider, string type, Func<string, string?> environment, Func<string, string> readFile)
    {
        var variable = string.IsNullOrWhiteSpace(provider.ApiKeyEnvironmentVariable)
            ? ProviderOptions.DefaultApiKeyEnvironmentVariable
            : provider.ApiKeyEnvironmentVariable.Trim();
        var fromEnvironment = environment(variable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return (fromEnvironment.Trim(), $"environment variable {variable}");
        }

        if (!string.IsNullOrWhiteSpace(provider.ApiKeyFile))
        {
            if (!Path.IsPathFullyQualified(provider.ApiKeyFile))
            {
                throw new InvalidOperationException($"SecureYourCode:Llm:Provider:ApiKeyFile must be an absolute path, but was '{provider.ApiKeyFile}'.");
            }

            string content;
            try
            {
                content = readFile(provider.ApiKeyFile);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException($"The API key file '{provider.ApiKeyFile}' could not be read: {exception.Message}", exception);
            }

            if (!string.IsNullOrWhiteSpace(content))
            {
                return (content.Trim(), $"file {provider.ApiKeyFile}");
            }

            throw new InvalidOperationException($"The API key file '{provider.ApiKeyFile}' is empty.");
        }

        if (type == "ollama")
        {
            return ("", "none (ollama)");
        }

        throw new InvalidOperationException(
            $"No API key found in ApiKey mode: set the environment variable {variable} or SecureYourCode:Llm:Provider:ApiKeyFile.");
    }
}
