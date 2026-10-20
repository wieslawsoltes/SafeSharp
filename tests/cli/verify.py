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
        source.write_bytes(SOURCE.encode("utf-8"))
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
        project_text = f"""<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework><LangVersion>14</LangVersion>
    <Nullable>enable</Nullable><TreatWarningsAsErrors>false</TreatWarningsAsErrors></PropertyGroup>
  <ItemGroup><Reference Include="SafeSharp.Contracts"><HintPath>{escape(str(contracts))}</HintPath></Reference></ItemGroup>
</Project>"""
        project.write_text(project_text, encoding="utf-8")
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

        # Exercise every budget producer, including compilation-end service analysis.
        # Visibility, severity, and source suppression must never change completeness.
        services = "using SafeSharp;\n" + "\n".join(
            f"[ServiceLifetime(ServiceLifetime.{'Singleton' if index == 0 else 'Transient'})]\n"
            f"class Service{index} {{ public Service{index}(Service{index + 1} next) {{ }} }}"
            for index in range(65)
        ) + "\n[ServiceLifetime(ServiceLifetime.Transient)] class Service65 { }\n"
        budget_fixtures = [
            ("SS0015", "Ownership", "warning", "safesharp_max_iterations = 1\n", SOURCE),
            ("SS0029", "SafeSharp.Ownership", "note", "", services),
            ("SS0043", "SafeSharp.Lifetimes", "warning", "safesharp_max_region_steps = 1\n", """using SafeSharp;
class LifetimeBudget {
    [Outlives("a", "b"), Outlives("b", "c")]
    [return: Lifetime("c")]
    object Pick([Lifetime("a")] object a, [Lifetime("b")] object b,
                [Lifetime("c")] object c) => a;
}
"""),
            ("SS0046", "SafeSharp.Ownership", "note", "safesharp_max_contract_steps = 1\n", """using System;
using System.IO;
using SafeSharp;
sealed class CleanupBudget : IDisposable {
    [Owned] private readonly MemoryStream stream = new();
    public void Dispose() { stream.Dispose(); }
}
"""),
        ]

        def check_budget(rule: str, fixture: str, configuration: str, expected_severity: str | None,
                         *, properties: str = "", complete: bool = False) -> dict:
            # Keep line endings identical to the in-memory text used by the span assertions.
            source.write_bytes(fixture.encode("utf-8"))
            editorconfig.write_text("root = true\n[*.cs]\n" + configuration, encoding="utf-8")
            project.write_text(project_text.replace("</PropertyGroup>", properties + "</PropertyGroup>"), encoding="utf-8")
            run(["--project", str(project), "--no-restore", "--format", "json", "--output", str(project_output),
                 "--fail-on", "none"], 0 if complete else 2, directory)
            checked = json.loads(project_output.read_text(encoding="utf-8"))
            assert checked["analysisComplete"] is complete, (rule, configuration)
            matching = [item for item in checked["diagnostics"] if item["id"] == rule]
            assert bool(matching) == (expected_severity is not None), (rule, configuration, matching)
            assert all(item["severity"] == expected_severity for item in matching), (rule, configuration, matching)
            # Recreating a diagnostic to restore its default severity must retain
            # its formatted message and exact source/evidence, not its format template.
            assert all("{0}" not in item["message"] and item["message"] for item in matching), matching
            # MSBuild may expand Windows short paths; compare the source file itself.
            assert all(Path(item["source"]["path"]).samefile(source) and item["source"]["length"] > 0
                       for item in matching), {"expectedPath": str(source), "diagnostics": matching}
            if rule in ("SS0029", "SS0043", "SS0046"):
                expected_span = {"SS0029": "Service0", "SS0043": "Pick", "SS0046": "CleanupBudget"}[rule]
                assert all(fixture[item["source"]["start"]:item["source"]["start"] + item["source"]["length"]]
                           == expected_span for item in matching), matching
            elif matching:
                assert all(item["properties"].get("SafeSharp.Certainty") and item["evidence"] for item in matching), matching
            return checked

        budget_checks = 0
        for rule, category, default_severity, options, fixture in budget_fixtures:
            for configured, expected in ((None, default_severity), ("default", default_severity), ("warning", "warning"), ("error", "error"),
                                         ("suggestion", "note"), ("silent", None), ("none", None)):
                configuration = options + (f"dotnet_diagnostic.{rule}.severity = {configured}\n" if configured else "")
                check_budget(rule, fixture, configuration, expected)
                budget_checks += 1

            # Category and global analyzer settings are not represented by the
            # per-ID syntax-tree severity API. They need explicit presentation coverage.
            for configuration in (f"dotnet_analyzer_diagnostic.category-{category}.severity = none\n",
                                  "dotnet_analyzer_diagnostic.severity = none\n"):
                check_budget(rule, fixture, options + configuration, None)
                budget_checks += 1

            check_budget(rule, "#pragma warning disable " + rule + "\n" + fixture, options, None)
            check_budget(rule, fixture, options, None, properties=f"<NoWarn>{rule}</NoWarn>")
            check_budget(rule, fixture, options, None, properties="<WarningLevel>0</WarningLevel>")
            budget_checks += 3
            print(f"Verified {rule}: configured severity, category/global suppression, pragma, NoWarn, and warning level zero.", flush=True)

        rule, category, _, options, fixture = budget_fixtures[2]
        # Explicit rules override categories; categories override global severities.
        check_budget(rule, fixture, options + f"dotnet_analyzer_diagnostic.category-{category}.severity = none\n"
                     f"dotnet_diagnostic.{rule}.severity = error\n", "error")
        check_budget(rule, fixture, options + "dotnet_analyzer_diagnostic.severity = none\n"
                     f"dotnet_analyzer_diagnostic.category-{category}.severity = suggestion\n", "note")
        check_budget(rule, fixture, options + f"dotnet_analyzer_diagnostic.category-{category}.severity = none\n"
                     f"dotnet_diagnostic.{rule}.severity = default\n", "warning")
        check_budget(rule, fixture, options + "dotnet_analyzer_diagnostic.severity = none\n"
                     f"dotnet_analyzer_diagnostic.category-{category}.severity = default\n", "warning")
        # Roslyn accepts "refactoring" as a compatibility spelling of hidden severity.
        check_budget(rule, fixture, options + f"dotnet_analyzer_diagnostic.category-{category}.severity = ReFaCtOrInG\n", None)
        check_budget(rule, fixture, options + "dotnet_analyzer_diagnostic.severity = refactoring\n", None)
        check_budget(rule, fixture, options + f"dotnet_analyzer_diagnostic.category-{category}.severity = warning\n",
                     "error", properties="<TreatWarningsAsErrors>true</TreatWarningsAsErrors>")
        suppressed = fixture.replace("class LifetimeBudget", f"[System.Diagnostics.CodeAnalysis.SuppressMessage(\"{category}\", "
                                     f"\"{rule}\", Justification = \"CLI integration fixture\")]\nclass LifetimeBudget")
        check_budget(rule, suppressed, options, None)
        # Hiding a rule without reaching its budget is still a complete analysis.
        check_budget(rule, fixture, f"dotnet_diagnostic.{rule}.severity = none\n", None, complete=True)
        # Raising the warning level internally must not expose ordinary diagnostics
        # already promoted to errors: Roslyn gives those errors warning level zero.
        mixed = check_budget(rule, SOURCE + fixture.replace("using SafeSharp;\n", ""),
                             options + "dotnet_diagnostic.SS0001.severity = error\n", None,
                             properties="<WarningLevel>0</WarningLevel>")
        assert not any(item["id"] == "SS0001" for item in mixed["diagnostics"]), mixed["diagnostics"]
        budget_checks += 10

        # Global analyzer configuration has a separate syntax-tree provider entrypoint.
        globalconfig = directory / ".globalconfig"
        for configuration in (f"dotnet_diagnostic.{rule}.severity = none\n",
                              f"dotnet_analyzer_diagnostic.category-{category}.severity = none\n",
                              "dotnet_analyzer_diagnostic.severity = none\n"):
            globalconfig.write_text("is_global = true\n" + configuration, encoding="utf-8")
            check_budget(rule, fixture, options, None)
            budget_checks += 1
        globalconfig.unlink()
        project.write_text(project_text, encoding="utf-8")

        corrupt_project = directory / "Corrupt.csproj"
        corrupt_project.write_text("not an MSBuild project", encoding="utf-8")
        run(["--project", str(corrupt_project), "--no-restore", "--fail-on", "none"], 2, directory)

    print("SafeSharp CLI integration passed: five formats, source and MSBuild project/solution inputs, "
          "C# 14, severity/suppression, threshold exits, compiler/workspace failure, "
          f"atomic output preservation, and {budget_checks} ownership/service/lifetime/contract-budget "
          "severity, suppression, and completeness cases.")


if __name__ == "__main__":
    main()
