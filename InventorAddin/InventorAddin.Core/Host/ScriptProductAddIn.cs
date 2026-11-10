using System;
using System.Linq;
using InventorAddin.Services;
using InventorAddin.Services.Gea;
using InventorAddin.ViewModels;
using InventorAddin.Views;
using Inv = Inventor;

namespace InventorAddin.Host;

/// <summary>
/// The add-in of a product built by a .gea script (Transformer, Gas turbine, Generator): the window is the Script (.gea) panel that lists only
/// the templates of that product, and the first template opens by itself the first time the button is pressed. With these same scripts, the add-in's web worker builds jobs placed on the web.
/// </summary>
public abstract class ScriptProductAddIn : CadflowAddIn
{
    private ScriptViewModel? _viewModel;

    /// <summary>File names of the .gea templates of the product (in the Scripts folder); the first template is the default.</summary>
    protected abstract string[] Templates { get; }

    protected override System.Windows.UIElement CreateContent(Inv.Application app, IDialogService dialogs)
    {
        _viewModel = new ScriptViewModel(new GeaDocumentService(app), dialogs,
            name => Templates.Contains(name, StringComparer.OrdinalIgnoreCase));
        // The default template goes first in the list so OpenDefaultTemplateIfEmpty opens the right template of the product
        var first = _viewModel.Templates.FirstOrDefault(t => string.Equals(t.Name, Templates[0], StringComparison.OrdinalIgnoreCase));
        if (first != null) _viewModel.SelectedTemplate = first;

        var panel = new ScriptPanel { DataContext = _viewModel };
        panel.IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue) _viewModel.Refresh(); // re-read from the model in case the user edited by hand
        };
        return panel;
    }

    protected override void OnFirstShown() => _viewModel?.OpenDefaultTemplateIfEmpty();

    protected override void OnRefresh() => _viewModel?.Refresh();

    protected override void OnDispose()
    {
        _viewModel?.Dispose();
        _viewModel = null;
    }
}
