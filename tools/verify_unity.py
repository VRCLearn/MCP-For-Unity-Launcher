"""Compile isolated projects and verify Launcher's dependency setup in Unity."""

import argparse
import json
import os
from pathlib import Path
import re
import shutil
import subprocess


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--unity-executable", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True, help="New validation directory")
    parser.add_argument("--mcp-package", type=Path, required=True, help="Local MCP for Unity 10.3+ package directory")
    args = parser.parse_args()
    if not args.unity_executable.is_file():
        parser.error("--unity-executable must be an existing Unity executable")
    manifest = args.mcp_package / "package.json"
    if not manifest.is_file():
        parser.error("--mcp-package must contain package.json")
    metadata = json.loads(manifest.read_text(encoding="utf-8-sig"))
    version = re.fullmatch(r"10\.(\d+)\.(\d+)", metadata.get("version", ""))
    if metadata.get("name") != "com.coplaydev.unity-mcp" or not version or int(version[1]) < 3:
        parser.error("--mcp-package must be a stable MCP for Unity version >=10.3.0 and <11.0.0")
    output = args.output.resolve()
    if output.exists():
        parser.error("--output must be a new directory; existing projects are never modified")
    unity_version = subprocess.run([str(args.unity_executable.resolve()), "-version"],
                                   capture_output=True, text=True, check=True).stdout.strip()
    if not re.fullmatch(r"\d+\.\d+\.\d+[abcfpx]\d+.*", unity_version):
        parser.error("Unity -version did not return a recognizable editor version")
    output.mkdir(parents=True)
    repository = Path(__file__).resolve().parents[1]
    launcher = repository / "Packages/com.vrclearn.mcp-for-unity-launcher"
    fixture = repository / "tests/unity/LauncherCompilationProbe.cs"
    ignore = shutil.ignore_patterns(".git", "__pycache__", "__pycache__.meta", "*.pyc", "*.pyc.meta", "*.pyo", "*.pyo.meta")
    for scenario, expected in (("missing", "0"), ("present", "1")):
        project = output / scenario
        (project / "Assets/Editor").mkdir(parents=True)
        (project / "Packages").mkdir()
        (project / "ProjectSettings").mkdir()
        (project / "ProjectSettings/ProjectVersion.txt").write_text(f"m_EditorVersion: {unity_version}\n", encoding="utf-8")
        (project / "Packages/manifest.json").write_text('{"dependencies":{}}\n', encoding="utf-8")
        shutil.copytree(launcher, project / "Packages" / launcher.name, ignore=ignore)
        shutil.copy2(fixture, project / "Assets/Editor/LauncherCompilationProbe.cs")
        if expected == "1":
            shutil.copytree(args.mcp_package, project / "Packages/com.coplaydev.unity-mcp", ignore=ignore)
        log = project / "unity.log"
        environment = dict(os.environ, MCP_LAUNCHER_EXPECT_INTEGRATION=expected)
        command = [str(args.unity_executable.resolve()), "-batchmode", "-nographics", "-projectPath", str(project),
                   "-executeMethod", "LauncherCompilationProbe.Run", "-logFile", str(log)]
        print(f"Verifying {scenario}: {log}", flush=True)
        result = subprocess.run(command, env=environment, check=False)
        text = log.read_text(encoding="utf-8", errors="replace") if log.exists() else ""
        if result.returncode or "LAUNCHER_VERIFICATION: passed" not in text:
            raise RuntimeError(f"{scenario} failed (exit {result.returncode}); inspect {log}")
        print(f"{scenario}: passed", flush=True)


if __name__ == "__main__":
    main()
