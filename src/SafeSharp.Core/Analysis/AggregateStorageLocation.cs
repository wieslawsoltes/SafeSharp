using System;

namespace SafeSharp.Core;

/// <summary>Parses canonical array cells without interpreting source names as storage syntax.</summary>
internal static class AggregateStorageLocation
{
    internal static bool TryParse(string location, out string aggregate, out string index, out string suffix)
    {
        aggregate = index = suffix = string.Empty;
        var prefix = OwnershipTransfer.AggregateElementCachePrefix;
        if (!location.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var separator = location.IndexOf(':', prefix.Length);
        if (separator <= prefix.Length || !int.TryParse(location.Substring(prefix.Length, separator - prefix.Length), out var length) ||
            length < 0 || length > location.Length - separator - 1) return false;
        var start = separator + 1 + length;
        if (start >= location.Length || location[start] != '[') return false;
        var end = location.IndexOf(']', start + 1);
        if (end < 0) return false;
        aggregate = location.Substring(separator + 1, length);
        index = location.Substring(start, end - start + 1);
        suffix = location.Substring(end + 1);
        return true;
    }

    internal static bool Overlaps(string left, string right)
    {
        if (left == right) return true;
        if (left.IndexOf('*') < 0 && right.IndexOf('*') < 0) return false;
        var first = left.Split(',');
        var second = right.Split(',');
        if (first.Length != second.Length) return true; // An opaque rank supplies no separation proof.
        for (var index = 0; index < first.Length; index++)
            if (first[index] != second[index] && first[index].IndexOf('*') < 0 && second[index].IndexOf('*') < 0) return false;
        return true;
    }
}
