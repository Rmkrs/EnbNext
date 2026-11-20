using System.Globalization;
using EnbNext.Presentation;
using Rect = EnbNext.Presentation.EnbUiRect;

namespace EnbNext.Engine;

/// <summary>
/// Next Modern UI wiki window (retail has none): a Net-7 Wiki page inside the game, in a
/// window the player moves, resizes and closes. The mission details open it on the page named after the mission
/// (net7wiki.bmsite.net/index.php?title=Slowing_the_Chavez, as Net7 Client Manager does); a name the wiki does not
/// have shows the wiki's own page for it with its search, and "Search" asks the wiki's search for the name instead.
/// Nothing is sent to the game server.
/// </summary>
internal sealed partial class EnbInventoryUi
{
    private const string WikiHome = "https://net7wiki.bmsite.net/", WikiAddress = WikiHome + "index.php?title=";
    private static readonly EnbUiPoint WikiDefaultSize = new(660, 600), WikiMinimumSize = new(380, 260);
    private bool _modernWiki, _wikiLoaded;
    private IEnbInventoryBrowser? _wikiBrowser;
    private string _wikiTopic = "";
    private string? _wikiError;
    private Rect _modernWikiBounds;
    internal EnbUiPoint? ModernWikiPosition { get; set; }
    internal Action<EnbUiPoint>? ModernWikiMoved { get; set; }
    /// <summary>The window's size in Modern UI units; null: the default.</summary>
    internal EnbUiPoint? ModernWikiSize { get; set; }
    internal Action<EnbUiPoint>? ModernWikiResized { get; set; }
    public bool ModernWikiOpen => _modernWiki;

