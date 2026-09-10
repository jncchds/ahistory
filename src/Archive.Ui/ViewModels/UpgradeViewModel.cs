using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Archive.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Archive.Ui.ViewModels;

/// <summary>
/// Asks whether to carry a save made by an older version forward to this one.
/// </summary>
/// <remarks>
/// <para>
/// Shown instead of the main window, not over it. There is nothing to browse until the save is
/// readable, and a prompt on top of an empty archive reads as an error rather than a question.
/// </para>
/// <para>
/// The upgrade is one-way — the schema only ever moves forward — so this asks rather than tells,
/// and takes a copy of the save first unless that is explicitly turned off.
/// </para>
/// </remarks>
public sealed partial class UpgradeViewModel : ObservableObject
{
    private readonly Database _database;
    private readonly string _backupPath;
    private readonly ILogger _log;

    public UpgradeViewModel(
        Database database,
        IReadOnlyList<string> pending,
        string backupPath,
        ILogger<UpgradeViewModel>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(pending);

        _database = database;
        _backupPath = backupPath;
        _log = logger ?? NullLogger<UpgradeViewModel>.Instance;

        SavePath = database.DatabasePath;
        Pending = new ObservableCollection<string>(pending);
    }

    /// <summary>Raised once the save is usable, so the head can open the archive proper.</summary>
    public event EventHandler? Completed;

    public string SavePath { get; }

    public ObservableCollection<string> Pending { get; }

    public string Summary =>
        Pending.Count == 1
            ? "This save was made by an older version of ahistory. One change to its structure is needed before it can be opened."
            : $"This save was made by an older version of ahistory. {Pending.Count} changes to its structure are needed before it can be opened.";

    public string BackupPath => _backupPath;

    [ObservableProperty]
    private bool _backUpFirst = true;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _error;

    /// <summary>
    /// Applies the pending migrations, having said what it is about to do.
    /// </summary>
    /// <remarks>
    /// On a background thread because a large archive is copied first, and a window that stops
    /// redrawing looks like a window that has crashed.
    /// </remarks>
    [RelayCommand]
    private async Task UpgradeAsync()
    {
        IsBusy = true;
        Error = null;

        try
        {
            var backup = BackUpFirst ? _backupPath : null;

            var applied = await Task.Run(() => _database.Upgrade(backup)).ConfigureAwait(true);

            _log.LogInformation("Upgraded the save by {Count} migration(s).", applied.Count);

            Completed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            // The save is untouched or restored from the copy; either way the archive is not
            // half-migrated, because each migration runs in its own transaction.
            _log.LogError(ex, "Upgrading the save failed.");
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
