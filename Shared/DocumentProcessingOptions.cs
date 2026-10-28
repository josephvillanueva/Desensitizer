using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace Desensitizer;

// Options for one Word export. The defaults replace each removed picture or
// embedded object with a visible placeholder that records its size.
// Image metadata is omitted by default because it may contain unreviewed names.
public sealed class DocumentProcessingOptions
{
    public const string DefaultImagePlaceholder = "[IMAGE REMOVED]";
    public const int MaxPlaceholderLength = 120;

    // Placeholder label. A null or empty value removes images silently, which
    // was the behavior before placeholders existed. A label ending in "]" gets
    // its details inside the brackets: "[IMAGE REMOVED: 2.50 x 1.75 in; alt text: ...]".
    public string? ImagePlaceholder { get; set; } = DefaultImagePlaceholder;
    // Append the size in inches when the drawing declares one.
    public bool IncludeImageSize { get; set; } = true;
    // Append the alt text, but only after it was passed through the same
    // mappings as the document body. The export audit still checks it.
    public bool IncludeImageAltText { get; set; } = false;
    // Longest alt text kept in a placeholder, counted after mapping.
    public int MaxAltTextLength { get; set; } = 200;

    public bool PlaceholdersEnabled => !string.IsNullOrEmpty(ImagePlaceholder);

    private static readonly Regex TokenShape = new(@"\[[A-Z_]+_\d{3,}\]", RegexOptions.CultureInvariant);

    // A placeholder must never look like a replacement token. Rehydration counts
    // unknown tokens of that shape, and detectors treat them as protected values.
    public void Validate()
    {
        if (MaxAltTextLength < 0) throw new ArgumentException("The alt text length limit cannot be negative.");
        if (!PlaceholdersEnabled) return;
        var label = ImagePlaceholder!;
        if (label.Length > MaxPlaceholderLength)
            throw new ArgumentException("The image placeholder is longer than " + MaxPlaceholderLength + " characters.");
        if (label.Any(c => char.IsControl(c) || (c >= '' && c <= '')))
            throw new ArgumentException("The image placeholder cannot contain control or private-use characters.");
        if (string.IsNullOrWhiteSpace(label))
            throw new ArgumentException("The image placeholder cannot be blank. Use an empty value to remove images silently.");
        if (TokenShape.IsMatch(label))
            throw new ArgumentException("The image placeholder cannot look like a replacement token such as [PERSON_001].");
    }
}
