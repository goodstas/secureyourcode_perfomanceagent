#!/usr/bin/env python3
"""Per-developer environment setup for SecureYourCode. Safe to re-run.

Every developer runs this on their own machine. It:
  1. checks prerequisites (.NET 10 SDK, git, Python >= 3.10; Node.js/npm in online mode only);
  2. creates the per-user StateRoot (default ~/.secureyourcode, override with SECUREYOURCODE_STATE_ROOT);
  3. installs the pinned Graphify (with MCP extra) into a virtual environment under StateRoot, or checks an
     existing installation named with --graphify-python/--graphify-cli;
  4. online mode: installs the pinned standalone Copilot CLI under StateRoot (used only for `copilot login`);
  5. builds and tests the solution (the build fetches the Copilot runtime pinned by the SDK: from GitHub online,
     from the bundle or your mirror in air-gapped mode);
  6. checks the configured model backend through the SDK (tools/copilot-smoke): the Copilot sign-in in Copilot
     mode, or the endpoint configuration in ApiKey mode.

Nothing is installed globally, and nothing machine- or account-specific is written to the repository.

Two modes (H8):
  --mode online     (default) packages come from nuget.org, PyPI, npm and GitHub; developers sign in to Copilot.
  --mode airgapped  no internet. Dependencies come from a bundle made with tools/airgap/bundle.py on a connected
                    machine (--bundle), or from your organization's mirrors (--nuget-source, --pip-index-url,
                    --copilot-cli-base-url, or --copilot-npm-registry for the @github/copilot npm package, which the
                    SDK can drive over stdio instead of its release archive). The GitHub sign-in is not used; the host
                    runs in ApiKey mode against your own endpoint (SecureYourCode:Llm in appsettings.json).

Usage:
  python tools/setup.py                                   # online: set up and check sign-in (no model call)
  python tools/setup.py --login                           # online: also run the interactive Copilot sign-in
  python tools/setup.py --check-model                     # also send one short prompt through the configured backend
  python tools/setup.py --mode airgapped --bundle <dir>   # air-gapped, everything from the bundle
  python tools/setup.py --mode airgapped --nuget-source <dir|url> --pip-index-url <url> --copilot-cli-base-url <url>
  python tools/setup.py --mode airgapped --nuget-source <url> --pip-index-url <url> --copilot-npm-registry <url>
  python tools/setup.py --mode airgapped --bundle <dir> --graphify-python <python> --graphify-cli <graphify>
"""
from __future__ import annotations

import argparse
import json
import os
import platform
import shutil
import socket
import subprocess
import sys
from pathlib import Path

# Pinned versions. Keep in sync with docs/architecture.md.
GRAPHIFY_VERSION = "0.9.71"
# Also verified against 0.9.62 (H8): same tools, arguments and output layout. Pass --graphify-version to use it.
COPILOT_CLI_VERSION = "1.0.89"  # CopilotCliVersion bundled with GitHub.Copilot.SDK 1.0.15
# Other CLI versions the SDK was verified to drive over stdio (H8). The SDK checks protocol compatibility itself at start.
COPILOT_CLI_VERIFIED_ALTERNATIVES = ("1.0.83",)
DOTNET_MAJOR = "10"
MIN_PYTHON = (3, 10)

REPO = Path(__file__).resolve().parent.parent
IS_WINDOWS = os.name == "nt"
DOTNET_ENV = {"DOTNET_CLI_TELEMETRY_OPTOUT": "1", "DOTNET_NOLOGO": "1", "DOTNET_SKIP_FIRST_TIME_EXPERIENCE": "1"}


def state_root() -> Path:
    override = os.environ.get("SECUREYOURCODE_STATE_ROOT")
    if not override:
        return Path.home() / ".secureyourcode"
    if not Path(override).is_absolute():
        fail(f"SECUREYOURCODE_STATE_ROOT must be an absolute path (the host requires this too), but was '{override}'.")
    return Path(override)


def step(title: str) -> None:
    print(f"\n== {title}", flush=True)


def fail(message: str) -> None:
    print(f"ERROR: {message}", file=sys.stderr)
    sys.exit(1)


def tool(name: str) -> str:
    path = shutil.which(name)
    if path is None:
        fail(f"'{name}' was not found on PATH. See README.md for prerequisites.")
    return path


def run(cmd: list[str], **kwargs) -> subprocess.CompletedProcess:
    print("  $ " + " ".join(str(c) for c in cmd), flush=True)
    return subprocess.run([str(c) for c in cmd], **kwargs)


