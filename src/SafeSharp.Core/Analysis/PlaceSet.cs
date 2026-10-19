using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;

namespace SafeSharp.Core;

/// <summary>
/// Immutable interned liveness set. Mutable solver buffers use dense 64-bit words; frozen sparse
/// boundaries retain only nonzero words. Adjacent boundaries may safely share the same instance.
/// </summary>
/// <remarks>
/// Original C# implementation informed by rustc's indexed dense/mixed bit sets, not copied Rust code.
/// https://github.com/rust-lang/rust/blob/f8297e351a40c1439a467bbbb6879088047f50b3/compiler/rustc_index/src/bit_set.rs
/// </remarks>
internal sealed class PlaceSet : ISet<string>
{
    private readonly PlaceUniverse universe;
    private readonly ulong[] words;
    private readonly int[]? sparseIndices;

    internal PlaceSet(PlaceUniverse universe, ulong[] words, int[]? sparseIndices, int count)
    {
        this.universe = universe;
        this.words = words;
        this.sparseIndices = sparseIndices;
        Count = count;
    }

    public int Count { get; }
    public bool IsReadOnly => true;

    internal static PlaceSet Freeze(PlaceUniverse universe, ulong[] buffer, CancellationToken cancellationToken)
    {
        var builder = new PlaceSetBuilder(buffer.Length);
        for (var index = 0; index < buffer.Length; index++)
        {
            if ((index & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            builder.SetWord(index, buffer[index]);
        }
        return Freeze(universe, builder, cancellationToken);
    }

    internal static PlaceSet Freeze(PlaceUniverse universe, PlaceSetBuilder buffer, CancellationToken cancellationToken)
    {
        if (buffer.Count == 0) return universe.Empty;
        // The second array has a header too. Retain a dense array for small or reasonably full sets.
        if (buffer.OccupiedWords * 3 + 4 >= buffer.Words.Length * 2)
            return new PlaceSet(universe, (ulong[])buffer.Words.Clone(), null, buffer.Count);

        var words = new ulong[buffer.OccupiedWords];
        var indices = new int[buffer.OccupiedWords];
        var next = 0;
        for (var summaryIndex = 0; summaryIndex < buffer.Occupancy.Length; summaryIndex++)
        {
            if ((summaryIndex & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            var remaining = buffer.Occupancy[summaryIndex];
            while (remaining != 0)
            {
                var index = (summaryIndex << 6) + BitWords.TrailingZeroes(remaining);
                words[next] = buffer.Words[index];
                indices[next++] = index;
                remaining &= remaining - 1;
            }
        }
        return new PlaceSet(universe, words, indices, buffer.Count);
    }

    internal void UnionInto(PlaceSetBuilder buffer, CancellationToken cancellationToken)
    {
        for (var index = 0; index < words.Length; index++)
        {
            if ((index & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            var target = sparseIndices is null ? index : sparseIndices[index];
            buffer.SetWord(target, buffer.Words[target] | words[index]);
        }
    }

    internal void ExceptFrom(PlaceSetBuilder buffer, CancellationToken cancellationToken)
    {
        for (var index = 0; index < words.Length; index++)
        {
            if ((index & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            var target = sparseIndices is null ? index : sparseIndices[index];
            buffer.SetWord(target, buffer.Words[target] & ~words[index]);
        }
    }

    internal bool Matches(PlaceSetBuilder buffer, CancellationToken cancellationToken)
    {
        if (Count != buffer.Count) return false;
        // Equal cardinalities reduce equality to checking this set's occupied words. This is
        // constant work for a short-lived temporary even when the method has thousands of places.
        for (var index = 0; index < words.Length; index++)
        {
            if ((index & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (words[index] != buffer.Words[sparseIndices is null ? index : sparseIndices[index]]) return false;
        }
        return true;
    }

    public bool Contains(string item)
    {
        if (item is null || !universe.TryIndex(item, out var id)) return false;
        var index = id >> 6;
        if (sparseIndices is not null) index = Array.BinarySearch(sparseIndices, index);
        return index >= 0 && index < words.Length && (words[index] & (1UL << (id & 63))) != 0;
    }

    public IEnumerator<string> GetEnumerator()
    {
        for (var index = 0; index < words.Length; index++)
        {
            var remaining = words[index];
            var first = (sparseIndices is null ? index : sparseIndices[index]) << 6;
            while (remaining != 0)
            {
                yield return universe.Names[first + BitWords.TrailingZeroes(remaining)];
                remaining &= remaining - 1;
            }
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public void CopyTo(string[] array, int arrayIndex)
    {
        if (array is null) throw new ArgumentNullException(nameof(array));
        if (arrayIndex < 0 || arrayIndex > array.Length) throw new ArgumentOutOfRangeException(nameof(arrayIndex));
        if (array.Length - arrayIndex < Count) throw new ArgumentException("The destination array is too small.", nameof(array));
        foreach (var item in this) array[arrayIndex++] = item;
    }

    // The ownership transfer API consumes ISet<string>; its liveness arguments are read-only.
    // Keep the full interface contract without exposing a mutation path into shared boundaries.
    private HashSet<string> AsHashSet() => new(this, StringComparer.Ordinal);
    public bool IsProperSubsetOf(IEnumerable<string> other) => AsHashSet().IsProperSubsetOf(other);
    public bool IsProperSupersetOf(IEnumerable<string> other) => AsHashSet().IsProperSupersetOf(other);
    public bool IsSubsetOf(IEnumerable<string> other) => AsHashSet().IsSubsetOf(other);
    public bool IsSupersetOf(IEnumerable<string> other) => AsHashSet().IsSupersetOf(other);
    public bool Overlaps(IEnumerable<string> other) => AsHashSet().Overlaps(other);
    public bool SetEquals(IEnumerable<string> other) => AsHashSet().SetEquals(other);
    bool ISet<string>.Add(string item) => throw new NotSupportedException("Liveness sets are immutable.");
    void ICollection<string>.Add(string item) => throw new NotSupportedException("Liveness sets are immutable.");
    public bool Remove(string item) => throw new NotSupportedException("Liveness sets are immutable.");
    public void Clear() => throw new NotSupportedException("Liveness sets are immutable.");
    public void ExceptWith(IEnumerable<string> other) => throw new NotSupportedException("Liveness sets are immutable.");
    public void IntersectWith(IEnumerable<string> other) => throw new NotSupportedException("Liveness sets are immutable.");
    public void SymmetricExceptWith(IEnumerable<string> other) => throw new NotSupportedException("Liveness sets are immutable.");
    public void UnionWith(IEnumerable<string> other) => throw new NotSupportedException("Liveness sets are immutable.");
}
