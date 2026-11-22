namespace RoomKit;

/// <summary>A story's <c>Category: &lt;name&gt;</c> line, with the rule of <c>job.py with_category</c>: only the lines
/// before the first <c>## </c> line count, and the first of them that is <c>Category:</c>, then spaces or tabs, then a
/// value (a trailing <c>\r</c> ignored) is the category line. Its value is the category only when it is exactly one of
/// <see cref="Names"/>.</summary>
internal static class StoryCategory
{
    private const string Prefix = "Category:";

    private const char Bom = '﻿';

    /// <summary>The nine names, in <c>job.py</c>'s <c>CATEGORIES</c> order.</summary>
    public static readonly IReadOnlyList<string> Names =
        ["feature", "bug", "security", "ux", "ui", "tidy-up", "test", "ci", "docs"];

    /// <summary>True for exactly one of the nine names (case-sensitive).</summary>
    public static bool IsName(string? name)
    {
        return name is not null && Names.Contains(name, StringComparer.Ordinal);
    }

    /// <summary>The story's category, or <c>null</c> when it has no category line or its value is not one of the
    /// nine names. A leading BOM is ignored.</summary>
    public static string? Parse(string? text)
    {
        if (text is null)
        {
            return null;
        }

        string body = text.Length > 0 && text[0] == Bom ? text[1..] : text;
        string[] lines = body.Split('\n');
        int at = Find(lines);
        if (at < 0)
        {
            return null;
        }

        string value = Value(EditableText.Content(lines[at]));
        return IsName(value) ? value : null;
    }

    /// <summary>The story with its category line set to <paramref name="name"/> (one of the nine): the first counting
    /// line is replaced (keeping its <c>\r</c>), else the line goes before the first <c>## </c> heading with one blank
    /// line on each side (blank lines just before the heading collapse to that one), else after the last non-blank
    /// line with a blank line before and a line break after. Every other line keeps its bytes; new lines use the
    /// file's ending (CRLF when its first line ends with <c>\r</c>).</summary>
    public static EditableText Set(EditableText story, string name)
    {
        string line = Prefix + " " + name;
        List<string> lines = [.. story.Lines];
        int at = Find(lines);
        if (at >= 0)
        {
            lines[at] = line + (lines[at].EndsWith('\r') ? "\r" : "");
            return story.With(lines);
        }

        string ending = lines.Count > 0 && lines[0].EndsWith('\r') ? "\r" : "";
        int head = Heading(lines);
        if (head < 0)
        {
            while (lines.Count > 0 && IsBlank(lines[^1]))
            {
                lines.RemoveAt(lines.Count - 1);
            }

            if (!story.FinalNewline && lines.Count > 0 && !lines[^1].EndsWith('\r'))
            {
                lines[^1] += ending;
            }

            lines.AddRange([ending, line + ending]);
            return new EditableText(lines, true, story.HasBom);
        }

        int start = head;
        while (start > 0 && IsBlank(lines[start - 1]))
        {
            start--;
        }

        lines.RemoveRange(start, head - start);
        lines.InsertRange(start, [ending, line + ending, ending]);
        return story.With(lines);
    }

    /// <summary>The index of the first counting <c>Category:</c> line before the first heading, or -1.</summary>
    private static int Find(IReadOnlyList<string> lines)
    {
        int head = Heading(lines);
        int end = head < 0 ? lines.Count : head;
        for (int i = 0; i < end; i++)
        {
            string content = EditableText.Content(lines[i]);
            if (content.StartsWith(Prefix, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private static int Heading(IReadOnlyList<string> lines)
    {
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].StartsWith("## ", StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The value after <c>Category:</c> and the spaces and tabs that follow it.</summary>
    private static string Value(string content)
    {
        return content[Prefix.Length..].TrimStart(' ', '\t');
    }

    private static bool IsBlank(string line)
    {
        return EditableText.Content(line).Length == 0;
    }
}
