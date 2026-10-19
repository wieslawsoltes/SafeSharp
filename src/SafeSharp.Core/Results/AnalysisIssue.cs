using System.Collections.Generic;
using System.Collections.Immutable;

namespace SafeSharp.Core;

public enum IssueCertainty { Definite, Possible }

/// <summary>A source-grounded explanation step. It is evidence, not a fabricated executable counterexample path.</summary>
public sealed class AnalysisEvidence
{
    public AnalysisEvidence(string kind, string message, SourceSite site)
    {
        Kind = kind;
        Message = message;
        Site = site;
    }

    public string Kind { get; }
    public string Message { get; }
    public SourceSite Site { get; }
}

public sealed class AnalysisIssue
{
    public AnalysisIssue(string id, string message, SourceSite site, IssueCertainty certainty,
        string? resourceId = null, IEnumerable<SourceSite>? relatedLocations = null,
        IEnumerable<AnalysisEvidence>? evidence = null)
    {
        Id = id;
        Message = message;
        Site = site;
        Certainty = certainty;
        ResourceId = resourceId;
        RelatedLocations = relatedLocations?.ToImmutableArray() ?? ImmutableArray<SourceSite>.Empty;
        Evidence = evidence?.ToImmutableArray() ?? ImmutableArray<AnalysisEvidence>.Empty;
    }

    public string Id { get; }
    public string Message { get; }
    public SourceSite Site { get; }
    public ImmutableArray<SourceSite> RelatedLocations { get; }
    public IssueCertainty Certainty { get; }
    public string? ResourceId { get; }
    public ImmutableArray<AnalysisEvidence> Evidence { get; }
}
