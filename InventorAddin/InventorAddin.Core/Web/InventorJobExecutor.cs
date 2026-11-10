using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using InventorAddin.Gea;
using InventorAddin.Host;
using InventorAddin.Models;
using InventorAddin.Services;
using InventorAddin.Services.Gea;
using Inv = Inventor;

namespace InventorAddin.Web;

/// <summary>A file produced for a job (a path in the working folder) and its kind.</summary>
public sealed record OutputFile(string Path, string Kind);

public sealed record JobResult(
    bool Ok,
    string? Error,
    bool Retryable,
    Dictionary<string, double> Summary,
    IReadOnlyList<OutputFile> Files)
{
    public static JobResult Fail(string error, bool retryable) => new(false, error, retryable, new(), Array.Empty<OutputFile>());
}

/// <summary>
/// Builds one job sent by the web: opens a new Part, builds the box with the add-in's own <see cref="InventorBoxGateway"/>
/// (same Transaction, same feature error checks), then exports .ipt, STEP and images.
/// May only be called on the Inventor UI thread.
/// </summary>
internal sealed class InventorJobExecutor
{
    private const string StepTranslatorId = "{90AF7F40-0C01-11D5-8E83-0010B541CD80}";
    private const string ImportedProduct = "imported"; // matches the constant of the same name in be/Cadflow.Api/Program.cs

    private readonly Inv.Application _app;
    private readonly InventorBoxGateway _gateway;

    public InventorJobExecutor(Inv.Application app)
    {
        _app = app;
        _gateway = new InventorBoxGateway(app);
    }

    public string? InventorVersion()
    {
        try { return _app.SoftwareVersion.DisplayVersion; }
        catch { return null; }
    }

