using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;

namespace SafeSharp.Reporting;

/// <summary>
/// A bounded view of explicit guard requirements and recorded capability intervals. Display names
/// never establish identity equality, and human-readable Held text never supplies graph records.
/// </summary>
internal sealed class SynchronizationWitness
{
    internal const int MaximumRecords = 64;
    internal const int MaximumMetadataLength = 65536;
    private const string Prefix = "SafeSharp.Synchronization";
    private const string HeldPrefix = Prefix + "Held.";

    private SynchronizationWitness(string required, string? identity, string protocol, string access, string accessMode,
        ImmutableArray<SynchronizationHold> held, string? omissionReason)
    {
        Required = required; RequiredIdentity = identity; Protocol = protocol; Access = access; AccessMode = accessMode;
        Held = held; OmissionReason = omissionReason;
    }

    internal string Required { get; }
    internal string? RequiredIdentity { get; }
    internal string Protocol { get; }
    internal string Access { get; }
    internal string AccessMode { get; }
    internal ImmutableArray<SynchronizationHold> Held { get; }
    internal string? OmissionReason { get; }
    internal bool CanRender => OmissionReason is null;
    internal string RequiredLabel => Required + (RequiredIdentity is null || RequiredIdentity == Required ? "" : "\nidentity: " + RequiredIdentity) +
        "\n" + Protocol + " · " + AccessMode;

    internal static SynchronizationWitness? FromDiagnostic(ReportDiagnostic diagnostic)
    {
        var properties = diagnostic.Properties;
        if (!properties.Keys.Any(IsSynchronizationProperty)) return null;
        var characters = 0;
        foreach (var property in properties.Where(property => IsSynchronizationProperty(property.Key)))
        {
            if (property.Key.Length > MaximumMetadataLength - characters) return Omitted("synchronization metadata exceeds the 65,536-character parsing limit");
            characters += property.Key.Length;
            if (property.Value?.Length > MaximumMetadataLength - characters) return Omitted("synchronization metadata exceeds the 65,536-character parsing limit");
            characters += property.Value?.Length ?? 0;
        }
        if (!properties.TryGetValue(Prefix + "HeldCount", out var countText))
            return Omitted("structured held-capability records were not supplied; human-readable state was not reconstructed");
        if (!Number(countText, out var count)) return Omitted("the structured held-record count is malformed");
        if (count > MaximumRecords) return Omitted("the preview limit is 64 held-capability records");
        if (!properties.TryGetValue(Prefix + "Required", out var required) || !Text(required))
            return Omitted("the required guard is missing or malformed");
        if (!properties.TryGetValue(Prefix + "Protocol", out var protocol) || !ProtocolName(protocol, requirement: true))
            return Omitted("the required protocol is missing or malformed");
        properties.TryGetValue(Prefix + "RequiredIdentity", out var identity);
        if (identity is not null && !Text(identity)) return Omitted("the required guard identity is malformed");
        properties.TryGetValue(Prefix + "Access", out var access);
        if (access is not null && !Text(access)) return Omitted("the accessed member is malformed");
        properties.TryGetValue(Prefix + "AccessMode", out var accessMode);
        if (accessMode is not null && !Text(accessMode)) return Omitted("the required access mode is malformed");
        foreach (var key in properties.Keys.Where(key => key.StartsWith(HeldPrefix, StringComparison.Ordinal)))
        {
            var separator = key.IndexOf('.', HeldPrefix.Length);
            if (separator < 0 || !Number(key.Substring(HeldPrefix.Length, separator - HeldPrefix.Length), out var index) ||
                index >= count || key.Substring(HeldPrefix.Length, separator - HeldPrefix.Length) != ReportText.Number(index))
                return Omitted("a structured record index is malformed or outside the declared count");
            var field = key.Substring(separator + 1);
            if (field is not ("Name" or "Protocol" or "Minimum" or "Maximum"))
                return Omitted("a structured held record has an unknown field");
        }
        var held = ImmutableArray.CreateBuilder<SynchronizationHold>(count);
        for (var index = 0; index < count; index++)
        {
            var prefix = HeldPrefix + ReportText.Number(index) + ".";
            if (!properties.TryGetValue(prefix + "Name", out var name) || !Text(name) ||
                !properties.TryGetValue(prefix + "Protocol", out var kind) || !ProtocolName(kind, requirement: false) ||
                !properties.TryGetValue(prefix + "Minimum", out var minimumText) || !Number(minimumText, out var minimum) ||
                !properties.TryGetValue(prefix + "Maximum", out var maximumText))
                return Omitted("a structured held record is missing or malformed");
            int? maximum = null;
            if (maximumText != "many")
            {
                if (!Number(maximumText, out var value) || value < minimum)
                    return Omitted("a structured acquisition interval is malformed");
                maximum = value;
            }
            held.Add(new SynchronizationHold(name!, kind!, minimum, maximum));
        }
        return new SynchronizationWitness(required!, identity, protocol!, access ?? "access not specified", accessMode ?? "mode not specified", held.MoveToImmutable(), null);
    }

    internal static bool IsSynchronizationProperty(string key) => key.StartsWith(Prefix, StringComparison.Ordinal);
    internal string MetadataLabel(ReportDiagnostic diagnostic) =>
        (OmissionReason ?? "Recorded diagnostic metadata; capability edges are state membership, not a thread schedule.") + "\n" +
        string.Join("\n", diagnostic.Properties.OrderBy(property => property.Key, StringComparer.Ordinal)
            .Select(property => property.Key + ": " + (property.Value ?? "(null)")));

    private static bool Number(string? value, out int number) => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number);
    private static bool ProtocolName(string? value, bool requirement) => value is "Monitor" or "ThreadingLock" or "Read" or "UpgradeableRead" or "Write" ||
        requirement && value == "ReaderWriter";
    private static bool Text(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        for (var index = 0; index < value!.Length; index++)
        {
            var character = value[index];
            if (char.IsControl(character) && character is not ('\n' or '\r' or '\t')) return false;
            // These BMP noncharacters cannot occur in an XML 1.0 SVG text node.
            if (character is '\ufffe' or '\uffff') return false;
            if (char.IsLowSurrogate(character)) return false;
            if (!char.IsHighSurrogate(character)) continue;
            if (index + 1 >= value.Length || !char.IsLowSurrogate(value[++index])) return false;
        }
        return true;
    }
    private static SynchronizationWitness Omitted(string reason) => new(string.Empty, null, string.Empty, string.Empty, string.Empty,
        ImmutableArray<SynchronizationHold>.Empty, "Synchronization graph omitted: " + reason + ".");
}

internal sealed class SynchronizationHold
{
    internal SynchronizationHold(string name, string protocol, int minimum, int? maximum)
    { Name = name; Protocol = protocol; Minimum = minimum; Maximum = maximum; }
    internal string Name { get; }
    internal string Protocol { get; }
    internal int Minimum { get; }
    internal int? Maximum { get; }
    internal string Interval => ReportText.Number(Minimum) + ".." + (Maximum is { } value ? ReportText.Number(value) : "many");
    internal string Label => Name + "\n" + Protocol + " · acquisitions " + Interval;
}
