#!/usr/bin/env python3
"""Exercise the built CLI through its real process/file/MSBuild boundaries.

Build SafeSharp.Tool first, then run:
  python3 tests/cli/verify.py --tool src/SafeSharp.Tool/bin/Release/net10.0/SafeSharp.Tool.dll
Use --dotnet /path/to/dotnet when the SDK is not on PATH. All fixtures live in a
temporary directory; this never modifies source projects or package feeds.
"""

from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import subprocess
import tempfile
from xml.sax.saxutils import escape


SOURCE = """using System.IO;
using SafeSharp;
public static class OwnershipSample
{
    public static void Broken()
    {
        var resource = new MemoryStream();
        var moved = Ownership.Move(resource);
        resource.WriteByte(1);
        moved.Dispose();
        moved.WriteByte(2);
    }
    public static void Valid()
    {
        using var owner = new MemoryStream();
        var shared = Ownership.Borrow(owner);
        _ = shared.Length;
        owner.WriteByte(1);
    }
}
public class ModernCSharp
{
    public int Value { get; set => field = value; }
}
"""


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--tool", type=Path, required=True)
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("--contracts", type=Path)
    args = parser.parse_args()
    tool = args.tool.resolve(strict=True)
    contracts = (args.contracts or tool.parent / "SafeSharp.Contracts.dll").resolve(strict=True)
    command = [args.dotnet, str(tool)] if tool.suffix == ".dll" else [str(tool)]
    environment = os.environ.copy()
    dotnet_path = Path(args.dotnet)
    if dotnet_path.is_absolute():
        environment.setdefault("DOTNET_ROOT", str(dotnet_path.parent))
        environment["PATH"] = str(dotnet_path.parent) + os.pathsep + environment.get("PATH", "")

    def run(arguments: list[str], expected: int, cwd: Path) -> subprocess.CompletedProcess[str]:
        result = subprocess.run(command + arguments, cwd=cwd, env=environment,
                                text=True, capture_output=True, timeout=120)
        assert result.returncode == expected, (
            f"Unexpected exit {result.returncode}, expected {expected}: {' '.join(arguments)}\n"
            f"stdout:\n{result.stdout[-5000:]}\nstderr:\n{result.stderr[-5000:]}"
        )
        return result

    with tempfile.TemporaryDirectory(prefix="safesharp-cli-") as temporary:
        directory = Path(temporary)
        source = directory / "Ownership example.cs"
        source.write_text(SOURCE, encoding="utf-8")
        for format_name in ("json", "sarif", "dot", "mermaid", "html"):
            output = directory / ("report." + format_name)
            run(["--source", str(source), "--format", format_name, "--output", str(output),
                 "--fail-on", "none"], 0, directory)
            content = output.read_text(encoding="utf-8")
            assert content, format_name
            if format_name == "json":
                report = json.loads(content)
                ids = {item["id"] for item in report["diagnostics"]}
                assert {"SS0001", "SS0004"} <= ids, ids
                assert report["analysisComplete"] is True
                assert report["methods"] and all("blocks" in method for method in report["methods"])
                assert any(block["hasSnapshots"] for method in report["methods"] for block in method["blocks"])
            elif format_name == "sarif":
                report = json.loads(content)
                assert report["version"] == "2.1.0"
                assert any(item["ruleId"] == "SS0001" for item in report["runs"][0]["results"])
            elif format_name == "dot":
                assert content.startswith("digraph SafeSharp {") and " -> " in content
            elif format_name == "mermaid":
                assert content.startswith("flowchart TD") and " --> " in content
            else:
                assert "<svg" in content and "Live before" in content and "SS0001" in content

        baseline = ["--source", str(source), "--format", "json"]
        result = run(baseline + ["--fail-on", "warning"], 1, directory)
        assert json.loads(result.stdout)["diagnostics"]  # stdout contains only report data.
        run(baseline + ["--fail-on", "error"], 0, directory)

        invalid = directory / "Invalid.cs"
        invalid.write_text("class Invalid { void Broken( }", encoding="utf-8")
        output = directory / "existing.json"
        output.write_text("existing report must survive", encoding="utf-8")
        failed = run(["--source", str(invalid), "--format", "json", "--output", str(output),
                      "--fail-on", "none"], 2, directory)
        assert "Compilation failed" in failed.stderr
        assert output.read_text(encoding="utf-8") == "existing report must survive"
        run(["--source", str(directory / "missing.cs"), "--fail-on", "none"], 2, directory)
        run(["--project", str(source), "--source", str(source)], 2, directory)
        invalid.unlink()

        # MSBuild is responsible for references, C# language version, generated inputs,
        # and analyzer configuration. The source-mode reference pack is not reused here.
        project = directory / "Fixture.csproj"
        project.write_text(f"""<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework><LangVersion>14</LangVersion>
    <Nullable>enable</Nullable><TreatWarningsAsErrors>false</TreatWarningsAsErrors></PropertyGroup>
  <ItemGroup><Reference Include="SafeSharp.Contracts"><HintPath>{escape(str(contracts))}</HintPath></Reference></ItemGroup>
</Project>""", encoding="utf-8")
        editorconfig = directory / ".editorconfig"
        editorconfig.write_text("""root = true
[*.cs]
dotnet_diagnostic.SS0001.severity = error
dotnet_diagnostic.SS0004.severity = none
""", encoding="utf-8")
        project_output = directory / "project.json"
        run(["--project", str(project), "--format", "json", "--output", str(project_output),
             "--fail-on", "error"], 1, directory)
        report = json.loads(project_output.read_text(encoding="utf-8"))
        assert report["analysisComplete"] is True
        assert any(item["id"] == "SS0001" and item["severity"] == "error" for item in report["diagnostics"])
        assert not any(item["id"] == "SS0004" for item in report["diagnostics"])

        # Generate a real solution through the SDK, then exercise solution loading without restore.
        for arguments in (["new", "sln", "--name", "Fixture", "--format", "sln"],
                          ["sln", "Fixture.sln", "add", "Fixture.csproj"]):
            created = subprocess.run([args.dotnet] + arguments, cwd=directory, env=environment,
                                     text=True, capture_output=True, timeout=60)
            assert created.returncode == 0, created.stdout + created.stderr
        solution_output = directory / "solution.json"
        run(["--solution", str(directory / "Fixture.sln"), "--no-restore", "--format", "json",
             "--output", str(solution_output), "--fail-on", "none"], 0, directory)
        solution_report = json.loads(solution_output.read_text(encoding="utf-8"))
        assert len(solution_report["methods"]) == len(report["methods"])

        # Suppressing the budget diagnostic must not produce a false successful verification.
        editorconfig.write_text("""root = true
[*.cs]
safesharp_max_iterations = 1
dotnet_diagnostic.SS0015.severity = none
""", encoding="utf-8")
        run(["--project", str(project), "--no-restore", "--format", "json", "--output", str(project_output),
             "--fail-on", "none"], 2, directory)
        report = json.loads(project_output.read_text(encoding="utf-8"))
        assert report["analysisComplete"] is False
        assert any(method["budgetExceeded"] for method in report["methods"])

        corrupt_project = directory / "Corrupt.csproj"
        corrupt_project.write_text("not an MSBuild project", encoding="utf-8")
        run(["--project", str(corrupt_project), "--no-restore", "--fail-on", "none"], 2, directory)

    print("SafeSharp CLI integration passed: five formats, source and MSBuild project/solution inputs, "
          "C# 14, severity/suppression, threshold exits, compiler/workspace failure, "
          "atomic output preservation, and suppressed-budget incomplete status.")


if __name__ == "__main__":
    main()
