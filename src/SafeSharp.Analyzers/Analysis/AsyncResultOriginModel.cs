using Microsoft.CodeAnalysis;

namespace SafeSharp.Analyzers.Analysis;

/// <summary>
/// Compilation-scoped recognition of framework operations which store, preserve, or extract one
/// asynchronous result. Symbol identity prevents a user-defined namesake from acquiring a contract.
/// </summary>
/// <remarks>
/// The carrier and its output are distinct values, as in Rust's Future::Output and Poll::Ready:
/// https://github.com/rust-lang/rust/blob/f8297e351a40c1439a467bbbb6879088047f50b3/library/core/src/future/future.rs
/// This is an original C# provenance model; C# Task/ValueTask execution and aliasing rules remain
/// those of .NET. In particular, allocating a Task does not prove that its eventual result is fresh.
/// </remarks>
internal sealed class AsyncResultOriginModel
{
    private readonly INamedTypeSymbol? task;
    private readonly INamedTypeSymbol? taskOfT;
    private readonly INamedTypeSymbol? valueTask;
    private readonly INamedTypeSymbol? valueTaskOfT;
    private readonly INamedTypeSymbol? configuredTask;
    private readonly INamedTypeSymbol? configuredValueTask;
    private readonly INamedTypeSymbol? taskAwaiter;
    private readonly INamedTypeSymbol? valueTaskAwaiter;
    private readonly INamedTypeSymbol? configuredTaskAwaiter;
    private readonly INamedTypeSymbol? configuredValueTaskAwaiter;

    internal AsyncResultOriginModel(Compilation compilation)
    {
        task = FrameworkType(compilation, "System.Threading.Tasks.Task");
        taskOfT = FrameworkType(compilation, "System.Threading.Tasks.Task`1");
        valueTask = FrameworkType(compilation, "System.Threading.Tasks.ValueTask");
        valueTaskOfT = FrameworkType(compilation, "System.Threading.Tasks.ValueTask`1");
        configuredTask = FrameworkType(compilation, "System.Runtime.CompilerServices.ConfiguredTaskAwaitable`1");
        configuredValueTask = FrameworkType(compilation, "System.Runtime.CompilerServices.ConfiguredValueTaskAwaitable`1");
        taskAwaiter = FrameworkType(compilation, "System.Runtime.CompilerServices.TaskAwaiter`1");
        valueTaskAwaiter = FrameworkType(compilation, "System.Runtime.CompilerServices.ValueTaskAwaiter`1");
        configuredTaskAwaiter = FrameworkType(compilation, "System.Runtime.CompilerServices.ConfiguredTaskAwaitable`1+ConfiguredTaskAwaiter");
        configuredValueTaskAwaiter = FrameworkType(compilation, "System.Runtime.CompilerServices.ConfiguredValueTaskAwaitable`1+ConfiguredValueTaskAwaiter");
    }

    internal bool IsCarrier(ITypeSymbol? type) => Is(type, taskOfT) || Is(type, valueTaskOfT);

    internal bool IsAwaitable(ITypeSymbol? type) => IsCarrier(type) || Is(type, configuredTask) || Is(type, configuredValueTask);

    internal bool IsCarrierAllocation(ITypeSymbol? type)
    {
        for (var named = type as INamedTypeSymbol; named is not null; named = named.BaseType)
            if (IsCarrier(named)) return true;
        return false;
    }

    internal AsyncResultOriginOperation Classify(IMethodSymbol method)
    {
        var type = method.ContainingType;
        if (method.MethodKind == MethodKind.Constructor && Is(type, valueTaskOfT) && method.Parameters.Length == 1)
        {
            var payload = type.TypeArguments[0];
            var parameter = method.Parameters[0].Type;
            if (SymbolEqualityComparer.Default.Equals(parameter, payload)) return AsyncResultOriginOperation.WrapArgument;
            if (Is(parameter, taskOfT) && parameter is INamedTypeSymbol carrier &&
                SymbolEqualityComparer.Default.Equals(carrier.TypeArguments[0], payload))
                return AsyncResultOriginOperation.PreserveArgument;
        }
        if (method.IsStatic && method.Name == "FromResult" && method.Parameters.Length == 1 &&
            (Is(type, task) || Is(type, valueTask)) && IsCarrier(method.ReturnType))
            return AsyncResultOriginOperation.WrapArgument;
        if (!method.IsStatic && IsAwaitable(type))
        {
            if (method.Name is "ConfigureAwait" or "GetAwaiter" or "AsTask" or "Preserve")
                return AsyncResultOriginOperation.PreserveReceiver;
            if (method.MethodKind == MethodKind.PropertyGet && method.Name == "get_Result" && IsCarrier(type))
                return AsyncResultOriginOperation.UnwrapReceiver;
        }
        if (!method.IsStatic && method.Name == "GetResult" && IsAwaiter(type))
            return AsyncResultOriginOperation.UnwrapReceiver;
        return AsyncResultOriginOperation.None;
    }

    private bool IsAwaiter(ITypeSymbol? type) => Is(type, taskAwaiter) || Is(type, valueTaskAwaiter) ||
        Is(type, configuredTaskAwaiter) || Is(type, configuredValueTaskAwaiter);

    private static bool Is(ITypeSymbol? type, INamedTypeSymbol? definition) => definition is not null &&
        type is INamedTypeSymbol named && SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, definition);

    private static INamedTypeSymbol? FrameworkType(Compilation compilation, string metadataName)
    {
        var type = compilation.GetTypeByMetadataName(metadataName);
        return type is not null && !type.Locations.Any(location => location.IsInSource) && type.ContainingAssembly.Identity.Name is
            "mscorlib" or "System.Private.CoreLib" or "System.Runtime" or "netstandard" or
            "System.Threading.Tasks" or "System.Threading.Tasks.Extensions" ? type : null;
    }
}

internal enum AsyncResultOriginOperation
{
    None,
    WrapArgument,
    PreserveArgument,
    PreserveReceiver,
    UnwrapReceiver
}
