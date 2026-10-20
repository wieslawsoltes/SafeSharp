# Maintaining dependency and compiler-host compatibility

SafeSharp's shipping analyzers and its development hosts have different dependency requirements. Update each dependency where it runs, and retain the supported compiler-host contract when merging a dependency pull request.

| Component | Dependency contract |
| --- | --- |
| `SafeSharp.Analyzers` and `SafeSharp.CodeFixes` | Compile against Roslyn 4.14 and .NET Standard 2.0. Code fixes retain the explicit System.Composition.AttributedModel 9.0.0 package and 9.0.0.0 assembly contract. |
| Core bundled beside the analyzer | Use the .NET Standard 2.0 Core asset with the System.Collections.Immutable 9 assembly contract. |
| Unit-test host and standalone tool | Exercise Roslyn 5.9 and its compatible MEF dependency family on .NET 10. These versions may advance independently of the shipping analyzer API floor. |
| `eng/package-host` | Keep the explicit Roslyn 4.14 and System.Composition 9.0.0 package references. This fixture validates the minimum supported host with the MEF 9.0.0.0 runtime assembly contract. |

The [Roslyn team's compatibility guidance](https://github.com/dotnet/roslyn/discussions/81256) explains why an analyzer must reference a compiler API version supported by its intended hosts. A newer compiler reference can cause CS9057 or a runtime load/API failure on an older host even when the analyzer does not deliberately call a new API. A dependency update must not silently change the documented minimum host version.

## Reviewing an update

1. Bring the dependency branch up to date with `main` and inspect every changed package reference. In particular, distinguish the analyzer/code-fix compile references from the tool and test-host references.
2. Resolve the complete dependency family. Updating Roslyn Workspaces while retaining an incompatible direct MEF reference produces a NuGet downgrade error. Review their requirements together, including manual MEF updates, and keep the shipping code-fix reference aligned with its older compiler-host contract.
3. Run the normal build, unit tests, CLI checks, semantic benchmark smoke checks, and all package checks. The ordinary test host exercises newer Roslyn APIs; the independent package host exercises Roslyn 4.14.
4. Inspect the exact branch-head CI result before merging. A passing test on a newer host does not establish that a compiled analyzer will load in the minimum supported host.

The two original Roslyn update pull requests proposed the same version changes. The accepted update uses Roslyn 5.9 in the test host and the compatible System.Composition 10.0.13 servicing dependency while preserving the analyzer and code-fix compile references at 4.14/9. The tool already uses Roslyn 5.9. Keeping the minimum fixture on 4.14 means both supported API generations remain exercised.

## Checks against the shipped assemblies

The package host uses [PEReader](https://learn.microsoft.com/en-us/dotnet/api/system.reflection.portableexecutable.pereader) and [assembly-reference metadata](https://learn.microsoft.com/en-us/dotnet/api/system.reflection.metadata.assemblyreference) to inspect the binaries extracted from the actual analyzer and code-fix packages before loading them. It checks:

- The analyzer, code fix, and the Core DLL adjacent to the analyzer retain their .NET Standard 2.0 assembly reference.
- Microsoft.CodeAnalysis assembly references do not exceed 4.14.0.0.
- System.Collections.Immutable, System.Reflection.Metadata, and System.Composition assembly references do not exceed 9.0.0.0.

These are assembly-version checks, not NuGet package-version comparisons. Compatible servicing packages can keep the same assembly version. The Core check reads the DLL bundled with the analyzer directly; it does not substitute the different Core asset that a .NET 10 package consumer may select.

Inspection of the published [System.Composition.AttributedModel 9.0.21 package](https://api.nuget.org/v3-flatcontainer/system.composition.attributedmodel/9.0.21/system.composition.attributedmodel.9.0.21.nupkg) found assembly version **9.0.0.21** in its .NET Standard 2.0 asset, above the **9.0.0.0** reference supplied by the 9.0.0 package. In contrast, the .NET Standard 2.0 asset in [System.Collections.Immutable 9.0.21](https://api.nuget.org/v3-flatcontainer/system.collections.immutable/9.0.21/system.collections.immutable.9.0.21.nupkg) retains assembly version **9.0.0.0**. The Immutable servicing update preserves the checked assembly floor; the MEF update would raise it. Keep both the shipping code-fix compile dependency and the minimum-host runtime fixture on their explicit MEF 9.0.0 packages unless the supported host contract is deliberately revised.

This metadata check matters because the compatibility fixture runs on .NET 10. Runtime assembly unification could otherwise satisfy a new framework dependency and hide a requirement that an older compiler host does not meet. The existing fixture also discovers and executes the packaged analyzers and applies the exported MEF code fix in a real Roslyn 4.14 compilation. Metadata checks and execution checks cover different failure modes; both remain required.

The check covers the named host-owned dependency families and the exercised analyzer/code-fix operations. It does not prove compatibility with every IDE integration or every possible API path. A deliberate change to the minimum supported host requires updated documentation, reviewed compile references, and corresponding package compatibility checks.

## Dependabot policy

The repository keeps weekly NuGet and GitHub Actions updates enabled. NuGet uses one repository-root entry because the updater follows `ProjectReference` edges into other project directories. A directory identifies where discovery starts; it does not isolate the package references that an update can change.

This behavior was observed after introducing separate modern-host, compiler-host, and Core entries: discovery starting from an analyzer proposed an incompatible Core generation update, while discovery starting from modern development projects proposed shipping Roslyn/MEF generation updates. The native updater explicitly [resolves transitive project references during discovery](https://github.com/dependabot/dependabot-core/blob/main/nuget/helpers/lib/NuGetUpdater/NuGetUpdater.Core/Discover/SdkProjectDiscovery.cs). A single policy avoids relying on directory isolation and avoids separate jobs proposing overlapping changes.

| Dependency family | Automatic version-update policy |
| --- | --- |
| `Microsoft.CodeAnalysis*` and `System.Collections.Immutable` throughout the repository | Ignore major/minor proposals. Compatible patch releases remain eligible. Generation changes require a reviewed manual update, including changes limited to the tool or test host. |
| `System.Composition*` throughout the repository | Ignore major, minor, and patch version proposals. Review all MEF version updates manually, including modern test/tool servicing updates, because a servicing release can raise the assembly-version floor. |
| Other NuGet dependencies | All version levels remain eligible. |
| GitHub Actions | All version levels remain eligible. |

Roslyn servicing remains eligible for the existing dependency group; its MEF requirements must be reviewed with each update. The shipping compiler API floor remains Roslyn 4.14 with the MEF/Immutable 9.0.0.0 assembly contracts; tests and the standalone tool retain their modern dependency families. Their versions can still advance independently through an explicitly reviewed change. Applying the MEF rule throughout the project-reference graph also makes modern MEF patch updates manual; this is the explicit tradeoff for retaining the minimum host without relying on directory isolation.

The [root configuration](../.github/dependabot.yml) also discovers new project directories and shared `Directory.Build.props` dependencies. The upstream NuGet updater has [tests for parent build properties and their package references](https://github.com/dependabot/dependabot-core/blob/main/nuget/helpers/lib/NuGetUpdater/NuGetUpdater.Core.Test/Discover/DiscoveryWorkerTests.Project.cs). When adding or moving a manifest, check the update-job log to confirm successful discovery and restore; a successful job status alone does not prove that every project was processed. A new ecosystem needs its own applicable configuration entry. Review shared build-property changes across every affected project.

These `ignore.update-types` rules apply to **version updates**. GitHub documents that [enabled security updates still propose the minimum patched version](https://github.blog/changelog/2021-05-21-dependabot-version-updates-can-now-ignore-major-minor-patch-releases/). This configuration does not enable or disable the repository's security-update setting. A security fix that also raises the minimum supported host requires an explicit compatibility decision; the package gate continues to prevent an unreviewed host-contract change.

Core has conditional references to different versions of the same package ID for its two target frameworks. The native updater [groups discovered dependencies by package name](https://github.com/dependabot/dependabot-core/blob/main/nuget/helpers/lib/NuGetUpdater/NuGetUpdater.Core/Discover/SdkProjectDiscovery.cs); the configuration does not manage these references independently. Inspect the generated diff, update-job discovery results, and exact-head CI after every change. The emitted assembly metadata and package dependency checks remain the compatibility authority, including for servicing releases.

A follow-up grouped update demonstrated the need for this distinction: it proposed raising shipping Roslyn/MEF references while also offering a valid test-host MEF servicing update. The test-host update to 10.0.13 is accepted; the shipping 4.14/9 API contract is preserved.

See GitHub's [Dependabot group and update configuration reference](https://docs.github.com/en/code-security/reference/supply-chain-security/dependabot-options-reference) and the [installation and validation guide](integration.md).

