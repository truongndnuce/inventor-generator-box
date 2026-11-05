using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using InventorAddin.ViewModels;

namespace InventorAddin.Views;

/// <summary>View thuần: chỉ binding vào <see cref="PanelViewModel"/>. Code-behind chỉ chuyển sự kiện focus thành thuộc tính của ViewModel.</summary>
public partial class AddInPanel : UserControl
{
    public AddInPanel(PanelViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        AddHandler(Keyboard.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(OnGotFocus), true);
        AddHandler(Keyboard.LostKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(OnLostFocus), true);
        IsVisibleChanged += (_, e) =>
        {
            // Mỗi lần mở UI đọc lại giá trị từ model, phòng khi người dùng sửa Parameter bằng tay
            if ((bool)e.NewValue) viewModel.Refresh();
        };
    }

    private PanelViewModel Vm => (PanelViewModel)DataContext;

    private void OnGotFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement { DataContext: ParamViewModel p })
            Vm.FocusedParam = p.Name;
    }

    private void OnLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement { DataContext: ParamViewModel })
            Vm.FocusedParam = null;
    }
}
