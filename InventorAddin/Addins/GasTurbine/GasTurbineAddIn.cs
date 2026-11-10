using System;
using System.Drawing;
using System.Runtime.InteropServices;
using InventorAddin.Host;

namespace InventorAddin.Addins.GasTurbine;

/// <summary>
/// The Gas Turbine add-in: the Script (.gea) window has only the templates of this product, and the web worker builds the "gas-turbine" jobs placed on the web.
/// Everything else (the ribbon sharing the Cadflow tab, the window, the worker) is handled by <see cref="CadflowAddIn"/>.
/// </summary>
[ComVisible(true)]
[Guid(ClientId)]
[ClassInterface(ClassInterfaceType.None)]
public sealed class GasTurbineAddIn : ScriptProductAddIn
{
    public const string ClientId = "AC1D2DE6-5E6E-4D4B-9D11-30C202F749C8";

    protected override string AddinId => "gas-turbine";
    protected override string Title => "Gas Turbine";
    protected override string Description => "Build an industrial gas turbine (casings, rings, combustor cans, enclosure, skid) from .gea scripts";
    protected override string[] JobProducts => new[] { "gas-turbine" };
    protected override string[] Templates => new[] { "turbine.gea" };

    protected override void DrawIcon(Graphics g, int size)
    {
        using var cas = new SolidBrush(Color.FromArgb(0x8A, 0x93, 0xA0));
        using var ring = new SolidBrush(Color.FromArgb(0xC9, 0xCD, 0xD1));
        using var skid = new SolidBrush(Color.FromArgb(0x4A, 0x7C, 0xB8));
        using var pen = new Pen(Color.FromArgb(0x2B, 0x3A, 0x4F), Math.Max(1f, size / 16f));
        g.FillRectangle(skid, size * 0.05f, size * 0.78f, size * 0.9f, size * 0.12f);
        g.FillRectangle(cas, size * 0.08f, size * 0.30f, size * 0.22f, size * 0.46f);
        g.FillRectangle(cas, size * 0.30f, size * 0.38f, size * 0.30f, size * 0.38f);
        g.FillRectangle(cas, size * 0.60f, size * 0.34f, size * 0.32f, size * 0.42f);
        for (int i = 0; i < 3; i++) g.FillRectangle(ring, size * (0.34f + i * 0.09f), size * 0.32f, size * 0.04f, size * 0.50f - size * 0.06f);
        g.DrawRectangle(pen, size * 0.08f, size * 0.30f, size * 0.84f, size * 0.46f);
    }
}