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
    private static readonly HeadlessUnitTestSession Session =
        HeadlessUnitTestSession.GetOrStartForAssembly(typeof(Headless).Assembly);

    internal static Task RunAsync(Action body) =>
        Session.Dispatch(() => { body(); return Task.CompletedTask; }, CancellationToken.None);

    internal static Task RunAsync(Func<Task> body) =>
        Session.Dispatch(body, CancellationToken.None);
}
