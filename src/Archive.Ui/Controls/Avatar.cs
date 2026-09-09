using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace Archive.Ui.Controls;

/// <summary>
/// A circle with someone's initials, coloured from their name.
/// </summary>
/// <remarks>
/// There are no profile pictures in a Telegram export, so this is what a conversation list has to
/// be scannable by. The colour is derived from the name rather than assigned, which means the same
/// person is the same colour every time the app opens — that consistency is the whole point, and
/// it is why a random colour per session would be worse than none.
/// </remarks>
public sealed class Avatar : TemplatedControl
{
    public static readonly StyledProperty<string?> DisplayNameProperty =
        AvaloniaProperty.Register<Avatar, string?>(nameof(DisplayName));

    public static readonly StyledProperty<string> InitialsProperty =
        AvaloniaProperty.Register<Avatar, string>(nameof(Initials), string.Empty);

    public static readonly StyledProperty<IBrush?> CircleBrushProperty =
        AvaloniaProperty.Register<Avatar, IBrush?>(nameof(CircleBrush));

    /// <summary>How many avatar colours the palette defines.</summary>
    private const int Palette = 7;

    static Avatar() =>
        DisplayNameProperty.Changed.AddClassHandler<Avatar>((avatar, _) => avatar.Rebuild());

    public string? DisplayName
    {
        get => GetValue(DisplayNameProperty);
        set => SetValue(DisplayNameProperty, value);
    }

    public string Initials
    {
        get => GetValue(InitialsProperty);
        private set => SetValue(InitialsProperty, value);
    }

    public IBrush? CircleBrush
    {
        get => GetValue(CircleBrushProperty);
        private set => SetValue(CircleBrushProperty, value);
    }

    private void Rebuild()
    {
        var name = DisplayName;

        Initials = InitialsOf(name);
        CircleBrush = BrushFor(name);
    }

    /// <summary>
    /// One or two letters from a name.
    /// </summary>
    /// <remarks>
    /// Works on the first character of each of the first two words, whatever script they are in —
    /// Cyrillic names are as common in this archive as Latin ones, and anything that assumed
    /// ASCII would render half the contact list blank.
    /// </remarks>
    internal static string InitialsOf(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "?";
        }

        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var letters = new List<string>(2);

        foreach (var word in words)
        {
            // The first *text element*, not the first char: an emoji or a combining pair is one
            // visible character made of several.
            var enumerator = StringInfo.GetTextElementEnumerator(word);

            if (enumerator.MoveNext() && enumerator.GetTextElement() is { Length: > 0 } element)
            {
                letters.Add(element.ToUpperInvariant());
            }

            if (letters.Count == 2)
            {
                break;
            }
        }

        return letters.Count > 0 ? string.Concat(letters) : "?";
    }

    private IBrush? BrushFor(string? name)
    {
        var index = StableIndex(name);

        return this.FindResource($"Avatar{index}") as IBrush;
    }

    /// <summary>
    /// A palette slot for a name, stable across runs.
    /// </summary>
    /// <remarks>
    /// <see cref="string.GetHashCode()"/> is randomized per process, so using it would give
    /// someone a different colour every launch. This is a plain sum, which is a poor hash and
    /// exactly the right one: it only has to spread names across seven buckets, and it has to do
    /// it the same way tomorrow.
    /// </remarks>
    internal static int StableIndex(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return 0;
        }

        var total = 0;

        foreach (var c in name)
        {
            total = (total + c) % Palette;
        }

        return total;
    }
}