def check(cmd: list[str], **kwargs) -> None:
    if run(cmd, **kwargs).returncode != 0:
        fail(f"command failed: {' '.join(str(c) for c in cmd)}")


def output(cmd: list[str]) -> str:
    return subprocess.run([str(c) for c in cmd], capture_output=True, text=True).stdout.strip()


def check_prerequisites(online: bool, needs_npm: bool = True) -> None:
    step("Prerequisites")
    if sys.version_info < MIN_PYTHON:
        fail(f"Python {MIN_PYTHON[0]}.{MIN_PYTHON[1]}+ is required; this is {sys.version.split()[0]}.")
    print(f"  python {sys.version.split()[0]} ({sys.executable})")

    sdks = output([tool("dotnet"), "--list-sdks"]).splitlines()
    if not any(line.startswith(DOTNET_MAJOR + ".") for line in sdks):
        fail(f".NET {DOTNET_MAJOR} SDK not found. Installed SDKs: {sdks or 'none'}")
    print(f"  dotnet SDKs: {', '.join(line.split()[0] for line in sdks)}")

    print(f"  {output([tool('git'), '--version'])}")
    if online or needs_npm:
        print(f"  node {output([tool('node'), '--version'])}, npm {output([tool('npm'), '--version'])}")
    else:
        print("  node/npm: not needed (Copilot runtime from a release archive)")


class Sources:
    """Where dependencies come from: the public sources (online), a bundle, or the organization's mirrors."""

    def __init__(self, args: argparse.Namespace) -> None:
        self.online = args.mode == "online"
        self.bundle: Path | None = Path(args.bundle).resolve() if args.bundle else None
        if self.bundle is not None and not (self.bundle / "MANIFEST.json").exists():
            fail(f"'{self.bundle}' is not a bundle made by tools/airgap/bundle.py (MANIFEST.json missing).")
        self.nuget_source: str | None = args.nuget_source or (str(self.bundle / "nuget") if self.bundle else None)
        self.pip_index_url: str | None = args.pip_index_url
        self.pip_find_links: Path | None = None if args.pip_index_url else (self.bundle / "python" if self.bundle else None)
        self.copilot_cli_base_url: str | None = args.copilot_cli_base_url
        self.copilot_npm_registry: str | None = args.copilot_npm_registry
        self.copilot_cli_binary: Path | None = Path(args.copilot_cli_binary).resolve() if args.copilot_cli_binary else None
        explicit_cli = bool(self.copilot_npm_registry or self.copilot_cli_binary)
        self.copilot_cli_dir: Path | None = None if (args.copilot_cli_base_url or explicit_cli) else (self.bundle / "copilot-cli" if self.bundle else None)
        if self.copilot_cli_binary is not None and not self.copilot_cli_binary.exists():
            fail(f"--copilot-cli-binary '{self.copilot_cli_binary}' does not exist.")
        if not self.online:
            missing = []
            if not self.nuget_source:
                missing.append("--nuget-source (or --bundle)")
            if not (self.pip_index_url or self.pip_find_links or (args.graphify_python and args.graphify_cli)):
                missing.append("--pip-index-url, --bundle, or --graphify-python/--graphify-cli")
            if not (self.copilot_cli_base_url or self.copilot_cli_dir or explicit_cli):
                missing.append("--copilot-cli-base-url, --copilot-npm-registry, --copilot-cli-binary, or --bundle")
            if missing:
                fail("air-gapped mode needs a source for every dependency; missing: " + "; ".join(missing))


def setup_graphify(root: Path, version: str, sources: Sources, python_override: str | None, cli_override: str | None) -> None:
    if python_override or cli_override:
        step("Graphify (existing installation)")
        if not (python_override and cli_override):
            fail("--graphify-python and --graphify-cli must be given together.")
        python, cli = Path(python_override).resolve(), Path(cli_override).resolve()
        for path in (python, cli):
            if not path.exists():
                fail(f"'{path}' does not exist.")
        installed = output([python, "-c", "import graphify.serve, importlib.metadata as m; print(m.version('graphifyy'))"])
        if not installed:
            fail(f"'{python}' cannot import graphify.serve; install graphifyy[mcp] into that interpreter.")
        print(f"  graphifyy {installed} at {python}")
        if installed != version:
            print(f"  note: pinned version is {version}; {installed} is used as installed (record it in docs/architecture.md if it differs).")
        print("  Put these paths in appsettings.json (SecureYourCode:Graphify:Python / :Cli) or set")
        print(f'    SecureYourCode__Graphify__Python="{python}"  SecureYourCode__Graphify__Cli="{cli}"')
        return

    step(f"Graphify {version} (venv under StateRoot)")
    venv = root / "graphify-venv"
    python = venv / ("Scripts/python.exe" if IS_WINDOWS else "bin/python")
    if not python.exists():
        check([sys.executable, "-m", "venv", venv])
    pip = [python, "-m", "pip", "install", "--disable-pip-version-check", "-q"]
    if sources.pip_find_links is not None:
        pip += ["--no-index", "--find-links", sources.pip_find_links]
    elif sources.pip_index_url:
        pip += ["--index-url", sources.pip_index_url]
    check(pip + [f"graphifyy[mcp]=={version}"])
    check([python, "-c", "import graphify.serve, importlib.metadata as m; print('  graphifyy', m.version('graphifyy'))"])


