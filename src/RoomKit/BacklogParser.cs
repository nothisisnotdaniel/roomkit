using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace RoomKit;

/// <summary>One <c>(after: …)</c> dependency: an item number, in another project when <c>Project</c> is set and in
/// the same project when it is <c>null</c>.</summary>
public sealed record BacklogDependency(string? Project, int Number);

/// <summary>One backlog line. Old free-text lines: the text before the first <c>": "</c> and the text after it,
/// with no number. Numbered lines (<c>[#N](target) Title (after: …) → job</c>): the number, the story link
/// target, the title, the dependencies and the <c>→ job</c> tail as <c>Rest</c>. <c>Waiting</c> is filled in by
/// the backlogs endpoint. <c>Line</c> is the 0-based index of the line in the file (split on <c>\n</c>); it is a
/// position, not content, so it is left out of equality.</summary>
public sealed record BacklogEntry(
    string Title,
    string Rest,
    int? Number = null,
    string Mark = " ",
    string? Story = null,
    IReadOnlyList<BacklogDependency>? After = null)
{
    public IReadOnlyList<BacklogDependency> After { get; init; } = After ?? [];

    public bool Waiting { get; init; }

    /// <summary>The story's category (<see cref="StoryCategory"/>), filled in by the backlogs endpoint; <c>null</c>
    /// without one or when the story can't be read.</summary>
    public string? Category { get; init; }

    public int Line { get; init; }

    [JsonIgnore]
    public bool IsDone => Mark == "x";

    public bool Equals(BacklogEntry? other)
    {
        return other is not null
            && Title == other.Title
            && Rest == other.Rest
            && Number == other.Number
            && Mark == other.Mark
            && Story == other.Story
            && Waiting == other.Waiting
            && Category == other.Category
            && After.SequenceEqual(other.After);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Title, Rest, Number, Mark, Story, Waiting, After.Count);
    }
}

/// <summary>A backlog's lines grouped by mark, each group in file order. <c>StopLine</c> is the 0-based index of the
/// first exact stop line (<see cref="BacklogParser.StopLineText"/>), or <c>null</c> without one; it is no entry of
/// any group.</summary>
public sealed record Backlog(
    IReadOnlyList<BacklogEntry> Open,
    IReadOnlyList<BacklogEntry> Approval,
    IReadOnlyList<BacklogEntry> User,
    IReadOnlyList<BacklogEntry> Done)
{
    public static readonly Backlog Empty = new([], [], [], []);

    public int? StopLine { get; init; }

    /// <summary>Every entry, open, approval, user and done in that order.</summary>
    public IEnumerable<BacklogEntry> All => Open.Concat(Approval).Concat(User).Concat(Done);
}

/// <summary>Turns <c>backlog.md</c> lines into entries. Only lines starting with <c>- [ ] </c>, <c>- [?] </c>,
/// <c>- [h] </c> and <c>- [x] </c> / <c>- [X] </c> count; everything else is ignored.</summary>
internal static partial class BacklogParser
{
    private const string Separator = ": ";

    /// <summary>The auto stop line, exactly as <c>job.py</c> writes and reads it (<c>STOP_LINE</c>).</summary>
    public const string StopLineText = "- [stop] Auto stops here";

    /// <summary>True for the exact stop line once its line end is stripped (like <c>job.py is_stop</c>).</summary>
    public static bool IsStopLine(string content)
    {
        return string.Equals(content.TrimEnd('\r', '\n'), StopLineText, StringComparison.Ordinal);
    }

    public static Backlog Parse(IEnumerable<string> lines)
    {
        List<BacklogEntry> open = [];
        List<BacklogEntry> approval = [];
        List<BacklogEntry> user = [];
        List<BacklogEntry> done = [];

        int index = 0;
        int? stopLine = null;
        foreach (string line in lines)
        {
            if (stopLine is null && IsStopLine(line))
            {
                stopLine = index;
            }

            List<BacklogEntry>? group = GroupFor(line, open, approval, user, done);
            if (group is not null)
            {
                string mark = line[3] == 'X' ? "x" : line[3].ToString();
                group.Add(ToEntry(line[6..], mark) with { Line = index });
            }

            index += 1;
        }

        return new Backlog(open, approval, user, done) { StopLine = stopLine };
    }

