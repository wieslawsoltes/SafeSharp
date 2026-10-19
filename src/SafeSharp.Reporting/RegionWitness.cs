using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;

namespace SafeSharp.Reporting;

/// <summary>
/// A bounded presentation of declared lifetime edges. Proof strings and solver components are
/// retained as diagnostic metadata; neither is used to invent additional graph edges.
/// </summary>
internal sealed class RegionWitness
{
    internal const int MaximumRegions = 64;
    internal const int MaximumEdges = 256;
    private const int MaximumMetadataLength = 65536;

    private RegionWitness(ImmutableArray<string> regions, ImmutableArray<RegionEdge> edges,
        string? required, ImmutableHashSet<string> sources, ImmutableArray<string> otherOrigins, string? omissionReason)
    {
        Regions = regions;
        Edges = edges;
        Required = required;
        Sources = sources;
        OtherOrigins = otherOrigins;
        OmissionReason = omissionReason;
    }

    internal ImmutableArray<string> Regions { get; }
    internal ImmutableArray<RegionEdge> Edges { get; }
    internal string? Required { get; }
    internal ImmutableHashSet<string> Sources { get; }
    internal ImmutableArray<string> OtherOrigins { get; }
    internal string? OmissionReason { get; }
    internal bool CanRender => OmissionReason is null;
    internal string Role(string region) => region == Required
        ? Sources.Contains(region) ? "source + required" : "required"
        : Sources.Contains(region) ? "source" : "declared";
    internal string Label(string region) => region + "\n" + Role(region);
    internal string Color(string region) => region == Required ? "#ffb578" : Sources.Contains(region) ? "#79d8cd" : "#6f86a4";
    internal string Fill(string region) => region == Required ? "#49362b" : Sources.Contains(region) ? "#173c44" : "#152237";

    internal static RegionWitness? FromDiagnostic(ReportDiagnostic diagnostic)
    {
        if (!diagnostic.Properties.Keys.Any(IsRegionProperty)) return null;
        var properties = diagnostic.Properties;
        if (!properties.TryGetValue("SafeSharp.RegionNames", out var names) || names is null)
            return Omitted("Named regions were not supplied; no region graph was reconstructed.");
        if (!properties.TryGetValue("SafeSharp.RegionConstraints", out var constraints) || constraints is null)
            return Omitted("Declared constraints were not supplied; no region graph was reconstructed.");
        properties.TryGetValue("SafeSharp.RequiredRegion", out var required);
        properties.TryGetValue("SafeSharp.SourceRegions", out var sources);
        if (new[] { names, constraints, required, sources }.Any(value => value?.Length > MaximumMetadataLength))
            return Omitted("Region graph omitted: graph metadata exceeds the 65,536-character parsing limit per property.");
        var regions = Tokens(names!, ',');
        if (regions is null || regions.Any(name => !IsIdentifier(name)))
            return Omitted("Region graph omitted: named-region metadata is malformed.");
        regions = regions.Distinct(StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        if (regions.Length > MaximumRegions)
            return Omitted("Region graph omitted: the preview limit is 64 named regions and 256 declared edges.");
        var known = new HashSet<string>(regions, StringComparer.Ordinal);
        var edges = new SortedDictionary<string, RegionEdge>(StringComparer.Ordinal);
        var declaredEdges = Tokens(constraints!, ';');
        if (declaredEdges is null) return Omitted("Region graph omitted: declared-edge metadata is malformed.");
        foreach (var text in declaredEdges)
        {
            var endpoints = text.Split(':');
            if (endpoints.Length != 2 || !known.Contains(endpoints[0].Trim()) || !known.Contains(endpoints[1].Trim()))
                return Omitted("Region graph omitted: a declared edge is malformed or references an undeclared region.");
            var longer = endpoints[0].Trim();
            var shorter = endpoints[1].Trim();
            edges[longer + "\0" + shorter] = new RegionEdge(longer, shorter);
            if (edges.Count > MaximumEdges)
                return Omitted("Region graph omitted: the preview limit is 64 named regions and 256 declared edges.");
        }
        required = string.IsNullOrWhiteSpace(required) ? null : required!.Trim();
        if (required is not null && !known.Contains(required))
            return Omitted("Region graph omitted: the required region is not a declared vertex.");
        var origins = Tokens(sources ?? string.Empty, ',');
        if (origins is null || origins.Any(origin => !IsIdentifier(origin) && !(origin.Length > 2 &&
            origin[0] == '<' && origin[origin.Length - 1] == '>' && IsIdentifier(origin.Substring(1, origin.Length - 2)))))
            return Omitted("Region graph omitted: source-region metadata is malformed.");
        if (regions.Length == 0) return Omitted("No named regions were supplied; diagnostic metadata is retained.");
        // Local, Unknown, Null, Parameter and any future origin-kind words are annotations rather
        // than vertices unless the producer explicitly included the name in RegionNames.
        return new RegionWitness(regions.ToImmutableArray(), edges.Values.ToImmutableArray(), required,
            origins.Where(known.Contains).ToImmutableHashSet(StringComparer.Ordinal),
            origins.Where(origin => !known.Contains(origin)).Distinct(StringComparer.Ordinal).OrderBy(origin => origin, StringComparer.Ordinal).ToImmutableArray(), null);
    }

    internal static bool IsRegionProperty(string name) => name is "SafeSharp.RegionNames" or "SafeSharp.RegionConstraints"
        or "SafeSharp.RequiredRegion" or "SafeSharp.SourceRegions" or "SafeSharp.RegionProof" or "SafeSharp.RegionComponents";

    internal string MetadataLabel(ReportDiagnostic diagnostic) =>
        (OmissionReason is null ? "Diagnostic metadata (not graph edges)" : OmissionReason + "\nDiagnostic metadata") + "\n" +
        string.Join("\n", diagnostic.Properties.OrderBy(property => property.Key, StringComparer.Ordinal)
            .Select(property => property.Key + ": " + (property.Value ?? "(null)")));

    private static RegionWitness Omitted(string reason) => new(ImmutableArray<string>.Empty, ImmutableArray<RegionEdge>.Empty,
        null, ImmutableHashSet<string>.Empty, ImmutableArray<string>.Empty, reason);

    private static string[]? Tokens(string text, char separator)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();
        var values = text.Split(separator).Select(value => value.Trim()).ToArray();
        return values.Any(value => value.Length == 0) ? null : values;
    }

    // This is a delimiter-safe identifier grammar, not a replacement for Roslyn's declaration
    // validation. In particular, the producer reserves the receiver name "this" separately.
    private static bool IsIdentifier(string value)
    {
        if (value.Length == 0) return false;
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsSurrogate(value[index]) && !(char.IsHighSurrogate(value[index]) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]))) return false;
            var category = CharUnicodeInfo.GetUnicodeCategory(value, index);
            var letter = category is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
                or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter or UnicodeCategory.LetterNumber;
            if (!letter && value[index] != '_' && (index == 0 || category is not (UnicodeCategory.DecimalDigitNumber
                or UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.ConnectorPunctuation or UnicodeCategory.Format))) return false;
            if (char.IsHighSurrogate(value[index]) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1])) index++;
        }
        return true;
    }
}

internal sealed class RegionEdge
{
    internal RegionEdge(string longer, string shorter) { Longer = longer; Shorter = shorter; }
    internal string Longer { get; }
    internal string Shorter { get; }
}
