using System.Collections.ObjectModel;
using Archive.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Archive.Ui.ViewModels;

/// <summary>
/// One platform account, shown inside the person it belongs to.
/// </summary>
/// <remarks>
/// The account rows live under a person rather than in a list of their own. Two flat lists side by
/// side made the one thing that matters — which accounts are the same human — something the reader
/// had to reconstruct by matching a name in one list against a line of small text in the other.
/// </remarks>
public sealed partial class AccountItem : ObservableObject
{
    private readonly Func<IdentityRow, PersonRow, Task> _move;
    private readonly Func<IdentityRow, Task> _detach;

    private bool _resetting;

    public AccountItem(
        IdentityRow row,
        IReadOnlyList<PersonRow> moveTargets,
        Func<IdentityRow, PersonRow, Task> move,
        Func<IdentityRow, Task> detach)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(moveTargets);

        Row = row;
        MoveTargets = [.. moveTargets];
        _move = move ?? throw new ArgumentNullException(nameof(move));
        _detach = detach ?? throw new ArgumentNullException(nameof(detach));
    }

    public IdentityRow Row { get; }

    /// <summary>Everyone this account could be moved to — everyone except its current person.</summary>
    public ObservableCollection<PersonRow> MoveTargets { get; }

    /// <summary>
    /// Picking someone moves the account to them.
    /// </summary>
    /// <remarks>
    /// Reset to null immediately, so the box reads as an action rather than as a setting: the row
    /// is about to vanish from this person's list, and leaving a name showing in it would suggest
    /// the move had not happened. It also keeps the box honest when the move is held back for an
    /// owner confirmation and may yet be cancelled.
    /// </remarks>
    [ObservableProperty]
    private PersonRow? _moveTarget;

    /// <summary>Which platform, spelled out — the id is for keys, not for reading.</summary>
    public string PlatformLabel => Row.Platform switch
    {
        "telegram" => "Telegram",
        "hangouts" => "Hangouts",
        "googlechat" => "Google Chat",
        "messenger" => "Messenger",
        "instagram" => "Instagram",
        "sms" => "SMS",
        "whatsapp" => "WhatsApp",
        "skype" => "Skype",
        "discord" => "Discord",
        "vk" => "VK",
        "qip" => "QIP",
        var other => other,
    };

    /// <summary>
    /// True when this account is the archive owner's, taken from the export itself.
    /// </summary>
    /// <remarks>
    /// Such an account cannot be detached — it is what makes "me" definite (P5) — so the button is
    /// not offered rather than offered and refused.
    /// </remarks>
    public bool IsSeeded => Row.Confidence == "seed";

    [RelayCommand]
    private Task Detach() => _detach(Row);

    partial void OnMoveTargetChanged(PersonRow? value)
    {
        if (_resetting || value is null)
        {
            return;
        }

        _resetting = true;
        MoveTarget = null;
        _resetting = false;

        _ = _move(Row, value);
    }
}

/// <summary>
/// One suggested match, seen from the person currently being read.
/// </summary>
/// <remarks>
/// A suggestion is symmetric — two accounts that might be one human — but it is always looked at
/// from somewhere. Naming the <em>other</em> side is what makes the row a sentence: "this person
/// might also be that one".
/// </remarks>
public sealed partial class MatchItem : ObservableObject
{
    private readonly Func<MergeSuggestion, Task> _accept;
    private readonly Func<MergeSuggestion, Task> _reject;

    public MatchItem(
        MergeSuggestion suggestion,
        string viewedPersonId,
        Func<MergeSuggestion, Task> accept,
        Func<MergeSuggestion, Task> reject)
    {
        ArgumentNullException.ThrowIfNull(suggestion);

        Suggestion = suggestion;
        _accept = accept ?? throw new ArgumentNullException(nameof(accept));
        _reject = reject ?? throw new ArgumentNullException(nameof(reject));

        var otherIsRight = string.Equals(suggestion.LeftPersonId, viewedPersonId, StringComparison.Ordinal);

        OtherPersonName = otherIsRight ? suggestion.RightPersonName : suggestion.LeftPersonName;
        OtherAccountName = otherIsRight ? suggestion.RightDisplayName : suggestion.LeftDisplayName;
        OtherPlatform = otherIsRight ? suggestion.RightPlatform : suggestion.LeftPlatform;
    }

    public MergeSuggestion Suggestion { get; }

    public string OtherPersonName { get; }

    public string OtherAccountName { get; }

    public string OtherPlatform { get; }

    public string Reason => Suggestion.Reason;

    [RelayCommand]
    private Task Accept() => _accept(Suggestion);

    [RelayCommand]
    private Task Reject() => _reject(Suggestion);
}

/// <summary>Which people the list shows.</summary>
/// <param name="Label">What the filter is called in the dropdown.</param>
public sealed record PeopleFilter(string Label, PeopleFilterKind Kind)
{
    public override string ToString() => Label;
}

/// <summary>
/// The three ways of looking at the people list.
/// </summary>
/// <remarks>
/// The last two are the work: a merge feature is used by finding the handful of rows that need
/// attention among hundreds that do not, and a list with no way to narrow it is one nobody
/// finishes going through.
/// </remarks>
public enum PeopleFilterKind
{
    Everyone,
    WithPossibleMatches,
    WithUnconfirmedAccounts,
}
