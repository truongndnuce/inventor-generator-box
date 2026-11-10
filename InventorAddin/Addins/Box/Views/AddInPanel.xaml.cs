using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using InventorAddin.ViewModels;

namespace InventorAddin.Views;

/// <summary>A pure View: it only binds to <see cref="PanelViewModel"/> (Box add-in).
/// The code-behind only turns focus events into properties of the ViewModel.</summary>
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
            // Every time the UI opens, re-read the values from the model, in case the user edited a Parameter by hand
            if (!(bool)e.NewValue) return;
            viewModel.Refresh();
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