def copilot_cli(root: Path) -> Path:
    return root / "copilot-cli" / "node_modules" / ".bin" / ("copilot.cmd" if IS_WINDOWS else "copilot")


def copilot_platform() -> str:
    """The @github/copilot platform package suffix for this machine (also the SDK's runtime platform name)."""
    system = {"win32": "win32", "linux": "linux", "darwin": "darwin"}.get(sys.platform)
    if system is None:
        fail(f"unsupported platform for the Copilot CLI: {sys.platform}")
    machine = platform.machine().lower()
    arch = "arm64" if machine in ("arm64", "aarch64") else "x64"
    return f"{system}-{arch}"


def copilot_native_binary(root: Path) -> Path:
    """The native CLI binary inside the npm platform package, usable as the SDK's CopilotCliBinaryPath (H8)."""
    return root / "copilot-cli" / "node_modules" / "@github" / f"copilot-{copilot_platform()}" / ("copilot.exe" if IS_WINDOWS else "copilot")


def installed_cli_version(binary: Path) -> str | None:
    """The version from the npm package metadata next to the binary. `copilot --version` is not used: it was observed
    to print the newest known version (1.0.89 from a 1.0.83 binary), while the SDK reports the real one at runtime."""
    manifest = binary.parent / "package.json"
    if not manifest.exists():
        return None
    try:
        return json.loads(manifest.read_text(encoding="utf-8")).get("version")
    except (OSError, ValueError):
        return None


def setup_copilot_cli(root: Path, registry: str | None = None, purpose: str = "for sign-in only",
                      version: str = COPILOT_CLI_VERSION) -> Path:
    step(f"Copilot CLI {version} (under StateRoot, {purpose})")
    binary = copilot_native_binary(root)
    if installed_cli_version(binary) != version:
        registry_args = ["--registry", registry] if registry else []
        check([tool("npm"), "install", "--prefix", root / "copilot-cli", "--no-fund", "--no-audit", *registry_args,
               f"@github/copilot@{version}"])
    print(f"  @github/copilot {installed_cli_version(binary) or 'unknown version'} at {binary.parent}")
    return binary


def verify_cli_binary(binary: Path) -> None:
    version = installed_cli_version(binary)
    if version == COPILOT_CLI_VERSION:
        print(f"  Copilot runtime for the host: {binary} ({version}, the SDK's pinned version)")
    elif version in COPILOT_CLI_VERIFIED_ALTERNATIVES:
        print(f"  Copilot runtime for the host: {binary} ({version}; verified with the SDK, pinned is {COPILOT_CLI_VERSION})")
    else:
        print(f"  WARNING: Copilot runtime for the host: {binary} ({version or 'version unknown, no package.json next to it'}).")
        print(f"  The SDK is built for {COPILOT_CLI_VERSION} and verified with {', '.join(COPILOT_CLI_VERIFIED_ALTERNATIVES)};")
        print("  the SDK rejects an incompatible protocol at start (the backend check below shows the version it actually ran).")


