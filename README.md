# SafeSharp

Rust-inspired ownership and borrowing analysis for C# using the Roslyn compiler API.

SafeSharp is under active development. The implementation separates a reusable dataflow engine, Roslyn integration, explicit ownership contracts, and analysis reports. Its documented guarantees apply to the supported C# subset and are not a proof of Rust-equivalent memory safety for arbitrary C#.

## Development

The repository is being initialized with .NET projects, semantic regression tests, NuGet packaging, and CI. See subsequent commits for the implementation and the feature/support matrix.
