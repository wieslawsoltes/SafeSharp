"""Check the actual package assets consumers load, not just whether `dotnet pack` exited successfully."""
import pathlib
import sys
import xml.etree.ElementTree as ET
import zipfile

root = pathlib.Path(sys.argv[1] if len(sys.argv) > 1 else "artifacts/packages")
expected = {
    "SafeSharp.Contracts": {"lib/netstandard2.0/SafeSharp.Contracts.dll"},
    "SafeSharp.Core": {"lib/netstandard2.0/SafeSharp.Core.dll"},
    "SafeSharp.Analyzers": {"analyzers/dotnet/cs/SafeSharp.Analyzers.dll", "analyzers/dotnet/cs/SafeSharp.Core.dll", "buildTransitive/SafeSharp.Analyzers.props"},
    "SafeSharp.Reporting": {"lib/netstandard2.0/SafeSharp.Reporting.dll"},
    "SafeSharp.CodeFixes": {"analyzers/dotnet/cs/SafeSharp.CodeFixes.dll"},
    "SafeSharp.Tool": {"tools/net10.0/any/SafeSharp.Tool.dll", "tools/net10.0/any/DotnetToolSettings.xml"},
}
found = set()
for path in sorted(root.glob("*.nupkg")):
    with zipfile.ZipFile(path) as package:
        names = set(package.namelist())
        specification = next(name for name in names if name.endswith(".nuspec"))
        document = ET.fromstring(package.read(specification))
        metadata = document.find("{*}metadata")
        identity = metadata.find("{*}id").text
        if identity not in expected:
            raise SystemExit("Unexpected package " + identity)
        if identity in found:
            raise SystemExit("Multiple versions present for " + identity + "; use a clean package directory.")
        found.add(identity)
        required = expected[identity] | {"README.md", "LICENSE"}
        if missing := required - names:
            raise SystemExit(f"{identity}: missing assets {sorted(missing)}")
        if metadata.find("{*}license").attrib.get("type") != "expression":
            raise SystemExit(identity + ": SPDX license expression required")
        repository = metadata.find("{*}repository")
        if repository is None or repository.attrib.get("url") != "https://github.com/wieslawsoltes/SafeSharp":
            raise SystemExit(identity + ": missing repository metadata")
        if identity in {"SafeSharp.Analyzers", "SafeSharp.CodeFixes"}:
            if any(name.startswith("lib/") for name in names):
                raise SystemExit(identity + ": analyzer package must not add runtime references")
            if any(pathlib.PurePosixPath(name).name.startswith("Microsoft.CodeAnalysis") for name in names):
                raise SystemExit(identity + ": must use the host's Roslyn assemblies")
        print(identity + ": verified package assets and metadata")
if expected.keys() - found:
    raise SystemExit("Missing packages: " + ", ".join(sorted(expected.keys() - found)))
