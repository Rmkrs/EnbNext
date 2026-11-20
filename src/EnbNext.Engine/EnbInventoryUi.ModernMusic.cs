using System.Globalization;
using EnbNext.Assets;
using EnbNext.Presentation;
using Rect = EnbNext.Presentation.EnbUiRect;

namespace EnbNext.Engine;

/// <summary>
/// Next Modern UI music player (retail has none): what plays and how far it is, with
/// pause, previous and next; a click on the bar moves within the piece. Combat music can be kept from interrupting the
/// music, the music of other races can play (or only theirs), and music files of one's own join from a folder. Below,
/// every piece there is, by race: a click plays it. Left alone, music is retail's (EnbMusicPlayer).
/// </summary>
internal sealed partial class EnbInventoryUi
{
    private bool _modernMusic;
    private Rect _modernMusicBounds, _musicSeekBar;
    private int _musicScroll;
    private long _musicPublished;
    /// <summary>The list as drawn: a heading (its group) or a piece (its index in the library).</summary>
    private readonly List<(string? Heading, int Track)> _musicRows = [];
    internal EnbMusicPlayer? ModernMusic { get; set; }
    internal EnbUiPoint? ModernMusicPosition { get; set; }
    internal Action<EnbUiPoint>? ModernMusicMoved { get; set; }
    /// <summary>The player's choices changed (combat music, races): to keep them.</summary>
    internal Action? ModernMusicChanged { get; set; }
    /// <summary>Show the folder for one's own music (it is made when missing).</summary>
    internal Action<string>? ModernMusicFolderRequested { get; set; }
    public bool ModernMusicOpen => _modernMusic;

    public bool ToggleModernMusic()
    {
        if (!_interactive || !ModernUi || ModernMusic is null) return false;
        _modernMusic = !_modernMusic;
        if (_modernMusic) { RaiseModern("mmusic"); Opened?.Invoke(); }
        InteractionSoundRequested?.Invoke(_modernMusic ? "SR_scifielectronic40.wav" : "SR_scifielectronic42.wav");
        Publish(); return true;
    }

    /// <summary>Each update: an open window follows the music a few times a second.</summary>
    private void TickModernMusic()
    {
        if (!_modernMusic) return;
        if (!ModernUi) { _modernMusic = false; return; }
        long now = Environment.TickCount64;
        if (now - _musicPublished >= 250) { _musicPublished = now; Publish(); }
    }

    private static string MusicTime(double seconds)
    {
        int total = (int)Math.Max(0, Math.Floor(seconds));
        return (total / 60).ToString(CultureInfo.InvariantCulture) + ":" + (total % 60).ToString("00", CultureInfo.InvariantCulture);
    }

    private static string MusicKind(string category) => category switch { "Combat" => "combat", "Planet" => "planet", "Starbase" => "station", _ => "space" };