class LocalMirror:
    """Serves the bundle's copilot-cli folder over loopback HTTP, so the SDK's build downloads from it (H8).

    The SDK's build targets only accept an http(s) download base (MSBuild's DownloadFile), and still verify the archive
    against the bundled SHA256SUMS.txt. An organization mirror can be used instead with --copilot-cli-base-url.
    """

    def __init__(self, directory: Path) -> None:
        self.directory = directory
        self.process: subprocess.Popen | None = None
        self.url = ""

    def __enter__(self) -> "LocalMirror":
        with socket.socket() as probe:
            probe.bind(("127.0.0.1", 0))
            port = probe.getsockname()[1]
        self.process = subprocess.Popen(
            [sys.executable, "-m", "http.server", str(port), "--bind", "127.0.0.1", "--directory", str(self.directory)],
            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        self.url = f"http://127.0.0.1:{port}"
        print(f"  serving {self.directory} at {self.url} for the Copilot runtime download")
        return self

    def __exit__(self, *exc) -> None:
        if self.process is not None:
            self.process.terminate()
            self.process.wait(timeout=10)


def build_and_test(sources: Sources, cli_binary: Path | None) -> None:
    step("Build and test the solution")
    dotnet = tool("dotnet")
    env = dict(os.environ, **DOTNET_ENV)
    if cli_binary is not None:
        # The SDK's build targets accept a pre-installed CLI binary and skip the release-archive download; the SDK then
        # drives that binary over stdio (verified in H8). MSBuild reads environment variables as properties, so the
        # same variable makes every later `dotnet build`/`dotnet run` use it too.
        env["CopilotCliBinaryPath"] = str(cli_binary)
    projects = [REPO / "SecureYourCode.slnx", REPO / "tools" / "copilot-smoke"]
    if sources.online:
        restore_args: list[str] = []
    else:
        restore_args = ["--source", sources.nuget_source]
        # The host installs Husky.Net into the demo repo at startup (and the integration tests do the same); tell it
        # where the package is. Set this permanently as well (appsettings SecureYourCode:NuGetSource, or the variable).
        env["SECUREYOURCODE_NUGET_SOURCE"] = sources.nuget_source
    for project in projects:
        check([dotnet, "restore", project, *restore_args], env=env)

    def build_all(build_env: dict[str, str]) -> None:
        # The demo repo references the Release analyzer build: the host refuses to start without it, and the host's
        # integration tests build the demo project with it. So build it before the tests.
        check([dotnet, "build", REPO / "src" / "SecureYourCode.PerformanceAnalyzer", "-c", "Release", "--no-restore"], env=build_env)
        check([dotnet, "build", REPO / "SecureYourCode.slnx", "--no-restore"], env=build_env)
        check([dotnet, "test", REPO / "SecureYourCode.slnx", "--no-build"], env=build_env)
        check([dotnet, "build", REPO / "tools" / "copilot-smoke", "--no-restore"], env=build_env)

    if sources.online or cli_binary is not None:
        build_all(env)
    elif sources.copilot_cli_base_url:
        build_all(dict(env, COPILOT_CLI_DOWNLOAD_BASE_URL=sources.copilot_cli_base_url))
    else:
        with LocalMirror(sources.copilot_cli_dir) as mirror:
            build_all(dict(env, COPILOT_CLI_DOWNLOAD_BASE_URL=mirror.url))


def copilot_login(root: Path) -> None:
    step("Copilot sign-in (interactive)")
    # The host uses the SDK's isolated client mode, which reads credentials only from COPILOT_HOME, never from the
    # system keychain. With the keychain disabled, the CLI asks to store the token in a plaintext config file there:
    # answer y. The file stays in <StateRoot>/copilot (per user, outside the repository).
    print("  When asked 'Store token in plaintext config file? (y/N)', answer y (see README.md).")
    env = dict(os.environ, COPILOT_HOME=str(root / "copilot"), COPILOT_DISABLE_KEYTAR="1")
    check([copilot_cli(root), "login"], env=env)


def login_hint(root: Path) -> str:
    cli, home = copilot_cli(root), root / "copilot"
    if IS_WINDOWS:
        return f'$env:COPILOT_HOME="{home}"; $env:COPILOT_DISABLE_KEYTAR="1"; & "{cli}" login'
    return f'COPILOT_HOME="{home}" COPILOT_DISABLE_KEYTAR=1 "{cli}" login'


def check_backend(root: Path, send_prompt: bool, sources: Sources) -> bool:
    online = sources.online
    step("Model backend check through the SDK")
    mode = "chat" if send_prompt else "auth"
    env = dict(os.environ, **DOTNET_ENV)
    if sources.nuget_source and not sources.online:
        env["SECUREYOURCODE_NUGET_SOURCE"] = sources.nuget_source
    result = run([tool("dotnet"), "run", "--no-build", "--project", REPO / "tools" / "copilot-smoke", "--", mode], env=env)
    if result.returncode == 2:
        print("\n  Not signed in. Sign in with your own GitHub account (needs a Copilot plan), either:")
        print("    python tools/setup.py --login")
        print(f"  or directly:\n    {login_hint(root)}")
        if not online:
            print("  (air-gapped: switch to ApiKey mode instead; see README.md > Air-gapped setup)")
        return False
    if result.returncode == 3:
        fail("The SecureYourCode configuration is invalid; see the message above and README.md.")
    if result.returncode != 0:
        fail("Model backend check failed; see output above.")
    return True


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--mode", choices=["online", "airgapped"], default="online", help="where dependencies come from (default online)")
    parser.add_argument("--bundle", help="air-gapped: folder made by tools/airgap/bundle.py")
    parser.add_argument("--nuget-source", help="air-gapped: NuGet source (folder or feed URL) instead of the bundle's")
    parser.add_argument("--pip-index-url", help="air-gapped: internal Python package index instead of the bundle's wheels")
    parser.add_argument("--copilot-cli-base-url", help="air-gapped: http(s) mirror of github/copilot-cli releases "
                        "(serves v<version>/github-copilot-<version>-<platform>.tgz and SHA256SUMS.txt)")
    parser.add_argument("--copilot-npm-registry", help="air-gapped: npm registry (for example Artifactory's npm remote) to install "
                        f"@github/copilot@{COPILOT_CLI_VERSION} from; its native binary becomes the host's Copilot runtime")
    parser.add_argument("--copilot-cli-binary", help="air-gapped: an already installed Copilot CLI native binary "
                        "(node_modules/@github/copilot-<platform>/copilot[.exe]) to use as the host's runtime")
    parser.add_argument("--copilot-cli-version", default=COPILOT_CLI_VERSION, help="with --copilot-npm-registry: the @github/copilot "
                        f"version to install (default {COPILOT_CLI_VERSION}; also verified: {', '.join(COPILOT_CLI_VERIFIED_ALTERNATIVES)})")
    parser.add_argument("--graphify-version", default=GRAPHIFY_VERSION, help=f"graphifyy version to install (default {GRAPHIFY_VERSION})")
    parser.add_argument("--graphify-python", help="use an existing interpreter that has graphifyy[mcp] (with --graphify-cli)")
    parser.add_argument("--graphify-cli", help="the graphify executable of that installation")
    parser.add_argument("--login", action="store_true", help="online: run the interactive Copilot sign-in")
    parser.add_argument("--check-model", action="store_true", help="send one short prompt through the configured backend")
    args = parser.parse_args()
    if args.login and args.mode != "online":
        parser.error("--login needs --mode online (ApiKey mode has no sign-in)")

    sources = Sources(args)
    root = state_root()
    print(f"Repository: {REPO}\nStateRoot:  {root}\nMode:       {args.mode}")
    check_prerequisites(sources.online, needs_npm=sources.copilot_npm_registry is not None)
    root.mkdir(parents=True, exist_ok=True)
    setup_graphify(root, args.graphify_version, sources, args.graphify_python, args.graphify_cli)
    cli_binary: Path | None = None
    if sources.online:
        setup_copilot_cli(root)
    elif sources.copilot_npm_registry:
        cli_binary = setup_copilot_cli(root, sources.copilot_npm_registry, purpose="the host's Copilot runtime", version=args.copilot_cli_version)
        verify_cli_binary(cli_binary)
    elif sources.copilot_cli_binary is not None:
        step("Copilot CLI (existing installation)")
        cli_binary = sources.copilot_cli_binary
        verify_cli_binary(cli_binary)
    build_and_test(sources, cli_binary)
    if args.login:
        copilot_login(root)
    ready = check_backend(root, args.check_model, sources)

    step("Summary")
    if not sources.online:
        print("  Air-gapped mode: set SecureYourCode:Llm:Mode to \"ApiKey\" with your endpoint in appsettings.json (or")
        print("  SecureYourCode__Llm__* environment variables) and put the key in SECUREYOURCODE_LLM_API_KEY. See README.md.")
        print(f"  Also set SecureYourCode:NuGetSource (or SECUREYOURCODE_NUGET_SOURCE) to {sources.nuget_source}")
        print("  so the host can install the Husky.Net hook tool into the demo repo without nuget.org.")
    if cli_binary is not None:
        print(f"  Set the environment variable CopilotCliBinaryPath permanently to {cli_binary}")
        print("  so that every later dotnet build/run uses this Copilot runtime instead of downloading the release archive.")
    print("  Environment ready." if ready else "  Environment ready except the model backend (see above).")


if __name__ == "__main__":
    main()
