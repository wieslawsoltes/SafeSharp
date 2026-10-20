using System;
using System.Threading;

namespace SafeSharp.Core;

/// <summary>
/// Mutable word-parallel scratch state with a second-level occupancy bitmap. Counts and occupied
/// words are updated with each changed word, so freezing a sparse temporary does not scan its domain.
/// </summary>
internal sealed class PlaceSetBuilder
{
    internal PlaceSetBuilder(int wordCount)
    {
        Words = new ulong[wordCount];
        Occupancy = new ulong[(wordCount + 63) / 64];
    }

    internal ulong[] Words { get; }
    internal ulong[] Occupancy { get; }
    internal int Count { get; private set; }
    internal int OccupiedWords { get; private set; }
    // XOR gives the sole occupied word in O(1) whenever OccupiedWords == 1.
    internal int OccupiedWordXor { get; private set; }

    internal void Clear()
    {
        Array.Clear(Words, 0, Words.Length);
        Array.Clear(Occupancy, 0, Occupancy.Length);
        Count = OccupiedWords = OccupiedWordXor = 0;
    }

    internal bool SetWord(int index, ulong value)
    {
        var previous = Words[index];
        if (previous == value) return false;
        Words[index] = value;
        Count += BitWords.Count(value) - BitWords.Count(previous);
        if (previous == 0)
        {
            OccupiedWords++;
            OccupiedWordXor ^= index;
            Occupancy[index >> 6] |= 1UL << (index & 63);
        }
        else if (value == 0)
        {
            OccupiedWords--;
            OccupiedWordXor ^= index;
            Occupancy[index >> 6] &= ~(1UL << (index & 63));
        }
        return true;
    }

    internal bool Add(int index)
    {
        if (index < 0) return false;
        var word = index >> 6;
        var previous = Words[word];
        var bit = 1UL << (index & 63);
        if ((previous & bit) != 0) return false;
        Words[word] = previous | bit;
        Count++;
        if (previous == 0)
        {
            OccupiedWords++;
            OccupiedWordXor ^= word;
            Occupancy[word >> 6] |= 1UL << (word & 63);
        }
        return true;
    }

    internal bool Remove(int index)
    {
        var word = index >> 6;
        var previous = Words[word];
        var bit = 1UL << (index & 63);
        if ((previous & bit) == 0) return false;
        var value = previous & ~bit;
        Words[word] = value;
        Count--;
        if (value == 0)
        {
            OccupiedWords--;
            OccupiedWordXor ^= word;
            Occupancy[word >> 6] &= ~(1UL << (word & 63));
        }
        return true;
    }

    internal bool SetRange(int first, int end, bool value, CancellationToken cancellationToken)
    {
        if (first == end) return false;
        var firstWord = first >> 6;
        var lastWord = (end - 1) >> 6;
        var changed = false;
        for (var index = firstWord; index <= lastWord; index++)
        {
            if ((index & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            var mask = ulong.MaxValue;
            if (index == firstWord) mask &= ulong.MaxValue << (first & 63);
            if (index == lastWord) mask &= ulong.MaxValue >> (63 - ((end - 1) & 63));
            changed |= SetWord(index, value ? Words[index] | mask : Words[index] & ~mask);
        }
        return changed;
    }
}