    public JobResult Run(WorkItem job, string workDir, Action<string, string?> progress)
    {
        // The server already checked, but the worker checks again: if wrong, stop before touching Inventor.
        bool isBox = job.ProductCode == "box";
        var scripted = ScriptedProduct.Find(job.ProductCode); // transformer, gas turbine, generator...
        bool isImported = job.ProductCode == ImportedProduct; // a .gea script imported by the user on the web
        if (!isBox && scripted == null && !isImported)
            return JobResult.Fail($"Unknown product '{job.ProductCode}'.", retryable: false);
        if (isImported && string.IsNullOrEmpty(job.ScriptText))
            return JobResult.Fail("The job has no script text.", retryable: false);

        // An imported script is checked with its own FORM block (below); built-in products use the shared C# rules.
        var values = isImported ? new Dictionary<string, double>(job.Inputs)
            : isBox ? BoxParams.Complete(job.Inputs) : scripted!.Complete(job.Inputs);
        if (!isImported)
        {
            var issues = isBox ? BoxParams.Validate(values) : scripted!.Validate(values);
            if (issues.Count > 0)
                return JobResult.Fail("Invalid inputs: " + issues[0].Message, retryable: false);
        }

        Directory.CreateDirectory(workDir);
        Inv.PartDocument? doc = null;
        try
        {
            progress("Creating Part", "Creating a new Part document.");
            doc = NewPart();

            // Box is built by the box.gea script when the server sends the script along (so export/import of .gea matches what was built);
            // an old server that does not send the script uses the C# gateway as before.
            bool useScript = !isBox || !string.IsNullOrEmpty(job.ScriptText);

            OperationResult created;
            if (!useScript)
            {
                progress("Building box", "Building body and lid.");
                created = _gateway.Create(values, allowForeignContent: true);
            }
            else
            {
                // Build with the .gea script: the same script can be opened in the add-in to edit and Apply again,
                // because the script and values are stored in the .ipt.
                // Prefer the script copy the server sends (exactly the one recorded for the job, so export matches); if none, use the one that ships with the add-in.
                string scriptName, scriptText;
                string? scriptPath = null;
                if (!string.IsNullOrEmpty(job.ScriptText))
                {
                    scriptName = string.IsNullOrWhiteSpace(job.ScriptName) ? "script.gea" : job.ScriptName!;
                    scriptText = job.ScriptText!;
                }
                else
                {
                    // The server sent no script (old server): use the one that ships with the add-in. (Box does not take this branch.)
                    scriptName = scripted!.ScriptFile;
                    scriptPath = GeaScriptLibrary.BundledPath(scriptName);
                    if (!File.Exists(scriptPath))
                        return JobResult.Fail($"Script {scriptName} is missing next to the add-in ({scriptPath}).", retryable: false);
                    scriptText = File.ReadAllText(scriptPath);
                }

                var script = GeaScript.Compile(scriptText, GeaScriptLibrary.LibraryResolver(scriptPath), scriptName); // a job from an old server may still contain INCLUDE
                if (script.HasErrors)
                    return JobResult.Fail($"{scriptName} has errors: " + script.Diagnostics.First(d => d.Severity == GeaSeverity.Error).Format(scriptName), retryable: false);
                if (script.Program.Target != "PART")
                    return JobResult.Fail($"{scriptName}: TARGET {script.Program.Target} is not supported (only PART).", retryable: false);
                var form = script.EvaluateFormValues(values, job.TextInputs);
                if (!form.Ok)
                    return JobResult.Fail("Invalid inputs: " + form.Errors[0].Message, retryable: false);

                var stage = isImported ? "Building from script" : isBox ? "Building box" : scripted!.Stage;
                progress(stage, isImported ? $"Running the imported script \"{scriptName}\"."
                    : isBox ? $"Running {scriptName}: body and lid."
                    : $"Running {scriptName}: {scripted!.StageMessage}");
                created = new GeaDocumentService(_app).Apply(doc, script, form, scriptPath, msg => progress(stage, msg));
            }
            if (!created.Ok)
                return JobResult.Fail(created.Message, retryable: false);

            var files = new List<OutputFile>();
            var summary = new Dictionary<string, double>();

            progress("Measuring", null);
            try
            {
                var def = doc.ComponentDefinition;
                // The transformer is a solid block model (an envelope), so a mass computed from the volume would be wrong; do not report it.
                if (isBox) summary["massKg"] = Math.Round(Convert.ToDouble(def.MassProperties.Mass), 4);
                summary["volumeCm3"] = Math.Round(Convert.ToDouble(def.MassProperties.Volume), 3);
                summary["bodies"] = def.SurfaceBodies.Count;
            }
            catch (Exception ex)
            {
                Log.Warn("Mass properties unavailable: " + ex.Message);
            }

            // A preview is only a quick picture for the screen: no .ipt, no STEP, one view, no drawing.
            if (string.Equals(job.Kind, "preview", StringComparison.OrdinalIgnoreCase))
            {
                progress("Exporting", "Capturing the preview picture.");
                var previewPath = Path.Combine(workDir, job.Id + PictureViews[0].Suffix + ".png");
                if (!TryCapturePicture(previewPath, PictureViews[0].Orientation))
                    return JobResult.Fail("Could not capture the preview picture (see the add-in log).", retryable: true);
                files.Add(new OutputFile(previewPath, "image"));
                return new JobResult(true, null, false, summary, files);
            }

            progress("Saving", "Saving Inventor part.");
            var iptPath = Path.Combine(workDir, job.Id + ".ipt");
            doc.SaveAs(iptPath, false);
            files.Add(new OutputFile(iptPath, "ipt"));

            progress("Exporting", "Exporting STEP and picture.");
            var stepPath = Path.Combine(workDir, job.Id + ".stp");
            if (TryExportStep((Inv._Document)doc, stepPath)) files.Add(new OutputFile(stepPath, "step"));
            else progress("Exporting", "STEP export failed (see add-in log); continuing.");

            // Several views so the web can show an image gallery. The iso view keeps the old name (<id>.png) for compatibility.
            foreach (var (suffix, orientation) in PictureViews)
            {
                var pngPath = Path.Combine(workDir, job.Id + suffix + ".png");
                if (TryCapturePicture(pngPath, orientation)) files.Add(new OutputFile(pngPath, "image"));
                else progress("Exporting", $"Picture '{(suffix.Length == 0 ? "iso" : suffix.TrimStart('_'))}' failed (see add-in log); continuing.");
            }

            // 2D drawing (PDF). Built after the images because creating a drawing changes the active view. A drawing error does not fail the job.
            progress("Drawing", "Creating the 2D drawing (views, section, dimensions).");
            var pdfPath = Path.Combine(workDir, job.Id + ".pdf");
            // Level heights to write the dimension string on the section: only known for the two built-in products. An imported script drops this dimension string.
            var levels = isImported ? Array.Empty<double>()
                : isBox
                    ? new[] { 0, values[BoxParams.Bottom], values[BoxParams.Height] }
                    : scripted!.Levels(values);
            var dwgPath = Path.Combine(workDir, job.Id + ".dwg");
            var drawing = new DrawingBuilder(_app).Build(doc, zUp: isBox && !useScript, levels, pdfPath, dwgPath); // a box built in C# has a vertical Z axis; built with the .gea builders the vertical axis is Y
            if (drawing.Ok)
            {
                files.Add(new OutputFile(pdfPath, "pdf"));
                if (drawing.DwgOk) files.Add(new OutputFile(dwgPath, "dwg"));
                summary["drawingViews"] = drawing.Views;
                summary["drawingDims"] = drawing.Dimensions;
                summary["drawingScaleDenominator"] = drawing.ScaleDenominator;
                if (drawing.Warning != null) progress("Drawing", $"Drawing {drawing.Sheet} 1:{drawing.ScaleDenominator:0.#} created with warnings: {drawing.Warning}");
                else progress("Drawing", $"Drawing {drawing.Sheet} 1:{drawing.ScaleDenominator:0.#}, {drawing.Views} views, {drawing.Dimensions} dimensions.");
            }
            else progress("Drawing", "Drawing failed (see add-in log); continuing: " + drawing.Warning);

            return new JobResult(true, null, false, summary, files);
        }
        catch (Exception ex)
        {
            Log.Error($"Job {job.Id} failed in Inventor", ex);
            // An error from Inventor/COM (dialog, document, license...) may be temporary: allow a retry.
            return JobResult.Fail(ex.Message, retryable: true);
        }
        finally
        {
            CloseQuietly(doc);
        }
    }

