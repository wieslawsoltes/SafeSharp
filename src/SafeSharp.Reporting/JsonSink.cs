using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace SafeSharp.Reporting;

/// <summary>
/// Minimal streaming JSON emission for analyzer-host-compatible netstandard2.0. No runtime JSON
/// dependency is loaded into compiler processes. All text goes through one escaping boundary.
/// </summary>
internal sealed class JsonSink
{
    private readonly TextWriter writer;
    private readonly Stack<Container> containers = new Stack<Container>();
    private bool hasRoot;

    public JsonSink(TextWriter writer) => this.writer = writer;

    public void StartObject() { BeforeValue(); writer.Write('{'); containers.Push(new Container(true)); }
    public void StartArray() { BeforeValue(); writer.Write('['); containers.Push(new Container(false)); }

    public void EndObject()
    {
        var container = containers.Pop();
        if (!container.IsObject || container.ExpectingValue) throw new InvalidOperationException("Invalid JSON object.");
        writer.Write('}');
    }

    public void EndArray()
    {
        if (containers.Pop().IsObject) throw new InvalidOperationException("Invalid JSON array.");
        writer.Write(']');
    }

    public void Name(string name)
    {
        var container = containers.Peek();
        if (!container.IsObject || container.ExpectingValue) throw new InvalidOperationException("Invalid JSON property.");
        if (!container.First) writer.Write(',');
        container.First = false;
        WriteString(name);
        writer.Write(':');
        container.ExpectingValue = true;
    }

    public void Property(string name, string? value) { Name(name); String(value); }
    public void Property(string name, int value) { Name(name); Number(value); }
    public void Property(string name, bool value) { Name(name); Boolean(value); }

    public void String(string? value)
    {
        BeforeValue();
        if (value is null) writer.Write("null");
        else WriteString(value);
    }

    public void Number(int value) { BeforeValue(); writer.Write(value.ToString(CultureInfo.InvariantCulture)); }
    public void Boolean(bool value) { BeforeValue(); writer.Write(value ? "true" : "false"); }

    private void BeforeValue()
    {
        if (containers.Count == 0)
        {
            if (hasRoot) throw new InvalidOperationException("JSON already has a root.");
            hasRoot = true;
            return;
        }
        var container = containers.Peek();
        if (container.IsObject)
        {
            if (!container.ExpectingValue) throw new InvalidOperationException("JSON object requires a property name.");
            container.ExpectingValue = false;
        }
        else
        {
            if (!container.First) writer.Write(',');
            container.First = false;
        }
    }

    private void WriteString(string value)
    {
        writer.Write('"');
        foreach (var character in value)
        {
            switch (character)
            {
                case '"': writer.Write("\\\""); break;
                case '\\': writer.Write("\\\\"); break;
                case '\b': writer.Write("\\b"); break;
                case '\f': writer.Write("\\f"); break;
                case '\n': writer.Write("\\n"); break;
                case '\r': writer.Write("\\r"); break;
                case '\t': writer.Write("\\t"); break;
                default:
                    // Escape HTML-sensitive characters as well, so JSON remains safe if a caller
                    // deliberately embeds it in a non-executable HTML JSON data block.
                    if (character < 0x20 || character == '<' || character == '>' || character == '&' ||
                        character == '\u2028' || character == '\u2029' || char.IsSurrogate(character))
                    {
                        writer.Write("\\u");
                        writer.Write(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else writer.Write(character);
                    break;
            }
        }
        writer.Write('"');
    }

    private sealed class Container
    {
        public Container(bool isObject) => IsObject = isObject;
        public bool IsObject { get; }
        public bool First { get; set; } = true;
        public bool ExpectingValue { get; set; }
    }
}
