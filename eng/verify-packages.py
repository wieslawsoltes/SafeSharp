"""Check the actual package assets consumers load, not just whether `dotnet pack` exited successfully."""
import hashlib
import pathlib
import sys
import xml.etree.ElementTree as ET
import zipfile

root = pathlib.Path(sys.argv[1] if len(sys.argv) > 1 else "artifacts/packages")
expected = {
    "SafeSharp.Contracts": {"lib/netstandard2.0/SafeSharp.Contracts.dll"},
    "SafeSharp.Core": {"lib/netstandard2.0/SafeSharp.Core.dll", "lib/net10.0/SafeSharp.Core.dll"},
    "SafeSharp.Analyzers": {"analyzers/dotnet/cs/SafeSharp.Analyzers.dll", "analyzers/dotnet/cs/SafeSharp.Core.dll", "buildTransitive/SafeSharp.Analyzers.props"},
    "SafeSharp.Reporting": {"lib/netstandard2.0/SafeSharp.Reporting.dll"},
    "SafeSharp.CodeFixes": {"analyzers/dotnet/cs/SafeSharp.CodeFixes.dll"},
    "SafeSharp.Tool": {
        "tools/net10.0/any/SafeSharp.Tool.dll", "tools/net10.0/any/DotnetToolSettings.xml",
        "tools/net10.0/any/SafeSharp.Tool.deps.json", "tools/net10.0/any/SafeSharp.Tool.runtimeconfig.json",
        "tools/net10.0/any/SafeSharp.Contracts.dll", "tools/net10.0/any/SafeSharp.Core.dll",
        "tools/net10.0/any/SafeSharp.Analyzers.dll", "tools/net10.0/any/SafeSharp.Reporting.dll",
        "tools/net10.0/any/Microsoft.Build.Locator.dll",
        "tools/net10.0/any/BuildHost-netcore/Microsoft.CodeAnalysis.Workspaces.MSBuild.BuildHost.dll",
    },
}
found = set()
versions = set()
assemblies = {}
for path in sorted(root.glob("*.nupkg")):
    with zipfile.ZipFile(path) as package:
        names = set(package.namelist())
        specifications = [name for name in names if name.endswith(".nuspec")]
        if len(specifications) != 1:
            raise SystemExit(f"{path.name}: expected exactly one NuGet specification")
        specification = specifications[0]
        document = ET.fromstring(package.read(specification))
        metadata = document.find("{*}metadata")
        if metadata is None or metadata.find("{*}id") is None or metadata.find("{*}version") is None:
            raise SystemExit(f"{path.name}: package identity and version are required")
        identity = metadata.find("{*}id").text
        version = metadata.find("{*}version").text
        versions.add(version)
        if identity not in expected:
            raise SystemExit("Unexpected package " + identity)
        if identity in found:
            raise SystemExit("Multiple versions present for " + identity + "; use a clean package directory.")
        found.add(identity)
        required = expected[identity] | {"README.md", "LICENSE"}
        if missing := required - names:
            raise SystemExit(f"{identity}: missing assets {sorted(missing)}")
        license_element = metadata.find("{*}license")
        if license_element is None or license_element.attrib.get("type") != "expression" or license_element.text != "MIT":
            raise SystemExit(identity + ": SPDX license expression required")
        repository = metadata.find("{*}repository")
        if repository is None or repository.attrib.get("url") != "https://github.com/wieslawsoltes/SafeSharp":
            raise SystemExit(identity + ": missing repository metadata")
        if identity in {"SafeSharp.Analyzers", "SafeSharp.CodeFixes"}:
            if any(name.startswith("lib/") for name in names):
                raise SystemExit(identity + ": analyzer package must not add runtime references")
            if any(pathlib.PurePosixPath(name).name.startswith("Microsoft.CodeAnalysis") for name in names):
                raise SystemExit(identity + ": must use the host's Roslyn assemblies")
            if metadata.findall(".//{*}dependency"):
                raise SystemExit(identity + ": analyzer implementation dependencies must not flow into application runtime assets")
        elif identity != "SafeSharp.Tool":
            dependencies = {dependency.attrib["id"]: dependency.attrib.get("version", "")
                            for dependency in metadata.findall(".//{*}dependency")}
            if any(name.startswith(("Microsoft.CodeAnalysis", "Microsoft.Build")) for name in dependencies):
                raise SystemExit(identity + ": reusable runtime libraries must not depend on Roslyn or MSBuild")
            if identity == "SafeSharp.Core":
                groups = {group.attrib.get("targetFramework", ""): {
                    dependency.attrib["id"]: dependency.attrib.get("version", "")
                    for dependency in group.findall("{*}dependency")}
                    for group in metadata.findall(".//{*}dependencies/{*}group")}
                if groups.get(".NETStandard2.0", {}).get("System.Collections.Immutable") != "9.0.0":
                    raise SystemExit(identity + ": .NETStandard2.0 must preserve its Immutable 9.0.0 dependency contract")
                # .NET 10 prunes framework-provided references from the nuspec.
                # An explicit native group is still required to prevent NuGet
                # from falling back to the .NET Standard dependency group.
                if "net10.0" not in groups:
                    raise SystemExit(identity + ": an explicit net10.0 dependency group is required")
                if groups["net10.0"].get("System.Collections.Immutable") not in (None, "10.0.13"):
                    raise SystemExit(identity + ": net10.0 must use platform-provided Immutable or the selected 10.0.13 package")
            if identity == "SafeSharp.Reporting" and dependencies.get("SafeSharp.Core") != version:
                raise SystemExit(identity + ": reporting must depend on the matching Core release")
        if identity == "SafeSharp.Tool":
            if not any(item.attrib.get("name") == "DotnetTool" for item in metadata.findall(".//{*}packageType")):
                raise SystemExit(identity + ": DotnetTool package type required")
            for assembly in ("Microsoft.Build.dll", "Microsoft.Build.Framework.dll", "Microsoft.Build.Tasks.Core.dll", "Microsoft.Build.Utilities.Core.dll"):
                if "tools/net10.0/any/" + assembly in names:
                    raise SystemExit(identity + ": MSBuild must resolve from the selected SDK, not the tool's runtime directory")
            settings = ET.fromstring(package.read("tools/net10.0/any/DotnetToolSettings.xml"))
            commands = settings.findall(".//Command")
            if len(commands) != 1 or commands[0].attrib.get("Name") != "safesharp" or commands[0].attrib.get("EntryPoint") != "SafeSharp.Tool.dll":
                raise SystemExit(identity + ": installed command must invoke SafeSharp.Tool.dll as safesharp")
        for name in names:
            filename = pathlib.PurePosixPath(name).name
            if filename.startswith("SafeSharp.") and filename.endswith(".dll"):
                assemblies[(identity, name)] = hashlib.sha256(package.read(name)).digest()
        symbols_path = path.with_suffix(".snupkg")
        if not symbols_path.is_file():
            raise SystemExit(identity + ": matching portable symbol package is required")
        with zipfile.ZipFile(symbols_path) as symbols:
            symbol_names = set(symbols.namelist())
            symbol_metadata = ET.fromstring(symbols.read(specification)).find("{*}metadata")
            if symbol_metadata is None or symbol_metadata.findtext("{*}id") != identity or symbol_metadata.findtext("{*}version") != version:
                raise SystemExit(identity + ": symbol package identity/version does not match")
            required_symbols = {name[:-4] + ".pdb" for name in required
                                if name.endswith(".dll") and pathlib.PurePosixPath(name).name.startswith("SafeSharp.")}
            if missing := required_symbols - symbol_names:
                raise SystemExit(f"{identity}: missing portable symbols {sorted(missing)}")
            if any(symbols.read(name)[:4] != b"BSJB" for name in required_symbols):
                raise SystemExit(identity + ": symbol package must contain portable PDB metadata")
        print(identity + ": verified package assets, metadata, and portable symbols")
