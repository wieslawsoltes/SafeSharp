using System;

namespace SafeSharp.Core;

/// <summary>A compiler-independent, zero-based source span.</summary>
public sealed class SourceSite : IEquatable<SourceSite>
{
    public SourceSite(string path, int start, int length, int line = 0, int column = 0)
    {
        Path = path ?? throw new ArgumentNullException(nameof(path));
        if (start < 0) throw new ArgumentOutOfRangeException(nameof(start));
        if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
        if (line < 0) throw new ArgumentOutOfRangeException(nameof(line));
        if (column < 0) throw new ArgumentOutOfRangeException(nameof(column));
        Start = start;
        Length = length;
        Line = line;
        Column = column;
    }

    public string Path { get; }
    public int Start { get; }
    public int Length { get; }
    public int Line { get; }
    public int Column { get; }
    public bool Equals(SourceSite? other) => other is not null && Path == other.Path && Start == other.Start && Length == other.Length;
    public override bool Equals(object? obj) => obj is SourceSite other && Equals(other);
    public override int GetHashCode() => unchecked(((StringComparer.Ordinal.GetHashCode(Path) * 397) ^ Start) * 397 ^ Length);
    public override string ToString() => Path + "(" + (Line + 1) + "," + (Column + 1) + ")";
}