    /// <summary>Parses the text after the mark: a numbered line when it starts with a well-formed
    /// <c>[#N](target)</c>, the old title/rest split otherwise.</summary>
    public static BacklogEntry ToEntry(string text, string mark = " ")
    {
        BacklogEntry? numbered = ToNumberedEntry(text, mark);
        if (numbered is not null)
        {
            return numbered;
        }

        int index = text.IndexOf(Separator, StringComparison.Ordinal);
        if (index < 0)
        {
            return new BacklogEntry(text, "", Mark: mark);
        }

        return new BacklogEntry(text[..index], text[(index + Separator.Length)..], Mark: mark);
    }

    private static BacklogEntry? ToNumberedEntry(string text, string mark)
    {
        Match link = NumberedLink().Match(text);
        if (!link.Success || !TryParseNumber(link.Groups["number"].Value, out int number))
        {
            return null;
        }

        Match body = Body().Match(link.Groups["body"].Value);
        List<BacklogDependency> after = [];
        if (body.Groups["after"].Success)
        {
            foreach (string part in body.Groups["after"].Value.Split(';'))
            {
                Match dependency = Dependency().Match(part.Trim());
                if (dependency.Success && TryParseNumber(dependency.Groups["number"].Value, out int dependencyNumber))
                {
                    string project = dependency.Groups["project"].Value;
                    after.Add(new BacklogDependency(project.Length == 0 ? null : project, dependencyNumber));
                }
            }
        }

        string rest = body.Groups["rest"].Success ? body.Groups["rest"].Value : "";
        string title = body.Groups["title"].Value.Trim();
        return new BacklogEntry(title, rest, number, mark, link.Groups["target"].Value, after);
    }

    /// <summary>The text after the mark of a numbered line with its title replaced by <paramref name="title"/>; the
    /// link, the <c>(after: …)</c> and the <c>→ …</c> tail keep their bytes. <c>null</c> for a line that is not
    /// numbered.</summary>
    public static string? ReplaceTitle(string text, string title)
    {
        Match link = NumberedLink().Match(text);
        if (!link.Success)
        {
            return null;
        }

        Group bodyGroup = link.Groups["body"];
        if (!bodyGroup.Success)
        {
            return text + " " + title;
        }

        Group titleGroup = Body().Match(bodyGroup.Value).Groups["title"];
        string tail = bodyGroup.Value[(titleGroup.Index + titleGroup.Length)..];
        if (tail.Length > 0 && tail[0] != ' ' && tail[0] != '\t')
        {
            tail = " " + tail;
        }

        return text[..bodyGroup.Index] + title + tail;
    }

    /// <summary>Parses ASCII digits only (no sign, no spaces, no other scripts' digits).</summary>
    public static bool TryParseNumber(string digits, out int number)
    {
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out number);
    }

    /// <summary>True for a line the parser counts as an item (<c>- [ ] </c>, <c>- [?] </c>, <c>- [h] </c>,
    /// <c>- [x] </c> or <c>- [X] </c> at the start).</summary>
    public static bool IsItem(string line)
    {
        return line.Length >= 6 && line.StartsWith("- [", StringComparison.Ordinal) && line[4] == ']' && line[5] == ' '
            && line[3] is ' ' or '?' or 'h' or 'x' or 'X';
    }

    private static List<BacklogEntry>? GroupFor(
        string line,
        List<BacklogEntry> open,
        List<BacklogEntry> approval,
        List<BacklogEntry> user,
        List<BacklogEntry> done)
    {
        if (line.Length < 6 || !line.StartsWith("- [", StringComparison.Ordinal) || line[4] != ']' || line[5] != ' ')
        {
            return null;
        }

        return line[3] switch
        {
            ' ' => open,
            '?' => approval,
            'h' => user,
            'x' or 'X' => done,
            _ => null,
        };
    }

    [GeneratedRegex(@"\A\[#(?<number>[0-9]{1,9})\]\((?<target>[^()\s]+)\)(?:[ \t]+(?<body>.*))?\z",
        RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex NumberedLink();

    // Non-backtracking: a lazy title followed by optional space runs would backtrack quadratically.
    [GeneratedRegex(@"\A(?<title>.*?)(?:[ \t]*\(after:[ \t]*(?<after>[^()]*)\))?(?:[ \t]*(?<rest>→.*))?\z",
        RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.NonBacktracking)]
    private static partial Regex Body();

    [GeneratedRegex(@"\A(?<project>[A-Za-z0-9._-]*)#(?<number>[0-9]{1,9})\z", RegexOptions.CultureInvariant)]
    private static partial Regex Dependency();
}
