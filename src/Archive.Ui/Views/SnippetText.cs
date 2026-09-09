using Archive.Data;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Archive.Ui.Views;

/// <summary>
/// Renders a search snippet with the matched parts emphasised.
/// </summary>
/// <remarks>
/// <para>
/// An attached property rather than a converter, because the result is an
/// <see cref="InlineCollection"/> — several runs inside one wrapping paragraph — and a binding
/// can only produce a single value.
/// </para>
/// <para>
/// This is the cost of a native UI that a browser would not have had: in HTML the snippet is a
/// string with <c>&lt;mark&gt;</c> in it. It is also the safer shape — there is no markup being
/// parsed here, so a message containing something that looks like a tag is just text
/// (decisions.md D1).
/// </para>
/// </remarks>
public static class SnippetText
{
    public static readonly AttachedProperty<IReadOnlyList<SnippetSegment>?> SegmentsProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, IReadOnlyList<SnippetSegment>?>(
            "Segments", typeof(SnippetText));

    static SnippetText() => SegmentsProperty.Changed.AddClassHandler<TextBlock>(OnSegmentsChanged);

    public static IReadOnlyList<SnippetSegment>? GetSegments(TextBlock target) =>
        target.GetValue(SegmentsProperty);

    public static void SetSegments(TextBlock target, IReadOnlyList<SnippetSegment>? value) =>
        target.SetValue(SegmentsProperty, value);

    private static void OnSegmentsChanged(TextBlock target, AvaloniaPropertyChangedEventArgs args)
    {
        var segments = args.NewValue as IReadOnlyList<SnippetSegment>;

        target.Inlines?.Clear();

        if (segments is null || segments.Count == 0)
        {
            return;
        }

        target.Inlines ??= [];

        foreach (var segment in segments)
        {
            var run = new Run(segment.Text);

            if (segment.IsMatch)
            {
                run.FontWeight = FontWeight.Bold;
                run.Background = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xD5, 0x4F));
            }

            target.Inlines.Add(run);
        }
    }
}
