namespace Archive.Data;

/// <summary>
/// How a save's schema stands relative to the migrations this build carries.
/// </summary>
/// <remarks>
/// The distinction that matters is <see cref="Behind"/> against everything else. Only a save that
/// is behind can be carried forward, because migrations only ever move in one direction: there is
/// no rollback, and there never will be. Every other unhappy state is refused, and the point of
/// naming them separately is that each needs different advice — telling someone to start a new
/// save when their save is simply newer than their app would throw away the newer one.
/// </remarks>
public enum SchemaState
{
    /// <summary>No migrations applied yet: a save that is about to be created.</summary>
    Empty,

    /// <summary>Exactly the migrations this build carries, and the schema they produce.</summary>
    UpToDate,

    /// <summary>An older build's save. The only state that can be upgraded.</summary>
    Behind,

    /// <summary>Made by a newer build. The app is what needs upgrading, not the save.</summary>
    Ahead,

    /// <summary>
    /// The same migrations by name, but not the schema they produce — or a set that is not a
    /// prefix of this build's. Nothing here can reconcile it.
    /// </summary>
    Diverged,
}

/// <summary>What an inspection of a save's schema found.</summary>
/// <param name="State">The classification.</param>
/// <param name="Pending">
/// Migrations this build has that the save has not run, in the order they would run.
/// </param>
/// <param name="Unknown">
/// Migrations the save has run that this build does not have. Non-empty only when
/// <see cref="State"/> is <see cref="SchemaState.Ahead"/>.
/// </param>
public sealed record SchemaStatus(
    SchemaState State,
    IReadOnlyList<string> Pending,
    IReadOnlyList<string> Unknown)
{
    /// <summary>True when the save can be opened as it stands.</summary>
    public bool IsUsable => State is SchemaState.UpToDate;

    /// <summary>True when carrying the save forward is possible and is all that is needed.</summary>
    public bool CanUpgrade => State is SchemaState.Behind;
}

/// <summary>
/// Thrown when a save is behind and nothing has said whether to carry it forward.
/// </summary>
/// <remarks>
/// Upgrading is one-way and cannot be undone in place, so it is never done as a side effect of
/// opening a save. This exception is how the decision reaches whoever is in a position to ask:
/// the CLI turns it into an instruction, the desktop head into a prompt.
/// </remarks>
public sealed class SchemaUpgradeRequiredException : InvalidOperationException
{
    public SchemaUpgradeRequiredException(string savePath, IReadOnlyList<string> pending)
        : base($"The save at '{savePath}' was made by an older version of ahistory and needs "
             + $"{pending.Count} migration(s) applied before it can be opened: "
             + $"{string.Join(", ", pending)}.")
    {
        SavePath = savePath;
        Pending = pending;
    }

    public string SavePath { get; }

    /// <summary>The migrations that would run, in order.</summary>
    public IReadOnlyList<string> Pending { get; }
}
