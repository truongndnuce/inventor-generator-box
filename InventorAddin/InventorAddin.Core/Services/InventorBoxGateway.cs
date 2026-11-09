using System;
using System.Collections.Generic;
using InventorAddin.Host;
using InventorAddin.Models;
using Inv = Inventor;

namespace InventorAddin.Services;

/// <summary>
/// Lớp duy nhất gọi Inventor API. Đơn vị nội bộ của API là cm: mọi chuyển đổi mm↔cm nằm ở <see cref="MmToCm"/>/<see cref="CmToMm"/>.
/// Mọi thao tác ghi gói trong một Transaction để Ctrl+Z hoàn tác trọn vẹn.
/// </summary>
public sealed class InventorBoxGateway : IBoxGateway
{
    public const int SchemaVersion = 1; // đánh dấu phiên bản cấu trúc dữ liệu Box ghi vào Part

    private const string MarkerSetName = "BX_Box";

    private readonly Inv.Application _app;

    public InventorBoxGateway(Inv.Application app) => _app = app;

    public static double MmToCm(double mm) => mm / 10.0;

    public static double CmToMm(double cm) => Math.Round(cm * 10.0, 6);

    // ---------------------------------------------------------------- trạng thái

    private Inv.PartDocument? ActivePart(out DocState? state)
    {
        state = null;
        Inv._Document? doc = null;
        try { doc = _app.ActiveDocument; } catch { /* không có document */ }

        if (doc == null)
        {
            state = new DocState(DocKind.NoDocument, "No document is open. Open or create a Part.");
            return null;
        }
        if (doc.DocumentType != Inv.DocumentTypeEnum.kPartDocumentObject)
        {
            state = new DocState(DocKind.NotPart, "Only Part documents are supported. Switch to a Part (.ipt).");
            return null;
        }
        return (Inv.PartDocument)doc;
    }

    public DocState GetState()
    {
        try
        {
            var part = ActivePart(out var early);
            if (part == null) return early!;

            if (part.AttributeSets.NameIsUsed[MarkerSetName])
                return new DocState(DocKind.BoxPart, "This Part contains a box created by the add-in. You can edit its parameters.");

            var def = part.ComponentDefinition;
            bool foreign = def.SurfaceBodies.Count > 0 || def.Features.Count > 0 || def.Sketches.Count > 0
                           || def.Parameters.UserParameters.Count > 0;
            return foreign
                ? new DocState(DocKind.ForeignContent, "This Part already has other geometry or parameters. Creating the box will add to it.")
                : new DocState(DocKind.EmptyPart, "Empty Part. Click Create to build the box.");
        }
        catch (Exception ex)
        {
            Log.Error("GetState failed", ex);
            return new DocState(DocKind.NoDocument, "Could not read the document state: " + ex.Message);
        }
    }

    public ReadResult ReadValues()
    {
        try
        {
            var part = ActivePart(out _);
            if (part == null || !part.AttributeSets.NameIsUsed[MarkerSetName])
                return new ReadResult(null, "This Part does not contain a box created by the add-in.");

            var pars = part.ComponentDefinition.Parameters.UserParameters;
            var result = new Dictionary<string, double>();
            var missing = new List<string>();
            foreach (var d in BoxParams.All)
            {
                try { result[d.Name] = CmToMm(Convert.ToDouble(pars[d.Name].Value)); }
                catch { missing.Add(d.Name); }
            }
            return missing.Count > 0
                ? new ReadResult(null, "Missing parameters: " + string.Join(", ", missing))
                : new ReadResult(result, null);
        }
        catch (Exception ex)
        {
            Log.Error("ReadValues failed", ex);
            return new ReadResult(null, "Could not read the parameters: " + ex.Message);
        }
    }

    // ---------------------------------------------------------------- tạo

    public OperationResult Create(IReadOnlyDictionary<string, double> values, bool allowForeignContent)
    {
        var issues = BoxParams.Validate(values);
        if (issues.Count > 0) return new OperationResult(false, issues[0].Message);

        var part = ActivePart(out var early);
        if (part == null) return new OperationResult(false, early!.Message);

        var state = GetState();
        if (state.Kind == DocKind.BoxPart)
            return new OperationResult(false, "This Part already has a box. Edit its parameters instead of creating a new one.");
        if (state.Kind == DocKind.ForeignContent && !allowForeignContent)
            return new OperationResult(false, "Confirmation is required before adding the box to a Part that already has content.");

        var def = part.ComponentDefinition;
        foreach (Inv.UserParameter p in def.Parameters.UserParameters)
            if (p.Name.StartsWith(BoxParams.Prefix, StringComparison.OrdinalIgnoreCase))
                return new OperationResult(false, $"Parameter '{p.Name}' already exists and was not created by the add-in. Not overwriting.");

        return RunInTransaction(part, "Create box", () =>
        {
            new BoxBuilder(_app, part, values).Build();
            var set = part.AttributeSets.Add(MarkerSetName, false);
            set.Add("Schema", Inv.ValueTypeEnum.kIntegerType, SchemaVersion);
        }, "Box created.");
    }

    // ---------------------------------------------------------------- chỉnh

    public OperationResult Apply(IReadOnlyDictionary<string, double> values)
    {
        var issues = BoxParams.Validate(values);
        if (issues.Count > 0) return new OperationResult(false, issues[0].Message);

        var part = ActivePart(out var early);
        if (part == null) return new OperationResult(false, early!.Message);
        if (!part.AttributeSets.NameIsUsed[MarkerSetName])
            return new OperationResult(false, "This Part does not contain a box created by the add-in.");

        return RunInTransaction(part, "Edit box parameters", () =>
        {
            var pars = part.ComponentDefinition.Parameters.UserParameters;
            foreach (var d in BoxParams.All)
            {
                var p = pars[d.Name];
                if (Math.Abs(CmToMm(Convert.ToDouble(p.Value)) - values[d.Name]) > 1e-9)
                    p.Value = MmToCm(values[d.Name]);
            }
        }, "Parameters applied.");
    }

    // ---------------------------------------------------------------- tiện ích

    /// <summary>Chạy <paramref name="work"/> trong một Transaction; lỗi hoặc model hỏng thì hoàn tác toàn bộ.</summary>
    internal OperationResult RunInTransaction(Inv.PartDocument part, string name, Action work, string okMessage) =>
        InventorTransactions.Run(_app, part, name, work, okMessage);
}

