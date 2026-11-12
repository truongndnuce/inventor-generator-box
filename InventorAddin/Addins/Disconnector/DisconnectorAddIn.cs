using System;
using System.Drawing;
using System.Runtime.InteropServices;
using InventorAddin.Host;

namespace InventorAddin.Addins.Disconnector;

/// <summary>
/// The Disconnector add-in: the Script (.gea) window has only the templates of this product, and the web worker builds the "disconnector" jobs placed on the web.
/// Everything else (the ribbon sharing the Cadflow tab, the window, the worker) is handled by <see cref="CadflowAddIn"/>.
/// </summary>
[ComVisible(true)]
[Guid(ClientId)]
[ClassInterface(ClassInterfaceType.None)]
public sealed class DisconnectorAddIn : ScriptProductAddIn
{
    public const string ClientId = "68938A0C-1FB5-4BD7-8131-FDE6676F16A7";

    protected override string AddinId => "disconnector";
    protected override string Title => "Disconnector";
    protected override string Description => "Build a high-voltage double-column disconnector (posts, frame, insulator columns, blade arms, control cabinet) from a .gea script";
    protected override string[] JobProducts => new[] { "disconnector" };
    protected override string[] Templates => new[] { "disconnector.gea" };

    protected override void DrawIcon(Graphics g, int size)
    {
        using var insulator = new SolidBrush(Color.FromArgb(0xD8, 0xA8, 0x30));
        using var arm = new SolidBrush(Color.FromArgb(0xC0, 0xC4, 0xCA));
        using var post = new SolidBrush(Color.FromArgb(0x70, 0x78, 0x84));
        using var pen = new Pen(Color.FromArgb(0x2B, 0x3A, 0x4F), Math.Max(1f, size / 16f));
        g.FillRectangle(post, size * 0.18f, size * 0.62f, size * 0.12f, size * 0.30f);
        g.FillRectangle(post, size * 0.70f, size * 0.62f, size * 0.12f, size * 0.30f);
        g.FillRectangle(arm, size * 0.12f, size * 0.56f, size * 0.76f, size * 0.07f);
        g.FillRectangle(insulator, size * 0.18f, size * 0.20f, size * 0.12f, size * 0.36f);
        g.FillRectangle(insulator, size * 0.70f, size * 0.20f, size * 0.12f, size * 0.36f);
        g.DrawRectangle(pen, size * 0.18f, size * 0.20f, size * 0.12f, size * 0.36f);
        g.DrawRectangle(pen, size * 0.70f, size * 0.20f, size * 0.12f, size * 0.36f);
        g.FillRectangle(arm, size * 0.10f, size * 0.12f, size * 0.80f, size * 0.08f);
    }
}
