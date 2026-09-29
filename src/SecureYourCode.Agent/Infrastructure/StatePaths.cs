namespace SecureYourCode.Agent.Infrastructure;

/// <summary>Resolved AppWorkspace and StateRoot, and the well-known locations under them (plan §2).</summary>
public sealed class StatePaths
{
    public const string StateRootEnvironmentVariable = "SECUREYOURCODE_STATE_ROOT";
    private const string SolutionFileName = "SecureYourCode.slnx";

    private StatePaths(string appWorkspace, string stateRoot)
    {
        AppWorkspace = appWorkspace;
        StateRoot = stateRoot;
    }

    public string AppWorkspace { get; }

    public string StateRoot { get; }

    /// <summary>The analysis target: the materialized demo repository.</summary>
    public string RepoPath => Path.Combine(StateRoot, "demo-repo");

    public string AccessTokenFile => Path.Combine(StateRoot, "access-token");

    public string CopilotDirectory => Path.Combine(StateRoot, "copilot");

    public string GraphifyPython => OperatingSystem.IsWindows()
        ? Path.Combine(StateRoot, "graphify-venv", "Scripts", "python.exe")
        : Path.Combine(StateRoot, "graphify-venv", "bin", "python");

    public string GraphifyCli => OperatingSystem.IsWindows()
        ? Path.Combine(StateRoot, "graphify-venv", "Scripts", "graphify.exe")
        : Path.Combine(StateRoot, "graphify-venv", "bin", "graphify");

    /// <summary>Published graphs, temp extraction folders and current.txt (plan §4.1).</summary>
    public string GraphsDirectory => Path.Combine(StateRoot, "graphs");

    /// <summary>Per-run artifacts such as analysis.sarif (plan §4.4).</summary>
    public string RunsDirectory => Path.Combine(StateRoot, "runs");

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

        return new StatePaths(appWorkspace, stateRoot);
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
