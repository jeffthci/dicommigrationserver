using System.Globalization;

namespace DicomMigration.Dicom;

/// <summary>
/// Splits a DICOM StudyDate range into smaller windows so the inventory can query a large
/// archive in paced steps instead of one open-ended C-FIND.
/// </summary>
public static class DateChunker
{
    private const string DicomDate = "yyyyMMdd";

    /// <summary>
    /// Returns the DICOM date ranges to query. A single-element list means "query this as-is"
    /// (including a null element for an unfiltered query).
    ///
    /// Chunking is skipped — deliberately, rather than guessing — when it cannot be done safely:
    /// when disabled, when there is no range to split, or when the range is open-ended
    /// ("20200101-" or "-20201231"), because the missing end is whatever the archive happens to
    /// hold and inventing a bound could silently skip studies.
    /// </summary>
    public static IReadOnlyList<string?> Split(string? studyDateRange, int chunkDays)
    {
        if (chunkDays <= 0 || string.IsNullOrWhiteSpace(studyDateRange))
            return new List<string?> { studyDateRange };

        var range = studyDateRange.Trim();
        var dash = range.IndexOf('-');

        // A single date (no dash) is already one window.
        if (dash < 0)
            return new List<string?> { range };

        var fromText = range[..dash];
        var toText = range[(dash + 1)..];

        if (!TryParse(fromText, out var from) || !TryParse(toText, out var to) || to < from)
            return new List<string?> { range };

        var chunks = new List<string?>();
        var cursor = from;

        while (cursor <= to)
        {
            // -1 because the window is inclusive on both ends: a 7-day chunk starting Monday
            // ends the following Sunday, not the Monday after.
            var end = cursor.AddDays(chunkDays - 1);
            if (end > to) end = to;

            chunks.Add(cursor.ToString(DicomDate, CultureInfo.InvariantCulture) + "-" +
                       end.ToString(DicomDate, CultureInfo.InvariantCulture));

            cursor = end.AddDays(1);
        }

        return chunks;
    }

    private static bool TryParse(string text, out DateTime value) =>
        DateTime.TryParseExact(text.Trim(), DicomDate, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out value);
}
