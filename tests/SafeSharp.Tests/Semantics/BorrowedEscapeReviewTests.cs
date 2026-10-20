using Microsoft.CodeAnalysis;

namespace SafeSharp.Tests.Semantics;

/// <summary>
/// Independent regressions for provenance snapshots, persistent captured storage, and
/// call contracts. Each fixture is compiled before the real semantic analyzer runs.
/// </summary>
public sealed class BorrowedEscapeReviewTests
{
    private const string Prelude = "using System; using System.Threading.Tasks; using SafeSharp; ";

    [Fact]
    public Task Indexer_receiver_is_evaluated_before_index_reassignment() => EscapesAsync("""
        sealed class Box { public object this[Box key] { set { } } }
        class C
        {
            void M([Borrowed] object borrowed, Box external)
            {
                var receiver = external;
                receiver[receiver = new Box()] = borrowed;
            }
        }
        """, "this[]");

    [Fact]
    public Task Array_receiver_is_evaluated_before_index_reassignment() => EscapesAsync("""
        class C
        {
            void M([Borrowed] object borrowed, object[] external)
            {
                var receiver = external;
                receiver[(receiver = new object[1]).Length - 1] = borrowed;
            }
        }
        """, "array element");

    [Fact]
    public Task Earlier_value_argument_preserves_borrow_before_later_branch_reassignment() => EscapesAsync("""
        class C
        {
            object? field;
            [ReturnsBorrowed("first")] static object First(object first, object second) => first;
            void M([Borrowed] object borrowed, bool choose)
            {
                field = First(borrowed, choose ? borrowed = new object() : borrowed = new object());
            }
        }
        """, "field");

    [Fact]
    public Task Earlier_value_argument_does_not_acquire_later_borrowed_assignment() => AcceptsAsync("""
        class C
        {
            object? field;
            [ReturnsBorrowed("first")] static object First(object first, object second) => first;
            void M([Borrowed] object borrowed, bool choose)
            {
                var initial = new object();
                field = First(initial, choose ? initial = borrowed : initial = borrowed);
            }
        }
        """);

    [Fact]
    public Task Local_function_write_reaches_previously_escaped_captured_cell() => EscapesAsync("""
        class C
        {
            Func<object>? callback;
            void M([Borrowed] object borrowed)
            {
                object cell = new object();
                callback = () => cell;
                void Set() { cell = borrowed; }
                Set();
            }
        }
        """, "callback");

    [Fact]
    public Task Replacement_container_in_escaped_capture_remains_persistent() => EscapesAsync("""
        class C
        {
            Func<object[]>? callback;
            void M([Borrowed] object borrowed)
            {
                var cell = new object[1];
                callback = () => cell;
                var replacement = new object[1];
                cell = replacement;
                replacement[0] = borrowed;
            }
        }
        """, "callback");

    [Theory]
    [InlineData("catch (Exception)")]
    [InlineData("finally")]
    public Task Exception_handler_preserves_prior_captured_storage_escape(string handler) => EscapesAsync("""
        class C
        {
            Func<object[]>? callback;
            void M([Borrowed] object borrowed)
            {
                var cell = new object[1];
                callback = () => cell;
                try { throw new Exception(); }
        """ + handler + """
                { cell[0] = borrowed; }
            }
        }
        """, "callback");

    [Fact]
    public Task Branch_merge_keeps_possible_borrowed_origin() => EscapesAsync("""
        class C
        {
            object? field;
            void M([Borrowed] object borrowed, bool choose)
            {
                object alias = new object();
                if (choose) alias = borrowed;
                field = alias;
            }
        }
        """, "field");

    [Fact]
    public Task Loop_fixed_point_keeps_borrow_from_an_earlier_iteration() => EscapesAsync("""
        class C
        {
            object? field;
            void M([Borrowed] object borrowed, int remaining)
            {
                object alias = new object();
                while (remaining-- > 0)
                {
                    field = alias;
                    alias = borrowed;
                }
            }
        }
        """, "field");

    [Fact]
    public Task Declared_borrowed_return_remains_a_valid_loan() => AcceptsAsync("""
        class C
        {
            [ReturnsBorrowed("borrowed")]
            object M([Borrowed] object borrowed) => borrowed;
        }
        """);

    [Fact]
    public Task DoesNotEscape_return_cannot_be_exempted_by_returned_borrow_contract() => EscapesAsync("""
        class C
        {
            [ReturnsBorrowed("borrowed")]
            object M([DoesNotEscape] object borrowed) => borrowed;
        }
        """, "return from 'M'", noEscape: true);

    [Fact]
    public Task Declared_returned_closure_borrow_is_permitted_at_the_escape_boundary() => AcceptsAsync("""
        class C
        {
            [ReturnsBorrowed("borrowed")]
            Func<object> M([Borrowed] object borrowed) => () => borrowed;
        }
        """);

    [Fact]
    public Task DoesNotEscape_capture_is_not_exempted_by_returned_closure_borrow() => EscapesAsync("""
        class C
        {
            [ReturnsBorrowed("borrowed")]
            Func<object> M([DoesNotEscape] object borrowed) => () => borrowed;
        }
        """, "return from 'M'", noEscape: true);

