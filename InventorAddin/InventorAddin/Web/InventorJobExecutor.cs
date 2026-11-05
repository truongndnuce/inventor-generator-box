using System;
using System.Collections.Generic;
using System.IO;
using InventorAddin.Host;
using InventorAddin.Models;
using InventorAddin.Services;
using Inv = Inventor;

namespace InventorAddin.Web;

/// <summary>File sinh ra cho một job (đường dẫn trong thư mục làm việc) và loại của nó.</summary>
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
/// Dựng một job do web gửi tới: mở Part mới, dựng hộp bằng chính <see cref="InventorBoxGateway"/> của add-in
/// (cùng Transaction, cùng kiểm tra lỗi feature), rồi xuất .ipt, STEP và ảnh.
/// Chỉ được gọi trên UI thread của Inventor.
/// </summary>
internal sealed class InventorJobExecutor
{
    private const string StepTranslatorId = "{90AF7F40-0C01-11D5-8E83-0010B541CD80}";

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
        // Server đã kiểm tra, nhưng worker kiểm tra lại: sai thì dừng mà chưa đụng tới Inventor.
        bool isBox = job.ProductCode == "box";
        bool isTransformer = job.ProductCode == TransformerParams.Code;
        if (!isBox && !isTransformer)
            return JobResult.Fail($"Unknown product '{job.ProductCode}'.", retryable: false);

        var values = isBox ? BoxParams.Complete(job.Inputs) : TransformerParams.Complete(job.Inputs);
        var issues = isBox ? BoxParams.Validate(values) : TransformerParams.Validate(values);
        if (issues.Count > 0)
            return JobResult.Fail("Invalid inputs: " + issues[0].Message, retryable: false);

        Directory.CreateDirectory(workDir);
        Inv.PartDocument? doc = null;
        try
        {
            progress("Creating Part", "Creating a new Part document.");
            doc = NewPart();

            OperationResult created;
            if (isBox)
            {
                progress("Building box", "Building body and lid.");
                created = _gateway.Create(values, allowForeignContent: true);
            }
            else
            {
                progress("Building transformer", "Building tank, radiators, conservator and bushings.");
                created = _gateway.RunInTransaction(doc, "Create transformer", () =>
                {
                    new TransformerBuilder(_app, doc, values).Build();
                    var marker = doc.AttributeSets.Add("TR_Transformer", false);
                    marker.Add("Schema", Inv.ValueTypeEnum.kIntegerType, 1);
                }, "Transformer created.");
            }
            if (!created.Ok)
                return JobResult.Fail(created.Message, retryable: false);

            var files = new List<OutputFile>();
            var summary = new Dictionary<string, double>();

            progress("Measuring", null);
            try
            {
                var def = doc.ComponentDefinition;
                // Máy biến áp là mô hình hình khối đặc (vỏ bao), khối lượng tính từ thể tích sẽ sai nên không báo.
                if (isBox) summary["massKg"] = Math.Round(Convert.ToDouble(def.MassProperties.Mass), 4);
                summary["volumeCm3"] = Math.Round(Convert.ToDouble(def.MassProperties.Volume), 3);
                summary["bodies"] = def.SurfaceBodies.Count;
            }
            catch (Exception ex)
            {
                Log.Warn("Mass properties unavailable: " + ex.Message);
            }

            progress("Saving", "Saving Inventor part.");
            var iptPath = Path.Combine(workDir, job.Id + ".ipt");
            doc.SaveAs(iptPath, false);
            files.Add(new OutputFile(iptPath, "ipt"));

            progress("Exporting", "Exporting STEP and picture.");
            var stepPath = Path.Combine(workDir, job.Id + ".stp");
            if (TryExportStep((Inv._Document)doc, stepPath)) files.Add(new OutputFile(stepPath, "step"));
            else progress("Exporting", "STEP export failed (see add-in log); continuing.");

            // Nhiều góc nhìn để web hiển thị thư viện ảnh. Góc iso giữ tên cũ (<id>.png) cho tương thích.
            foreach (var (suffix, orientation) in PictureViews)
            {
                var pngPath = Path.Combine(workDir, job.Id + suffix + ".png");
                if (TryCapturePicture(pngPath, orientation)) files.Add(new OutputFile(pngPath, "image"));
                else progress("Exporting", $"Picture '{(suffix.Length == 0 ? "iso" : suffix.TrimStart('_'))}' failed (see add-in log); continuing.");
            }

            // Bản vẽ 2D (PDF). Dựng sau ảnh vì tạo drawing làm đổi view đang active. Lỗi bản vẽ không làm hỏng job.
            progress("Drawing", "Creating the 2D drawing (views, section, dimensions).");
            var pdfPath = Path.Combine(workDir, job.Id + ".pdf");
            var levels = isBox
                ? new[] { 0, values[BoxParams.Bottom], values[BoxParams.Height] }
                : new[] { 0, values[TransformerParams.BaseThickness], values[TransformerParams.BaseThickness] + values[TransformerParams.TankHeight],
                          values[TransformerParams.BaseThickness] + values[TransformerParams.TankHeight] + values[TransformerParams.CoverThickness] };
            var drawing = new DrawingBuilder(_app).Build(doc, zUp: isBox, levels, pdfPath);
            if (drawing.Ok)
            {
                files.Add(new OutputFile(pdfPath, "pdf"));
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
            // Lỗi từ Inventor/COM (hộp thoại, tài liệu, giấy phép...) có thể là tạm thời: cho phép thử lại.
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
        doc.Activate(); // gateway làm việc trên document đang active
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
            // Chừa lề 15% quanh mô hình để không bị cắt mép.
            camera.GetExtents(out double extW, out double extH);
            camera.SetExtents(extW * 1.15, extH * 1.15);
            camera.Apply();

            // Ảnh cùng tỷ lệ với khung nhìn, nếu không phần ngoài tỷ lệ sẽ bị cắt.
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
        try { doc?.Close(true); } // true = bỏ qua việc hỏi lưu (đã lưu bản .ipt ở trên)
        catch (Exception ex) { Log.Warn("Could not close the job document: " + ex.Message); }
    }
}
