using System.Windows.Controls;
using System.Windows.Input;
using InventorAddin.ViewModels;

namespace InventorAddin.Views;

/// <summary>View of the Script (.gea) tab: it only binds to <see cref="ScriptViewModel"/>; the code-behind only jumps to the error line.</summary>
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
