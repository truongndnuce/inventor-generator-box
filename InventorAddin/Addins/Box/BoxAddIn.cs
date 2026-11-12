using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using InventorAddin.Host;
using InventorAddin.Services;
using InventorAddin.ViewModels;
using InventorAddin.Views;
using Inv = Inventor;

namespace InventorAddin.Addins.Box;

/// <summary>
/// The Box add-in: creates a box with a lid and edits the parameters on the open Part (presets, preview, Apply), and also takes "box" jobs from the web.
/// The ribbon, the window and the web worker are handled by <see cref="CadflowAddIn"/>; here we only wire Service → ViewModel → View.
/// </summary>
[ComVisible(true)]
[Guid(ClientId)]
[ClassInterface(ClassInterfaceType.None)]
public sealed class BoxAddIn : CadflowAddIn
{
    // The GUID is kept from the original Box add-in so an old installation is replaced instead of duplicated.
    public const string ClientId = "7B5E3A62-4C1D-4F0B-9A27-5D8E1C3F6A94";

    private PanelViewModel? _viewModel;

    protected override string AddinId => "box";
    protected override string Title => "Box Generator";
    protected override string Description => "Create a box with lid and edit its parameters";
    protected override string[] JobProducts => new[] { "box" };

    protected override System.Windows.UIElement CreateContent(Inv.Application app, IDialogService dialogs)
    {
        var defaultPresets = Path.Combine(Path.GetDirectoryName(typeof(BoxAddIn).Assembly.Location)!, "presets.default.json");
        _viewModel = new PanelViewModel(new InventorBoxGateway(app), new PresetStore(Log.DataDirectory, defaultPresets), dialogs);
        return new AddInPanel(_viewModel);
    }

    protected override void OnRefresh() => _viewModel?.Refresh();

    protected override void OnDispose() => _viewModel = null;

    protected override void DrawIcon(Graphics g, int size)
    {
        float m = size * 0.12f, h = size * 0.5f;
        using var body = new SolidBrush(Color.FromArgb(0x4A, 0x7C, 0xB8));
        using var lid = new SolidBrush(Color.FromArgb(0xE0, 0xA8, 0x4C));
        using var pen = new Pen(Color.FromArgb(0x2B, 0x3A, 0x4F), Math.Max(1f, size / 16f));
        g.FillRectangle(body, m, size * 0.42f, size - 2 * m, h * 0.9f);
        g.DrawRectangle(pen, m, size * 0.42f, size - 2 * m, h * 0.9f);
        g.FillRectangle(lid, m * 0.6f, size * 0.22f, size - 1.2f * m, size * 0.2f);
        g.DrawRectangle(pen, m * 0.6f, size * 0.22f, size - 1.2f * m, size * 0.2f);
    }
}
