namespace Archive.Ui.Services;

/// <summary>
/// Asks the user for a folder.
/// </summary>
/// <remarks>
/// An interface so view models stay free of Avalonia and can be tested without a window. It is
/// also the thing a browser-hosted UI could never have provided — picking an export folder by
/// typing its path was the main cost of not going native (decisions.md D1).
/// </remarks>
public interface IFolderPicker
{
    Task<string?> PickAsync(string title);
}
