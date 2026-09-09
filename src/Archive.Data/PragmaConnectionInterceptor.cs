using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Archive.Data;

/// <summary>
/// Applies the save's pragma block to connections EF Core opens for itself.
/// </summary>
/// <remarks>
/// Without this there are two kinds of connection to the same file: ours, which enforces foreign
/// keys and fires triggers on cascaded deletes, and EF's, which does neither. Bugs from that
/// divergence do not look like connection bugs — they look like the data being wrong — so the
/// two paths are made indistinguishable instead.
/// </remarks>
public sealed class PragmaConnectionInterceptor : DbConnectionInterceptor
{
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        Database.Configure(connection);
        base.ConnectionOpened(connection, eventData);
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        Database.Configure(connection);
        await base.ConnectionOpenedAsync(connection, eventData, cancellationToken).ConfigureAwait(false);
    }
}