if expected.keys() - found:
    raise SystemExit("Missing packages: " + ", ".join(sorted(expected.keys() - found)))
if len(versions) != 1:
    raise SystemExit("All SafeSharp packages must have the same release version: " + ", ".join(sorted(versions)))
# A bundled binary must match the library asset selected by that host's TFM.
# In particular the compiler-host Core remains netstandard2.0/Immutable 9,
# while the .NET 10 tool selects the Core asset built against Immutable 10.
bundled_assets = (
    ("SafeSharp.Analyzers", "analyzers/dotnet/cs/SafeSharp.Core.dll",
     "SafeSharp.Core", "lib/netstandard2.0/SafeSharp.Core.dll"),
    ("SafeSharp.Tool", "tools/net10.0/any/SafeSharp.Analyzers.dll",
     "SafeSharp.Analyzers", "analyzers/dotnet/cs/SafeSharp.Analyzers.dll"),
    ("SafeSharp.Tool", "tools/net10.0/any/SafeSharp.Contracts.dll",
     "SafeSharp.Contracts", "lib/netstandard2.0/SafeSharp.Contracts.dll"),
    ("SafeSharp.Tool", "tools/net10.0/any/SafeSharp.Core.dll",
     "SafeSharp.Core", "lib/net10.0/SafeSharp.Core.dll"),
    ("SafeSharp.Tool", "tools/net10.0/any/SafeSharp.Reporting.dll",
     "SafeSharp.Reporting", "lib/netstandard2.0/SafeSharp.Reporting.dll"),
)
for bundle, bundled_path, component, component_path in bundled_assets:
    if assemblies.get((bundle, bundled_path)) != assemblies.get((component, component_path)):
        raise SystemExit(f"{bundle}: bundled {bundled_path} differs from {component}'s {component_path}")
print("Package family: one release version, matching TFM-specific bundled binaries, isolated analyzer dependencies, and SDK-owned MSBuild resolution.")
