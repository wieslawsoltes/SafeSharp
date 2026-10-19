using SafeSharp.Reporting;

namespace SafeSharp.Tool;

internal enum FailureThreshold { None, Warning, Error }
internal enum InputKind { Source, Project, Solution }

internal sealed class ToolOptions
{
    public InputKind InputKind { get; private set; }
    public List<string> Inputs { get; } = [];
    public List<string> References { get; } = [];
    public List<string> Defines { get; } = [];
    public ReportFormat Format { get; private set; } = ReportFormat.Html;
    public FailureThreshold FailureThreshold { get; private set; } = FailureThreshold.Warning;
    public string? Output { get; private set; }
    public string Configuration { get; private set; } = "Debug";
    public string? Framework { get; private set; }
    public bool Restore { get; private set; } = true;
    public bool Help { get; private set; }
    public bool Version { get; private set; }

    public static ToolOptions Parse(string[] args)
    {
        var options = new ToolOptions();
        InputKind? inputKind = null;
        for (var index = 0; index < args.Length; index++)
        {
            string Value()
            {
                if (++index >= args.Length || args[index].StartsWith("--", StringComparison.Ordinal))
                    throw new ToolException($"Option '{args[index - 1]}' requires a value.");
                return args[index];
            }

            void Input(InputKind kind)
            {
                if (inputKind.HasValue && (kind != inputKind.Value || kind != InputKind.Source))
                    throw new ToolException("Choose exactly one of --project, --solution, or one or more --source files.");
                inputKind = kind;
                options.InputKind = kind;
                options.Inputs.Add(Value());
            }

            switch (args[index])
            {
                case "--help" or "-h": options.Help = true; break;
                case "--version": options.Version = true; break;
                case "--source": Input(InputKind.Source); break;
                case "--project": Input(InputKind.Project); break;
                case "--solution": Input(InputKind.Solution); break;
                case "--reference": options.References.Add(Value()); break;
                case "--define": options.Defines.Add(Value()); break;
                case "--output" or "-o": options.Output = Value(); break;
                case "--configuration": options.Configuration = Value(); break;
                case "--framework": options.Framework = Value(); break;
                case "--no-restore": options.Restore = false; break;
                case "--format":
                    var format = Value();
                    if (!Enum.TryParse<ReportFormat>(format, true, out var parsedFormat) || !Enum.IsDefined(parsedFormat))
                        throw new ToolException($"Unknown format '{format}'. Expected json, sarif, dot, mermaid, or html.");
                    options.Format = parsedFormat;
                    break;
                case "--fail-on":
                    var threshold = Value();
                    if (!Enum.TryParse<FailureThreshold>(threshold, true, out var parsedThreshold) || !Enum.IsDefined(parsedThreshold))
                        throw new ToolException($"Unknown failure threshold '{threshold}'. Expected warning, error, or none.");
                    options.FailureThreshold = parsedThreshold;
                    break;
                default: throw new ToolException($"Unknown argument '{args[index]}'. Run safesharp --help for usage.");
            }
        }
        if (options.Help || options.Version) return options;
        if (!inputKind.HasValue) throw new ToolException("Supply --project, --solution, or --source. Run safesharp --help for usage.");
        if (inputKind != InputKind.Source && (options.References.Count > 0 || options.Defines.Count > 0))
            throw new ToolException("--reference and --define apply to --source mode. Project settings come from MSBuild.");
        if (inputKind == InputKind.Source && options.Framework is not null)
            throw new ToolException("--framework applies to MSBuild project or solution analysis; --source uses the tool runtime's framework.");
        for (var index = 0; index < options.Inputs.Count; index++)
        {
            options.Inputs[index] = Path.GetFullPath(options.Inputs[index]);
            if (!File.Exists(options.Inputs[index])) throw new ToolException($"Input file does not exist: {options.Inputs[index]}");
        }
        for (var index = 0; index < options.References.Count; index++)
        {
            options.References[index] = Path.GetFullPath(options.References[index]);
            if (!File.Exists(options.References[index])) throw new ToolException($"Reference assembly does not exist: {options.References[index]}");
        }
        if (options.Output is not null && options.Output != "-") options.Output = Path.GetFullPath(options.Output);
        return options;
    }

    public const string Usage = """
        SafeSharp — C# ownership, borrowing, disposal, and service lifetime analysis

        Usage:
          safesharp --project App.csproj --format html --output ownership.html
          safesharp --solution App.sln --format sarif --output ownership.sarif
          safesharp --source Example.cs [--source More.cs] --format json

        Inputs (choose one mode):
          --project FILE        Load one C# project with its MSBuild references and settings.
          --solution FILE       Load and analyze every C# project in a solution.
          --source FILE         Analyze a standalone C# file; repeat for multiple files.

        Reporting:
          --format FORMAT       html (default), json, sarif, dot, or mermaid.
          --output, -o FILE     Write atomically to FILE. Default or '-' writes to stdout.
          --fail-on LEVEL       warning (default), error, or none. Findings return exit code 1.

        Project/solution settings:
          --configuration NAME  MSBuild configuration; default Debug.
          --framework TFM       Select a target framework for a multi-target project.
          --no-restore          Skip the default 'dotnet restore'; assets must already exist.

        Standalone-source settings:
          --reference FILE      Add a metadata assembly; repeat as needed.
          --define SYMBOL       Add a conditional-compilation symbol; repeat as needed.

        Exit codes: 0 completed below threshold; 1 findings; 2 input/build/analyzer failure
                    or incomplete analysis; 130 canceled. --fail-on none never hides failures.
        --help, -h              Show this help.
        --version               Show the installed version.

        Project analysis requires an installed .NET SDK. Standalone analysis uses the tool's
        runtime reference assemblies and does not represent another target framework.
        JSON/HTML include computed CFG and instruction states; SARIF includes precise source
        locations and abstract provenance. No external graph renderer is required for HTML.
        """;
}

internal sealed class ToolException : Exception
{
    public ToolException(string message) : base(message) { }
    public ToolException(string message, Exception innerException) : base(message, innerException) { }
}
