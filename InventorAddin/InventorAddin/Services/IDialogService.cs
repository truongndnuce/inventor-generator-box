namespace InventorAddin.Services;

/// <summary>Hộp thoại do View cung cấp, để ViewModel không phụ thuộc WPF window.</summary>
public interface IDialogService
{
    bool Confirm(string message);

    string? Prompt(string title, string label, string initial);
}
