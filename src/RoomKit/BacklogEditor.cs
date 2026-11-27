using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace RoomKit;

/// <summary>A text file split into lines for editing: the lines without their <c>\n</c> (a <c>\r</c> stays part of
/// its line), whether the file ends with a newline and whether it starts with a UTF-8 BOM. Turning it back into
/// bytes gives the same bytes for every line that was not changed.</summary>
public sealed class EditableText
{
    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public EditableText(IReadOnlyList<string> lines, bool finalNewline, bool hasBom)
    {
        Lines = lines;
        FinalNewline = finalNewline;
        HasBom = hasBom;
    }

    public IReadOnlyList<string> Lines { get; }

    public bool FinalNewline { get; }

    public bool HasBom { get; }

    /// <summary>Splits UTF-8 bytes into lines, or returns <c>null</c> when they are not valid UTF-8.</summary>
    public static EditableText? Parse(byte[] bytes)
    {
        bool hasBom = bytes.AsSpan().StartsWith(Bom);
        string text;
        try
        {
            text = StrictUtf8.GetString(hasBom ? bytes[Bom.Length..] : bytes);
        }
        catch (ArgumentException)
        {
            return null;
        }

        if (text.Length == 0)
        {
            return new EditableText([], true, hasBom);
        }

        bool finalNewline = text.EndsWith('\n');
        string body = finalNewline ? text[..^1] : text;
        return new EditableText(body.Split('\n'), finalNewline, hasBom);
    }

    /// <summary>The same file with other lines.</summary>
    public EditableText With(IReadOnlyList<string> lines)
    {
        return new EditableText(lines, FinalNewline, HasBom);
    }

    public string Text => string.Join("\n", Lines) + (FinalNewline && Lines.Count > 0 ? "\n" : "");

    public byte[] ToBytes()
    {
        byte[] text = StrictUtf8.GetBytes(Text);
        return HasBom ? [.. Bom, .. text] : text;
    }

    /// <summary>A line without its trailing <c>\r</c>.</summary>
    public static string Content(string line)
    {
        return line.TrimEnd('\r');
    }
}

/// <summary>One edited backlog: the new text and the subject for the commit message (<c>#N</c>, or
/// <c>line &lt;index+1&gt;</c> for an unnumbered line).</summary>
public sealed record BacklogEdit(EditableText Text, string Subject);

/// <summary>Where <c>add</c> puts the new line.</summary>
public enum AddPosition
{
    Top,
    Bottom,
}

/// <summary>Where <c>top</c> moves a line.</summary>
public enum MoveDirection
{
    Top,
}

/// <summary>Pure edits of <c>backlog.md</c> lines and story text. Every method returns <c>null</c> when the change
/// is not allowed (wrong line, wrong mark, a change that changes nothing). Slug, next number, story template and
/// the top and bottom positions follow <c>factory/tools/job.py</c>.</summary>
public static partial class BacklogEditor
{
    public const int MaxTitleLength = 200;

    public const int MaxNoteLength = 1000;

    public const int MaxCaptureLength = 2000;

    public const int MaxCaptureTitleLength = 60;

    private const string CaptureWhy = "Still to be written: this is a quick-capture draft.";


    private const string NotesHeading = "## Notes";

    private const string ProposedByJob = "Proposed by job";

    private const string StopPrefix = "- [stop]";

    /// <summary>The subject of every stop line change: its commit message is <c>backlog: stop line</c>.</summary>
    public const string StopSubject = "stop line";

    public static BacklogEdit? Approve(EditableText text, int line)
    {
        if (!IsMark(text, line, '?'))
        {
            return null;
        }

        string raw = text.Lines[line];
        List<string> lines = [.. text.Lines];
        lines[line] = "- [ ] " + raw[6..];
        return new BacklogEdit(text.With(lines), Subject(text, line));
    }

    /// <summary>Ticks off a <c>[h]</c> line: <c>- [h] </c> becomes <c>- [x] </c>, every other byte stays.</summary>
    public static BacklogEdit? Done(EditableText text, int line)
    {
        if (!IsMark(text, line, 'h'))
        {
            return null;
        }

        string raw = text.Lines[line];
        List<string> lines = [.. text.Lines];
        lines[line] = "- [x] " + raw[6..];
        return new BacklogEdit(text.With(lines), Subject(text, line));
    }

