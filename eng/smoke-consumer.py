"""Exercise all six packages through SDK builds, the installed tool, and a Roslyn 4.14 MEF host."""
import argparse
import json
import os
import pathlib
import shutil
import subprocess
import tempfile
from xml.sax.saxutils import escape

parser = argparse.ArgumentParser()
parser.add_argument("packages")
parser.add_argument("--version", required=True)
args = parser.parse_args()
feed = pathlib.Path(args.packages).resolve()
dotnet = os.environ.get("DOTNET_HOST_PATH") or shutil.which("dotnet")
if not dotnet:
    raise SystemExit("dotnet must be installed and available on PATH or DOTNET_HOST_PATH")
dotnet = pathlib.Path(dotnet).resolve()
required_packages = ("SafeSharp.Contracts", "SafeSharp.Core", "SafeSharp.Analyzers", "SafeSharp.Reporting", "SafeSharp.CodeFixes", "SafeSharp.Tool")
for package in required_packages:
    if not (feed / f"{package}.{args.version}.nupkg").is_file():
        raise SystemExit(f"Missing current package: {package}.{args.version}.nupkg in {feed}")

def run(arguments, directory, expected=0):
    process = subprocess.run([str(dotnet), *arguments], cwd=directory, env=environment,
                             text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=300)
    if process.returncode != expected:
        raise SystemExit(f"Command returned {process.returncode}, expected {expected}: {arguments}\n{process.stdout}")
    return process.stdout

