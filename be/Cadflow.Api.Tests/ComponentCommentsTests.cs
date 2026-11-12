namespace Cadflow.Api.Tests;

/// <summary>
/// Convention for component libraries (Scripts/lib/*.gea): the downloaded file must explain every step.
/// The first paragraph of the leading comment is a short description; every statement paragraph (consecutive lines, no blank line) in a FORM/BUILD block has a comment.
/// </summary>
public class ComponentCommentsTests
{
    public static IEnumerable<object[]> Libraries() =>
        Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Scripts", "lib"), "*.gea")
            .Select(f => new object[] { Path.GetFileName(f) });

    private static string[] Lines(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Scripts", "lib", name)).Replace("\r\n", "\n").Split('\n');

    internal static bool IsComment(string l) => l.TrimStart().StartsWith('!');

    [Fact]
    public void There_is_at_least_one_library()
    {
        Assert.NotEmpty(Libraries());
    }

    [Theory]
    [MemberData(nameof(Libraries))]
    public void File_starts_with_a_description(string file)
    {
        var header = Lines(file).TakeWhile(l => l.Trim().Length == 0 || IsComment(l)).ToArray();
        var text = header.Select(l => l.Trim().TrimStart('!').Trim()).Where(t => t.Length > 0 && !t.All(c => c == '=')).ToArray();
        Assert.True(text.Length > 0, $"{file}: missing description comment at the top of the file.");
    }

    /// <summary>Statement paragraphs in FORM/BUILD blocks that have no comment (empty when the convention is met).</summary>
    public static List<string> UncommentedParagraphs(string file, string[] lines)
    {
        var problems = new List<string>();
        string? block = null;
        var para = new List<(int No, string Text)>();

        void Flush()
        {
            if (block != null && para.Count > 0 && !para.Any(p => IsComment(p.Text)))
                problems.Add($"{file}:{para[0].No} ({block}): the statement paragraph starting with '{para[0].Text.Trim()}' has no explanatory comment.");
            para.Clear();
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var t = lines[i].Trim();
            if (block == null)
            {
                if (t == "BEGIN_FORM") block = "FORM";
                else if (t == "BEGIN_BUILD") block = "BUILD";
                continue;
            }
            if (t == "END_FORM" || t == "END_BUILD") { Flush(); block = null; continue; }
            if (t.Length == 0) { Flush(); continue; }
            para.Add((i + 1, lines[i]));
        }
        return problems;
    }

    [Theory]
    [MemberData(nameof(Libraries))]
    public void Every_statement_paragraph_in_FORM_and_BUILD_has_a_comment(string file)
    {
        var problems = UncommentedParagraphs(file, Lines(file));
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }
}
