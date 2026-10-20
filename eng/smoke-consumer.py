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
parser.add_argument("--dependency-feed", type=pathlib.Path,
                    help="Optional local feed for third-party dependencies; SafeSharp packages still come only from the supplied release feed.")
args = parser.parse_args()
feed = pathlib.Path(args.packages).resolve()
dependency_source = "https://api.nuget.org/v3/index.json"
if args.dependency_feed is not None:
    dependency_feed = args.dependency_feed.resolve()
    if not dependency_feed.is_dir():
        raise SystemExit(f"Dependency feed does not exist: {dependency_feed}")
    dependency_source = str(dependency_feed)
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
        '"/><add key="dependencies" value="' + escape(dependency_source, {'"': '&quot;'}) + '"/></packageSources>'
        '<fallbackPackageFolders><clear/></fallbackPackageFolders><packageSourceMapping>'
        '<packageSource key="local"><package pattern="SafeSharp.*"/></packageSource>'
        '<packageSource key="dependencies"><package pattern="*"/></packageSource>'
        '</packageSourceMapping></configuration>', encoding="utf-8")
    (root / "Consumer.csproj").write_text(f'''<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Library</OutputType><Nullable>enable</Nullable><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup>
  <ItemGroup>
    <PackageReference Include="SafeSharp.Contracts" Version="{args.version}" />
    <PackageReference Include="SafeSharp.Analyzers" Version="{args.version}" PrivateAssets="all" />
    <PackageReference Include="SafeSharp.CodeFixes" Version="{args.version}" PrivateAssets="all" />
  </ItemGroup>
</Project>''', encoding="utf-8")
    (root / "Consumer.cs").write_text('''using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SafeSharp;
[return: Lifetime("a"), Borrowed]
public delegate MemoryStream StreamSelector([Borrowed, Lifetime("a")] MemoryStream first, [Borrowed, Lifetime("b")] MemoryStream second);
[return: TypeLifetime("^a", "return")]
public delegate Func<object> TiedFactory([Lifetime("a")] object value);
public static class Consumer {
    public static void Good() { using var stream = new MemoryStream(); var view = First(stream); _ = view.Length; }
    [return: Lifetime("a")] private static MemoryStream First([Lifetime("a")] MemoryStream stream) => stream;
    public static async Task GoodAsync() { var stream = new MemoryStream(); var completion = stream.DisposeAsync(); await completion.ConfigureAwait(false); }
    private static Task<MemoryStream> OpenAsync() => Task.FromResult(new MemoryStream());
    public static async Task GoodResult() { var pending = OpenAsync(); var configured = pending.ConfigureAwait(false); using var stream = await configured; _ = stream.Length; }
    [return: Lifetime("x"), Borrowed] private static MemoryStream SelectFirst([Borrowed, Lifetime("x")] MemoryStream first, [Borrowed, Lifetime("y")] MemoryStream second) => first;
    public static StreamSelector GoodCallback() => SelectFirst;
    [return: TypeLifetime("^x", "return")] private static Func<object> Capture([Lifetime("x")] object value) => () => value;
    public static TiedFactory GoodNestedCallback() => Capture;
    public static async Task GoodValueTask() { var pending = new ValueTask<int>(Task.FromResult(1)); var task = pending.AsTask(); _ = await task; _ = await task; }
    private static void RequireContents([TypeLifetime("item", "arg0")] List<object> values) { }
    public static void GoodCall([TypeLifetime("a", "arg0")] List<object> values) { RequireContents(values); }
    public static void GoodArrayReference() { var values = new[] { new MemoryStream() }; ref MemoryStream cell = ref values[0]; cell.Dispose(); }
}
[Send, Sync] public sealed class Snapshot { public readonly int Value; public Snapshot(int value) { Value = value; } }
public sealed class GuardedCounter {
    private readonly object gate = new object();
    [GuardedBy(nameof(gate))] private int value;
    public void Good() { lock (gate) value++; }
}
public sealed class GuardedAsyncCounter {
    private readonly SemaphoreSlim gate = new(1, 1);
    [GuardedBy(nameof(gate))] private int value;
    public async Task Good() { await gate.WaitAsync().ConfigureAwait(false); try { value++; await Task.Yield(); value++; } finally { gate.Release(); } }
}
''', encoding="utf-8")
    good = run(["build", "Consumer.csproj", "--nologo", "-v", "minimal"], root)
    if any(code in good for code in ("AD0001", "AD0002", "CS8032", "CS8034")):
        raise SystemExit("Analyzer failed to load in the package consumer\n" + good)
    assets = json.loads((root / "obj" / "project.assets.json").read_text(encoding="utf-8"))
    code_fixes = assets["libraries"][f"SafeSharp.CodeFixes/{args.version}"]
    if "analyzers/dotnet/cs/SafeSharp.CodeFixes.dll" not in code_fixes["files"]:
        raise SystemExit("Packaged code fixes did not reach the consumer's analyzer assets")
    (root / "Consumer.cs").write_text('''using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SafeSharp;
[return: Lifetime("a"), Borrowed]
public delegate MemoryStream StreamSelector([Borrowed, Lifetime("a")] MemoryStream first, [Borrowed, Lifetime("b")] MemoryStream second);
[return: TypeLifetime("fresh", "return")]
public delegate Func<object> UniversalFactory([Lifetime("a")] object value);
public static class Consumer {
    public static void Bad() { var stream = new MemoryStream(); stream.Dispose(); stream.WriteByte(1); }
    public static void Pending() { var stream = new MemoryStream(); var pending = stream.DisposeAsync(); }
    [ReturnsOwned] private static Task<MemoryStream> OpenAsync() => Task.FromResult(new MemoryStream());
    public static void PendingResult() { var pending = OpenAsync(); }
    public static async Task RepeatResult() { var pending = OpenAsync(); var stream = await pending; stream.Dispose(); var repeated = await pending; _ = repeated.Length; }
    [return: Lifetime("y"), Borrowed] private static MemoryStream SelectSecond([Borrowed, Lifetime("x")] MemoryStream first, [Borrowed, Lifetime("y")] MemoryStream second) => second;
    public static StreamSelector BadCallback() => SelectSecond;
    [return: TypeLifetime("^x", "return")] private static Func<object> Capture([Lifetime("x")] object value) => () => value;
    public static UniversalFactory BadNestedCallback() => Capture;
    public static async Task RepeatValueTask() { var pending = new ValueTask<int>(Task.FromResult(1)); _ = await pending; _ = await pending; }
    private static void RequireContents([TypeLifetime("item", "arg0")] List<object> values) { }
    public static void BadCall(List<object> values) { RequireContents(values); }
    public static void BadArrayReference() { var values = new[] { new MemoryStream() }; ref MemoryStream cell = ref values[0]; cell.Dispose(); _ = values[0].Length; }
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
public sealed class GuardedCounter {
    private readonly object gate = new object();
    [GuardedBy(nameof(gate))] private int value;
    public void Bad() { value++; }
}
public sealed class GuardedAsyncCounter {
    private readonly SemaphoreSlim gate = new(1, 1);
    [GuardedBy(nameof(gate))] private int value;
    public void Bad() { value++; }
}
''', encoding="utf-8")
    bad = run(["build", "Consumer.csproj", "--no-restore", "--nologo", "-v", "minimal"], root, expected=1)
    expected_rules = {"SS0001", "SS0003", "SS0017", "SS0021", "SS0027", "SS0041", "SS0044", "SS0045", "SS0047", "SS0048", "SS0049", "SS0050"}
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
        delegates = [diagnostic for diagnostic in payload["diagnostics"] if diagnostic["id"] == "SS0047"]
        if any(not diagnostic.get("properties", {}).get("SafeSharp.RegionRequirements") or
               not diagnostic.get("properties", {}).get("SafeSharp.RegionBindings") or
               not diagnostic.get("relatedLocations") for diagnostic in delegates):
            raise SystemExit(f"Packaged tool {mode} report lost universal delegate evidence")
        if not any(diagnostic["properties"].get("SafeSharp.RegionQuantification") == "one universal delegate binder"
                   for diagnostic in delegates):
            raise SystemExit(f"Packaged tool {mode} report lost the first-order delegate check")
        nested_delegates = [diagnostic for diagnostic in delegates
                            if diagnostic["properties"].get("SafeSharp.RegionQuantification", "").startswith("nested universal delegate binders")]
        if len(nested_delegates) != 1 or any(not nested_delegates[0]["properties"].get(key) for key in
                ("SafeSharp.RegionUniverses", "SafeSharp.RegionUniversePromotions", "SafeSharp.RegionVisibilityProofs")):
            raise SystemExit(f"Packaged tool {mode} report lost the nested placeholder leak or its visibility proof")
        consumed = [diagnostic for diagnostic in payload["diagnostics"] if diagnostic["id"] == "SS0048"]
        if len(consumed) != 1 or consumed[0].get("certainty") != "Definite" or not consumed[0].get("relatedLocations"):
            raise SystemExit(f"Packaged tool {mode} report lost ValueTask capability consumption evidence")
        unguarded = [diagnostic for diagnostic in payload["diagnostics"] if diagnostic["id"] == "SS0049"]
        if len(unguarded) != 2 or {item.get("properties", {}).get("SafeSharp.SynchronizationProtocol") for item in unguarded} != {"Monitor", "SemaphoreSlim"} or \
                any(not item.get("relatedLocations") or item.get("properties", {}).get("SafeSharp.SynchronizationRequired") != "gate" or
                    "SafeSharp.SynchronizationHeld" not in item.get("properties", {}) for item in unguarded):
            raise SystemExit(f"Packaged tool {mode} report lost an unheld Monitor or semaphore guard's evidence")
        calls = [diagnostic for diagnostic in payload["diagnostics"] if diagnostic["id"] == "SS0050"]
        if len(calls) != 1 or not calls[0].get("relatedLocations") or any(not calls[0].get("properties", {}).get(key) for key in
                ("SafeSharp.CallTarget", "SafeSharp.CallReason", "SafeSharp.CallEvidence")):
            raise SystemExit(f"Packaged tool {mode} report lost the direct-call lifetime obligation's evidence")
        array_refs = [diagnostic for diagnostic in payload["diagnostics"] if diagnostic.get("method") == "Consumer.BadArrayReference()"]
        if not any(item["id"] == "SS0001" for item in array_refs) or any(item["id"] == "SS0003" for item in array_refs):
            raise SystemExit(f"Packaged tool {mode} report lost canonical array/ref disposal identity")
        pending_results = [diagnostic for diagnostic in payload["diagnostics"] if diagnostic["id"] == "SS0003" and
                           diagnostic.get("method") == "Consumer.PendingResult()"]
        if len(pending_results) != 1 or pending_results[0].get("certainty") != "Possible" or \
                "factory may produce" not in pending_results[0]["message"]:
            raise SystemExit(f"Packaged tool {mode} report lost possible async production obligations")
        repeated_methods = [method for method in payload["methods"] if method["name"] == "Consumer.RepeatResult()"]
        if len(repeated_methods) != 1:
            raise SystemExit(f"Packaged tool {mode} report lost the repeated-result method")
        # OpenAsync's completed producer and Bad's ordinary use-after-dispose must
        # not satisfy these checks on behalf of the caller's two deferred receives.
        receives = [step for block in repeated_methods[0]["blocks"] for step in block.get("steps", [])
                    if step["instruction"]["kind"] == "ReceiveAsyncResult"]
        if len(receives) != 2:
            raise SystemExit(f"Packaged tool {mode} report did not retain both repeated-result receives")
        received_ids = []
        received_states = []
        for step in sorted(receives, key=lambda item: item["instruction"]["source"]["start"]):
            resources = step["after"]["resources"]
            carriers = [resource for resource in resources if resource.get("kind") == "AsyncFactoryCarrier"]
            if len(carriers) != 1 or carriers[0].get("resultState") != "Materialized" or \
                    len(carriers[0].get("producedResourceIds", [])) != 1:
                raise SystemExit(f"Packaged tool {mode} report lost the caller's cached result identity")
            result_id = carriers[0]["producedResourceIds"][0]
            result_places = [place for place in step["after"]["places"] if place["place"] == step["instruction"]["target"]]
            if len(result_places) != 1 or {reference["resourceId"] for reference in result_places[0]["references"]} != {result_id}:
                raise SystemExit(f"Packaged tool {mode} report did not bind the received alias to the cached output")
            results = [resource for resource in resources if resource["id"] == result_id and resource["ownership"] == "Owned"]
            if len(results) != 1:
                raise SystemExit(f"Packaged tool {mode} report lost owned async output metadata")
            received_ids.append(result_id)
            received_states.append(results[0]["status"])
        if received_ids[0] != received_ids[1] or received_states != ["Alive", "Disposed"]:
            raise SystemExit(f"Packaged tool {mode} report recreated ownership after disposal of an awaited output")
        if not any(diagnostic["id"] == "SS0001" and diagnostic.get("method") == "Consumer.RepeatResult()" and
                   diagnostic.get("resourceId") == received_ids[0] and diagnostic.get("certainty") == "Definite"
                   for diagnostic in payload["diagnostics"]):
            raise SystemExit(f"Packaged tool {mode} report lost the repeated-result use-after-dispose diagnostic")

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
    print("NuGet consumer: fresh package cache; valid direct-call/nested lifetime/delegate/async/ValueTask/Monitor/semaphore contracts and canonical array references accepted; SS0001/03/17/21/27/41/44/45/47/48/49/50 enforced; call obligations, universe visibility, consumption and guard evidence retained; cached async identities, installed CLI source/project modes and packaged MSBuild-budget/EditorConfig precedence verified.")
