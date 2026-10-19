"""Install local NuGet packages in real SDK consumers; verify a good build, an enforced bad build, and the packaged tool."""
import argparse
import json
import os
import pathlib
import shutil
import subprocess
import tempfile

parser = argparse.ArgumentParser()
parser.add_argument("packages")
parser.add_argument("--version", required=True)
args = parser.parse_args()
feed = pathlib.Path(args.packages).resolve()
dotnet = os.environ.get("DOTNET_HOST_PATH") or shutil.which("dotnet")
if not dotnet:
    raise SystemExit("dotnet must be installed and available on PATH or DOTNET_HOST_PATH")

def run(arguments, directory, expected=0):
    process = subprocess.run([str(dotnet), *arguments], cwd=directory, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    if process.returncode != expected:
        raise SystemExit(f"Command returned {process.returncode}, expected {expected}: {arguments}\n{process.stdout}")
    return process.stdout

with tempfile.TemporaryDirectory(prefix="safesharp-consumer-") as temporary:
    root = pathlib.Path(temporary)
    (root / "NuGet.Config").write_text(
        '<configuration><packageSources><clear/><add key="local" value="' + str(feed).replace("&", "&amp;").replace('"', "&quot;") +
        '"/><add key="nuget" value="https://api.nuget.org/v3/index.json"/></packageSources></configuration>', encoding="utf-8")
    (root / "Consumer.csproj").write_text(f'''<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Library</OutputType><Nullable>enable</Nullable><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup>
  <ItemGroup>
    <PackageReference Include="SafeSharp.Contracts" Version="{args.version}" />
    <PackageReference Include="SafeSharp.Analyzers" Version="{args.version}" PrivateAssets="all" />
  </ItemGroup>
</Project>''', encoding="utf-8")
    (root / "Consumer.cs").write_text('''using System.IO;
public static class Consumer { public static void Good() { using var stream = new MemoryStream(); stream.WriteByte(1); } }
''', encoding="utf-8")
    good = run(["build", "Consumer.csproj", "--nologo", "-v", "minimal"], root)
    if "AD0001" in good or "CS8032" in good:
        raise SystemExit("Analyzer failed to load in the package consumer\n" + good)
    (root / "Consumer.cs").write_text('''using System.IO;
public static class Consumer { public static void Bad() { var stream = new MemoryStream(); stream.Dispose(); stream.WriteByte(1); } }
''', encoding="utf-8")
    bad = run(["build", "Consumer.csproj", "--no-restore", "--nologo", "-v", "minimal"], root, expected=1)
    if "error SS0001" not in bad or "AD0001" in bad or "CS8032" in bad:
        raise SystemExit("Bad consumer did not fail with the ownership diagnostic\n" + bad)
    run(["tool", "install", "SafeSharp.Tool", "--version", args.version, "--tool-path", str(root / "tools"), "--add-source", str(feed)], root)
    executable = root / "tools" / ("safesharp.exe" if os.name == "nt" else "safesharp")
    report = root / "report.json"
    process = subprocess.run([str(executable), "--source", str(root / "Consumer.cs"), "--format", "json", "--output", str(report), "--fail-on", "warning"],
                             cwd=root, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    if process.returncode != 1 or not report.exists():
        raise SystemExit("Packaged tool did not analyze and fail the unsafe consumer\n" + process.stdout)
    payload = json.loads(report.read_text(encoding="utf-8"))
    if "SS0001" not in json.dumps(payload):
        raise SystemExit("Packaged tool report lost the analyzer finding")
    print("NuGet consumer: safe build passed; unsafe build rejected with SS0001; installed CLI produced a failing ownership report.")