    // ---------------------------------------------------------------- Inventor

    private Inv.PartDocument NewPart()
    {
        var template = _app.FileManager.GetTemplateFile(
            Inv.DocumentTypeEnum.kPartDocumentObject, Inv.SystemOfMeasureEnum.kMetricSystemOfMeasure);
        var doc = (Inv.PartDocument)_app.Documents.Add(Inv.DocumentTypeEnum.kPartDocumentObject, template, true);
        doc.Activate(); // the gateway works on the active document
        return doc;
    }

    private bool TryExportStep(Inv._Document doc, string path)
    {
        try
        {
            var translator = (Inv.TranslatorAddIn)_app.ApplicationAddIns.ItemById[StepTranslatorId];
            if (!translator.Activated) translator.Activate();

            var context = _app.TransientObjects.CreateTranslationContext();
            context.Type = Inv.IOMechanismEnum.kFileBrowseIOMechanism;
            var options = _app.TransientObjects.CreateNameValueMap();
            if (translator.HasSaveCopyAsOptions[doc, context, options])
                options.Value["ApplicationProtocolType"] = 3; // AP214 (Automotive Design)

            var medium = _app.TransientObjects.CreateDataMedium();
            medium.FileName = path;
            translator.SaveCopyAs(doc, context, options, medium);
            return File.Exists(path);
        }
        catch (Exception ex)
        {
            Log.Error("STEP export failed", ex);
            return false;
        }
    }

    private static readonly (string Suffix, Inv.ViewOrientationTypeEnum Orientation)[] PictureViews =
    {
        ("", Inv.ViewOrientationTypeEnum.kIsoTopRightViewOrientation),
        ("_front", Inv.ViewOrientationTypeEnum.kFrontViewOrientation),
        ("_top", Inv.ViewOrientationTypeEnum.kTopViewOrientation),
        ("_right", Inv.ViewOrientationTypeEnum.kRightViewOrientation),
    };

    private bool TryCapturePicture(string path, Inv.ViewOrientationTypeEnum orientation)
    {
        try
        {
            var view = _app.ActiveView;
            var camera = view.Camera;
            camera.ViewOrientationType = orientation;
            camera.Fit();
            camera.Apply();
            // Leave a 15% margin around the model so edges are not cut off.
            camera.GetExtents(out double extW, out double extH);
            camera.SetExtents(extW * 1.15, extH * 1.15);
            camera.Apply();

            // The image has the same aspect ratio as the viewport, otherwise the part outside the ratio would be cropped.
            const int height = 960;
            int width = view.Height > 0 ? (int)Math.Round(height * (double)view.Width / view.Height) : 1280;
            view.SaveAsBitmap(path, Math.Clamp(width, 480, 2400), height);
            return File.Exists(path);
        }
        catch (Exception ex)
        {
            Log.Error("Picture capture failed", ex);
            return false;
        }
    }

    private static void CloseQuietly(Inv.PartDocument? doc)
    {
        try { doc?.Close(true); } // true = do not ask to save (the .ipt was saved above)
        catch (Exception ex) { Log.Warn("Could not close the job document: " + ex.Message); }
    }
}
