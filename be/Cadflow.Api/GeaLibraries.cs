using System.Text;
using System.Text.RegularExpressions;

namespace Cadflow.Api;

/// <summary>
/// Component libraries for <c>INCLUDE lib:&lt;name&gt;</c>: the <c>Scripts/lib/&lt;name&gt;.gea</c> files that ship with the server.
/// The server expands INCLUDE before it stores a script, so the Inventor worker receives an already expanded script and needs no library.
/// </summary>
public static partial class GeaLibraries
{
    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex ValidName();

    private static string Dir => Path.Combine(AppContext.BaseDirectory, "Scripts", "lib");

    /// <summary>Names of the bundled libraries (<c>Scripts/lib/*.gea</c> files with a valid name), sorted.</summary>
    public static IEnumerable<string> Names() =>
        Directory.Exists(Dir)
            ? Directory.GetFiles(Dir, "*.gea").Select(f => Path.GetFileNameWithoutExtension(f)).Where(n => ValidName().IsMatch(n)).OrderBy(n => n, StringComparer.Ordinal)
            : [];

    /// <summary>Content of the library, or null if the name is invalid or there is no such file (never reaches outside the lib folder).</summary>
    public static string? Resolve(string name) =>
        ValidName().IsMatch(name) && Path.Combine(Dir, name + ".gea") is var path && File.Exists(path)
            ? File.ReadAllText(path, Encoding.UTF8)
            : null;
}
