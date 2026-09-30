namespace SecureYourCode.Agent.Infrastructure;

/// <summary>Resolved AppWorkspace and StateRoot, and the well-known locations under them (plan §2).</summary>
public sealed class StatePaths
{
    public const string StateRootEnvironmentVariable = "SECUREYOURCODE_STATE_ROOT";
    public const string NuGetSourceEnvironmentVariable = "SECUREYOURCODE_NUGET_SOURCE";
    private const string SolutionFileName = "SecureYourCode.slnx";

    private StatePaths(string appWorkspace, string stateRoot, string? graphifyPython, string? graphifyCli, string? nuGetSource)
    {
        AppWorkspace = appWorkspace;
        StateRoot = stateRoot;
        NuGetSource = nuGetSource;
        GraphifyPython = graphifyPython ?? (OperatingSystem.IsWindows()
            ? Path.Combine(stateRoot, "graphify-venv", "Scripts", "python.exe")
            : Path.Combine(stateRoot, "graphify-venv", "bin", "python"));
        GraphifyCli = graphifyCli ?? (OperatingSystem.IsWindows()
            ? Path.Combine(stateRoot, "graphify-venv", "Scripts", "graphify.exe")
            : Path.Combine(stateRoot, "graphify-venv", "bin", "graphify"));
    }

    public string AppWorkspace { get; }

    public string StateRoot { get; }

    /// <summary>The analysis target: the materialized demo repository.</summary>
    public string RepoPath => Path.Combine(StateRoot, "demo-repo");

    public string AccessTokenFile => Path.Combine(StateRoot, "access-token");

    /// <summary>The NuGet source for tool installs in the demo repo (SecureYourCode:NuGetSource or SECUREYOURCODE_NUGET_SOURCE), if any (H8).</summary>
    public string? NuGetSource { get; }

    /// <summary>A NuGet configuration naming only <see cref="NuGetSource"/>, written by the host under StateRoot when a source is configured.</summary>
    public string NuGetConfigFile => Path.Combine(StateRoot, "nuget.config");

    public string CopilotDirectory => Path.Combine(StateRoot, "copilot");

    /// <summary>The interpreter with graphifyy[mcp]: SecureYourCode:Graphify:Python, otherwise the venv under StateRoot (H8).</summary>
    public string GraphifyPython { get; }

    /// <summary>The graphify CLI: SecureYourCode:Graphify:Cli, otherwise the venv under StateRoot (H8).</summary>
    public string GraphifyCli { get; }

    /// <summary>Published graphs, temp extraction folders and current.txt (plan §4.1).</summary>
    public string GraphsDirectory => Path.Combine(StateRoot, "graphs");

    /// <summary>Per-run artifacts such as analysis.sarif (plan §4.4).</summary>
    public string RunsDirectory => Path.Combine(StateRoot, "runs");

    /// <summary>Published report folders and latest.txt (plan §4.7).</summary>
    public string ReportsDirectory => Path.Combine(StateRoot, "reports");

    /// <summary>Per-run copies of the benchmark templates (plan §4.6).</summary>
    public string VerificationDirectory => Path.Combine(StateRoot, "verification");

    /// <summary>The host-owned benchmark templates in AppWorkspace (reviewed code, never model-written).</summary>
    public string VerificationTemplatesDirectory => Path.Combine(AppWorkspace, "src", "SecureYourCode.Agent", "Verification", "Templates");

    public string DemoProject => Path.Combine(RepoPath, "DemoShop.csproj");

    public string DemoShopSource => Path.Combine(AppWorkspace, "test-assets", "demo-shop");

    public string HookTemplatesDirectory => Path.Combine(AppWorkspace, "hook-templates");

    /// <summary>The Release build of the SecureYourCode analyzer that the demo repo references (plan §4.3).</summary>
    public string AnalyzerAssembly => Path.Combine(
        AppWorkspace, "src", "SecureYourCode.PerformanceAnalyzer", "bin", "Release", "netstandard2.0", "SecureYourCode.PerformanceAnalyzer.dll");

    public static StatePaths Resolve(SecureYourCodeOptions options)
    {
        var appWorkspace = string.IsNullOrWhiteSpace(options.AppWorkspace)
            ? FindAppWorkspace()
            : RequireAbsolute(options.AppWorkspace, "SecureYourCode:AppWorkspace");

        var configuredStateRoot = !string.IsNullOrWhiteSpace(options.StateRoot)
            ? options.StateRoot
            : Environment.GetEnvironmentVariable(StateRootEnvironmentVariable);
        var stateRoot = string.IsNullOrWhiteSpace(configuredStateRoot)
            ? Path.Combine(UserHome(), ".secureyourcode")
            : RequireAbsolute(configuredStateRoot, "StateRoot");

        if (IsSameOrInside(stateRoot, appWorkspace) || IsSameOrInside(appWorkspace, stateRoot))
        {
            throw new InvalidOperationException(
                $"StateRoot '{stateRoot}' and AppWorkspace '{appWorkspace}' must not contain each other.");
        }

        var graphify = options.Graphify ?? new GraphifyOptions();
        var graphifyPython = string.IsNullOrWhiteSpace(graphify.Python) ? null : RequireAbsolute(graphify.Python, "SecureYourCode:Graphify:Python");
        var graphifyCli = string.IsNullOrWhiteSpace(graphify.Cli) ? null : RequireAbsolute(graphify.Cli, "SecureYourCode:Graphify:Cli");
        if ((graphifyPython is null) != (graphifyCli is null))
        {
            throw new InvalidOperationException("SecureYourCode:Graphify:Python and SecureYourCode:Graphify:Cli must be set together (both, or neither).");
        }

        var configuredNuGetSource = !string.IsNullOrWhiteSpace(options.NuGetSource)
            ? options.NuGetSource
            : Environment.GetEnvironmentVariable(NuGetSourceEnvironmentVariable);
        var nuGetSource = string.IsNullOrWhiteSpace(configuredNuGetSource) ? null : configuredNuGetSource.Trim();
        if (nuGetSource is not null && !Uri.TryCreate(nuGetSource, UriKind.Absolute, out _))
        {
            throw new InvalidOperationException($"SecureYourCode:NuGetSource must be an absolute folder path or a feed URL, but was '{nuGetSource}'.");
        }

        return new StatePaths(appWorkspace, stateRoot, graphifyPython, graphifyCli, nuGetSource);
    }

    private static string UserHome()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
        {
            throw new InvalidOperationException(
                $"The user home directory could not be determined; set {StateRootEnvironmentVariable} to an absolute path.");
        }

        return home;
    }

    private static string RequireAbsolute(string path, string name)
    {
        if (!Path.IsPathFullyQualified(path))
        {
            throw new InvalidOperationException($"{name} must be an absolute path, but was '{path}'.");
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static string FindAppWorkspace()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, SolutionFileName)))
            {
                return Path.TrimEndingDirectorySeparator(directory.FullName);
            }
        }

        throw new InvalidOperationException(
            $"{SolutionFileName} was not found above '{AppContext.BaseDirectory}'; set SecureYourCode:AppWorkspace.");
    }

    private static bool IsSameOrInside(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative == "."
            || (relative != ".."
                && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !Path.IsPathRooted(relative));
    }
}
