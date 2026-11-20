using System.Globalization;
using EnbNext.Assets;

namespace EnbNext.Engine;

/// <summary>005AAE40/005AB720: one active log and retained failed-save drafts per character.</summary>
internal sealed class EnbCaptainLogController
{
    private EnbRetailCaptainLog? storage;
    private uint character;
    private string saved = "";
    private readonly Dictionary<uint, (int Page, string Text)> drafts = [];
    public bool IsOpen => storage is not null;
    public int Page { get; private set; }
    public bool Initialized { get; private set; }

    public void Open(EnbRetailCaptainLog log, uint owner, EnbRetailTextEditor body, EnbRetailTextEditor entry)
    {
        storage = log; character = owner;
        Page = log.ReadCurrentPage();
        bool retained = drafts.TryGetValue(owner, out var draft);
        if (retained) Page = draft.Page;
        saved = log.ReadPage(Page, out bool initialized); Initialized = initialized;
        body.Text = retained ? draft.Text : saved;
        entry.Text = Page.ToString(CultureInfo.InvariantCulture); entry.MaxLength = 3;
    }

    public void Save(string text)
    {
        if (storage is null) return;
        try
        {
            if (text != saved) { storage.SavePage(Page, text); saved = text; }
            storage.SaveCurrentPage(Page);
            drafts.Remove(character);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            drafts[character] = (Page, text);
            throw;
        }
    }

    // The caller saves before navigation; failed reads leave the current editor and page intact.
    public void ReadPage(int page, EnbRetailTextEditor body, EnbRetailTextEditor entry)
    {
        if (storage is null) return;
        page = Math.Clamp(page, 1, 999);
        string text = storage.ReadPage(page, out bool initialized);
        Initialized = initialized; Page = page; saved = text;
        body.Text = text; body.Entered = initialized;
        entry.Text = page.ToString(CultureInfo.InvariantCulture);
    }

    public void Close() => storage = null;
}
