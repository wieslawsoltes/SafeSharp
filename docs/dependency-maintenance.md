# Maintaining dependency and compiler-host compatibility

SafeSharp's shipping analyzers and its development hosts have different dependency requirements. Update each dependency where it runs, and retain the supported compiler-host contract when merging a dependency pull request.

| Component | Dependency contract |
| --- | --- |
| `SafeSharp.Analyzers` and `SafeSharp.CodeFixes` | Compile against Roslyn 4.14 and .NET Standard 2.0. Code fixes retain the System.Composition 9 assembly contract. |
| Core bundled beside the analyzer | Use the .NET Standard 2.0 Core asset with the System.Collections.Immutable 9 assembly contract. |
| Unit-test host and standalone tool | Exercise Roslyn 5.9 and its compatible MEF dependency family on .NET 10. These versions may advance independently of the shipping analyzer API floor. |
| `eng/package-host` | Keep the explicit Roslyn 4.14 and System.Composition 9 references. This fixture validates the minimum supported host. |

The [Roslyn team's compatibility guidance](https://github.com/dotnet/roslyn/discussions/81256) explains why an analyzer must reference a compiler API version supported by its intended hosts. A newer compiler reference can cause CS9057 or a runtime load/API failure on an older host even when the analyzer does not deliberately call a new API. A dependency update must not silently change the documented minimum host version.

## Reviewing an update

1. Bring the dependency branch up to date with `main` and inspect every changed package reference. In particular, distinguish the analyzer/code-fix compile references from the tool and test-host references.
2. Resolve the complete dependency family. Roslyn Workspaces and System.Composition are grouped in Dependabot because updating Workspaces while retaining an incompatible direct MEF reference produces a NuGet downgrade error. Keep the shipping code-fix MEF reference aligned with its older compiler-host contract.
3. Run the normal build, unit tests, CLI checks, semantic benchmark smoke checks, and all package checks. The ordinary test host exercises newer Roslyn APIs; the independent package host exercises Roslyn 4.14.
4. Inspect the exact branch-head CI result before merging. A passing test on a newer host does not establish that a compiled analyzer will load in the minimum supported host.

The two original Roslyn update pull requests proposed the same version changes. The accepted update uses Roslyn 5.9 in the test host and its required System.Composition 10.0.1 dependency while preserving the analyzer and code-fix compile references at 4.14/9. The tool already uses Roslyn 5.9. Keeping the minimum fixture on 4.14 means both supported API generations remain exercised.

## Checks against the shipped assemblies

The package host uses [PEReader](https://learn.microsoft.com/en-us/dotnet/api/system.reflection.portableexecutable.pereader) and [assembly-reference metadata](https://learn.microsoft.com/en-us/dotnet/api/system.reflection.metadata.assemblyreference) to inspect the binaries extracted from the actual analyzer and code-fix packages before loading them. It checks:

- The analyzer, code fix, and the Core DLL adjacent to the analyzer retain their .NET Standard 2.0 assembly reference.
- Microsoft.CodeAnalysis assembly references do not exceed 4.14.0.0.
- System.Collections.Immutable, System.Reflection.Metadata, and System.Composition assembly references do not exceed 9.0.0.0.

These are assembly-version checks, not NuGet package-version comparisons. Compatible servicing packages can keep the same assembly version. The Core check reads the DLL bundled with the analyzer directly; it does not substitute the different Core asset that a .NET 10 package consumer may select.

This metadata check matters because the compatibility fixture runs on .NET 10. Runtime assembly unification could otherwise satisfy a new framework dependency and hide a requirement that an older compiler host does not meet. The existing fixture also discovers and executes the packaged analyzers and applies the exported MEF code fix in a real Roslyn 4.14 compilation. Metadata checks and execution checks cover different failure modes; both remain required.

The check covers the named host-owned dependency families and the exercised analyzer/code-fix operations. It does not prove compatibility with every IDE integration or every possible API path. A deliberate change to the minimum supported host requires updated documentation, reviewed compile references, and corresponding package compatibility checks.

## Dependabot policy

The repository keeps weekly NuGet and GitHub Actions update proposals enabled. Roslyn and System.Composition updates share a group to make dependency-family changes reviewable together. There is no blanket ignore rule for compiler packages and no excluded shipping manifest: dependency and security maintenance remain visible. An automatically proposed version change still needs to satisfy the host-compatibility gates above.

See GitHub's [Dependabot group and update configuration reference](https://docs.github.com/en/code-security/reference/supply-chain-security/dependabot-options-reference) and the [installation and validation guide](integration.md).
