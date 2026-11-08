using System;
using System.Drawing;
using System.Runtime.InteropServices;
using InventorAddin.Host;

namespace InventorAddin.Addins.Generator;

/// <summary>
/// Add-in Generator: cửa sổ Script (.gea) chỉ có mẫu của sản phẩm này, và web worker dựng job "generator" do web đặt.
/// Mọi thứ khác (ribbon dùng chung tab Cadflow, cửa sổ, worker) do <see cref="CadflowAddIn"/> lo.
/// </summary>
[ComVisible(true)]
[Guid(ClientId)]
[ClassInterface(ClassInterfaceType.None)]
public sealed class GeneratorAddIn : ScriptProductAddIn
{
    public const string ClientId = "C9A01552-DFC8-4C51-BFB9-B18388DCB671";

    protected override string AddinId => "generator";
    protected override string Title => "Water-cooled Generator";
    protected override string Description => "Build a large water-cooled generator (stator frame, water cooler, bearing housings, rotor shaft, exciter cabinet) from a .gea script";
    protected override string[] JobProducts => new[] { "generator" };
    protected override string[] Templates => new[] { "generator.gea" };

    protected override void DrawIcon(Graphics g, int size)
    {
        using var stator = new SolidBrush(Color.FromArgb(0x8A, 0x93, 0xA0));
        using var cab = new SolidBrush(Color.FromArgb(0xEE, 0xEE, 0xEE));
        using var cooler = new SolidBrush(Color.FromArgb(0x4A, 0x7C, 0xB8));
        using var shaft = new SolidBrush(Color.FromArgb(0xE0, 0xA8, 0x4C));
        using var pen = new Pen(Color.FromArgb(0x2B, 0x3A, 0x4F), Math.Max(1f, size / 16f));
        g.FillRectangle(cooler, size * 0.30f, size * 0.62f, size * 0.42f, size * 0.26f);
        g.FillRectangle(shaft, size * 0.04f, size * 0.42f, size * 0.92f, size * 0.12f);
        g.FillRectangle(stator, size * 0.22f, size * 0.22f, size * 0.56f, size * 0.46f);
        g.DrawRectangle(pen, size * 0.22f, size * 0.22f, size * 0.56f, size * 0.46f);
        g.FillRectangle(cab, size * 0.04f, size * 0.20f, size * 0.16f, size * 0.50f);
        g.DrawRectangle(pen, size * 0.04f, size * 0.20f, size * 0.16f, size * 0.50f);
    }
}