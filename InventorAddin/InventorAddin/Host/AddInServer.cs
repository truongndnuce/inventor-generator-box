using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Windows.Forms.Integration;
using InventorAddin.Services;
using InventorAddin.Services.Gea;
using InventorAddin.ViewModels;
using InventorAddin.Views;
using InventorAddin.Web;
using Inv = Inventor;

namespace InventorAddin.Host;

/// <summary>
/// Điểm vào của add-in (regfree, nạp qua file .addin) và là "composition root" của MVVM:
/// tạo Service (gateway Inventor, preset, dialog) → ViewModel → View rồi gắn View vào dockable window.
/// Mọi thứ chạy trên UI thread của Inventor.
/// </summary>
[ComVisible(true)]
[Guid(ClientId)]
[ClassInterface(ClassInterfaceType.None)]
public sealed class AddInServer : Inv.ApplicationAddInServer
{
    public const string ClientId = "7B5E3A62-4C1D-4F0B-9A27-5D8E1C3F6A94";
    private static readonly string ClientIdBraced = "{" + ClientId + "}";

    private const string TabId = "BX_Tab";
    private const string PanelId = "BX_Panel";
    private const string ButtonId = "BX_ShowPanelCmd";

    private Inv.Application? _app;
    private Inv.ButtonDefinition? _button;
    private Form? _window;
    private PanelViewModel? _viewModel;
    private ScriptViewModel? _scriptViewModel;
    private Inv.ApplicationEvents? _appEvents;
    private JobWorker? _worker;
    private bool _closing;

    public object? Automation => null;

    public void ExecuteCommand(int commandId) { }

    public void Activate(Inv.ApplicationAddInSite addInSiteObject, bool firstTime)
    {
        try
        {
            Log.Info($"Activate (firstTime={firstTime})");
            _app = addInSiteObject.Application;
            CreateUi();
        }
        catch (Exception ex)
        {
            Log.Error("Activate failed", ex);
        }
    }

