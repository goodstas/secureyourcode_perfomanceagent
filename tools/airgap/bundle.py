#!/usr/bin/env python3
"""Collect everything the air-gapped setup needs, on a machine that has internet access (H8).

Run this on a connected machine, then carry the bundle folder into the air-gapped environment and run
`python tools/setup.py --mode airgapped --bundle <folder>` there. The bundle contains:

  nuget/        every .nupkg the solution and the tools restore (a flat folder usable as a NuGet source)
  copilot-cli/  the Copilot runtime archives the GitHub.Copilot.SDK build downloads, in the release layout the SDK
                expects (v<version>/github-copilot-<version>-<platform>.tgz + SHA256SUMS.txt), for the chosen platforms.
                The SDK verifies each archive against SHA256SUMS.txt at build time, exactly as it does online.
  python/       graphifyy[mcp] and its dependencies as wheels/sdists for the target Python and platform
                (skip with --skip-python when the air-gapped side has an internal Python package index)
  MANIFEST.json versions and SHA-256 of every bundled file

Not bundled (install from your organization's sources): the .NET 10 SDK, Python 3.10+, git. Node.js/npm are not
needed in air-gapped mode (the standalone Copilot CLI is only used for the GitHub sign-in, which ApiKey mode does not use).

Usage:
  python tools/airgap/bundle.py <bundle-folder> [--platforms win32-x64,linux-x64] [--python-version 3.12]
                                [--python-platform win_amd64] [--graphify-version 0.9.71] [--skip-python]
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import platform
import re
import shutil
import subprocess
import sys
import tempfile
import urllib.request
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent.parent
sys.path.insert(0, str(REPO / "tools"))
from setup import GRAPHIFY_VERSION, SDK_VERSION, SDK_VERSIONS  # noqa: E402  (single source of truth for the pins)

COPILOT_CLI_VERSION = SDK_VERSIONS[SDK_VERSION]  # replaced by --sdk-version in main()

HUSKY_VERSION = "0.9.1"  # must equal GitHookInstaller.HuskyVersion (the host installs it into the demo repo at startup)
COPILOT_RELEASE_BASE = "https://github.com/github/copilot-cli/releases/download"
COPILOT_PLATFORMS = ["win32-x64", "win32-arm64", "linux-x64", "linux-arm64", "linuxmusl-x64", "linuxmusl-arm64", "darwin-x64", "darwin-arm64"]
# Every project that restores packages. The demo project and the benchmark templates reference no packages.
PROJECTS = [
    REPO / "SecureYourCode.slnx",
    REPO / "tools" / "copilot-smoke",
    REPO / "tools" / "h1-probe",
]


def run(cmd: list[str], **kwargs) -> None:
    print("  $ " + " ".join(str(c) for c in cmd), flush=True)
    subprocess.run([str(c) for c in cmd], check=True, **kwargs)


def sha256(path: Path) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def current_platform() -> str:
    system = {"Windows": "win32", "Linux": "linux", "Darwin": "darwin"}[platform.system()]
    arch = "arm64" if platform.machine().lower() in ("arm64", "aarch64") else "x64"
    return f"{system}-{arch}"


def bundle_nuget(target: Path, sdk_version: str) -> list[Path]:
    print(f"\n== NuGet packages (GitHub.Copilot.SDK {sdk_version})", flush=True)
    target.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="syc-bundle-") as tmp:
        env = dict(os.environ, DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_NOLOGO="1", NUGET_PACKAGES=tmp, CopilotSdkVersion=sdk_version)
        for project in PROJECTS:
            # A fresh packages folder makes NuGet fetch every package, so nothing is missed because it was cached.
            run(["dotnet", "restore", project, "--packages", tmp, "--force"], env=env)
        # Husky.Net (the demo repo's hook tool), fetched into the same folder through a throwaway tool-path install.
        run(["dotnet", "tool", "install", "husky", "--version", HUSKY_VERSION, "--tool-path", str(Path(tmp) / "husky-tool")], env=env)
        copied = []
        for nupkg in sorted(Path(tmp).rglob("*.nupkg")):
            destination = target / nupkg.name
            shutil.copy2(nupkg, destination)
            copied.append(destination)
    print(f"  {len(copied)} packages")
    return copied


def bundle_copilot_cli(target: Path, platforms: list[str]) -> list[Path]:
    print(f"\n== Copilot runtime {COPILOT_CLI_VERSION} for {', '.join(platforms)}", flush=True)
    release = target / f"v{COPILOT_CLI_VERSION}"
    release.mkdir(parents=True, exist_ok=True)
    base = f"{COPILOT_RELEASE_BASE}/v{COPILOT_CLI_VERSION}"
    files = []
    sums = release / "SHA256SUMS.txt"
    download(f"{base}/SHA256SUMS.txt", sums)
    files.append(sums)
    expected = {}
    for line in sums.read_text(encoding="utf-8").splitlines():
        match = re.match(r"^([0-9a-fA-F]{64})[\t ]+\*?(\S+)\s*$", line)
        if match:
            expected[match.group(2)] = match.group(1).lower()
    for plat in platforms:
        name = f"github-copilot-{COPILOT_CLI_VERSION}-{plat}.tgz"
        if name not in expected:
            raise SystemExit(f"SHA256SUMS.txt has no entry for {name}; valid platforms: {', '.join(COPILOT_PLATFORMS)}")
        archive = release / name
        download(f"{base}/{name}", archive)
        actual = sha256(archive)
        if actual != expected[name]:
            raise SystemExit(f"checksum mismatch for {name}: expected {expected[name]}, got {actual}")
        print(f"  {name}: sha256 verified")
        files.append(archive)
    return files


def download(url: str, destination: Path) -> None:
    if destination.exists():
        print(f"  {destination.name}: already present")
        return
    print(f"  GET {url}", flush=True)
    with urllib.request.urlopen(url, timeout=600) as response, open(destination, "wb") as out:
        shutil.copyfileobj(response, out)


def bundle_python(target: Path, version: str, python_version: str | None, python_platform: str | None) -> list[Path]:
    print(f"\n== graphifyy[mcp] {version} wheels", flush=True)
    target.mkdir(parents=True, exist_ok=True)
    cmd = [sys.executable, "-m", "pip", "download", "--disable-pip-version-check", "-d", target, f"graphifyy[mcp]=={version}"]
    if python_version or python_platform:
        # Cross-platform download: pip then requires binary-only resolution.
        cmd += ["--only-binary=:all:"]
        if python_version:
            cmd += ["--python-version", python_version]
        if python_platform:
            cmd += ["--platform", python_platform]
    run(cmd)
    return sorted(p for p in target.iterdir() if p.suffix in (".whl", ".gz", ".zip"))


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("bundle", type=Path, help="output folder (created if missing)")
    parser.add_argument("--platforms", default=None,
                        help=f"comma-separated Copilot runtime platforms (default: this machine's, plus win32-x64); any of {', '.join(COPILOT_PLATFORMS)}")
    parser.add_argument("--graphify-version", default=GRAPHIFY_VERSION, help=f"graphifyy version to bundle (default {GRAPHIFY_VERSION})")
    parser.add_argument("--python-version", help="target Python version for the wheels, e.g. 3.12 (default: this interpreter)")
    parser.add_argument("--python-platform", help="target wheel platform, e.g. win_amd64 or manylinux2014_x86_64 (default: this machine)")
    parser.add_argument("--skip-python", action="store_true", help="do not bundle Python packages (internal index available)")
    parser.add_argument("--sdk-version", choices=sorted(SDK_VERSIONS), default=SDK_VERSION,
                        help=f"GitHub.Copilot.SDK version to bundle for (default {SDK_VERSION}); sets the Copilot runtime version too")
    args = parser.parse_args()
    global COPILOT_CLI_VERSION
    COPILOT_CLI_VERSION = SDK_VERSIONS[args.sdk_version]

    platforms = [p.strip() for p in args.platforms.split(",")] if args.platforms else sorted({current_platform(), "win32-x64"})
    for plat in platforms:
        if plat not in COPILOT_PLATFORMS:
            parser.error(f"unknown platform '{plat}'; valid: {', '.join(COPILOT_PLATFORMS)}")

    bundle = args.bundle.resolve()
    bundle.mkdir(parents=True, exist_ok=True)
    files = []
    files += bundle_nuget(bundle / "nuget", args.sdk_version)
    files += bundle_copilot_cli(bundle / "copilot-cli", platforms)
    if not args.skip_python:
        files += bundle_python(bundle / "python", args.graphify_version, args.python_version, args.python_platform)

    manifest = {
        "copilotSdkVersion": args.sdk_version,
        "copilotCliVersion": COPILOT_CLI_VERSION,
        "copilotPlatforms": platforms,
        "graphifyVersion": None if args.skip_python else args.graphify_version,
        "files": {str(f.relative_to(bundle)).replace(os.sep, "/"): sha256(f) for f in files},
    }
    (bundle / "MANIFEST.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    print(f"\nBundle written to {bundle} ({len(files)} files). On the air-gapped machine:")
    print(f"  python tools/setup.py --mode airgapped --bundle <copied folder>")


if __name__ == "__main__":
    main()