    private void ModernMusicWindow()
    {
        if (ModernMusic is not { } player) { _modernMusic = false; return; }
        double u = ModernUnit, text = 14 * u, small = 12 * u, title = 16 * u, gap = 4 * u, pad = 10 * u, head = 24 * u;
        double rowHeight = Math.Round(22 * u), contentWidth = Math.Round(440 * u), box = Math.Round(16 * u);
        int visible = Math.Clamp((int)((_size.Height * .42) / rowHeight), 6, 16);
        double height = pad * 2 + head + gap + head + 18 * u + gap + 18 * u + gap + Math.Round(38 * u) + gap * 2 + head * 3 + gap * 3 + head + visible * rowHeight;
        double width = pad * 2 + contentWidth, x, y;
        if (ModernMusicPosition is { } at) { x = at.X * _size.Width; y = at.Y * _size.Height; }
        else { x = _size.Width - width - 12 * u; y = _size.Height * .12; }
        x = Snap(Math.Clamp(x, 0, Math.Max(0, _size.Width - width))); y = Snap(Math.Clamp(y, 0, Math.Max(0, _size.Height - height)));
        _modernMusicBounds = new(x, y, width, height);
        ModernFrame("DIALOG_BOX_WINDOW", _modernMusicBounds, "mmusic/frame");
        _tips.Remove("mmusic/frame");
        Flush();
        double cx = x + pad, cy = y + pad;
        var dim = new EnbRgb(.65f, .7f, .85f);
        ModernText("mmusic/title", "Music", new(cx, cy, contentWidth - head, head), new(1, .85f, .3f), title);
        ModernCloseButton("mmusic/close", new(x + width - pad - head, cy, head, head));
        cy += head + gap;

        // What plays: its name, its race and kind, and how far it is.
        var current = player.Current;
        string name = current?.Name ?? (player.Category.Length == 0 ? "No music here" : player.Paused ? "Paused" : "Between pieces");
        ModernText("mmusic/now", ModernEllipsis(name, contentWidth, title), new(cx, cy, contentWidth, head), current is null ? dim : new(1, 1, 1), title);
        cy += head;
        if (current is not null)
        {
            string from = current.Own ? "Your music" : current.Group;
            ModernText("mmusic/kind", from + " · " + MusicKind(current.Theme.Type) + " music", new(cx, cy, contentWidth, 18 * u), dim, small);
            ModernText("mmusic/time", MusicTime(player.Position) + " / " + MusicTime(player.Duration), new(cx, cy, contentWidth, 18 * u), dim, small, 2);
        }
        else if (player.Category.Length > 0)
            ModernText("mmusic/kind", "Now: " + MusicKind(player.Category) + " music", new(cx, cy, contentWidth, 18 * u), dim, small);
        cy += 18 * u + gap;
        // The bar: how far the piece is; a click moves there.
        _musicSeekBar = new(cx, Snap(cy + 5 * u), contentWidth, Math.Round(8 * u));
        Hit("mmusic/seek", FromPixels(new(cx, cy, contentWidth, 18 * u)));
        _images.Add(new(EnbModernIcons.Pill((int)Math.Round(_musicSeekBar.Width * _scale), (int)Math.Round(_musicSeekBar.Height * _scale), new(0, 0, 0), 150), _musicSeekBar));
        if (current is not null && player.Duration > 0)
        {
            double filled = Math.Max(_musicSeekBar.Height, _musicSeekBar.Width * Math.Clamp(player.Position / player.Duration, 0, 1));
            _images.Add(new(EnbModernIcons.Pill((int)Math.Round(filled * _scale), (int)Math.Round(_musicSeekBar.Height * _scale),
                _hover == "mmusic/seek" ? new(1, 1, .6f) : new(.35f, .55f, .95f), 230), new(_musicSeekBar.X, _musicSeekBar.Y, filled, _musicSeekBar.Height)));
        }
        _tips["mmusic/seek"] = current is null ? "How far the piece is" : "Click to play from there";
        cy += 18 * u + gap;

        // Previous, play or pause, next.
        double button = Math.Round(38 * u), spacing = Math.Round(12 * u);
        double bx = cx + (contentWidth - button * 3 - spacing * 2) / 2;
        SymbolButton("mmusic/prev", "previous", new(bx, cy, button, button), "Previous piece\n(near its start: the piece before)");
        SymbolButton("mmusic/play", player.Paused ? "play" : "pause", new(bx + button + spacing, cy, button, button), player.Paused ? "Play" : "Pause");
        SymbolButton("mmusic/next", "next", new(bx + (button + spacing) * 2, cy, button, button), "Next piece");
        cy += button + gap * 2;

        // The choices: combat music, the races whose music plays, and one's own music.
        void Check(string id, string label, bool on, double left, double right, string tip)
        {
            bool hot = _hover == id;
            var row = new Rect(left, cy, right - left, head);
            Hit(id, FromPixels(row));
            int pixels = (int)Math.Round(box * _scale);
            _images.Add(new(EnbModernIcons.Check(pixels, on, hot), new(Snap(left), Snap(row.Center.Y - box / 2), pixels / _scale, pixels / _scale)));
            ModernText(id + "/text", label, new(left + box + 8 * u, cy, right - left - box - 8 * u, head), hot ? new(1, 1, .45f) : new(.92f, .94f, 1), text);
            _tips[id] = tip;
        }
        Check("mmusic/combat", "Combat music", player.CombatMusic, cx, cx + contentWidth,
            player.CombatMusic ? "On: when you are attacked, combat music takes over (as the game always did).\nOff: the music plays on."
                : "Off: combat does not interrupt the music.\nOn: when you are attacked, combat music takes over.");
        cy += head + gap;
        double labelWidth = Math.Round(90 * u), columnWidth = Math.Floor((contentWidth - labelWidth) / 3);
        ModernText("mmusic/racesLabel", "Music of", new(cx, cy, labelWidth, head), dim, text);
        for (int race = 0; race < 3; race++)
        {
            string raceName = EnbMusicPlayer.RaceNames[race] + (race == player.OwnRace ? " (yours)" : "");
            double left = cx + labelWidth + race * columnWidth;
            Check("mmusic/race/" + race.ToString(CultureInfo.InvariantCulture), raceName, player.RaceChosen(race), left, left + columnWidth,
                "Whether " + EnbMusicPlayer.RaceNames[race] + " music plays (music for everyone always does).\nWith only your own race chosen, the music is the game's own choice.");
        }
        cy += head + gap;
        string own = player.FolderError is { } error ? "Your music: " + error
            : player.OwnCount == 0 ? "Your music: none yet" : "Your music: " + player.OwnCount.ToString(CultureInfo.InvariantCulture) + (player.OwnCount == 1 ? " piece" : " pieces");
        double folderButton = Math.Ceiling(ModernMeasure("Open folder", text) + 24 * u), rescanButton = Math.Ceiling(ModernMeasure("Look again", text) + 24 * u);
        double ownRoom = contentWidth - folderButton - rescanButton - gap * 3;
        ModernText("mmusic/own", ModernEllipsis(own, ownRoom, text), new(cx, cy, ownRoom, head), dim, text);
        _tips["mmusic/own"] = player.Folder;
        Hit("mmusic/own", FromPixels(new(cx, cy, ownRoom, head)));
        ModernButton("mmusic/folder", "Open folder", new(cx + contentWidth - folderButton - rescanButton - gap, cy, folderButton, head), text,
            "Open the folder for your own music:\n" + player.Folder + "\nmp3 or wav files play as space music; put them in its Combat or Planet folder for those moments.");
        ModernButton("mmusic/rescan", "Look again", new(cx + contentWidth - rescanButton, cy, rescanButton, head), text, "Read the folder again after adding or removing music.");
        cy += head + gap * 2;

        // Every piece, by race: the ones the music may choose now are bright, the one playing lit.
        var library = player.Library;
        _musicRows.Clear();
        string? group = null;
        for (int i = 0; i < library.Count; i++)
        {
            if (library[i].Group != group) { group = library[i].Group; _musicRows.Add((group, -1)); }
            _musicRows.Add((null, i));
        }
        ModernText("mmusic/listTitle", "All music", new(cx, cy, contentWidth, head), new(.7f, .8f, 1), text);
        ModernText("mmusic/listCount", library.Count.ToString(CultureInfo.InvariantCulture) + " pieces", new(cx, cy, contentWidth, head), dim, small, 2);
        cy += head;
        var list = new Rect(cx, cy, contentWidth, visible * rowHeight);
        ModernFrame("DIALOG_BOX_INFO", list);
        Flush();
        Hit("mmusic/list", FromPixels(list));
        _musicScroll = Math.Clamp(_musicScroll, 0, Math.Max(0, _musicRows.Count - visible));
        if (library.Count == 0) ModernText("mmusic/none", "The music list appears when the music starts.", new(cx + 8 * u, cy, contentWidth - 16 * u, rowHeight), dim, text);
        double nameWidth = contentWidth - ModernScrollbarRoom - 16 * u - Math.Round(70 * u);
        for (int n = 0; n < visible && _musicScroll + n < _musicRows.Count; n++)
        {
            var (heading, index) = _musicRows[_musicScroll + n];
            var row = new Rect(cx, Snap(cy + n * rowHeight), contentWidth - ModernScrollbarRoom, rowHeight);
            if (heading is not null)
            {
                ModernText("mmusic/head/" + (_musicScroll + n).ToString(CultureInfo.InvariantCulture), heading, new(row.X + 8 * u, row.Y, row.Width - 16 * u, row.Height), new(1, .85f, .3f), small);
                continue;
            }
            var track = library[index];
            string id = "mmusic/row/" + index.ToString(CultureInfo.InvariantCulture);
            Hit(id, FromPixels(row));
            bool playing = current is not null && current.Same(track.Theme), choosable = player.InPlaylist(track);
            if (playing) _images.Add(new(EnbModernIcons.Fill(new(.45f, .65f, 1), 46), new(row.X + 2 * u, row.Y, row.Width - 4 * u, row.Height)));
            if (_hover == id) _images.Add(new(EnbModernIcons.SlotHighlight((int)Math.Round(row.Width * _scale), (int)Math.Round(row.Height * _scale)), row));
            var color = playing ? new EnbRgb(1, 1, .6f) : choosable ? new EnbRgb(.92f, .94f, 1) : new EnbRgb(.6f, .64f, .74f);
            ModernText(id + "/name", ModernEllipsis(track.Name, nameWidth, text), new(row.X + 16 * u, row.Y, nameWidth, row.Height), color, text);
            ModernText(id + "/kind", MusicKind(track.Theme.Type), new(row.X, row.Y, row.Width - 8 * u, row.Height), dim, small, 2);
            _tips[id] = track.Name + "\n" + (track.Own ? "Your music" : track.Group) + " · " + MusicKind(track.Theme.Type) + " music" +
                (choosable ? "" : track.Theme.Sector != -1 ? "\n(plays in its own sector)" : "\n(not in the music chosen now)") + "\nClick to play it";
        }
        ModernScrollbar("mmusic/list", list, _musicRows.Count, visible, _musicScroll, v => _musicScroll = v);
        Flush();
    }