    public void Deactivate()
    {
        try
        {
            Log.Info("Deactivate");
            if (_button != null) _button.OnExecute -= OnButtonExecute;
            if (_appEvents != null) _appEvents.OnActivateDocument -= OnActivateDocument;
            _worker?.Dispose();
            _worker = null;
            _scriptViewModel?.Dispose();
            _closing = true;
            _window?.Close();
            _window?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error("Deactivate failed", ex);
        }
        finally
        {
            _viewModel = null;
            _scriptViewModel = null;
            _window = null;
            Release(ref _button);
            Release(ref _appEvents);
            Release(ref _app);
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }

    // ---------------------------------------------------------------- UI

    private void CreateUi()
    {
        var app = _app!;
        var ui = app.UserInterfaceManager;

        // Service -> ViewModel -> View. View được đặt trong một cửa sổ pop-up (modeless) thuộc cửa sổ chính của Inventor.
        var defaultPresets = Path.Combine(Path.GetDirectoryName(typeof(AddInServer).Assembly.Location)!, "presets.default.json");
        var dialogs = new WpfDialogService();
        _viewModel = new PanelViewModel(new InventorBoxGateway(app), new PresetStore(Log.DataDirectory, defaultPresets), dialogs);
        _scriptViewModel = new ScriptViewModel(new GeaDocumentService(app), dialogs);
        var host = new ElementHost { Dock = DockStyle.Fill, Child = new AddInPanel(_viewModel, _scriptViewModel) };
        _window = new Form
        {
            Text = "Box Generator",
            FormBorderStyle = FormBorderStyle.SizableToolWindow,
            StartPosition = FormStartPosition.Manual,
            ShowInTaskbar = false,
            ClientSize = new Size(440, 800),
            MinimumSize = new Size(340, 520),
        };
        _window.Controls.Add(host);
        // Đóng cửa sổ chỉ ẩn đi, giữ trạng thái cho lần mở sau
        _window.FormClosing += (_, e) =>
        {
            if (_closing) return;
            e.Cancel = true;
            _window.Hide();
        };

        // Nút trên ribbon
        _button = app.CommandManager.ControlDefinitions.AddButtonDefinition(
            "Box Generator", ButtonId, Inv.CommandTypesEnum.kNonShapeEditCmdType, ClientIdBraced,
            "Create a box with lid and edit its parameters", "Open the Box Generator window",
            IconFactory.Create(16), IconFactory.Create(32), Inv.ButtonDisplayEnum.kDisplayTextInLearningMode);
        _button.OnExecute += OnButtonExecute;
        AddRibbon(ui);

        // Làm mới khi đổi document
        try
        {
            _appEvents = app.ApplicationEvents;
            _appEvents.OnActivateDocument += OnActivateDocument;
        }
        catch (Exception ex)
        {
            Log.Warn("Could not subscribe to document activation event: " + ex.Message);
        }

        StartWebWorker(app);
    }

    // ---------------------------------------------------------------- web worker

    /// <summary>
    /// Nhận job do web đặt (be/Cadflow.Api). Cấu hình ở %AppData%\InventorBoxAddin\worker.json.
    /// Handle của cửa sổ (tạo trên UI thread) dùng để đẩy phần gọi Inventor API về đúng UI thread.
    /// </summary>
    private void StartWebWorker(Inv.Application app)
    {
        try
        {
            var settings = WorkerSettings.Load();
            if (!settings.Enabled)
            {
                Log.Info("Web worker disabled in worker.json");
                return;
            }

            _ = _window!.Handle; // ép tạo handle ngay trên UI thread, kể cả khi cửa sổ đang ẩn
            var ui = _window;
            _worker = new JobWorker(app, settings, work =>
            {
                var tcs = new TaskCompletionSource<object?>();
                ui.BeginInvoke(new Action(() =>
                {
                    try { tcs.SetResult(work()); }
                    catch (Exception ex) { tcs.SetException(ex); }
                }));
                return tcs.Task;
            });
            _worker.StatusChanged += status =>
            {
                try
                {
                    if (!ui.IsDisposed && ui.IsHandleCreated)
                        ui.BeginInvoke(new Action(() => ui.Text = "Box Generator - web: " + status));
                }
                catch { /* cửa sổ đang đóng */ }
            };
            _worker.Start();
            Log.Info($"Web worker started: {settings.WorkerName} -> {settings.ServerUrl}");
        }
        catch (Exception ex)
        {
            Log.Error("Could not start the web worker", ex);
        }
    }

    private void AddRibbon(Inv.UserInterfaceManager ui)
    {
        try
        {
            var ribbon = ui.Ribbons["Part"];
            Inv.RibbonTab tab;
            try { tab = ribbon.RibbonTabs[TabId]; }
            catch { tab = ribbon.RibbonTabs.Add("Box Generator", TabId, ClientIdBraced); }

            Inv.RibbonPanel panel;
            try { panel = tab.RibbonPanels[PanelId]; }
            catch { panel = tab.RibbonPanels.Add("Box", PanelId, ClientIdBraced); }

            try { panel.CommandControls.AddButton(_button!, true); }
            catch (Exception ex) { Log.Warn("Ribbon button already exists or could not be added: " + ex.Message); }
        }
        catch (Exception ex)
        {
            Log.Error("Creating ribbon failed", ex);
        }
    }

    /// <summary>Wrapper để dùng cửa sổ chính của Inventor làm owner của pop-up.</summary>
    private sealed class MainWindow : IWin32Window
    {
        public MainWindow(IntPtr handle) => Handle = handle;
        public IntPtr Handle { get; }
    }

    private void OnButtonExecute(Inv.NameValueMap context)
    {
        try
        {
            if (_window == null || _app == null) return;
            _viewModel?.Refresh();
            _scriptViewModel?.Refresh();

            if (!_window.Visible)
            {
                var owner = new MainWindow((IntPtr)_app.MainFrameHWND);
                // Đặt gần mép phải của màn hình chứa Inventor
                var area = Screen.FromHandle(owner.Handle).WorkingArea;
                _window.Location = new Point(Math.Max(area.Left, area.Right - _window.Width - 40), area.Top + 60);
                _window.Show(owner);
            }
            _window.Activate();
        }
        catch (Exception ex)
        {
            Log.Error("Opening window failed", ex);
        }
    }

    private void OnActivateDocument(Inv._Document documentObject, Inv.EventTimingEnum beforeOrAfter,
        Inv.NameValueMap context, out Inv.HandlingCodeEnum handlingCode)
    {
        handlingCode = Inv.HandlingCodeEnum.kEventNotHandled;
        if (beforeOrAfter != Inv.EventTimingEnum.kAfter) return;
        try
        {
            _viewModel?.Refresh();
            _scriptViewModel?.Refresh();
        }
        catch (Exception ex) { Log.Error("Refresh on document change failed", ex); }
    }

    private static void Release<T>(ref T? comObject) where T : class
    {
        try
        {
            if (comObject != null && Marshal.IsComObject(comObject)) Marshal.ReleaseComObject(comObject);
        }
        catch { /* đã được giải phóng */ }
        comObject = null;
    }
}

/// <summary>Vẽ icon đơn giản (hình hộp) và đổi sang IPictureDisp cho Inventor.</summary>
internal sealed class IconFactory : AxHost
{
    private IconFactory() : base(string.Empty) { }

    public static object? Create(int size)
    {
        try
        {
            using var bmp = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                float m = size * 0.12f, h = size * 0.5f;
                using var body = new SolidBrush(Color.FromArgb(0x4A, 0x7C, 0xB8));
                using var lid = new SolidBrush(Color.FromArgb(0xE0, 0xA8, 0x4C));
                using var pen = new Pen(Color.FromArgb(0x2B, 0x3A, 0x4F), Math.Max(1f, size / 16f));
                g.FillRectangle(body, m, size * 0.42f, size - 2 * m, h * 0.9f);
                g.DrawRectangle(pen, m, size * 0.42f, size - 2 * m, h * 0.9f);
                g.FillRectangle(lid, m * 0.6f, size * 0.22f, size - 1.2f * m, size * 0.2f);
                g.DrawRectangle(pen, m * 0.6f, size * 0.22f, size - 1.2f * m, size * 0.2f);
            }
            return GetIPictureDispFromPicture(bmp);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not create icon: " + ex.Message);
            return null;
        }
    }
}
