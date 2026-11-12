namespace InventorAddin.Services;

/// <summary>Dialogs provided by the View, so the ViewModel does not depend on a WPF window.</summary>
public interface IDialogService
{
    bool Confirm(string message);

    string? Prompt(string title, string label, string initial);

    /// <summary>Chooses a file to open; null if cancelled.</summary>
    string? OpenFile(string title, string filter, string? initialDirectory);

    /// <summary>Chooses where to save a file; null if cancelled.</summary>
    string? SaveFile(string title, string filter, string? initialDirectory, string? fileName);
}
