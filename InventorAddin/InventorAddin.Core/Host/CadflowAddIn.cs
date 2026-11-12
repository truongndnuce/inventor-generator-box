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
/// Base class for every add-in of the Cadflow suite (Box, Gas turbine, Transformer, Generator...). Each add-in is one DLL, one .addin file
/// and its own GUID, so they can be turned on/off independently, but they share one "Cadflow" tab on the Part ribbon: each add-in adds its own panel
/// to that tab (whichever add-in loads first creates the tab).
/// A subclass only declares the name, icon, the product codes it accepts from the web and the window content; everything else (ribbon, window, web worker,
/// refreshing when the document changes, releasing COM) is done here. Everything runs on the UI thread of Inventor.
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

    // ---------------------------------------------------------------- declared by the subclass

    /// <summary>A short code without diacritics, unique for each add-in ("box", "gas-turbine"...): used as the suffix of the command id, the panel and the worker name.</summary>
    protected abstract string AddinId { get; }
    /// <summary>Name shown on the ribbon button and in the window title.</summary>
    protected abstract string Title { get; }
    protected abstract string Description { get; }
    /// <summary>Product codes (see the server) that this add-in builds when it receives a job from the web. Empty = accepts no jobs.</summary>
    protected abstract string[] JobProducts { get; }
    protected virtual Size WindowSize => new(440, 800);
    /// <summary>Draws the 16/32 px icon of the ribbon button.</summary>
    protected abstract void DrawIcon(Graphics g, int size);
    /// <summary>Creates the WPF content of the window (service → ViewModel → View).</summary>
    protected abstract System.Windows.UIElement CreateContent(Inv.Application app, IDialogService dialogs);
    /// <summary>Re-reads the state from the model (when the window opens or the document changes).</summary>
    protected virtual void OnRefresh() { }
    /// <summary>The first time the window is opened in the session.</summary>
    protected virtual void OnFirstShown() { }
    protected virtual void OnDispose() { }

    protected Inv.Application App => _app!;

    // ---------------------------------------------------------------- COM lifetime

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
        // Closing the window only hides it, keeping the state for the next time
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

    /// <summary>The shared Cadflow tab: whichever add-in loads first creates the tab, the later add-ins only add their own panel.</summary>
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
    /// Receives jobs placed on the web (be/Cadflow.Api) for the products of this add-in. Shared settings are in %AppData%\InventorBoxAddin\worker.json;
    /// the worker name gets the add-in as a suffix, so each add-in appears as its own worker on the server.
    /// The window handle (created on the UI thread) is used to push the part that calls the Inventor API back onto the right UI thread.
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

            _ = _window!.Handle; // force the handle to be created on the UI thread right now, even when the window is hidden
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
                catch { /* the window is closing */ }
            };
            _worker.Start();
            Log.Info($"[{AddinId}] Web worker started: {settings.WorkerName} -> {settings.ServerUrl} ({string.Join(", ", JobProducts)})");
        }
        catch (Exception ex)
        {
            Log.Error($"[{AddinId}] Could not start the web worker", ex);
        }
    }

    // ---------------------------------------------------------------- events

    /// <summary>Wrapper so the main Inventor window can be the owner of pop-ups.</summary>
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
                // Place it near the right edge of the screen that holds Inventor
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
        catch { /* already released */ }
        comObject = null;
    }
}

/// <summary>Draws a simple icon with GDI+ and converts it to IPictureDisp for Inventor.</summary>
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
