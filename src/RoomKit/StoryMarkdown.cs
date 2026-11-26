using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace RoomKit;

/// <summary>One inline run of a story block: <c>text</c>, <c>code</c>, <c>strong</c> or <c>em</c>, with plain
/// text only.</summary>
public sealed record StoryInline(string Kind, string Text);

/// <summary>One block of a story: <c>heading</c> (with <c>Level</c> 1-3 and <c>Inlines</c>), <c>paragraph</c>
/// (<c>Inlines</c>), <c>list</c> (<c>Ordered</c> and <c>Items</c>, each a list of inlines), <c>code</c>
/// (<c>Text</c>, verbatim) or <c>table</c> (<c>Header</c>, a list of cells, and <c>Rows</c>, each a list of cells;
/// a cell is a list of inlines). Fields a kind doesn't use are left out of the JSON.</summary>
public sealed record StoryBlock(
    string Kind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Level = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Ordered = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Text = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<StoryInline>? Inlines = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<IReadOnlyList<StoryInline>>? Items = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<IReadOnlyList<StoryInline>>? Header = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<IReadOnlyList<IReadOnlyList<StoryInline>>>? Rows = null);

/// <summary>Turns a story's Markdown into a block tree of plain texts: headings (levels 4-6 shown as 3),
/// paragraphs, flat lists, fenced code, pipe tables, and inline code, strong and em. A link keeps only its text.
/// Raw HTML is never interpreted: it stays literal text. The output holds no markup made by the renderer.</summary>
public static partial class StoryMarkdown
{
    /// <summary>Deepest inline nesting (link text, emphasis content) that is still parsed.</summary>
    private const int MaxDepth = 2;

    private const string EscapableCharacters = "\\`*_{}[]()#+-.!<>|~\"'";

    public static IReadOnlyList<StoryBlock> Parse(string markdown)
    {
        string[] lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        List<StoryBlock> blocks = [];
        List<string> paragraph = [];
        List<List<string>>? listItems = null;
        bool listOrdered = false;

        void FlushParagraph()
        {
            if (paragraph.Count > 0)
            {
                blocks.Add(new StoryBlock("paragraph", Inlines: ParseInlines(string.Join(" ", paragraph))));
                paragraph.Clear();
            }
        }

        void FlushList()
        {
            if (listItems is not null)
            {
                blocks.Add(new StoryBlock(
                    "list",
                    Ordered: listOrdered,
                    Items: listItems.Select(item => (IReadOnlyList<StoryInline>)ParseInlines(string.Join(" ", item)))
                        .ToList()));
                listItems = null;
            }
        }

        int index = 0;
        while (index < lines.Length)
        {
            string line = lines[index].TrimEnd('\r');
            Match fence = Fence().Match(line);
            if (fence.Success)
            {
                FlushParagraph();
                FlushList();
                string marker = fence.Groups["fence"].Value;
                List<string> code = [];
                index++;
                while (index < lines.Length && !IsClosingFence(lines[index].TrimEnd('\r'), marker))
                {
                    code.Add(lines[index].TrimEnd('\r'));
                    index++;
                }

                blocks.Add(new StoryBlock("code", Text: string.Join("\n", code)));
                index++;
                continue;
            }

            index++;
            if (string.IsNullOrWhiteSpace(line))
            {
                FlushParagraph();
                FlushList();
                continue;
            }

            Match heading = Heading().Match(line);
            if (heading.Success)
            {
                FlushParagraph();
                FlushList();
                int level = Math.Min(heading.Groups["hashes"].Value.Length, 3);
                List<StoryInline> inlines = ParseInlines(heading.Groups["text"].Value);
                blocks.Add(new StoryBlock("heading", Level: level, Inlines: inlines));
                continue;
            }

            if (ThematicBreak().IsMatch(line))
            {
                FlushParagraph();
                FlushList();
                continue;
            }

            if (index < lines.Length && IsTableStart(line, lines[index].TrimEnd('\r')))
            {
                FlushParagraph();
                FlushList();
                index = ParseTable(lines, index - 1, blocks);
                continue;
            }

            Match item = ListItem().Match(line);
            if (item.Success)
            {
                FlushParagraph();
                bool ordered = item.Groups["ordered"].Success;
                if (listItems is not null && ordered != listOrdered)
                {
                    FlushList();
                }

                listItems ??= [];
                listOrdered = ordered;
                listItems.Add([item.Groups["text"].Value.Trim()]);
                continue;
            }

            if (listItems is not null)
            {
                listItems[^1].Add(line.Trim());
                continue;
            }

            paragraph.Add(line.Trim());
        }

        FlushParagraph();
        FlushList();
        return blocks;
    }

