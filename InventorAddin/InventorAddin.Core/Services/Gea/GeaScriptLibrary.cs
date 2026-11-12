using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using InventorAddin.Host;

namespace InventorAddin.Services.Gea;

/// <summary>
/// Where .gea files live: the templates that ship with the add-in (the Scripts folder next to the DLL, read-only) and the user's own copies
/// (%AppData%\InventorBoxAddin\scripts). Opening a template from the add-in copies it to the user folder so it can be edited.
/// </summary>
public static class GeaScriptLibrary
{
    public static string BundledDirectory { get; } =
        Path.Combine(Path.GetDirectoryName(typeof(GeaScriptLibrary).Assembly.Location) ?? ".", "Scripts");

    public static string UserDirectory => Path.Combine(Log.DataDirectory, "scripts");

    /// <summary>The component libraries that ship with the add-in (Scripts\lib), used for <c>INCLUDE lib:</c>.</summary>
    public static string BundledLibDirectory => Path.Combine(BundledDirectory, "lib");

    /// <summary>Resolver for <c>INCLUDE lib:</c>: looks next to the open file (if any), then in the library that ships with the add-in.</summary>
    public static Func<string, string?> LibraryResolver(string? scriptPath = null) =>
        InventorAddin.Gea.GeaLibraryResolver.For(scriptPath == null ? null : Path.GetDirectoryName(scriptPath), BundledLibDirectory);

    public static string BundledPath(string fileName) => Path.Combine(BundledDirectory, fileName);

    public static IReadOnlyList<string> BundledScripts()
    {
        try
        {
            return Directory.Exists(BundledDirectory)
                ? Directory.GetFiles(BundledDirectory, "*.gea").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList()
                : Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>The editable copy of a template: copied into the user folder if not there yet, returns the path of the copy.</summary>
    public static string EditableCopy(string bundledPath)
    {
        Directory.CreateDirectory(UserDirectory);
        var target = Path.Combine(UserDirectory, Path.GetFileName(bundledPath));
        if (!File.Exists(target)) File.Copy(bundledPath, target);
        return target;
    }
}
