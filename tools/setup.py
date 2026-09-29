#!/usr/bin/env python3
"""Per-developer environment setup for SecureYourCode. Safe to re-run.

Every developer runs this on their own machine. It:
  1. checks prerequisites (.NET 10 SDK, git, Python >= 3.10, Node.js/npm);
  2. creates the per-user StateRoot (default ~/.secureyourcode, override with SECUREYOURCODE_STATE_ROOT);
  3. installs the pinned Graphify (with MCP extra) into a virtual environment under StateRoot;
  4. installs the pinned standalone Copilot CLI under StateRoot (used only for `copilot login`);
  5. builds and tests the solution (the build downloads the Copilot runtime pinned by the SDK);
  6. checks this developer's Copilot sign-in through the SDK (tools/copilot-smoke).

Nothing is installed globally, and nothing machine- or account-specific is written to the repository.
Each developer signs in with their own GitHub account; credentials stay in the OS credential store.

Usage:
  python tools/setup.py                 # set up and check sign-in (no model call)
  python tools/setup.py --login         # also run the interactive Copilot sign-in
  python tools/setup.py --check-model   # also send one short prompt (uses a tiny amount of Copilot quota)
"""
from __future__ import annotations

import argparse
import os
import shutil
import subprocess
import sys
from pathlib import Path

# Pinned versions. Keep in sync with docs/architecture.md.
GRAPHIFY_VERSION = "0.9.71"
COPILOT_CLI_VERSION = "1.0.89"  # must equal CopilotCliVersion bundled with GitHub.Copilot.SDK 1.0.15
DOTNET_MAJOR = "10"
MIN_PYTHON = (3, 10)

REPO = Path(__file__).resolve().parent.parent
IS_WINDOWS = os.name == "nt"


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


def check_prerequisites() -> None:
    step("Prerequisites")
    if sys.version_info < MIN_PYTHON:
        fail(f"Python {MIN_PYTHON[0]}.{MIN_PYTHON[1]}+ is required; this is {sys.version.split()[0]}.")
    print(f"  python {sys.version.split()[0]} ({sys.executable})")

    sdks = output([tool("dotnet"), "--list-sdks"]).splitlines()
    if not any(line.startswith(DOTNET_MAJOR + ".") for line in sdks):
        fail(f".NET {DOTNET_MAJOR} SDK not found. Installed SDKs: {sdks or 'none'}")
    print(f"  dotnet SDKs: {', '.join(line.split()[0] for line in sdks)}")

    print(f"  {output([tool('git'), '--version'])}")
    print(f"  node {output([tool('node'), '--version'])}, npm {output([tool('npm'), '--version'])}")


def setup_graphify(root: Path) -> None:
    step(f"Graphify {GRAPHIFY_VERSION} (venv under StateRoot)")
    venv = root / "graphify-venv"
    python = venv / ("Scripts/python.exe" if IS_WINDOWS else "bin/python")
    if not python.exists():
        check([sys.executable, "-m", "venv", venv])
    check([python, "-m", "pip", "install", "--disable-pip-version-check", "-q", f"graphifyy[mcp]=={GRAPHIFY_VERSION}"])
    check([python, "-c", "import graphify.serve, importlib.metadata as m; print('  graphifyy', m.version('graphifyy'))"])


def copilot_cli(root: Path) -> Path:
    return root / "copilot-cli" / "node_modules" / ".bin" / ("copilot.cmd" if IS_WINDOWS else "copilot")


def setup_copilot_cli(root: Path) -> None:
    step(f"Copilot CLI {COPILOT_CLI_VERSION} (under StateRoot, for sign-in only)")
    cli = copilot_cli(root)
    if not cli.exists() or COPILOT_CLI_VERSION not in output([cli, "--version"]):
        check([tool("npm"), "install", "--prefix", root / "copilot-cli", "--no-fund", "--no-audit",
               f"@github/copilot@{COPILOT_CLI_VERSION}"])
    print(f"  {output([cli, '--version']).splitlines()[0]}")


def build_and_test() -> None:
    step("Build and test the solution")
    dotnet = tool("dotnet")
    check([dotnet, "build", REPO / "SecureYourCode.slnx"])
    check([dotnet, "test", REPO / "SecureYourCode.slnx", "--no-build"])
    check([dotnet, "build", REPO / "tools" / "copilot-smoke"])


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


def check_copilot(root: Path, send_prompt: bool) -> bool:
    step("Copilot check through the SDK")
    mode = "chat" if send_prompt else "auth"
    result = run([tool("dotnet"), "run", "--no-build", "--project", REPO / "tools" / "copilot-smoke", "--", mode])
    if result.returncode == 2:
        print("\n  Not signed in. Sign in with your own GitHub account (needs a Copilot plan), either:")
        print("    python tools/setup.py --login")
        print(f"  or directly:\n    {login_hint(root)}")
        return False
    if result.returncode != 0:
        fail("Copilot check failed; see output above.")
    return True


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--login", action="store_true", help="run the interactive Copilot sign-in")
    parser.add_argument("--check-model", action="store_true", help="send one short prompt to verify model access")
    args = parser.parse_args()

    root = state_root()
    print(f"Repository: {REPO}\nStateRoot:  {root}")
    check_prerequisites()
    root.mkdir(parents=True, exist_ok=True)
    setup_graphify(root)
    setup_copilot_cli(root)
    build_and_test()
    if args.login:
        copilot_login(root)
    signed_in = check_copilot(root, args.check_model)

    step("Summary")
    print("  Environment ready." if signed_in else "  Environment ready except Copilot sign-in (see above).")


if __name__ == "__main__":
    main()