    /// <summary>Removes a <c>[?]</c> or <c>[ ]</c> line; the caller removes its story file and checks
    /// <see cref="WaitedOn"/> first.</summary>
    public static BacklogEdit? Drop(EditableText text, int line)
    {
        if (!IsMark(text, line, '?') && !IsMark(text, line, ' '))
        {
            return null;
        }

        List<string> lines = [.. text.Lines];
        lines.RemoveAt(line);
        return new BacklogEdit(text.With(lines), Subject(text, line));
    }

    /// <summary>True when <paramref name="line"/> is numbered and another item line that is not done (any mark
    /// but <c>x</c>/<c>X</c>) waits for that number in its <c>(after: …)</c>: a <c>#N</c> or a
    /// <c>&lt;Project&gt;#N</c> whose project is <paramref name="projectName"/> (ignoring case). References may be
    /// separated by <c>,</c>, <c>;</c> or spaces, as <c>job.py</c> reads them.</summary>
    public static bool WaitedOn(EditableText text, int line, string projectName)
    {
        if (NumberedEntry(text, line)?.Number is not int number)
        {
            return false;
        }

        for (int i = 0; i < text.Lines.Count; i++)
        {
            if (i == line || !IsOpenItem(text, i))
            {
                continue;
            }

            string content = EditableText.Content(text.Lines[i])[6..];
            int tail = content.IndexOf(" → ", StringComparison.Ordinal);
            Match clause = AfterClause().Match(tail < 0 ? content : content[..tail]);
            if (!clause.Success)
            {
                continue;
            }

            foreach (Match reference in AfterReference().Matches(clause.Groups["refs"].Value))
            {
                string project = reference.Groups["project"].Value;
                if (BacklogParser.TryParseNumber(reference.Groups["number"].Value, out int waited)
                    && waited == number
                    && (project.Length == 0 || string.Equals(project, projectName, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Moves an open line: <c>Top</c> puts it before the first item that is not done, or before the stop
    /// line when that comes first. Prose lines stay where they are.</summary>
    public static BacklogEdit? Move(EditableText text, int line, MoveDirection direction)
    {
        if (!IsOpenItem(text, line) || direction != MoveDirection.Top)
        {
            return null;
        }

        int first = FirstOpenItem(text);
        if (StopLine(text) is int stop && (first < 0 || stop < first))
        {
            first = stop;
        }

        if (first < 0 || first >= line)
        {
            return null;
        }

        List<string> lines = [.. text.Lines];
        string moved = lines[line];
        lines.RemoveAt(line);
        lines.Insert(first, moved);
        return new BacklogEdit(text.With(lines), Subject(text, line));
    }

    /// <summary>Reorders one group (the lines marked <c>[ ]</c> together with the stop line, or the lines marked
    /// <c>[?]</c>): the line <paramref name="line"/> goes directly before (<paramref name="before"/>) or after
    /// <paramref name="target"/> in the group's order, and the group's lines are written back into the indexes the
    /// group occupied. Every other line keeps its index and bytes. A <c>[ ]</c> line may take the stop line as
    /// <paramref name="target"/>. <c>null</c> when either line is not of the group, the marks differ, the lines are
    /// the same or nothing changes.</summary>
    public static BacklogEdit? Place(EditableText text, int line, int target, bool before)
    {
        if (line == target || !IsPlaceable(text, line))
        {
            return null;
        }

        char mark = EditableText.Content(text.Lines[line])[3];
        int? stop = mark == ' ' ? StopLine(text) : null;
        bool targetOk = target == stop
            || (IsPlaceable(text, target) && EditableText.Content(text.Lines[target])[3] == mark);
        if (!targetOk)
        {
            return null;
        }

        EditableText? placed = Reorder(text, GroupSlots(text, mark, stop), line, target, before);
        return placed is null ? null : new BacklogEdit(placed, Subject(text, line));
    }

    /// <summary>Puts the stop line (<see cref="BacklogParser.StopLineText"/>) directly after the <c>[ ]</c> line
    /// <paramref name="line"/>, ending like the file's first line. <c>null</c> when <paramref name="line"/> is not a
    /// <c>[ ]</c> line or the file already has a stop line.</summary>
    public static BacklogEdit? StopAdd(EditableText text, int line)
    {
        if (!IsMark(text, line, ' ') || StopLine(text) is not null)
        {
            return null;
        }

        List<string> lines = [.. text.Lines];
        lines.Insert(line + 1, BacklogParser.StopLineText + Ending(text));
        return new BacklogEdit(text.With(lines), StopSubject);
    }

    /// <summary>Moves the stop line <paramref name="line"/> (the first exact one) directly before or after the
    /// <c>[ ]</c> line <paramref name="target"/>, like <see cref="Place"/>: the stop line and the <c>[ ]</c> lines are
    /// written back into the indexes they occupied. <c>null</c> for any other line or target, or a no-op.</summary>
    public static BacklogEdit? StopMove(EditableText text, int line, int target, bool before)
    {
        if (StopLine(text) != line || !IsMark(text, target, ' '))
        {
            return null;
        }

        EditableText? moved = Reorder(text, GroupSlots(text, ' ', line), line, target, before);
        return moved is null ? null : new BacklogEdit(moved, StopSubject);
    }

    /// <summary>Removes the stop line <paramref name="line"/>, which must be the first exact one.</summary>
    public static BacklogEdit? StopRemove(EditableText text, int line)
    {
        if (StopLine(text) != line)
        {
            return null;
        }

        List<string> lines = [.. text.Lines];
        lines.RemoveAt(line);
        return new BacklogEdit(text.With(lines), StopSubject);
    }

    /// <summary>The index of the first exact stop line, or <c>null</c> (like <c>job.py stop_row</c>).</summary>
    public static int? StopLine(EditableText text)
    {
        for (int i = 0; i < text.Lines.Count; i++)
        {
            if (BacklogParser.IsStopLine(text.Lines[i]))
            {
                return i;
            }
        }

        return null;
    }

    /// <summary>True when the file has a second exact stop line, or a line that starts with <c>- [stop]</c> (ignoring
    /// case) but is not the exact stop line: the cases <c>job.py doctor</c> reports, where the server and
    /// <c>job.py</c> could disagree about the stop line.</summary>
    public static bool HasStopLineProblem(EditableText text)
    {
        int exact = 0;
        foreach (string line in text.Lines)
        {
            string content = EditableText.Content(line);
            if (BacklogParser.IsStopLine(content))
            {
                exact += 1;
            }
            else if (content.ToLowerInvariant().StartsWith(StopPrefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return exact > 1;
    }

    /// <summary>Replaces the title of an open numbered line, keeping the link and the tail. <paramref name="title"/>
    /// must already be a clean title (<see cref="CleanTitle"/>).</summary>
    public static BacklogEdit? Title(EditableText text, int line, string title)
    {
        if (!IsOpenItem(text, line) || NumberedEntry(text, line) is null)
        {
            return null;
        }

        string raw = text.Lines[line];
        string content = EditableText.Content(raw);
        string? replaced = BacklogParser.ReplaceTitle(content[6..], LineTitle(title));
        if (replaced is null)
        {
            return null;
        }

        string next = content[..6] + replaced + raw[content.Length..];
        if (next == raw)
        {
            return null;
        }

        List<string> lines = [.. text.Lines];
        lines[line] = next;
        return new BacklogEdit(text.With(lines), Subject(text, line));
    }

    /// <summary>Inserts <c>- [m] [#N](backlog/NNN-slug.md) Title</c> with the mark <paramref name="mark"/>
    /// (<c>" "</c> for <c>add</c>, <c>"?"</c> for <c>capture</c>): <c>Top</c> before the first item that is not
    /// done, <c>Bottom</c> after the last item line (at the end when there is none). With a stop line, <c>Top</c> is
    /// never below it and <c>Bottom</c> never above it (like <c>job.py add</c>).</summary>
    public static BacklogEdit Add(EditableText text, int number, string title, AddPosition where, string mark = " ")
    {
        List<int> items = ItemRows(text);
        int end = items.Count > 0 ? items[^1] + 1 : text.Lines.Count;
        int at = where == AddPosition.Top ? FirstOpenItem(text) : end;
        if (at < 0)
        {
            at = end;
        }

        if (StopLine(text) is int stop)
        {
            at = where == AddPosition.Top ? Math.Min(at, stop) : Math.Max(at, stop + 1);
        }

        string ending = Ending(text);
        List<string> lines = [.. text.Lines];
        lines.Insert(at, "- [" + mark + "] [#" + number.ToString(CultureInfo.InvariantCulture) + "](" + StoryPath(number, title)
            + ") " + LineTitle(title) + ending);
        return new BacklogEdit(text.With(lines), "#" + number.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>The numbered entry of a line, or <c>null</c> for any other line.</summary>
    public static BacklogEntry? NumberedEntry(EditableText text, int line)
    {
        if (line < 0 || line >= text.Lines.Count)
        {
            return null;
        }

        string content = EditableText.Content(text.Lines[line]);
        if (!BacklogParser.IsItem(content))
        {
            return null;
        }

        string mark = content[3] == 'X' ? "x" : content[3].ToString();
        BacklogEntry entry = BacklogParser.ToEntry(content[6..], mark) with { Line = line };
        return entry.Number is null ? null : entry;
    }

    /// <summary>True for an item line that is not done (<c>[ ]</c>, <c>[?]</c> or <c>[h]</c>).</summary>
    public static bool IsOpenItem(EditableText text, int line)
    {
        if (line < 0 || line >= text.Lines.Count)
        {
            return false;
        }

        string content = EditableText.Content(text.Lines[line]);
        return BacklogParser.IsItem(content) && content[3] != 'x' && content[3] != 'X';
    }

    /// <summary>The highest number among the numbered lines and the <c>backlog/</c> names (<c>NNN-…</c>), plus
    /// 1.</summary>
    public static int NextNumber(EditableText text, IEnumerable<string> storyFolderNames)
    {
        int highest = 0;
        foreach (string line in text.Lines)
        {
            string content = EditableText.Content(line);
            if (BacklogParser.IsItem(content))
            {
                Match match = LineNumber().Match(content[6..]);
                if (match.Success && BacklogParser.TryParseNumber(match.Groups["number"].Value, out int number))
                {
                    highest = Math.Max(highest, number);
                }
            }
        }

        foreach (string name in storyFolderNames)
        {
            Match match = FileNumber().Match(name);
            if (match.Success && BacklogParser.TryParseNumber(match.Groups["number"].Value, out int number))
            {
                highest = Math.Max(highest, number);
            }
        }

        return highest + 1;
    }

    /// <summary>Like <c>job.py slugify</c>: lowercase, runs of anything but <c>a-z0-9</c> become one separator,
    /// the first six words joined by <c>-</c>, cut to 50 characters, <c>-</c> trimmed, <c>item</c> when
    /// empty.</summary>
    public static string Slugify(string title)
    {
        string[] words = NotSlug().Replace(title.ToLowerInvariant(), " ")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string slug = string.Join("-", words.Take(6));
        if (slug.Length > 50)
        {
            slug = slug[..50];
        }

        slug = slug.Trim('-');
        return slug.Length == 0 ? "item" : slug;
    }

    /// <summary><c>backlog/NNN-slug.md</c>.</summary>
    public static string StoryPath(int number, string title)
    {
        return StoryReader.FolderName + "/" + number.ToString("D3", CultureInfo.InvariantCulture) + "-"
            + Slugify(title) + ".md";
    }

    /// <summary>Like <c>job.py render_story</c> for <c>{title, story?, what, why}</c>.</summary>
    public static string RenderStory(int number, string title, string? story, string what, string why)
    {
        List<string> lines = ["# " + number.ToString("D3", CultureInfo.InvariantCulture) + " " + title.Trim(), ""];
        if (!string.IsNullOrEmpty(story))
        {
            lines.AddRange([story.Trim(), ""]);
        }

        lines.AddRange(["## What", "", what.Trim(), "", "## Why", "", why.Trim(), ""]);
        return string.Join("\n", lines);
    }

    /// <summary>The story text with its first line changed from <c># NNN old</c> to <c># NNN new</c>, or
    /// <c>null</c> when the first line is not exactly the old heading.</summary>
    public static string? RetitleStory(string story, int number, string oldTitle, string newTitle)
    {
        string prefix = "# " + number.ToString("D3", CultureInfo.InvariantCulture) + " ";
        int end = story.IndexOf('\n', StringComparison.Ordinal);
        string first = end < 0 ? story : story[..end];
        if (first != prefix + oldTitle)
        {
            return null;
        }

        return prefix + LineTitle(newTitle) + (end < 0 ? "" : story[end..]);
    }

    /// <summary>The trimmed title, or <c>null</c> unless it has 1 to 200 characters, is one line, has no control
    /// characters, no <c>→</c> and no <c>(after:</c>.</summary>
    public static string? CleanTitle(string? title)
    {
        if (title is null)
        {
            return null;
        }

        string trimmed = title.Trim();
        if (trimmed.Length == 0 || !IsValidUnicode(trimmed))
        {
            return null;
        }

        foreach (char c in trimmed)
        {
            if (char.IsControl(c) || c == '\u2028' || c == '\u2029')
            {
                return null;
            }
        }

        if (trimmed.Contains('→', StringComparison.Ordinal)
            || trimmed.Contains("(after:", StringComparison.Ordinal)
            || trimmed.EnumerateRunes().Count() > MaxTitleLength)
        {
            return null;
        }

        return trimmed;
    }

    /// <summary>Story text from a request: valid Unicode, no control characters but <c>\n</c> and <c>\t</c>
    /// (so LF line endings only).</summary>
    public static bool IsStoryText(string text)
    {
        if (!IsValidUnicode(text))
        {
            return false;
        }

        foreach (char c in text)
        {
            if (char.IsControl(c) && c != '\n' && c != '\t')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The note as one line: every whitespace run becomes one space, then trimmed. <c>null</c> when it is
    /// empty, over <see cref="MaxNoteLength"/> characters (runes), not valid Unicode, holds another control
    /// character, or contains <c>Proposed by job</c> (ignoring case), so a note never names the proposing job.</summary>
    public static string? CleanNote(string? note)
    {
        if (note is null || !IsValidUnicode(note))
        {
            return null;
        }

        string clean = Whitespace().Replace(note, " ").Trim();
        if (clean.Length == 0 || clean.EnumerateRunes().Count() > MaxNoteLength
            || clean.Contains(ProposedByJob, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        foreach (char c in clean)
        {
            if (char.IsControl(c))
            {
                return null;
            }
        }

        return clean;
    }

    /// <summary>A quick-capture text as one line: every whitespace run becomes one space, then trimmed. <c>null</c>
    /// when it is empty, over <see cref="MaxCaptureLength"/> characters (runes), not valid Unicode, holds another
    /// control character, or contains <c>Proposed by job</c> (ignoring case).</summary>
    public static string? CleanCapture(string? text)
    {
        if (text is null || !IsValidUnicode(text))
        {
            return null;
        }

        string clean = Whitespace().Replace(text, " ").Trim();
        if (clean.Length == 0 || clean.EnumerateRunes().Count() > MaxCaptureLength
            || clean.Contains(ProposedByJob, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        foreach (char c in clean)
        {
            if (char.IsControl(c) || c == '\u2028' || c == '\u2029')
            {
                return null;
            }
        }

        return clean;
    }

    /// <summary>The title of a capture: the clean text, cut when it is over <see cref="MaxCaptureTitleLength"/>
    /// runes at the last space at or before that rune (a hard cut there when there is none), then trailing
    /// <c>.,;:!?</c> and spaces removed. It may be empty; the caller checks it with <see cref="CleanTitle"/>.</summary>
    public static string CaptureTitle(string clean)
    {
        Rune[] runes = clean.EnumerateRunes().ToArray();
        string title = clean;
        if (runes.Length > MaxCaptureTitleLength)
        {
            int cut = MaxCaptureTitleLength;
            for (int i = MaxCaptureTitleLength - 1; i > 0; i--)
            {
                if (runes[i].Value == ' ')
                {
                    cut = i;
                    break;
                }
            }

            StringBuilder builder = new();
            foreach (Rune rune in runes.Take(cut))
            {
                builder.Append(rune.ToString());
            }

            title = builder.ToString();
        }

        return title.TrimEnd('.', ',', ';', ':', '!', '?', ' ');
    }

    /// <summary>The story file of a capture: heading, the text under <c>## What</c>, a fixed <c>## Why</c> and a
    /// dated <see cref="RoomKitOptions.CaptureNote"/> note.</summary>
    public static string RenderDraft(int number, string title, string text, DateOnly today)
    {
        return "# " + number.ToString("D3", CultureInfo.InvariantCulture) + " " + title + "\n\n## What\n\n" + text
            + "\n\n## Why\n\n" + CaptureWhy + "\n\n" + NotesHeading + "\n\n- "
            + today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ": " + RoomContext.Options.CaptureNote + "\n";
    }

    /// <summary>Appends <c>- YYYY-MM-DD: note</c> to the story's first <c>## Notes</c> section (which ends at the
    /// next <c># </c> or <c>## </c> line): after the section's last non-blank line, with one blank line before it
    /// unless that line is itself a dated note. Without a section, <c>## Notes</c> and the note are added at the
    /// end, one blank line after the content. Every other line keeps its bytes; new lines use the file's ending
    /// (CRLF when its first line ends with <c>\r</c>). <paramref name="note"/> must be clean
    /// (<see cref="CleanNote"/>).</summary>
    public static EditableText AppendNote(EditableText story, DateOnly date, string note)
    {
        string ending = story.Lines.Count > 0 && story.Lines[0].EndsWith('\r') ? "\r" : "";
        string noteLine = "- " + date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ": " + note + ending;
        List<string> lines = [.. story.Lines];
        int heading = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            if (string.Equals(EditableText.Content(lines[i]).TrimEnd(), NotesHeading, StringComparison.Ordinal))
            {
                heading = i;
                break;
            }
        }

        if (heading < 0)
        {
            while (lines.Count > 0 && IsBlank(lines[^1]))
            {
                lines.RemoveAt(lines.Count - 1);
            }

            EndLastLine(lines, story.FinalNewline, ending);
            if (lines.Count > 0)
            {
                lines.Add(ending);
            }

            lines.AddRange([NotesHeading + ending, ending, noteLine]);
            return new EditableText(lines, true, story.HasBom);
        }

        int last = heading;
        for (int i = heading + 1; i < lines.Count; i++)
        {
            string content = EditableText.Content(lines[i]);
            if (content.StartsWith("# ", StringComparison.Ordinal) || content.StartsWith("## ", StringComparison.Ordinal))
            {
                break;
            }

            if (!IsBlank(lines[i]))
            {
                last = i;
            }
        }

        bool atEnd = last == lines.Count - 1;
        List<string> added = DatedNote().IsMatch(EditableText.Content(lines[last])) ? [noteLine] : [ending, noteLine];
        if (atEnd)
        {
            EndLastLine(lines, story.FinalNewline, ending);
        }

        lines.InsertRange(last + 1, added);
        return new EditableText(lines, story.FinalNewline || atEnd, story.HasBom);
    }

    /// <summary>The title as written on a backlog line: whitespace runs become one space (like
    /// <c>' '.join(title.split())</c>).</summary>
    public static string LineTitle(string title)
    {
        return Whitespace().Replace(title.Trim(), " ");
    }

    private static bool IsValidUnicode(string text)
    {
        try
        {
            _ = new UTF8Encoding(false, true).GetByteCount(text);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool IsBlank(string line)
    {
        return EditableText.Content(line).Trim().Length == 0;
    }

    /// <summary>When the file had no final line break, its last line gets the file's <c>\r</c> (if any) so the line
    /// added after it ends the same way as the others.</summary>
    private static void EndLastLine(List<string> lines, bool finalNewline, string ending)
    {
        if (!finalNewline && lines.Count > 0 && !lines[^1].EndsWith('\r'))
        {
            lines[^1] += ending;
        }
    }

    private static bool IsMark(EditableText text, int line, char mark)
    {
        if (line < 0 || line >= text.Lines.Count)
        {
            return false;
        }

        string content = EditableText.Content(text.Lines[line]);
        return BacklogParser.IsItem(content) && content[3] == mark;
    }

    private static bool IsPlaceable(EditableText text, int line)
    {
        return IsMark(text, line, ' ') || IsMark(text, line, '?');
    }

    /// <summary>The indexes of a group in file order: the lines marked <paramref name="mark"/>, plus
    /// <paramref name="stop"/> when it is set.</summary>
    private static List<int> GroupSlots(EditableText text, char mark, int? stop)
    {
        List<int> slots = [];
        for (int i = 0; i < text.Lines.Count; i++)
        {
            if (IsMark(text, i, mark) || i == stop)
            {
                slots.Add(i);
            }
        }

        return slots;
    }

    /// <summary>Moves <paramref name="line"/> directly before or after <paramref name="target"/> in the order of
    /// <paramref name="slots"/> and writes the lines back into those indexes. <c>null</c> when nothing changes.</summary>
    private static EditableText? Reorder(EditableText text, List<int> slots, int line, int target, bool before)
    {
        List<int> order = [.. slots];
        order.Remove(line);
        int at = order.IndexOf(target) + (before ? 0 : 1);
        order.Insert(at, line);

        List<string> lines = [.. text.Lines];
        for (int i = 0; i < slots.Count; i++)
        {
            lines[slots[i]] = text.Lines[order[i]];
        }

        return lines.SequenceEqual(text.Lines, StringComparer.Ordinal) ? null : text.With(lines);
    }

    /// <summary>The line end new lines get: <c>\r</c> when the file's first line ends with it (CRLF), else
    /// nothing.</summary>
    private static string Ending(EditableText text)
    {
        return text.Lines.Count > 0 && text.Lines[0].EndsWith('\r') ? "\r" : "";
    }

    private static List<int> ItemRows(EditableText text)
    {
        List<int> rows = [];
        for (int i = 0; i < text.Lines.Count; i++)
        {
            if (BacklogParser.IsItem(EditableText.Content(text.Lines[i])))
            {
                rows.Add(i);
            }
        }

        return rows;
    }

    private static int FirstOpenItem(EditableText text)
    {
        for (int i = 0; i < text.Lines.Count; i++)
        {
            if (IsOpenItem(text, i))
            {
                return i;
            }
        }

        return -1;
    }

    private static string Subject(EditableText text, int line)
    {
        BacklogEntry? entry = NumberedEntry(text, line);
        return entry?.Number is int number
            ? "#" + number.ToString(CultureInfo.InvariantCulture)
            : "line " + (line + 1).ToString(CultureInfo.InvariantCulture);
    }

    [GeneratedRegex(@"\A\[#(?<number>[0-9]{1,9})\]\([^)\s]+\)", RegexOptions.CultureInvariant)]
    private static partial Regex LineNumber();

    [GeneratedRegex(@"\(after:(?<refs>[^()]*)\)", RegexOptions.CultureInvariant)]
    private static partial Regex AfterClause();

    [GeneratedRegex(@"(?<![A-Za-z0-9_.#-])(?:(?<project>[A-Za-z0-9._-]+)#|#)(?<number>[0-9]{1,9})(?![0-9])",
        RegexOptions.CultureInvariant)]
    private static partial Regex AfterReference();

    [GeneratedRegex(@"\A(?<number>[0-9]{1,9})-", RegexOptions.CultureInvariant)]
    private static partial Regex FileNumber();

    [GeneratedRegex("[^a-z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex NotSlug();

    [GeneratedRegex(@"\A- [0-9]{4}-[0-9]{2}-[0-9]{2}: ", RegexOptions.CultureInvariant)]
    private static partial Regex DatedNote();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();
}