    /// <summary>A table starts with a header row holding a <c>|</c>, followed by a separator row with the same
    /// number of cells.</summary>
    private static bool IsTableStart(string header, string separator)
    {
        return header.Contains('|', StringComparison.Ordinal)
            && TableSeparator().IsMatch(separator)
            && separator.Contains('|', StringComparison.Ordinal)
            && SplitRow(header).Count == SplitRow(separator).Count;
    }

    /// <summary>Adds a <c>table</c> block from the header at <paramref name="start"/>, its separator and the body
    /// rows up to the first blank line or line without <c>|</c>. Extra cells in a body row are dropped and missing
    /// ones are not filled in, so the cell count never exceeds the input's pipes. Returns the next line's
    /// index.</summary>
    private static int ParseTable(string[] lines, int start, List<StoryBlock> blocks)
    {
        List<string> headerCells = SplitRow(lines[start].TrimEnd('\r'));
        int columns = headerCells.Count;
        List<IReadOnlyList<StoryInline>> header = headerCells.Select(Cell).ToList();
        List<IReadOnlyList<IReadOnlyList<StoryInline>>> rows = [];
        int index = start + 2;
        while (index < lines.Length)
        {
            string line = lines[index].TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line) || !line.Contains('|', StringComparison.Ordinal))
            {
                break;
            }