with tempfile.TemporaryDirectory(prefix="safesharp-consumer-") as temporary:
    root = pathlib.Path(temporary)
    # A rebuilt preview has the same identity: a fresh package folder prevents NuGet
    # from silently exercising the binary from an earlier invocation of this smoke.
    environment = os.environ.copy()
    environment["NUGET_PACKAGES"] = str(root / "packages")
    environment["NUGET_FALLBACK_PACKAGES"] = ""
    environment["DOTNET_ROOT"] = str(dotnet.parent)
    environment["DOTNET_HOST_PATH"] = str(dotnet)
    environment.setdefault("DOTNET_CLI_HOME", str(root / "cli-home"))
    environment["PATH"] = str(dotnet.parent) + os.pathsep + environment.get("PATH", "")
    (root / "NuGet.Config").write_text(
        '<configuration><packageSources><clear/><add key="local" value="' + escape(str(feed), {'"': '&quot;'}) +
        '"/><add key="nuget" value="https://api.nuget.org/v3/index.json"/></packageSources>'
        '<fallbackPackageFolders><clear/></fallbackPackageFolders><packageSourceMapping>'
        '<packageSource key="local"><package pattern="SafeSharp.*"/></packageSource>'
        '<packageSource key="nuget"><package pattern="*"/></packageSource>'
        '</packageSourceMapping></configuration>', encoding="utf-8")
    (root / "Consumer.csproj").write_text(f'''<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Library</OutputType><Nullable>enable</Nullable><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup>
  <ItemGroup>
    <PackageReference Include="SafeSharp.Contracts" Version="{args.version}" />
    <PackageReference Include="SafeSharp.Analyzers" Version="{args.version}" PrivateAssets="all" />
    <PackageReference Include="SafeSharp.CodeFixes" Version="{args.version}" PrivateAssets="all" />
  </ItemGroup>
</Project>''', encoding="utf-8")
    (root / "Consumer.cs").write_text('''using System.IO;
using System.Threading.Tasks;
using SafeSharp;
public static class Consumer {
    public static void Good() { using var stream = new MemoryStream(); var view = First(stream); _ = view.Length; }
    [return: Lifetime("a")] private static MemoryStream First([Lifetime("a")] MemoryStream stream) => stream;
    public static async Task GoodAsync() { var stream = new MemoryStream(); var completion = stream.DisposeAsync(); await completion.ConfigureAwait(false); }
}
[Send, Sync] public sealed class Snapshot { public readonly int Value; public Snapshot(int value) { Value = value; } }
''', encoding="utf-8")
    good = run(["build", "Consumer.csproj", "--nologo", "-v", "minimal"], root)
    if any(code in good for code in ("AD0001", "AD0002", "CS8032", "CS8034")):
        raise SystemExit("Analyzer failed to load in the package consumer\n" + good)
    assets = json.loads((root / "obj" / "project.assets.json").read_text(encoding="utf-8"))
    code_fixes = assets["libraries"][f"SafeSharp.CodeFixes/{args.version}"]
    if "analyzers/dotnet/cs/SafeSharp.CodeFixes.dll" not in code_fixes["files"]:
        raise SystemExit("Packaged code fixes did not reach the consumer's analyzer assets")
    (root / "Consumer.cs").write_text('''using System;
using System.IO;
using System.Threading.Tasks;
using SafeSharp;
public static class Consumer {
    public static void Bad() { var stream = new MemoryStream(); stream.Dispose(); stream.WriteByte(1); }
    public static void Pending() { var stream = new MemoryStream(); var pending = stream.DisposeAsync(); }
    public static object? Retained;
    public static void Retain([Borrowed] object value) { var alias = value; Retained = alias; }
    [return: Lifetime("a")] public static MemoryStream Wrong([Lifetime("a")] MemoryStream first, [Lifetime("b")] MemoryStream second) => second;
    public static Task Share(Context value) => Schedule(() => value.Read());
    private static Task Schedule([SharesAcrossThreads] Action callback) => Task.Run(callback);
}
[ThreadAffine] public sealed class Context { public void Read() { } }
[Send] public sealed class InvalidTransfer { private readonly Context value = new(); public void Read() => value.Read(); }
public sealed class IncompleteOwner : IDisposable {
    [Owned] private readonly MemoryStream stream = new();
    public void Dispose() { if (DateTime.UtcNow.Ticks > 0) stream.Dispose(); }
}
''', encoding="utf-8")
    bad = run(["build", "Consumer.csproj", "--no-restore", "--nologo", "-v", "minimal"], root, expected=1)
    expected_rules = {"SS0001", "SS0017", "SS0021", "SS0027", "SS0041", "SS0044", "SS0045"}
    if any("error " + rule not in bad for rule in expected_rules) or any(code in bad for code in ("AD0001", "AD0002", "CS8032", "CS8034")):
        raise SystemExit("Bad consumer did not exercise every packaged analyzer family\n" + bad)
    run(["tool", "install", "SafeSharp.Tool", "--version", args.version, "--tool-path", str(root / "tools"),
         "--configfile", str(root / "NuGet.Config"), "--no-cache"], root)
    executable = root / "tools" / ("safesharp.exe" if os.name == "nt" else "safesharp")
    for mode, inputs in (("source", ["--source", str(root / "Consumer.cs")]),
                         ("project", ["--project", str(root / "Consumer.csproj"), "--no-restore"])):
        report = root / (mode + "-report.json")
        process = subprocess.run([str(executable), *inputs, "--format", "json", "--output", str(report), "--fail-on", "warning"],
                                 cwd=root, env=environment, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=180)
        if process.returncode != 1 or not report.exists():
            raise SystemExit(f"Packaged tool did not analyze and fail the unsafe {mode} consumer\n" + process.stdout)
        payload = json.loads(report.read_text(encoding="utf-8"))
        if not payload.get("analysisComplete") or not expected_rules <= {diagnostic["id"] for diagnostic in payload["diagnostics"]}:
            raise SystemExit(f"Packaged tool {mode} report lost an analyzer finding")
        borrowed = [diagnostic for diagnostic in payload["diagnostics"] if diagnostic["id"] == "SS0027"]
        if any(diagnostic.get("certainty") != "Possible" or
               diagnostic.get("properties", {}).get("SafeSharp.BorrowedSource") != "value" or
               diagnostic.get("properties", {}).get("SafeSharp.DoesNotEscape") != "false" or
               not diagnostic.get("relatedLocations") for diagnostic in borrowed):
            raise SystemExit(f"Packaged tool {mode} report lost borrowed-escape certainty or provenance")

    # The analyzer's buildTransitive props must expose MSBuild budget defaults to
    # compiler analysis, and a file's EditorConfig setting must take precedence.
    project_file = root / "Consumer.csproj"
    original_project = project_file.read_text(encoding="utf-8")
    project_file.write_text(original_project.replace("</PropertyGroup>",
        "<SafeSharpMaxIterations>1</SafeSharpMaxIterations></PropertyGroup>"), encoding="utf-8")
    editorconfig = root / ".editorconfig"
    for override, expected_complete in (("", False), ("safesharp_max_iterations = 10000\n", True)):
        editorconfig.write_text("root = true\n[*.cs]\ndotnet_analyzer_diagnostic.severity = none\n" + override, encoding="utf-8")
        budget_report = root / "budget-report.json"
        process = subprocess.run([str(executable), "--project", str(project_file), "--no-restore", "--format", "json",
                                  "--output", str(budget_report), "--fail-on", "none"],
                                 cwd=root, env=environment, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=180)
        if process.returncode != (0 if expected_complete else 2):
            raise SystemExit("Packaged MSBuild budget configuration produced the wrong exit status\n" + process.stdout)
        payload = json.loads(budget_report.read_text(encoding="utf-8"))
        if payload.get("analysisComplete") is not expected_complete or any(item["id"] == "SS0015" for item in payload["diagnostics"]):
            raise SystemExit("Packaged budget configuration lost suppression or completeness")
        if any(method["budgetExceeded"] for method in payload["methods"]) is expected_complete:
            raise SystemExit("Packaged MSBuild defaults or EditorConfig budget precedence were not honored")
    project_file.write_text(original_project, encoding="utf-8")
    editorconfig.unlink()

    # A compiler build does not activate IDE code-fix exports. Compose the actual
    # packaged provider and apply its action in a separate host at our minimum
    # Roslyn API version. The same host consumes Core and Reporting from NuGet.
    host = root / "package-host"
    host.mkdir()
    host_sources = pathlib.Path(__file__).resolve().parent / "package-host"
    for name in ("PackageHost.csproj", "Program.cs"):
        shutil.copyfile(host_sources / name, host / name)
    analyzer_assets = assets["libraries"][f"SafeSharp.Analyzers/{args.version}"]
    analyzer_path = root / "packages" / analyzer_assets["path"] / "analyzers/dotnet/cs/SafeSharp.Analyzers.dll"
    code_fixes_path = root / "packages" / code_fixes["path"] / "analyzers/dotnet/cs/SafeSharp.CodeFixes.dll"
    run(["build", "PackageHost.csproj", "--nologo", "-v", "minimal",
         "-p:SafeSharpPackageVersion=" + args.version], host)
    host_output = run([str(host / "bin/Debug/net10.0/PackageHost.dll"), str(analyzer_path), str(code_fixes_path)], host)
    print(host_output.strip())
    print("NuGet consumer: fresh package cache; valid lifetime/async/concurrency contracts accepted; SS0001/17/21/27/41/44/45 enforced; installed CLI source/project modes and packaged MSBuild-budget/EditorConfig precedence verified.")
