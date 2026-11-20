using EnbNext.Presentation;
using EnbNext.Assets;
using EnbNext.Rendering;
using EnbNext.Net7;

namespace EnbNext.Engine;

internal sealed partial class EnbOptionsUi
{
    private IEnbOptionsBrowser? _manualBrowser;
    private IEnbOptionsBrowserSnapshot? _manualSnapshot;
    private bool _manualSnapshotAttempted;
    private EnbBrowserPanel? _manualPanel;
    private ulong _handoff, _manualHandoff;
    public bool ManualOpen => _manualBrowser is not null;
    public Func<uint, ulong, string?>? ManualBrowserQuery { get; set; }
    public Func<bool>? OtherBrowserOpen { get; set; }
    public event Action? ManualOpened;
    public event Action? ManualEscapeRequested;

    public bool OpenManual() => OpenInformationBrowser(6, "Online Manual");

    private string _browserTitle = "Online Manual";
    private int _browserAdmissionUpdates;
    private int _browserReturnPage;
    /// <summary>Why the page is not shown; a page that did not load is not a client fault, so not an Options error.</summary>
    private string? _browserFailure;
    private bool _browserRetried;
    public bool OpenInformationBrowser(int action, string title)
    {
        if (!_interactive || _assets is null || ManualOpen || OtherBrowserOpen?.Invoke() == true ||
            _owner is not uint character || character == 0 || session() != character) return false;
        string? query = ManualBrowserQuery?.Invoke(character, _handoff);
        if (query is null) return false;
        if (_captainLog.IsOpen && !SaveLog()) return false;
        try
        {
            if (_manualPanel is null)
            {
                _manualPanel=new(EnbRenderSession.Of(_assets).BrowserPanel,_panelResources!);
            }
            var address = EnbRetailInformationAddress.Load(_assets.Paths.ClientExecutablePath, action);
            // Browser runtime data is shared with the existing embedded news host;
            // the game has no manual preference or separately stored URL.
            string profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "EnbNext", "Browser", "Station", character.ToString("X8"));
            var target = Net7StationBrowser.AppendQuery(address, query);
            var browser = createBrowser(target,profile,query);
            int returnPage = _page == 11 ? 11 : 0;
            // 005D38F0 retains Help's actor while its browser child is open.
            CloseAllCore(returnPage==11);
            _browserReturnPage = returnPage;
            _browserTitle = title; _browserAdmissionUpdates = action == 6 ? 0 : 5;
            _manualBrowser = browser; _manualHandoff = _handoff; _page = 4; Error = null; _browserFailure = null; _browserRetried = false;
            browser.Failed += _ =>
            {
                if (_manualBrowser != browser) return;
                // The site or the network did not deliver the page: it is loaded once more before the window says so.
                // That is shown in the window only; it is not recorded as a session issue.
                if (!_browserRetried) { _browserRetried = true; browser.Reload(); return; }
                browser.IsVisible = false; _browserFailure = "The " + _browserTitle + " page could not be loaded. Close it and try again."; Publish();
            };
            browser.PageLoaded += () => { if (_manualBrowser == browser) { Error = null; _browserFailure = null; browser.IsVisible = _manualSnapshot is null; Publish(); } };
            browser.CloseRequested += () =>
            {
                if (_manualBrowser != browser) return;
                FinishInformationBrowser();
            };
            browser.EscapeRequested += () =>
            {
                if (_manualBrowser != browser || QuitOpen) return;
                ManualEscapeRequested?.Invoke();
                OpenQuit();
            };
            if (_browserAdmissionUpdates == 0) browser.Attach();
            ManualOpened?.Invoke(); Opened?.Invoke(); Publish();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or BadImageFormatException)
        { Error = "The " + title + " page could not be opened: " + ex.Message; Publish(); return false; }
    }

    private void FinishInformationBrowser()
    {
        int returnPage = _browserReturnPage;
        CloseManualBrowser();
        _page = returnPage;
        Error = null; Publish();
    }

    private void CloseManualBrowser()
    {
        ClearManualSnapshot();
        if (_manualBrowser is not { } browser) return;
        _manualBrowser = null; _browserAdmissionUpdates = 0; _browserReturnPage = 0; _browserFailure = null; browser.Dispose();
    }

    private void ClearManualSnapshot()
    {
        _manualSnapshotAttempted = false;
        if (_manualSnapshot is not { } snapshot) return;
        _manualSnapshot = null; snapshot.Dispose();
        if (_manualBrowser is { } browser) browser.IsVisible = true;
    }

    private async Task CaptureManualForQuitAsync(IEnbOptionsBrowser browser, int generation)
    {
        try
        {
            var bitmap = await browser.CaptureAsync();
            if (_manualBrowser != browser || !QuitOpen || generation != _quitState.Generation)
            { bitmap.Dispose(); return; }
            _manualSnapshot = bitmap;
            bitmap.Attach();
            browser.IsVisible = false;
            Publish();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.Runtime.InteropServices.COMException or OperationCanceledException)
        {
            if (_manualBrowser != browser || !QuitOpen || generation != _quitState.Generation) return;
            browser.IsVisible = false;
            Error = "The Manual preview is unavailable while the Quit menu is open.";
            Publish();
        }
    }

    private void PositionManualBrowser()
    {
        if (_manualBrowser is not { } browser) return;
        // Next Modern UI: the page in its smaller Modern UI window.
        var r = _manualPanel!.ModernFrames is not null ? _manualPanel.ModernPage(_size) : Screen(_manualPanel.Design("manual/BROWSER"));
        browser.Arrange(r);
        if (!QuitOpen)
        {
            if (_manualSnapshotAttempted) { ClearManualSnapshot(); browser.IsVisible = true; }
            return;
        }
        if (!_manualSnapshotAttempted)
        {
            _manualSnapshotAttempted = true;
            _ = CaptureManualForQuitAsync(browser, _quitState.Generation);
        }
        if (_manualSnapshot is not { } snapshot) return;
        var occlusion = Screen(_quitUi!.Panel.Bounds).Translate(-r.X, -r.Y);
        snapshot.Arrange(r,occlusion);
    }
}
