using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using InventorAddin.Gea;
using InventorAddin.Host;
using InventorAddin.Models;
using InventorAddin.Services.Gea;
using Inv = Inventor;

namespace InventorAddin.Web;

/// <summary>
/// The assembly side of the job executor: an imported script with TARGET ASSEMBLY. Each part named by <c>ADD_COMPONENT ... FROM "name"</c> is built first
/// (with the default form values of the product, from the script that ships with the add-in), saved in the job folder, then inserted into the assembly.
/// Outputs: .iam, the part .ipt files (the .iam refers to them), STEP, pictures and a BOM with one line per part and its quantity. No 2D drawing yet.
/// </summary>
internal sealed partial class InventorJobExecutor
{
    /// <summary>A part built for the assembly: its file in the job folder and its BOM line (quantity 1; the real quantity is counted at the end).</summary>
    private sealed record BuiltPart(string Name, string Path, BomLine Line);

    /// <summary>Names an ADD_COMPONENT may use: the box and the products that ship with a script.</summary>
    private static string AvailableParts() =>
        string.Join(", ", new[] { "box" }.Concat(ScriptedProduct.All.Select(p => p.Code)));

    private JobResult RunAssembly(WorkItem job, string workDir, Action<string, string?> progress, GeaScript script, string scriptName)
    {
        var values = new Dictionary<string, double>(job.Inputs);
        var form = script.EvaluateFormValues(values, job.TextInputs);
        if (!form.Ok)
            return JobResult.Fail("Invalid inputs: " + form.Errors[0].Message, retryable: false);

        Directory.CreateDirectory(workDir);
        Inv.AssemblyDocument? asm = null;
        var built = new Dictionary<string, BuiltPart>(StringComparer.OrdinalIgnoreCase);
        try
        {
            progress("Creating Assembly", "Creating a new Assembly document.");
            asm = NewAssembly();

            var host = new InventorAssemblyHost(_app, asm, name => BuildPartFile(name, workDir, progress, built).Path,
                msg => progress("Building assembly", msg));
            progress("Building assembly", $"Running {scriptName}: placing components.");
            var run = script.RunBuild(form, host);
            if (!run.Ok)
                return JobResult.Fail(run.Error!.Format(scriptName), retryable: false);

            asm.Activate(); // the parts built on the way were active; the pictures are taken of the assembly
            var files = new List<OutputFile>();
            var summary = new Dictionary<string, double>
            {
                ["components"] = host.PlacedParts.Count,
                ["distinctParts"] = built.Count,
            };

            if (string.Equals(job.Kind, "preview", StringComparison.OrdinalIgnoreCase))
            {
                progress("Exporting", "Capturing the preview picture.");
                var previewPath = Path.Combine(workDir, job.Id + PictureViews[0].Suffix + ".png");
                if (!TryCapturePicture(previewPath, PictureViews[0].Orientation))
                    return JobResult.Fail("Could not capture the preview picture (see the add-in log).", retryable: true);
                files.Add(new OutputFile(previewPath, "image"));
                return new JobResult(true, null, false, summary, files);
            }

            progress("Saving", "Saving Inventor assembly.");
            var iamPath = Path.Combine(workDir, job.Id + ".iam");
            asm.SaveAs(iamPath, false);
            files.Add(new OutputFile(iamPath, "iam"));
            foreach (var b in built.Values) files.Add(new OutputFile(b.Path, "ipt"));

            progress("Exporting", "Exporting STEP and pictures.");
            var stepPath = Path.Combine(workDir, job.Id + ".stp");
            if (TryExportStep((Inv._Document)asm, stepPath)) files.Add(new OutputFile(stepPath, "step"));
            else progress("Exporting", "STEP export failed (see add-in log); continuing.");

            foreach (var (suffix, orientation) in PictureViews)
            {
                var pngPath = Path.Combine(workDir, job.Id + suffix + ".png");
                if (TryCapturePicture(pngPath, orientation)) files.Add(new OutputFile(pngPath, "image"));
                else progress("Exporting", $"Picture '{(suffix.Length == 0 ? "iso" : suffix.TrimStart('_'))}' failed (see add-in log); continuing.");
            }

            // BOM: one line per distinct part, with how many times it was placed.
            progress("Exporting", "Writing the BOM.");
            var counts = host.PlacedParts.GroupBy(n => n, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
            var lines = built.Values.Where(b => counts.ContainsKey(b.Name)).Select(b => b.Line with { Quantity = counts[b.Name] }).ToList();
            var bomPath = Path.Combine(workDir, job.Id + "_bom.csv");
            if (WriteBomCsv(bomPath, lines, values, job.TextInputs)) files.Add(new OutputFile(bomPath, "bom"));
            else progress("Exporting", "BOM failed (see add-in log); continuing.");

            progress("Exporting", "No 2D drawing for an assembly yet.");
            return new JobResult(true, null, false, summary, files);
        }
        catch (Exception ex)
        {
            Log.Error($"Assembly job {job.Id} failed in Inventor", ex);
            return JobResult.Fail(ex.Message, retryable: true);
        }
        finally
        {
            CloseAssemblyQuietly(asm);
        }
    }

    private Inv.AssemblyDocument NewAssembly()
    {
        var template = _app.FileManager.GetTemplateFile(
            Inv.DocumentTypeEnum.kAssemblyDocumentObject, Inv.SystemOfMeasureEnum.kMetricSystemOfMeasure);
        var doc = (Inv.AssemblyDocument)_app.Documents.Add(Inv.DocumentTypeEnum.kAssemblyDocumentObject, template, true);
        doc.Activate();
        return doc;
    }

    /// <summary>
    /// Builds the part called <paramref name="partName"/> once per job (default form values), saves it as <c>&lt;name&gt;.ipt</c> in the job folder
    /// and closes it. An unknown name is GEA4021.
    /// </summary>
    private BuiltPart BuildPartFile(string partName, string workDir, Action<string, string?> progress, Dictionary<string, BuiltPart> cache)
    {
        var key = partName.Trim().ToLowerInvariant();
        if (cache.TryGetValue(key, out var done)) return done;

        string scriptFile;
        Dictionary<string, double> values;
        double? massKg = null;
        if (key == "box")
        {
            scriptFile = "box.gea";
            values = BoxParams.Complete(new Dictionary<string, double>());
        }
        else
        {
            var product = ScriptedProduct.Find(key)
                          ?? throw new GeaRuntimeException("GEA4021", $"Unknown part '{partName}'. Available: {AvailableParts()}.");
            scriptFile = product.ScriptFile;
            values = product.Complete(new Dictionary<string, double>());
        }

        progress("Building assembly", $"Building part {key}.");
        var scriptPath = GeaScriptLibrary.BundledPath(scriptFile);
        if (!File.Exists(scriptPath))
            throw new GeaRuntimeException("GEA4021", $"Script {scriptFile} of part '{key}' is missing next to the add-in ({scriptPath}).");
        var script = GeaScript.Compile(File.ReadAllText(scriptPath), GeaScriptLibrary.LibraryResolver(scriptPath), scriptFile);
        if (script.HasErrors)
            throw new GeaRuntimeException("GEA4021", $"{scriptFile} has errors: " + script.Diagnostics.First(d => d.Severity == GeaSeverity.Error).Format(scriptFile));
        var form = script.EvaluateFormValues(values, null);
        if (!form.Ok)
            throw new GeaRuntimeException("GEA4021", $"Part '{key}': " + form.Errors[0].Message);

        Inv.PartDocument? doc = null;
        try
        {
            doc = NewPart();
            var applied = new GeaDocumentService(_app).Apply(doc, script, form, scriptPath, null);
            if (!applied.Ok) throw new GeaRuntimeException("GEA4021", $"Part '{key}' could not be built: {applied.Message}");

            double? volumeCm3 = null;
            try
            {
                var def = doc.ComponentDefinition;
                volumeCm3 = Math.Round(Convert.ToDouble(def.MassProperties.Volume), 3);
                if (key == "box") massKg = Math.Round(Convert.ToDouble(def.MassProperties.Mass), 4); // the others are envelope models: no mass
            }
            catch (Exception ex) { Log.Warn("Mass properties unavailable: " + ex.Message); }

            var path = Path.Combine(workDir, key + ".ipt");
            doc.SaveAs(path, false);
            var line = PartBomLine(doc, Path.GetFileName(path), key, script.Program.ProductName, 1, massKg, volumeCm3);
            var result = new BuiltPart(key, path, line);
            cache[key] = result;
            return result;
        }
        finally
        {
            CloseQuietly(doc);
        }
    }

    private static void CloseAssemblyQuietly(Inv.AssemblyDocument? doc)
    {
        try { doc?.Close(true); } // true = do not ask to save (the .iam was saved above)
        catch (Exception ex) { Log.Warn("Could not close the job assembly: " + ex.Message); }
    }
}
