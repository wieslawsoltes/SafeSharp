using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace SafeSharp.Core;

/// <summary>Per-analysis ordinal place interning and structural projection ranges.</summary>
internal sealed class PlaceUniverse
{
    private readonly Dictionary<string, int> indices;
    private readonly ProjectionKill?[] projectionKills;

    internal PlaceUniverse(IEnumerable<FlowBlock> blocks, CancellationToken cancellationToken)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var block in blocks)
        foreach (var instruction in block.Instructions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (instruction.Kind != InstructionKind.Nop)
            {
                if (instruction.Source is not null) names.Add(instruction.Source);
                if (instruction.Target is not null) names.Add(instruction.Target);
            }
            if (instruction.Owner is not null) names.Add(instruction.Owner);
        }
        Names = names.ToArray();
        // Sort the actual domain once; LINQ's indexed sort also allocates key/index buffers.
        Array.Sort(Names, StringComparer.Ordinal);
        cancellationToken.ThrowIfCancellationRequested();
        indices = new Dictionary<string, int>(Names.Length, StringComparer.Ordinal);
        for (var index = 0; index < Names.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            indices.Add(Names[index], index);
        }
        WordCount = (Names.Length + 63) / 64;
        projectionKills = new ProjectionKill?[Names.Length];
        Empty = new PlaceSet(this, Array.Empty<ulong>(), null, 0);
    }

    internal string[] Names { get; }
    internal int WordCount { get; }
    internal PlaceSet Empty { get; }
    internal int Index(string? place) => place is null ? -1 : indices[place];
    internal bool TryIndex(string place, out int index) => indices.TryGetValue(place, out index);

    internal ProjectionKill KillFor(int index)
    {
        if (projectionKills[index] is { } cached) return cached;
        var name = Names[index];
        // An empty root is the structural parent of every place. Otherwise only '.' and '['
        // introduce projections: e.g. p!, p0 and p/f are not descendants of p.
        var kill = name.Length == 0
            ? new ProjectionKill(index, 0, Names.Length, 0, 0)
            : index + 1 == Names.Length || !Names[index + 1].StartsWith(name, StringComparison.Ordinal)
                ? new ProjectionKill(index, 0, 0, 0, 0)
                : new ProjectionKill(index, LowerBound(name + "."), LowerBound(name + "/"),
                    LowerBound(name + "["), LowerBound(name + "\\"));
        projectionKills[index] = kill;
        return kill;
    }

    private int LowerBound(string value)
    {
        var first = 0;
        var last = Names.Length;
        while (first < last)
        {
            var middle = first + ((last - first) / 2);
            if (StringComparer.Ordinal.Compare(Names[middle], value) < 0) first = middle + 1;
            else last = middle;
        }
        return first;
    }
}

/// <summary>The exact target and its two disjoint ordinal projection ranges.</summary>
internal readonly struct ProjectionKill
{
    internal ProjectionKill(int target, int fieldsStart, int fieldsEnd, int elementsStart, int elementsEnd)
    {
        Target = target;
        FieldsStart = fieldsStart;
        FieldsEnd = fieldsEnd;
        ElementsStart = elementsStart;
        ElementsEnd = elementsEnd;
    }

    internal int Target { get; }
    private int FieldsStart { get; }
    private int FieldsEnd { get; }
    private int ElementsStart { get; }
    private int ElementsEnd { get; }

    internal bool RemoveFrom(PlaceSetBuilder words, CancellationToken cancellationToken) =>
        words.Remove(Target)
        | words.SetRange(FieldsStart, FieldsEnd, false, cancellationToken)
        | words.SetRange(ElementsStart, ElementsEnd, false, cancellationToken);

    internal void AddTo(PlaceSetBuilder words, CancellationToken cancellationToken)
    {
        words.Add(Target);
        words.SetRange(FieldsStart, FieldsEnd, true, cancellationToken);
        words.SetRange(ElementsStart, ElementsEnd, true, cancellationToken);
    }
}

internal static class BitWords
{
    // Portable fixed-width bit arithmetic keeps the analyzer's netstandard2.0 host contract.
    internal static int Count(ulong value)
    {
        value -= (value >> 1) & 0x5555555555555555UL;
        value = (value & 0x3333333333333333UL) + ((value >> 2) & 0x3333333333333333UL);
        value = (value + (value >> 4)) & 0x0f0f0f0f0f0f0f0fUL;
        return (int)(unchecked(value * 0x0101010101010101UL) >> 56);
    }

    internal static int TrailingZeroes(ulong value)
    {
        var count = 0;
        if ((value & uint.MaxValue) == 0) { count += 32; value >>= 32; }
        if ((value & ushort.MaxValue) == 0) { count += 16; value >>= 16; }
        if ((value & byte.MaxValue) == 0) { count += 8; value >>= 8; }
        if ((value & 15) == 0) { count += 4; value >>= 4; }
        if ((value & 3) == 0) { count += 2; value >>= 2; }
        if ((value & 1) == 0) count++;
        return count;
    }
}
