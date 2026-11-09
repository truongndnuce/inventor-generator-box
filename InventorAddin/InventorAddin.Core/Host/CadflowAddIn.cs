using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Windows.Forms.Integration;
using InventorAddin.Services;
using InventorAddin.Web;
using Inv = Inventor;

namespace InventorAddin.Host;

/// <summary>
/// Lớp nền cho mọi add-in của bộ Cadflow (Box, Gas turbine, Transformer, Generator...). Mỗi add-in là một DLL, một file .addin
/// và một GUID riêng nên bật/tắt độc lập, nhưng chung một tab "Cadflow" trên ribbon của Part: mỗi add-in thêm một panel của mình
/// vào tab đó (add-in nào nạp trước thì tạo tab).
/// Lớp con chỉ khai báo tên, icon, mã sản phẩm nhận từ web và nội dung cửa sổ; phần còn lại (ribbon, cửa sổ, web worker,
/// làm mới khi đổi document, giải phóng COM) làm ở đây. Mọi thứ chạy trên UI thread của Inventor.
/// </summary>
public abstract class CadflowAddIn : Inv.ApplicationAddInServer
{
    public const string TabId = "CF_Tab";
    public const string TabName = "Cadflow";

    private Inv.Application? _app;
    private Inv.ButtonDefinition? _button;
    private Inv.ApplicationEvents? _appEvents;
    private Form? _window;
    private JobWorker? _worker;
    private bool _closing;
    private bool _shownOnce;

    // ---------------------------------------------------------------- lớp con khai báo

    /// <summary>Mã ngắn không dấu, duy nhất cho mỗi add-in ("box", "gas-turbine"...): dùng làm hậu tố id lệnh, panel và tên worker.</summary>
    protected abstract string AddinId { get; }
    /// <summary>Tên hiện trên nút ribbon và tiêu đề cửa sổ.</summary>
    protected abstract string Title { get; }
    protected abstract string Description { get; }
    /// <summary>Mã sản phẩm (xem server) mà add-in này dựng khi nhận job từ web. Rỗng = không nhận job.</summary>
    protected abstract string[] JobProducts { get; }
    protected virtual Size WindowSize => new(440, 800);
    /// <summary>Vẽ icon 16/32 px của nút ribbon.</summary>
    protected abstract void DrawIcon(Graphics g, int size);
    /// <summary>Tạo nội dung WPF của cửa sổ (service → ViewModel → View).</summary>
    protected abstract System.Windows.UIElement CreateContent(Inv.Application app, IDialogService dialogs);
    /// <summary>Đọc lại trạng thái từ model (khi mở cửa sổ hoặc đổi document).</summary>
    protected virtual void OnRefresh() { }
    /// <summary>Lần đầu cửa sổ được mở trong phiên làm việc.</summary>
    protected virtual void OnFirstShown() { }
    protected virtual void OnDispose() { }

    protected Inv.Application App => _app!;

    // ---------------------------------------------------------------- vòng đời COM

    public object? Automation => null;

    public void ExecuteCommand(int commandId) { }

    private string ClientIdBraced => "{" + GetType().GUID.ToString().ToUpperInvariant() + "}";

    public void Activate(Inv.ApplicationAddInSite addInSiteObject, bool firstTime)
    {
        try
        {
            Log.Info($"[{AddinId}] Activate (firstTime={firstTime})");
            _app = addInSiteObject.Application;
            CreateUi();
        }
        catch (Exception ex)
        {
            Log.Error($"[{AddinId}] Activate failed", ex);
        }
    }

