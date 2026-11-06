namespace InventorAddin.Services;

/// <summary>Hộp thoại do View cung cấp, để ViewModel không phụ thuộc WPF window.</summary>
public interface IDialogService
{
    bool Confirm(string message);

    string? Prompt(string title, string label, string initial);

    /// <summary>Chọn file để mở; null nếu hủy.</summary>
    string? OpenFile(string title, string filter, string? initialDirectory);

    /// <summary>Chọn nơi lưu file; null nếu hủy.</summary>
    string? SaveFile(string title, string filter, string? initialDirectory, string? fileName);
}
