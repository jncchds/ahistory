namespace Archive.Import.Tests;

/// <summary>What an importer pushed, in the order it pushed it.</summary>
internal sealed record RecordedMessage(NormalizedThread Thread, NormalizedMessage Message);

/// <summary>
/// Collects an importer's output without a database.
/// </summary>
/// <remarks>
/// Importer tests assert on what was read, not on what was stored. Going through a save would
/// mean a parsing bug and a committing bug produce the same failure, and each importer's job is
/// only to turn its own format into normalized messages correctly.
/// </remarks>
internal sealed class RecordingSink : IImportSink
{
    internal NormalizedIdentity? Owner { get; private set; }

    internal List<NormalizedThread> Threads { get; } = [];

    internal List<RecordedMessage> Messages { get; } = [];

    public void OnOwner(NormalizedIdentity owner) => Owner = owner;

    public void OnThread(NormalizedThread thread) => Threads.Add(thread);

    public void OnMessage(NormalizedThread thread, NormalizedMessage message) =>
        Messages.Add(new RecordedMessage(thread, message));
}