    public void Deactivate()
    {
        try
        {
            Log.Info($"[{AddinId}] Deactivate");
            if (_button != null) _button.OnExecute -= OnButtonExecute;
            if (_appEvents != null) _appEvents.OnActivateDocument -= OnActivateDocument;
            _worker?.Dispose();
            _worker = null;
            OnDispose();
            _closing = true;
            _window?.Close();
            _window?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error($"[{AddinId}] Deactivate failed", ex);
        }
        finally
        {
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
        var host = new ElementHost { Dock = DockStyle.Fill, Child = CreateContent(app, new WpfDialogService()) };
        _window = new Form
        {
            Text = Title,
            FormBorderStyle = FormBorderStyle.SizableToolWindow,
            StartPosition = FormStartPosition.Manual,
            ShowInTaskbar = false,
            ClientSize = WindowSize,
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

        _button = app.CommandManager.ControlDefinitions.AddButtonDefinition(
            Title, "CF_" + AddinId + "_ShowPanelCmd", Inv.CommandTypesEnum.kNonShapeEditCmdType, ClientIdBraced,
            Description, "Open the " + Title + " window",
            IconFactory.Create(16, DrawIcon), IconFactory.Create(32, DrawIcon), Inv.ButtonDisplayEnum.kDisplayTextInLearningMode);
        _button.OnExecute += OnButtonExecute;
        AddRibbon(app.UserInterfaceManager);

        try
        {
            _appEvents = app.ApplicationEvents;
            _appEvents.OnActivateDocument += OnActivateDocument;
        }
        catch (Exception ex)
        {
            Log.Warn($"[{AddinId}] Could not subscribe to document activation event: " + ex.Message);
        }

        StartWebWorker(app);
    }

    /// <summary>Tab Cadflow dùng chung: add-in nào nạp trước tạo tab, các add-in sau chỉ thêm panel của mình.</summary>
    private void AddRibbon(Inv.UserInterfaceManager ui)
    {
        try
        {
            var ribbon = ui.Ribbons["Part"];
            Inv.RibbonTab tab;
            try { tab = ribbon.RibbonTabs[TabId]; }
            catch { tab = ribbon.RibbonTabs.Add(TabName, TabId, ClientIdBraced); }

            var panelId = "CF_Panel_" + AddinId;
            Inv.RibbonPanel panel;
            try { panel = tab.RibbonPanels[panelId]; }
            catch { panel = tab.RibbonPanels.Add(Title, panelId, ClientIdBraced); }

            try { panel.CommandControls.AddButton(_button!, true); }
            catch (Exception ex) { Log.Warn($"[{AddinId}] Ribbon button already exists or could not be added: " + ex.Message); }
        }
        catch (Exception ex)
        {
            Log.Error($"[{AddinId}] Creating ribbon failed", ex);
        }
    }

    // ---------------------------------------------------------------- web worker

    /// <summary>
    /// Nhận job do web đặt (be/Cadflow.Api) cho các sản phẩm của add-in này. Cấu hình chung ở %AppData%\InventorBoxAddin\worker.json;
    /// tên worker thêm hậu tố add-in nên mỗi add-in hiện là một worker riêng trên server.
    /// Handle của cửa sổ (tạo trên UI thread) dùng để đẩy phần gọi Inventor API về đúng UI thread.
    /// </summary>
    private void StartWebWorker(Inv.Application app)
    {
        try
        {
            if (JobProducts.Length == 0) return;
            var settings = WorkerSettings.Load();
            if (!settings.Enabled)
            {
                Log.Info($"[{AddinId}] Web worker disabled in worker.json");
                return;
            }
            settings = settings.ForAddin(AddinId);

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
            }, JobProducts);
            _worker.StatusChanged += status =>
            {
                try
                {
                    if (!ui.IsDisposed && ui.IsHandleCreated)
                        ui.BeginInvoke(new Action(() => ui.Text = Title + " - web: " + status));
                }
                catch { /* cửa sổ đang đóng */ }
            };
            _worker.Start();
            Log.Info($"[{AddinId}] Web worker started: {settings.WorkerName} -> {settings.ServerUrl} ({string.Join(", ", JobProducts)})");
        }
        catch (Exception ex)
        {
            Log.Error($"[{AddinId}] Could not start the web worker", ex);
        }
    }

    // ---------------------------------------------------------------- sự kiện

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
            if (!_shownOnce)
            {
                _shownOnce = true;
                OnFirstShown();
            }
            OnRefresh();

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
            Log.Error($"[{AddinId}] Opening window failed", ex);
        }
    }

    private void OnActivateDocument(Inv._Document documentObject, Inv.EventTimingEnum beforeOrAfter,
        Inv.NameValueMap context, out Inv.HandlingCodeEnum handlingCode)
    {
        handlingCode = Inv.HandlingCodeEnum.kEventNotHandled;
        if (beforeOrAfter != Inv.EventTimingEnum.kAfter) return;
        try { OnRefresh(); }
        catch (Exception ex) { Log.Error($"[{AddinId}] Refresh on document change failed", ex); }
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

/// <summary>Vẽ icon đơn giản bằng GDI+ và đổi sang IPictureDisp cho Inventor.</summary>
internal sealed class IconFactory : AxHost
{
    private IconFactory() : base(string.Empty) { }

    public static object? Create(int size, Action<Graphics, int> draw)
    {
        try
        {
            using var bmp = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                draw(g, size);
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