    /// <summary>A button with one of the Modern UI symbols.</summary>
    private void SymbolButton(string id, string symbol, Rect screen, string tip)
    {
        var face = new Rect(Snap(screen.X), Snap(screen.Y), Math.Round(screen.Width * _scale) / _scale, Math.Round(screen.Height * _scale) / _scale);
        Hit(id, FromPixels(face));
        bool hot = _hover == id;
        _images.Add(new(EnbModernIcons.Button((int)Math.Round(face.Width * _scale), (int)Math.Round(face.Height * _scale), hot), face));
        int pixels = (int)Math.Round(face.Height * .62 * _scale);
        if (EnbModernSymbols.Get(symbol, pixels, hot) is { } image)
            _images.Add(new(image, new(Snap(face.Center.X - pixels / _scale / 2), Snap(face.Center.Y - pixels / _scale / 2), pixels / _scale, pixels / _scale)));
        _tips[id] = tip;
    }

    private bool ActivateModernMusic(string id, bool right)
    {
        if (!id.StartsWith("mmusic/", StringComparison.Ordinal)) return false;
        if (right || ModernMusic is not { } player) return true;
        switch (id)
        {
            case "mmusic/close": _modernMusic = false; InteractionSoundRequested?.Invoke("TextClick01.wav"); return true;
            case "mmusic/play": player.Paused = !player.Paused; break;
            case "mmusic/prev": player.Previous(); break;
            case "mmusic/next": player.Next(); break;
            case "mmusic/seek":
                if (player.Current is not null && player.Duration > 0 && _musicSeekBar.Width > 0)
                    player.SeekTo(Math.Clamp((_point.X - _musicSeekBar.X) / _musicSeekBar.Width, 0, 1) * player.Duration);
                break;
            case "mmusic/combat": player.CombatMusic = !player.CombatMusic; ModernMusicChanged?.Invoke(); break;
            case "mmusic/folder": ModernMusicFolderRequested?.Invoke(player.Folder); break;
            case "mmusic/rescan": player.Rescan(); break;
            default:
                if (id.StartsWith("mmusic/race/", StringComparison.Ordinal) && int.TryParse(id.AsSpan(12), NumberStyles.None, CultureInfo.InvariantCulture, out int race))
                { player.ToggleRace(race); ModernMusicChanged?.Invoke(); break; }
                if (id.StartsWith("mmusic/row/", StringComparison.Ordinal) && int.TryParse(id.AsSpan(11), NumberStyles.None, CultureInfo.InvariantCulture, out int index) &&
                    index >= 0 && index < player.Library.Count)
                { player.Play(player.Library[index]); break; }
                return true;
        }
        InteractionSoundRequested?.Invoke("fontclick.wav");
        return true;
    }

    /// <summary>The wheel over the list scrolls it.</summary>
    internal bool ScrollModernMusic(string id, double delta)
    {
        if (!_modernMusic || !id.StartsWith("mmusic/", StringComparison.Ordinal) || delta == 0) return false;
        _musicScroll = Math.Max(0, _musicScroll + (delta < 0 ? 3 : -3));
        Publish(); return true;
    }
}