            rows.Add(SplitRow(line).Take(columns).Select(Cell).ToList());
            index++;
        }

        blocks.Add(new StoryBlock("table", Header: header, Rows: rows));
        return index;
    }

    private static IReadOnlyList<StoryInline> Cell(string text)
    {
        return ParseInlines(text.Trim());
    }

    /// <summary>Splits a row at every <c>|</c> not escaped by a backslash; one leading and one trailing pipe are
    /// dropped.</summary>
    private static List<string> SplitRow(string line)
    {
        string row = line.Trim();
        if (row.StartsWith('|'))
        {
            row = row[1..];
        }

        if (row.EndsWith('|') && !row.EndsWith("\\|", StringComparison.Ordinal))
        {
            row = row[..^1];
        }

        List<string> cells = [];
        StringBuilder cell = new();
        for (int i = 0; i < row.Length; i++)
        {
            if (row[i] == '\\' && i + 1 < row.Length)
            {
                cell.Append(row[i]).Append(row[i + 1]);
                i++;
                continue;
            }

            if (row[i] == '|')
            {
                cells.Add(cell.ToString());
                cell.Clear();
                continue;
            }

            cell.Append(row[i]);
        }

        cells.Add(cell.ToString());
        return cells;
    }

    /// <summary>Splits text into inline runs; adjacent text runs are merged.</summary>
    public static List<StoryInline> ParseInlines(string text)
    {
        return ParseInlines(text, 0);
    }

    /// <summary>Link text and emphasis content are parsed one level deeper; at <see cref="MaxDepth"/> brackets,
    /// <c>*</c> and <c>_</c> stay literal, so nesting never grows the stack. Every match is looked up in tables
    /// built once per call (<see cref="InlineScan"/>), so the work stays near linear in the text length.</summary>
    private static List<StoryInline> ParseInlines(string text, int depth)
    {
        List<StoryInline> inlines = [];
        StringBuilder plain = new();
        InlineScan scan = new(text, links: depth < MaxDepth);
        bool nested = depth < MaxDepth;

        void FlushText()
        {
            if (plain.Length > 0)
            {
                inlines.Add(new StoryInline("text", plain.ToString()));
                plain.Clear();
            }
        }

        void AddRuns(IEnumerable<StoryInline> runs)
        {
            foreach (StoryInline run in runs)
            {
                if (run.Kind == "text")
                {
                    plain.Append(run.Text);
                }
                else
                {
                    FlushText();
                    inlines.Add(run);
                }
            }
        }

        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '\\' && i + 1 < text.Length && EscapableCharacters.Contains(text[i + 1]))
            {
                plain.Append(text[i + 1]);
                i += 2;
                continue;
            }

            if (c == '`')
            {
                int run = CountRun(text, i, '`');
                int close = scan.CodeClose(i);
                if (close >= 0)
                {
                    string code = text[(i + run)..close];
                    if (code.Length >= 2 && code[0] == ' ' && code[^1] == ' ' && code.Trim().Length > 0)
                    {
                        code = code[1..^1];
                    }

                    FlushText();
                    inlines.Add(new StoryInline("code", code));
                    i = close + run;
                    continue;
                }

                plain.Append(text, i, run);
                i += run;
                continue;
            }

            if (nested && (c == '[' || (c == '!' && i + 1 < text.Length && text[i + 1] == '[')))
            {
                int open = c == '!' ? i + 1 : i;
                int closeBracket = scan.BracketClose(open);
                if (closeBracket > open && closeBracket + 1 < text.Length && text[closeBracket + 1] == '(')
                {
                    int closeParen = scan.ParenClose(closeBracket + 1);
                    if (closeParen > 0)
                    {
                        AddRuns(ParseInlines(text[(open + 1)..closeBracket], depth + 1));
                        i = closeParen + 1;
                        continue;
                    }
                }
            }

            if (c == '*' || c == '_')
            {
                int literal = CountRun(text, i, c);
                int run = Math.Min(literal, 2);
                int close = nested ? scan.EmphasisClose(i, c, run) : -1;
                if (close >= 0)
                {
                    string inner = PlainText(ParseInlines(text[(i + run)..close], depth + 1));
                    FlushText();
                    inlines.Add(new StoryInline(run == 2 ? "strong" : "em", inner));
                    i = close + run;
                    continue;
                }

                plain.Append(text, i, literal);
                i += literal;
                continue;
            }

            plain.Append(c);
            i++;
        }

        FlushText();
        return inlines;
    }

    private static string PlainText(IEnumerable<StoryInline> runs)
    {
        return string.Concat(runs.Select(run => run.Text));
    }

    private static bool IsClosingFence(string line, string marker)
    {
        string trimmed = line.Trim();
        return trimmed.Length >= marker.Length && trimmed.All(ch => ch == marker[0]);
    }

    private static int CountRun(string text, int start, char ch)
    {
        int end = start;
        while (end < text.Length && text[end] == ch)
        {
            end++;
        }

        return end - start;
    }

    /// <summary>Match tables for one inline text, each built in one pass: the closing run of every backtick run,
    /// the closing <c>]</c> of every <c>[</c>, the closing <c>)</c> of every <c>(</c> (a link destination ends at
    /// whitespace) and, per emphasis delimiter, the sorted positions that can close it outside code spans.</summary>
    private sealed class InlineScan
    {
        private static readonly int[] EmphasisRuns = [1, 2];

        private readonly string text;
        private readonly Dictionary<int, int> codeClose = [];
        private readonly Dictionary<int, int> bracketClose = [];
        private readonly Dictionary<int, int> parenClose = [];
        private readonly Dictionary<(char, int), List<int>> closers = [];

        public InlineScan(string text, bool links)
        {
            this.text = text;
            MatchCodeRuns();
            if (links)
            {
                MatchPairs('[', ']', bracketClose, stopAtWhitespace: false);
                MatchPairs('(', ')', parenClose, stopAtWhitespace: true);
                FindEmphasisClosers();
            }
        }

        public int CodeClose(int start)
        {
            return codeClose.TryGetValue(start, out int close) ? close : -1;
        }

        public int BracketClose(int open)
        {
            return bracketClose.TryGetValue(open, out int close) ? close : -1;
        }

        public int ParenClose(int open)
        {
            return parenClose.TryGetValue(open, out int close) ? close : -1;
        }

        /// <summary>The first closer after the opener's first content character, or -1. The opener must be
        /// followed by a non-space and, for <c>_</c>, not stand inside a word.</summary>
        public int EmphasisClose(int start, char ch, int run)
        {
            int contentStart = start + run;
            if (contentStart >= text.Length || char.IsWhiteSpace(text[contentStart]))
            {
                return -1;
            }

            if (ch == '_' && start > 0 && char.IsLetterOrDigit(text[start - 1]))
            {
                return -1;
            }

            if (!closers.TryGetValue((ch, run), out List<int>? positions))
            {
                return -1;
            }

            int index = positions.BinarySearch(contentStart + 1);
            if (index < 0)
            {
                index = ~index;
            }

            return index < positions.Count ? positions[index] : -1;
        }

        /// <summary>Pairs each backtick run with the next run of the same length (found right to left).</summary>
        private void MatchCodeRuns()
        {
            List<(int Start, int Length)> runs = [];
            int i = 0;
            while (i < text.Length)
            {
                if (text[i] == '`')
                {
                    int length = CountRun(text, i, '`');
                    runs.Add((i, length));
                    i += length;
                    continue;
                }

                i++;
            }

            Dictionary<int, int> nextOfLength = [];
            for (int index = runs.Count - 1; index >= 0; index--)
            {
                (int start, int length) = runs[index];
                if (nextOfLength.TryGetValue(length, out int next))
                {
                    codeClose[start] = next;
                }

                nextOfLength[length] = start;
            }
        }

        private void MatchPairs(char open, char close, Dictionary<int, int> pairs, bool stopAtWhitespace)
        {
            Stack<int> openers = new();
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == open)
                {
                    openers.Push(i);
                }
                else if (text[i] == close && openers.Count > 0)
                {
                    pairs[openers.Pop()] = i;
                }
                else if (stopAtWhitespace && char.IsWhiteSpace(text[i]))
                {
                    openers.Clear();
                }
            }
        }

        /// <summary>A closer follows a non-space, is not inside a code span, for <c>_</c> is not followed by a
        /// letter or digit, and for a single delimiter is not followed by another one.</summary>
        private void FindEmphasisClosers()
        {
            int i = 1;
            while (i < text.Length)
            {
                char ch = text[i];
                if (ch == '`')
                {
                    int run = CountRun(text, i, '`');
                    int close = CodeClose(i);
                    i = close >= 0 ? close + run : i + run;
                    continue;
                }

                if ((ch == '*' || ch == '_') && !char.IsWhiteSpace(text[i - 1]))
                {
                    foreach (int run in EmphasisRuns)
                    {
                        if (IsCloser(i, ch, run))
                        {
                            if (!closers.TryGetValue((ch, run), out List<int>? positions))
                            {
                                positions = [];
                                closers[(ch, run)] = positions;
                            }

                            positions.Add(i);
                        }
                    }
                }

                i++;
            }
        }

        private bool IsCloser(int i, char ch, int run)
        {
            if (i + run > text.Length || (run == 2 && text[i + 1] != ch))
            {
                return false;
            }

            if (ch == '_' && i + run < text.Length && char.IsLetterOrDigit(text[i + run]))
            {
                return false;
            }

            return run == 2 || i + 1 >= text.Length || text[i + 1] != ch;
        }
    }

    [GeneratedRegex(@"\A {0,3}(?<fence>`{3,}|~{3,})", RegexOptions.CultureInvariant)]
    private static partial Regex Fence();

    // Non-backtracking: a lazy title followed by optional space runs would backtrack quadratically.
    [GeneratedRegex(@"\A {0,3}(?<hashes>#{1,6})(?:[ \t]+(?<text>.*?))?(?:[ \t]+#+)?[ \t]*\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex Heading();

    [GeneratedRegex(@"\A {0,3}([-*_])(?:[ \t]*\1){2,}[ \t]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex ThematicBreak();

    // Non-backtracking: the space runs around the optional pipes would backtrack quadratically.
    [GeneratedRegex(@"\A[ \t]*\|?[ \t]*:?-+:?[ \t]*(?:\|[ \t]*:?-+:?[ \t]*)*\|?[ \t]*\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex TableSeparator();

    [GeneratedRegex(@"\A[ \t]*(?:[-*+]|(?<ordered>[0-9]{1,9})[.)])[ \t]+(?<text>.*)\z", RegexOptions.CultureInvariant)]
    private static partial Regex ListItem();
}
