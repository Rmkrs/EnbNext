using EnbNext.Presentation;
namespace EnbNext.Engine;

// Explicit host-owned native surfaces. Engine owns their admission and lifetime.
internal interface IEnbOptionsBrowser : IDisposable
{
    event Action<string>? Failed;
    event Action? PageLoaded;
    event Action? CloseRequested;
    event Action? EscapeRequested;
    bool IsVisible {get;set;}
    void Attach();
    void Arrange(EnbUiRect bounds);
    Task<IEnbOptionsBrowserSnapshot> CaptureAsync();
    /// <summary>Loads the page again (after it failed to load).</summary>
    void Reload() { }
}
internal interface IEnbOptionsBrowserSnapshot : IDisposable
{
    void Attach();
    void Arrange(EnbUiRect bounds,EnbUiRect occlusion);
}
