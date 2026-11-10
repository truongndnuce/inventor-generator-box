using System;
using System.Drawing;
using System.Runtime.InteropServices;
using InventorAddin.Host;

namespace InventorAddin.Addins.Transformer;

/// <summary>
/// The Transformer add-in: the Script (.gea) window has only the templates of this product, and the web worker builds the "transformer" jobs placed on the web.
/// Everything else (the ribbon sharing the Cadflow tab, the window, the worker) is handled by <see cref="CadflowAddIn"/>.
/// </summary>
[ComVisible(true)]
[Guid(ClientId)]
[ClassInterface(ClassInterfaceType.None)]
public sealed class TransformerAddIn : ScriptProductAddIn
{
    public const string ClientId = "09177B53-BF27-477D-9A23-1B6C76E97E5F";

    protected override string AddinId => "transformer";
    protected override string Title => "Transformer";
    protected override string Description => "Build a power transformer (tank, radiators, conservator, bushings) from .gea scripts";
    protected override string[] JobProducts => new[] { "transformer", "imported" };
    protected override string[] Templates => new[] { "transformer.gea", "transformer-hv.gea", "tank-level1.gea" };

    protected override void DrawIcon(Graphics g, int size)
    {
        using var tank = new SolidBrush(Color.FromArgb(0x4A, 0x7C, 0xB8));
        using var fin = new SolidBrush(Color.FromArgb(0xB6, 0xBC, 0xC6));
        using var bush = new SolidBrush(Color.FromArgb(0xE0, 0xA8, 0x4C));
        using var pen = new Pen(Color.FromArgb(0x2B, 0x3A, 0x4F), Math.Max(1f, size / 16f));
        g.FillRectangle(fin, size * 0.08f, size * 0.45f, size * 0.16f, size * 0.38f);
        g.FillRectangle(fin, size * 0.76f, size * 0.45f, size * 0.16f, size * 0.38f);
        g.FillRectangle(tank, size * 0.22f, size * 0.38f, size * 0.56f, size * 0.46f);
        g.DrawRectangle(pen, size * 0.22f, size * 0.38f, size * 0.56f, size * 0.46f);
        for (int i = 0; i < 3; i++) g.FillRectangle(bush, size * (0.30f + i * 0.17f), size * 0.15f, size * 0.09f, size * 0.23f);
    }
}