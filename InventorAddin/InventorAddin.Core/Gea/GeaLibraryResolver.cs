using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace InventorAddin.Gea;

/// <summary>
/// Where libraries for <c>INCLUDE lib:&lt;name&gt;</c> are loaded from when the script is on disk (the Inventor add-in).
/// Search order: <c>&lt;file folder&gt;/lib/&lt;name&gt;.gea</c>, <c>&lt;file folder&gt;/&lt;name&gt;.gea</c>, then the library folder that ships with the add-in.
/// An invalid name (with a path, <c>..</c>, odd characters) never touches the disk.
/// </summary>
public static class GeaLibraryResolver
{
    private static readonly Regex ValidName = new("^[A-Za-z0-9_-]{1,64}$", RegexOptions.Compiled);

    public static Func<string, string?> For(string? scriptDirectory, string bundledLibDirectory) => name =>
    {
        if (!ValidName.IsMatch(name)) return null;
        var file = name + ".gea";
        if (!string.IsNullOrEmpty(scriptDirectory))
        {
            var beside = Read(Path.Combine(scriptDirectory, "lib", file)) ?? Read(Path.Combine(scriptDirectory, file));
            if (beside != null) return beside;
        }
        return Read(Path.Combine(bundledLibDirectory, file));
    };

    private static string? Read(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