    /// <summary>The address a page's own request to the game goes to (never loaded: the route buttons' requests).</summary>
    internal const string PageRequestHost = "enbnext.invalid";
    /// <summary>The places a wiki page may name, with their sector: the galaxy map's sectors; supplied by the world.</summary>
    internal Func<IEnumerable<(string Name, uint Sector)>>? WikiPlaces { get; set; }
    /// <summary>A route to a sector the wiki names was asked for (the sector, its name as the page shows it): whether a
    /// route was planned.</summary>
    internal Func<uint, string, bool>? WikiRouteRequested { get; set; }
    private string? _wikiStatus;
    private const string WikiRouteScript = @"(()=>{const places=@PLACES@;const norm=s=>(s||'').toLowerCase().replace(/_/g,' ').replace(/\(sic\)/g,' ').replace(/[‘’]/g,""'"").replace(/\s+/g,' ').trim();
const root=document.querySelector('#mw-content-text .mw-parser-output')||document.querySelector('.mw-parser-output');if(!root)return;
if(!document.getElementById('enbnext-route-style')){const st=document.createElement('style');st.id='enbnext-route-style';
st.textContent='.enbnext-route{display:inline-block;margin:0 2px 0 5px;padding:0 7px;border:0;border-radius:4px;background:#1f4fa8;color:#fff;font:600 11px/17px sans-serif;vertical-align:1px;cursor:pointer}.enbnext-route:hover{background:#2f68d8}.enbnext-route.done{background:#2f7d45}';
document.head.appendChild(st);}
for(const a of root.querySelectorAll('a[href]')){if(a.dataset.enbnextRoute)continue;let title='';
try{const u=new URL(a.href,location.href);if(u.hostname!==location.hostname)continue;title=u.searchParams.get('title')||(u.pathname.startsWith('/wiki/')?decodeURIComponent(u.pathname.substring(6)):'');}catch(e){continue;}
let sector=places[norm(title)];if(sector===undefined)sector=places[norm(a.textContent)];if(sector===undefined)continue;
a.dataset.enbnextRoute='1';const name=(a.textContent||title).trim();const b=document.createElement('button');b.type='button';b.className='enbnext-route';b.textContent='Route';b.title='Plan a route to '+name;
b.addEventListener('click',e=>{e.preventDefault();e.stopPropagation();for(const o of document.querySelectorAll('.enbnext-route.done')){o.classList.remove('done');o.textContent='Route';}b.classList.add('done');b.textContent='Route planned';
const f=document.createElement('iframe');f.style.display='none';f.src='https://@HOST@/route?sector='+sector+'&name='+encodeURIComponent(name);document.body.appendChild(f);setTimeout(()=>f.remove(),2000);});a.after(b);}})();";

    /// <summary>On every page the wiki window shows: a small Route button after each link to a place it knows (as Net7
    /// Client Manager's wiki window does); a click plans the route there.</summary>
    private void AddWikiRoutes(IEnbInventoryBrowser browser)
    {
        if (WikiPlaces?.Invoke() is { } places && WikiRouteCode(places) is { } code) browser.Script(code);
    }

    /// <summary>The page script that adds the Route buttons for these places (null: none known).</summary>
    internal static string? WikiRouteCode(IEnumerable<(string Name, uint Sector)> places)
    {
        var known = new Dictionary<string, uint>(StringComparer.Ordinal);
        foreach (var (name, sector) in places)
        {
            string key = string.Join(' ', name.Replace('\u2019', '\'').Replace('_', ' ').ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));
            if (key.Length > 0) known.TryAdd(key, sector);
        }
        if (known.Count == 0) return null;
        return WikiRouteScript.Replace("@HOST@", PageRequestHost, StringComparison.Ordinal).Replace("@PLACES@", System.Text.Json.JsonSerializer.Serialize(known), StringComparison.Ordinal);
    }

    private void WikiRequest(Uri request)
    {
        if (!request.AbsolutePath.Equals("/route", StringComparison.Ordinal)) return;
        var query = System.Web.HttpUtility.ParseQueryString(request.Query);
        if (!uint.TryParse(query["sector"], NumberStyles.None, CultureInfo.InvariantCulture, out uint sector)) return;
        string name = query["name"] is { Length: > 0 } shown ? shown : "the sector";
        bool planned = WikiRouteRequested?.Invoke(sector, name) == true;
        _wikiStatus = planned ? "Route planned to " + name + "." : "No route to " + name + " (the route planner is off, or the sector is not reachable).";
        InteractionSoundRequested?.Invoke(planned ? "TextClick03.wav" : "Roll_Select02b.wav");
        Publish();
    }

    /// <summary>The wiki page named after a title, as Net7 Client Manager's first page for a mission: typographic
    /// quotes plain, [tags] as (tags), spaces as underscores (the wiki's page names).</summary>
    internal static Uri WikiPage(string title)
    {
        string name = string.Join('_', title.Replace('\u2019', '\'').Replace('\u2018', '\'').Replace('[', '(').Replace(']', ')')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return new(WikiAddress + Uri.EscapeDataString(name));
    }
    /// <summary>The wiki's search for a name.</summary>
    internal static Uri WikiSearch(string text) => new(WikiAddress + "Special:Search&profile=default&search=" + Uri.EscapeDataString(text.Trim()));

    /// <summary>Opens the wiki window on the page named after a mission (or anything else the wiki names).</summary>
    internal bool OpenModernWiki(string topic) => OpenWiki(topic, WikiPage(topic));
    /// <summary>Opens the wiki window on the wiki's own front page (the help menu's Net-7 Wiki).</summary>
    internal bool OpenModernWikiHome() => OpenWiki("", new(WikiHome), true);

    private bool OpenWiki(string topic, Uri address, bool home = false)
    {
        if (!_interactive || !ModernUi || (!home && topic.Trim().Length == 0)) return false;
        CloseWikiBrowser();
        _modernWiki = true; _wikiTopic = topic.Trim(); _wikiError = null; _wikiLoaded = false; _wikiStatus = null;
        try
        {
            // Its own browser profile: nothing of the game's station pages or their session.
            string profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EnbNext", "Browser", "Wiki");
            var browser = (BrowserFactory ?? throw new InvalidOperationException("The in-game browser is not available."))(address, profile, "");
            _wikiBrowser = browser;
            browser.IsVisible = false;
            browser.Failed += message => { if (_wikiBrowser == browser) { browser.IsVisible = false; _wikiLoaded = false; _wikiError = "The wiki page could not be loaded. Close the window and try again."; Publish(); } };
            browser.PageLoaded += () => { if (_wikiBrowser == browser) { _wikiError = null; _wikiLoaded = true; AddWikiRoutes(browser); Publish(); } };
            browser.Requested += request => { if (_wikiBrowser == browser) WikiRequest(request); };
            browser.CloseRequested += () => { if (_wikiBrowser == browser) { CloseModernWiki(); Publish(); } };
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or BadImageFormatException or UriFormatException)
        { _wikiError = "The wiki could not be opened: " + ex.Message; }
        Opened?.Invoke();
        InteractionSoundRequested?.Invoke("SR_scifielectronic40.wav");
        Publish(); return true;
    }

    private void CloseWikiBrowser()
    {
        if (_wikiBrowser is not { } browser) return;
        _wikiBrowser = null; _wikiLoaded = false;
        browser.Dispose();
    }

    internal void CloseModernWiki()
    {
        _modernWiki = false; _wikiError = null;
        CloseWikiBrowser();
    }

    private void ModernWikiWindow()
    {
        double u = ModernUnit, text = 14 * u, title = 16 * u, gap = 6 * u, pad = 10 * u, head = 24 * u;
        var size = ModernSized(ModernWikiSize, WikiDefaultSize, WikiMinimumSize);
        double width = Math.Round(size.X * u), height = Math.Round(size.Y * u), x, y;
        if (ModernWikiPosition is { } at) { x = at.X * _size.Width; y = at.Y * _size.Height; }
        // At first on the left, clear of the missions window and its details (the page is drawn over other windows).
        else { x = 12 * u; y = _size.Height * .12; }
        x = Snap(Math.Clamp(x, 0, Math.Max(0, _size.Width - width))); y = Snap(Math.Clamp(y, 0, Math.Max(0, _size.Height - height)));
        _modernWikiBounds = new(x, y, width, height);
        ModernFrame("DIALOG_BOX_WINDOW", _modernWikiBounds, "mwiki/frame");
        _tips.Remove("mwiki/frame");
        Flush();
        double cx = x + pad, cy = y + pad, contentWidth = width - pad * 2;
        // The front page has no name to search for.
        double searchWidth = _wikiTopic.Length > 0 ? Math.Ceiling(ModernMeasure("Search", text) + 32 * u) : 0;
        double titleWidth = contentWidth - head - searchWidth - gap * (searchWidth > 0 ? 2 : 1);
        ModernText("mwiki/title", ModernEllipsis(_wikiTopic.Length > 0 ? "Net-7 Wiki: " + _wikiTopic : "Net-7 Wiki", titleWidth, title), new(cx, cy, titleWidth, head), new(1, .85f, .3f), title);
        if (searchWidth > 0) ModernButton("mwiki/search", "Search", new(x + width - pad - head - gap - searchWidth, cy, searchWidth, head), text,
            "Search the wiki for \"" + _wikiTopic + "\"\n(when its page is not the one you want)");
        ModernCloseButton("mwiki/close", new(x + width - pad - head, cy, head, head));
        cy += head + gap;
        // The page, above the grip's corner.
        var page = new Rect(cx, cy, contentWidth, Math.Max(0, y + height - pad - 14 * u - cy));
        _images.Add(new(EnbModernIcons.Fill(new(0, 0, 0), 150), page));
        if (_wikiBrowser is { } browser) { browser.Bounds = page; browser.IsVisible = _wikiLoaded && _wikiError is null; }
        if (_wikiError is not null)
            ModernText("mwiki/message", _wikiError, new(page.X + 12 * u, page.Y + 12 * u, page.Width - 24 * u, head), new(1, .45f, .45f), text);
        else if (!_wikiLoaded)
            ModernText("mwiki/loading", "Loading the wiki...", new(page.X, page.Y + page.Height / 2 - head / 2, page.Width, head), new(.75f, .8f, .92f), text, 1);
        // What the last route button did, under the page.
        if (_wikiStatus is { } status)
            ModernText("mwiki/status", ModernEllipsis(status, contentWidth - 24 * u, 12 * u), new(cx, page.Bottom, contentWidth - 24 * u, 14 * u), new(.7f, .85f, 1), 12 * u);
        ModernResizeGrip("mwiki/resize", _modernWikiBounds);
        Flush();
    }

    /// <summary>Before each publish: the page is hidden unless its window is drawn (not over the Quit menu, with the
    /// interface hidden or in the classic interface); drawing the window shows it again.</summary>
    private void HideWikiPage()
    {
        if (_wikiBrowser is { } browser && (!_modernWiki || !ModernUi || !IsVisible || ModernQuitShown)) browser.IsVisible = false;
    }

    private bool ActivateModernWiki(string id, bool right)
    {
        if (!id.StartsWith("mwiki/", StringComparison.Ordinal)) return false;
        if (right) return true;
        if (id == "mwiki/catalogue") { if (_itemsWikiName is { Length: > 0 } name) OpenModernWiki(name); }
        else if (id == "mwiki/item") { if (_examined is not null && _mdetailName.Trim().Length > 0) OpenModernWiki(_mdetailName); }
        else if (id == "mwiki/close") { CloseModernWiki(); InteractionSoundRequested?.Invoke("TextClick01.wav"); }
        else if (id == "mwiki/search" && _wikiTopic.Length > 0) { InteractionSoundRequested?.Invoke("fontclick.wav"); OpenWiki(_wikiTopic, WikiSearch(_wikiTopic)); }
        return true;
    }
}
