using Avalonia.Headless;

namespace Archive.Ui.Tests;

/// <summary>
/// Runs a test body on Avalonia's headless UI thread.
/// </summary>
/// <remarks>
/// <para>
/// Avalonia ships an xUnit adapter that would provide an [AvaloniaFact] attribute, but for
/// Avalonia 12 it depends on xUnit v3, and the rest of this solution is on v2 (AGENTS.md). Two
/// test frameworks in one repository is a worse tax than this helper, which is the only thing
/// the adapter would have given us.
/// </para>
/// <para>
/// Note the async overload. View models continue on the UI thread by design, so blocking that
/// thread to wait for one — GetAwaiter().GetResult() inside a test body — deadlocks instantly:
/// the continuation needs the very thread the test is holding.
/// </para>
/// </remarks>
internal static class Headless
{
    /// <summary>
    /// The collection every test that opens a window belongs to.
    /// </summary>
    /// <remarks>
    /// There is one headless session per assembly and one UI thread inside it, and xUnit runs test
    /// <em>classes</em> in parallel. So two classes dispatching into that thread at once is the
    /// default arrangement, not an unlucky one — and it surfaced as
    /// "the calling thread cannot access this object because a different thread owns it", from a
    /// window one of them had opened. It failed only on the Windows runner, which is to say it
    /// failed wherever the timing happened to line up, and never on the machine the tests were
    /// written on.
    ///
    /// Putting them in one collection serializes them against each other and against everything
    /// else, which costs a few seconds of a suite that takes eight.
    /// </remarks>
    internal const string Collection = "avalonia";

    private static readonly HeadlessUnitTestSession Session =
        HeadlessUnitTestSession.GetOrStartForAssembly(typeof(Headless).Assembly);

    internal static Task RunAsync(Action body) =>
        Session.Dispatch(() => { body(); return Task.CompletedTask; }, CancellationToken.None);

    internal static Task RunAsync(Func<Task> body) =>
        Session.Dispatch(body, CancellationToken.None);
}

/// <summary>Serializes the tests that share Avalonia's one headless UI thread.</summary>
[CollectionDefinition(Headless.Collection, DisableParallelization = true)]
public sealed class HeadlessCollection;
