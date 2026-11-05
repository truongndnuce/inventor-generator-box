using System.Windows;
using System.Windows.Controls;

namespace InventorAddin.Services;

/// <summary>Cài đặt <see cref="IDialogService"/> bằng MessageBox và một cửa sổ nhập tên nhỏ dựng bằng code.</summary>
public sealed class WpfDialogService : IDialogService
{
    private const string Caption = "Box Generator";

    public bool Confirm(string message) =>
        MessageBox.Show(message, Caption, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    public string? Prompt(string title, string label, string initial)
    {
        var box = new TextBox { Text = initial, Margin = new Thickness(0, 6, 0, 12), MinWidth = 240 };
        var ok = new Button { Content = "OK", IsDefault = true, Width = 70, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Width = 70 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var root = new StackPanel { Margin = new Thickness(14) };
        root.Children.Add(new TextBlock { Text = label });
        root.Children.Add(box);
        root.Children.Add(buttons);

        var window = new Window
        {
            Title = title,
            Content = root,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Topmost = true,
        };
        ok.Click += (_, _) => window.DialogResult = true;
        window.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return window.ShowDialog() == true ? box.Text : null;
    }
}
