using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using EnbNext.Assets;
using EnbNext.Net7.Gameplay;
using EnbNext.Presentation;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private sealed class DamageTextEntry
    {
        public required EnbUiRaster Raster;
        public required EnbDamageTextEffects Effects;
        public readonly EnbOpenGlBackdropRenderer Renderer = new();
        public uint Born, Updated, Victim, Attacker;
        public ulong Identity;
        public bool Cockpit;
        public Vector2 Anchor;
        public double Width, Height;
        public Vector3 LastPosition;
    }

    /// <summary>Horizontal offset of modern incoming text from our ship, in retail's half-screen units.</summary>
    private const float ModernIncomingSide = .06f;
    /// <summary>Next option: an attacker's name under its first incoming hit (Modern UI).</summary>
    internal bool AttackerNames { get; set; }
    private Net7DamageStream? _damageStream;
    private long _damageSequence;
    private readonly List<DamageTextEntry> _damageText = [];
    private EnbRetailIniDocument? _damageFontArt;
    private readonly Dictionary<string, EnbRgb> _damageColors = [];
    private readonly Dictionary<(string, int, int), EnbRetailFontFace> _damageFonts = [];
    internal int DamageTextCount => _damageText.Count;

    private bool AttackerOnScreen(uint attacker)
    {
        foreach (var entry in _damageText) if (entry.Attacker == attacker) return true;
        return false;
    }

    /// <summary>The attacker's display name, or null when the server has not supplied one (no "Unknown" stand-in).</summary>
    private static string? AttackerName(IEnbRenderObject attacker)
    {
        var p = attacker.DisplayNameProperties;
        string? owner = p?.Owner ?? attacker.PlayerPresentation?.Owner, name = p?.Name ?? attacker.Name;
        return string.IsNullOrWhiteSpace(attacker.ObjectType == 1 ? owner ?? name : name ?? owner) ? null : EnbRetailObjectDisplayName.Resolve(attacker);
    }

    private void DestroyDamageText(EnbGlApi? gl)
    {
        foreach (var entry in _damageText) entry.Renderer.Deinitialize(gl);
        _damageText.Clear();
        _damageStream = null; _damageSequence = 0;
    }

    private void RenderDamageText(EnbGlApi gl, int fb, int width, int height, LiveFrame frame)
    {
        var stream = frame.Snapshot.DamageEvents;
        if (!ReferenceEquals(_damageStream, stream))
        { DestroyDamageText(gl); _damageStream = stream; }
        if (stream is null || frame.Snapshot.IsTransitioning || _retailAssetsAccessor() is not { } assets) return;
        if (_damageFontArt is null)
        {
            string path = Path.Combine(assets.Paths.ClientIniDirectory, "fontart.ini");
            if (!EnbNext.Installation.EnbPathCase.FileExists(path)) return;
            _damageFontArt = EnbRetailIniDocument.Load(path);
            foreach (var section in _damageFontArt.EnumerateSections().Select(s => s.Value))
                if (section.GetValueOrDefault("ColorID") is { } id && section.GetValueOrDefault("RGB")?.Split(',') is { Length: 3 } rgb &&
                    byte.TryParse(rgb[0], out var r) && byte.TryParse(rgb[1], out var g) && byte.TryParse(rgb[2], out var b))
                    _damageColors[id] = new(r / 255f, g / 255f, b / 255f);
        }
        uint now = frame.CurrentClientTime;
        foreach (var notification in stream.Pending)
        {
            if (notification.Sequence <= _damageSequence) continue;
            _damageSequence = notification.Sequence;
            if (frame.Snapshot.LocalShipObjectId is not { } local || EnbDamageText.Resolve(notification.Damage, local) is not { } text) continue;
            // Modern UI attaches incoming text to the local ship the way 006EF4C0 attaches outgoing text to the victim,
            // so it follows that path's lifetime too; classic keeps retail's cockpit attachment (00583DC0).
            bool modern = CockpitReplaced && EnbModernText.Renderer is not null, cockpit = text.Incoming && !modern;
            if (!cockpit && unchecked(now - notification.ClientTime) >= 2000) continue;
            // Next option (Modern UI): the attacker's name under its first hit, in that hit's colour, and again only once all of
            // its numbers have faded. An attacker the scene no longer holds, or one without a name, has its number alone.
            uint source = notification.Damage.SourceId;
            bool named = modern && text.Incoming && AttackerNames && source != local && !AttackerOnScreen(source);
            var number = Add(text.Text, text.Style, false);
            if (text.Header is { } header && text.HeaderStyle is { } headerStyle) Add(header, headerStyle, true);
            if (named && number is not null && FindCombatSceneObject(frame.Snapshot, source) is { } attacker && AttackerName(attacker) is { } attackerName)
                Add(attackerName, text.Style, false, number);

            DamageTextEntry? Add(string value, string styleId, bool header, DamageTextEntry? under = null)
            {
                if (assets.CockpitLayout?.TryResolve(styleId, out var style) != true ||
                    !EnbDamageTextEffects.TryCreate(style, _damageFontArt, out var effects) ||
                    !_damageColors.TryGetValue(style.Properties.GetValueOrDefault("Color", ""), out var color)) return null;
                string fontId = style.Properties.GetValueOrDefault("FontType", "");
                var key = (fontId, width, height);
                if (!_damageFonts.TryGetValue(key, out var font))
                {
                    if (!EnbRetailFontFactory.TryResolveDefinition(assets, fontId, width, height, out var definition) ||
                        !EnbRetailFontFactory.TryCreate(assets, definition, out font)) return null;
                    _damageFonts[key] = font;
                }
                Vector2 anchor;
                IEnbRenderObject? victim = null;
                if (cockpit)
                {
                    if (Volatile.Read(ref _cockpitPresentation) is not { } cockpit) return null;
                    string name = text.Reactor ? "reactor_bar0" : "shield_bar0";
                    var pivot = cockpit.Asset.Hierarchy.Pivots.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (pivot is null) return null;
                    var position = EnbAuthoredUiAsset.ResolveTransforms(cockpit.Asset)[pivot.Ordinal].Translation;
                    anchor = new(position.X, position.Y);
                }
                else
                {
                    victim = FindCombatSceneObject(frame.Snapshot, notification.Damage.VictimId);
                    if (victim?.Spatial is null) return null;
                    // Modern incoming: shield hits rise left of our ship and reactor (EMP) drain right of it, the two
                    // columns retail keeps apart on its shield and reactor bars, so simultaneous hits do not overlap.
                    anchor = new(text.Incoming ? text.Reactor ? ModernIncomingSide : -ModernIncomingSide : 0, header ? .2f : .1f);
                }
                if (style.Properties.GetValueOrDefault("Offset")?.Split(',') is { Length: 2 } offset &&
                    float.TryParse(offset[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var ox) &&
                    float.TryParse(offset[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var oy))
                    anchor += cockpit ? new Vector2(ox * 320, oy * 240) : new Vector2(ox, oy);
                EnbUiRaster raster; double w, h;
                if (modern)
                {
                    // Next's face sized from the retail style's on-screen line ink height (brackets included) taken as Bahnschrift's
                    // digit height (~0.7 em), so modern numbers stay in proportion to each style and read slightly larger.
                    double scale = frame.Camera.Viewport.RenderScale;
                    float pixels = (float)Math.Max(8, (font.LineInkBottom - font.LineInkTop) * font.Scale * scale / .7 * (under is null ? 1 : .75));
                    if (EnbModernText.Renderer!.Render(value, pixels, color) is not { } modernRaster) return null;
                    raster = modernRaster; w = raster.Width / scale; h = raster.Height / scale;
                    // A name line sits just under its number and rises with it.
                    if (under is not null) anchor = under.Anchor - new Vector2(0, (float)((under.Height + h) / 2 * scale / (height / 2.0)));
                }
                else
                {
                    raster = font.RenderText(value, color);
                    w = raster.Width * font.Scale; h = raster.Height * font.Scale;
                }
                var entry = new DamageTextEntry
                {
                    Raster = raster, Effects = effects,
                    Born = notification.ClientTime, Updated = now, Cockpit = cockpit, Anchor = anchor,
                    Victim = notification.Damage.VictimId, Attacker = text.Incoming && !cockpit ? notification.Damage.SourceId : 0, Identity = victim?.RenderIdentity ?? 0,
                    LastPosition = victim?.Spatial?.Transform.Position ?? default, Width = w, Height = h
                };
                _damageText.Add(entry);
                return entry;
            }
        }
        stream.Acknowledge(_damageSequence);
        for (int i = 0; i < _damageText.Count;)
        {
            var entry = _damageText[i];
            if (entry.Cockpit ? entry.Effects.Complete : unchecked(now - entry.Born) >= 2000)
            { entry.Renderer.Deinitialize(gl); _damageText.RemoveAt(i); continue; }
            i++;
            uint elapsed = unchecked(now - entry.Updated); entry.Updated = now;
            entry.Effects.Advance(elapsed);
            double x, y;
            if (entry.Cockpit)
            {
                x = (entry.Anchor.X + 320) * width / 640;
                y = (240 - entry.Anchor.Y - entry.Effects.Scroll) * height / 480;
            }
            else
            {
                // 006E7690 detaches a removed source; the effect keeps its last transform.
                if (FindCombatSceneObject(frame.Snapshot, entry.Victim, entry.Identity)?.Spatial is { } spatial) entry.LastPosition = spatial.Transform.Position;
                if (!frame.Camera.TryProjectWorldPoint(entry.LastPosition, 0, out var projected)) continue;
                x = projected.PixelX + entry.Anchor.X * width / 2;
                y = projected.PixelY - (entry.Anchor.Y + entry.Effects.Scroll / 240) * height / 2;
            }
            double w = entry.Width * entry.Effects.Scale * frame.Camera.Viewport.RenderScale, h = entry.Height * entry.Effects.Scale * frame.Camera.Viewport.RenderScale;
            if (!entry.Renderer.IsInitialized) entry.Renderer.Initialize(gl);
            entry.Renderer.RenderOverlay(gl, fb, (int)Math.Round(x - w / 2), height - (int)Math.Round(y + h / 2),
                Math.Max(1, (int)Math.Round(w)), Math.Max(1, (int)Math.Round(h)), entry.Raster, opacity: entry.Effects.Opacity);
        }
        gl.Viewport(0, 0, width, height);
    }
}