    [Fact]
    public Task DoesNotEscape_argument_cannot_be_consumed_by_conversion() => EscapesAsync("""
        sealed class Result { }
        sealed class Source
        {
            public static implicit operator Result([Consumes] Source source) => new Result();
        }
        class C
        {
            void M([DoesNotEscape] Source borrowed)
            {
                Result result = borrowed;
                _ = result;
            }
        }
        """, "consuming parameter 'source'", noEscape: true);

    [Fact]
    public Task DoesNotEscape_argument_cannot_be_consumed_by_discarded_constructor() => EscapesAsync("""
        sealed class Consumer { public Consumer([Consumes] object input) { } }
        class C
        {
            void M([DoesNotEscape] object borrowed) { _ = new Consumer(borrowed); }
        }
        """, "consuming parameter 'input'", noEscape: true);

    [Fact]
    public Task DoesNotEscape_argument_cannot_be_consumed_by_discarded_operator() => EscapesAsync("""
        sealed class Source
        {
            public static Source operator +([Consumes] Source left, Source right) => new Source();
        }
        class C
        {
            void M([DoesNotEscape] Source borrowed) { _ = borrowed + new Source(); }
        }
        """, "consuming parameter 'left'", noEscape: true);

    [Fact]
    public Task DoesNotEscape_argument_cannot_be_consumed_by_ordinary_call() => EscapesAsync("""
        class C
        {
            static void Consume([Consumes] object input) { }
            void M([DoesNotEscape] object borrowed) { Consume(borrowed); }
        }
        """, "consuming parameter 'input'", noEscape: true);

    [Fact]
    public Task Built_in_delegate_combination_retains_borrowed_invocation_list() => EscapesAsync("""
        class C
        {
            Action? field;
            void M([Borrowed] Action borrowed, Action other) { field = borrowed + other; }
        }
        """, "field");

    [Theory]
    [InlineData("Borrowed")]
    [InlineData("BorrowedMut")]
    [InlineData("DoesNotEscape")]
    public Task Constructor_borrow_contract_does_not_imply_retained_contents(string contract) => AcceptsAsync("""
        sealed class Temporary
        {
        """ + "public Temporary([" + contract + "] object input) { Console.WriteLine(input); }" + """
        }
        class C
        {
            Temporary? field;
            void M([Borrowed] object borrowed) { field = new Temporary(borrowed); }
        }
        """);

    [Theory]
    [InlineData("Task<object>", "await borrowed")]
    [InlineData("Task<object>", "await borrowed.ConfigureAwait(false)")]
    [InlineData("ValueTask<object>", "await borrowed")]
    public Task Awaited_payload_does_not_inherit_unrelated_carrier_origin(string carrier, string expression) => AcceptsAsync("""
        class C
        {
            object? field;
        """ + "async Task M([Borrowed] " + carrier + " borrowed) { field = " + expression + "; }" + """
        }
        """);

    [Fact]
    public Task Custom_await_result_does_not_inherit_unrelated_stored_contents() => AcceptsAsync("""
        sealed class Awaitable
        {
            readonly object input;
            public Awaitable(object input) { this.input = input; }
            public System.Runtime.CompilerServices.TaskAwaiter<object> GetAwaiter()
                => Task.FromResult(new object()).GetAwaiter();
        }
        class C
        {
            object? field;
            async Task M([Borrowed] object borrowed)
            {
                var carrier = new Awaitable(borrowed);
                field = await carrier;
            }
        }
        """);

    [Theory]
    [InlineData("await Task.FromResult(borrowed)")]
    [InlineData("await Task.FromResult(borrowed).ConfigureAwait(false)")]
    [InlineData("await new ValueTask<object>(borrowed)")]
    [InlineData("await await Task.FromResult(Task.FromResult(borrowed))")]
    public Task Known_awaited_payload_preserves_borrowed_origin(string expression) => EscapesAsync("""
        class C
        {
            object? field;
        """ + "async Task M([Borrowed] object borrowed) { field = " + expression + "; }" + """
        }
        """, "field");

    private static Task AcceptsAsync(string source) => SemanticsAssert.NoDiagnosticAsync(Prelude + source);

    private static async Task EscapesAsync(string source, string destination, bool noEscape = false)
    {
        var diagnostics = await SemanticsAssert.AnalyzeAsync(Prelude + source);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "SS0046");
        var escaped = diagnostics.Where(diagnostic => diagnostic.Id == "SS0027").ToArray();
        Assert.NotEmpty(escaped);
        Assert.Contains(escaped, diagnostic => diagnostic.Properties.TryGetValue("SafeSharp.EscapeDestination", out var actual)
            && actual is not null && actual.Contains(destination, StringComparison.Ordinal));
        Assert.All(escaped, diagnostic =>
        {
            Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
            Assert.Equal("borrowed", diagnostic.Properties["SafeSharp.BorrowedSource"]);
            Assert.Equal("Possible", diagnostic.Properties["SafeSharp.Certainty"]);
            Assert.Equal(noEscape ? "true" : "false", diagnostic.Properties["SafeSharp.DoesNotEscape"]);
            Assert.NotEmpty(diagnostic.AdditionalLocations);
        });
    }
}
