"""Install local NuGet packages in real SDK consumers; verify a good build, an enforced bad build, and the packaged tool."""
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
required_packages = ("SafeSharp.Contracts", "SafeSharp.Analyzers", "SafeSharp.CodeFixes", "SafeSharp.Tool")
for package in required_packages:
    if not (feed / f"{package}.{args.version}.nupkg").is_file():
        raise SystemExit(f"Missing current package: {package}.{args.version}.nupkg in {feed}")

def run(arguments, directory, expected=0):
    process = subprocess.run([str(dotnet), *arguments], cwd=directory, env=environment, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
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
public static class Consumer { public static void Good() { using var stream = new MemoryStream(); stream.WriteByte(1); } }
''', encoding="utf-8")
    good = run(["build", "Consumer.csproj", "--nologo", "-v", "minimal"], root)
    if any(code in good for code in ("AD0001", "AD0002", "CS8032", "CS8034")):
        raise SystemExit("Analyzer failed to load in the package consumer\n" + good)
    assets = json.loads((root / "obj" / "project.assets.json").read_text(encoding="utf-8"))
    code_fixes = assets["libraries"][f"SafeSharp.CodeFixes/{args.version}"]
    if "analyzers/dotnet/cs/SafeSharp.CodeFixes.dll" not in code_fixes["files"]:
        raise SystemExit("Packaged code fixes did not reach the consumer's analyzer assets")
    (root / "Consumer.cs").write_text('''using System.IO;
public static class Consumer { public static void Bad() { var stream = new MemoryStream(); stream.Dispose(); stream.WriteByte(1); } }
''', encoding="utf-8")
    bad = run(["build", "Consumer.csproj", "--no-restore", "--nologo", "-v", "minimal"], root, expected=1)
    if "error SS0001" not in bad or any(code in bad for code in ("AD0001", "AD0002", "CS8032", "CS8034")):
        raise SystemExit("Bad consumer did not fail with the ownership diagnostic\n" + bad)
    run(["tool", "install", "SafeSharp.Tool", "--version", args.version, "--tool-path", str(root / "tools"),
         "--configfile", str(root / "NuGet.Config"), "--no-cache"], root)
    executable = root / "tools" / ("safesharp.exe" if os.name == "nt" else "safesharp")
    report = root / "report.json"
    process = subprocess.run([str(executable), "--source", str(root / "Consumer.cs"), "--format", "json", "--output", str(report), "--fail-on", "warning"],
                             cwd=root, env=environment, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    if process.returncode != 1 or not report.exists():
        raise SystemExit("Packaged tool did not analyze and fail the unsafe consumer\n" + process.stdout)
    payload = json.loads(report.read_text(encoding="utf-8"))
    if not payload.get("analysisComplete") or not any(diagnostic["id"] == "SS0001" for diagnostic in payload["diagnostics"]):
        raise SystemExit("Packaged tool report lost the analyzer finding")
    print("NuGet consumer: fresh package cache; analyzers and code fixes loaded; safe build passed; unsafe build rejected with SS0001; installed CLI produced a complete failing ownership report.")
