using System;
using System.Linq;
using InventorAddin.Services;
using InventorAddin.Services.Gea;
using InventorAddin.ViewModels;
using InventorAddin.Views;
using Inv = Inventor;

namespace InventorAddin.Host;

/// <summary>
/// Add-in của một sản phẩm dựng bằng script .gea (Transformer, Gas turbine, Generator): cửa sổ là bảng Script (.gea) chỉ liệt kê
/// các mẫu của sản phẩm đó, mẫu đầu tiên tự mở lần đầu bấm nút. Cùng các script này, web worker của add-in dựng job do web đặt.
/// </summary>
public abstract class ScriptProductAddIn : CadflowAddIn
{
    private ScriptViewModel? _viewModel;

    /// <summary>Tên file các mẫu .gea của sản phẩm (trong thư mục Scripts); mẫu đầu tiên là mẫu mặc định.</summary>
    protected abstract string[] Templates { get; }

    protected override System.Windows.UIElement CreateContent(Inv.Application app, IDialogService dialogs)
    {
        _viewModel = new ScriptViewModel(new GeaDocumentService(app), dialogs,
            name => Templates.Contains(name, StringComparer.OrdinalIgnoreCase));
        // Mẫu mặc định lên đầu danh sách để OpenDefaultTemplateIfEmpty mở đúng mẫu của sản phẩm
        var first = _viewModel.Templates.FirstOrDefault(t => string.Equals(t.Name, Templates[0], StringComparison.OrdinalIgnoreCase));
        if (first != null) _viewModel.SelectedTemplate = first;

        var panel = new ScriptPanel { DataContext = _viewModel };
        panel.IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue) _viewModel.Refresh(); // đọc lại từ model phòng khi người dùng sửa tay
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
