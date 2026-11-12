using System;
using System.Drawing;
using System.Runtime.InteropServices;
using InventorAddin.Host;

namespace InventorAddin.Addins.CircuitBreaker;

/// <summary>
/// The Circuit Breaker add-in: the Script (.gea) window has only the templates of this product, and the web worker builds the "circuit-breaker" jobs placed on the web.
/// Everything else (the ribbon sharing the Cadflow tab, the window, the worker) is handled by <see cref="CadflowAddIn"/>.
/// </summary>
[ComVisible(true)]
[Guid(ClientId)]
[ClassInterface(ClassInterfaceType.None)]
public sealed class CircuitBreakerAddIn : ScriptProductAddIn
{
    public const string ClientId = "6855250B-C6A3-4871-A9F7-C2EA814009B7";

    protected override string AddinId => "circuit-breaker";
    protected override string Title => "Circuit Breaker";
    protected override string Description => "Build a high-voltage circuit breaker (foundation, lattice stand, control cabinet, two interrupter columns) from a .gea script";
    protected override string[] JobProducts => new[] { "circuit-breaker" };
    protected override string[] Templates => new[] { "circuit-breaker.gea" };

    protected override void DrawIcon(Graphics g, int size)
    {
        using var column = new SolidBrush(Color.FromArgb(0x22, 0x26, 0x2B));
        using var collar = new SolidBrush(Color.FromArgb(0xC0, 0xC4, 0xCA));
        using var stand = new SolidBrush(Color.FromArgb(0x8A, 0x93, 0xA0));
        using var pad = new SolidBrush(Color.FromArgb(0x70, 0x70, 0x70));
        using var cab = new SolidBrush(Color.FromArgb(0xEE, 0xEE, 0xEE));
        using var pen = new Pen(Color.FromArgb(0x2B, 0x3A, 0x4F), Math.Max(1f, size / 16f));
        g.FillRectangle(pad, size * 0.18f, size * 0.86f, size * 0.64f, size * 0.10f);
        g.FillRectangle(stand, size * 0.40f, size * 0.62f, size * 0.20f, size * 0.24f);
        g.FillRectangle(cab, size * 0.14f, size * 0.66f, size * 0.24f, size * 0.16f);
        g.DrawRectangle(pen, size * 0.14f, size * 0.66f, size * 0.24f, size * 0.16f);
        g.FillRectangle(column, size * 0.42f, size * 0.34f, size * 0.16f, size * 0.28f);
        g.FillRectangle(collar, size * 0.38f, size * 0.30f, size * 0.24f, size * 0.05f);
        g.FillRectangle(column, size * 0.42f, size * 0.04f, size * 0.16f, size * 0.26f);
    }
}
