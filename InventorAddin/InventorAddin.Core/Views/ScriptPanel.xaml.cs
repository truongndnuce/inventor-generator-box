using System.Windows.Controls;
using System.Windows.Input;
using InventorAddin.ViewModels;

namespace InventorAddin.Views;

/// <summary>View của tab Script (.gea): chỉ binding vào <see cref="ScriptViewModel"/>; code-behind chỉ nhảy tới dòng lỗi.</summary>
public partial class ScriptPanel : UserControl
{
    public ScriptPanel()
    {
        InitializeComponent();
    }

    private void OnProblemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Problems.SelectedItem is not ScriptMessage { Line: > 0 } m) return;
        int index = m.Line - 1;
        if (index >= Editor.LineCount) return;
        int start = Editor.GetCharacterIndexFromLineIndex(index);
        Editor.Focus();
        Editor.Select(start, Editor.GetLineLength(index));
        Editor.ScrollToLine(index);
    }
}
