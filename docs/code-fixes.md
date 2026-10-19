# IDE code fixes

`SafeSharp.CodeFixes` supplies Roslyn IDE actions independently of the compiler analyzer package. Its assembly references Roslyn Workspaces and MEF APIs supplied by a compatible IDE; it has no runtime dependency on `SafeSharp.Analyzers` or `SafeSharp.Contracts`.

Install both packages to obtain diagnostics and IDE actions:

```xml
<ItemGroup>
  <PackageReference Include="SafeSharp.Analyzers" Version="0.1.0-preview.1" PrivateAssets="all" />
  <PackageReference Include="SafeSharp.CodeFixes" Version="0.1.0-preview.1" PrivateAssets="all" />
  <PackageReference Include="SafeSharp.Contracts" Version="0.1.0-preview.1" />
</ItemGroup>
```

The code-fix package contains only `SafeSharp.CodeFixes.dll` under `analyzers/dotnet/cs`. It does not redistribute the analyzer assembly and therefore does not register duplicate diagnostic analyzers when both packages are installed. Package availability is established by the release workflow; building packages locally does not publish them to NuGet.org.

## Implemented action

**SS0024 — Acknowledge unused result** converts an ignored required result into an explicit acknowledgement:

```csharp
CreateResult();
// becomes:
_ = CreateResult();

await CreateResultAsync();
// becomes:
_ = await CreateResultAsync();
```

The action preserves evaluation order, awaits, expression comments, statement comments, and conditional execution. It does not dispose the result or infer that ignoring an ownership-bearing result is correct; it records the programmer's explicit decision to ignore it.

C# interprets `_ = value` as assignment when `_` already names a local, parameter, field, or another bound symbol. The action checks semantic lookup before introducing discard syntax. If `_` is bound, it creates a local of the actual result type with a unique name. Language versions before C# 7 also use this fallback. The generated name includes the original source position so independently computed **Fix All** edits cannot introduce the same local name. Existing identifiers are checked before choosing the final name.

When a declaration replaces an embedded statement, such as the body of a single-statement `if`, the action adds the necessary block. Nullable type annotations are preserved. A type named `@var` cannot accidentally change inference because the fallback spells the result type, except for anonymous types; an anonymous result with a conflicting `@var` type receives no automatic action.

The provider supports Roslyn's document/project/solution batch **Fix All** mechanism. Tests exercise the actual CodeAction/ApplyChangesOperation pipeline and BatchFixer, recompile every changed document, and rerun the analyzer to ensure the intended diagnostic was removed. Tests include awaits, bound underscores, old language versions, comments, embedded statements, and multiple fixes in the same scope.

## Why resource ownership fixes are not synthesized automatically

Automatically adding `using` can move disposal earlier than an escaping callback, returned resource, or asynchronous operation expects. Automatically calling `Move` changes the ownership contract. These require a program-level ownership decision, so this package currently offers an explicit result acknowledgement only. Other diagnostics include source locations, explanations, and report traces for a deliberate repair.
