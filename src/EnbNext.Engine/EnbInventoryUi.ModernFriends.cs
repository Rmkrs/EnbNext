using System.Globalization;
using EnbNext.Assets;
using EnbNext.Presentation;
using Rect = EnbNext.Presentation.EnbUiRect;

namespace EnbNext.Engine;

/// <summary>
/// Next Modern UI friends list: the retail Friends window's content and actions (the same requests, selection,
/// double-click to message and status choice) in a Modern UI window: one line per friend sorted by name or location,
/// the retail buttons and labels, and the name to add typed in the window itself instead of a separate pop-up.
/// </summary>
internal sealed partial class EnbInventoryUi
{
    private Rect _modernFriendsBounds;
    internal EnbUiPoint? ModernFriendsPosition { get; set; }
    internal Action<EnbUiPoint>? ModernFriendsMoved { get; set; }

    /// <summary>A retail Friends window label (fa_tsi28a.ini), trimmed.</summary>
    private string FriendString(string id, string fallback)
        => _guildLayouts.TryGetValue("friends", out var layout) && layout.TryResolve(id, out var definition) &&
           definition.Properties.GetValueOrDefault("String0", "").Trim() is { Length: > 0 } text ? text : fallback;

    private void ModernFriendsPanel()
    {
        var revision = (FriendsData?.StreamId ?? Guid.Empty, FriendsData?.FriendsRevision ?? 0);
        if (_friendListSeen != revision) { _friendListSeen = revision; _friendSelected = null; }
        var rows = FriendRows;
        if (!rows.Any(f => f.Identity == _friendSelected)) _friendSelected = null;
        double u = ModernUnit, text = 14 * u, small = 12 * u, title = 16 * u, gap = 4 * u, pad = 10 * u, head = 24 * u;
        double rowHeight = Math.Round(22 * u), contentWidth = Math.Round(490 * u), nameWidth = Math.Round(200 * u), buttonPad = 12 * u;
        int visible = Math.Clamp(rows.Length, 8, 14);
        _friendsPage = Math.Clamp(_friendsPage, 0, Math.Max(0, rows.Length - visible));
        // The retail buttons, in rows that fit the window.
        var buttons = new[] { ("Add", FriendString("ADD_TSI28A", "Add New")), ("Remove", FriendString("REMOVE_TSI28A", "Remove")), ("AddTarget", FriendString("ADD_TARGET_TSI28A", "Add Target")),
            ("AddGroup", FriendString("ADD_GROUP_TSI28A", "Add Group")), ("Message", FriendString("CHAT_TSI28A", "Send Message")) };
        var placed = new List<(string Id, string Label, double X, int Row, double Width)>();
        double bx = 0; int buttonRows = 1;
        foreach (var (id, label) in buttons)
        {
            double w = Math.Ceiling(ModernMeasure(label, text) + buttonPad * 2);
            if (bx > 0 && bx + w > contentWidth) { bx = 0; buttonRows++; }
            placed.Add((id, label, bx, buttonRows - 1, w)); bx += w + gap;
        }
        bool editing = _friendEditing;
        double height = pad * 2 + head + gap + head + visible * rowHeight + gap * 2 + buttonRows * (head + gap) + gap + 2 * rowHeight
            + (editing ? head + gap * 2 : 0) + (_message is not null ? rowHeight : 0);
        double width = pad * 2 + contentWidth, x, y;
        if (ModernFriendsPosition is { } at) { x = at.X * _size.Width; y = at.Y * _size.Height; }
        else { x = (_size.Width - width) / 2; y = (_size.Height - height) * .4; }
        x = Snap(Math.Clamp(x, 0, Math.Max(0, _size.Width - width))); y = Snap(Math.Clamp(y, 0, Math.Max(0, _size.Height - height)));
        _modernFriendsBounds = new(x, y, width, height);
        ModernFrame("DIALOG_BOX_WINDOW", _modernFriendsBounds, "friends/frame");
        Flush();
        double cx = x + pad, cy = y + pad;
        ModernText("friends/title", FriendString("ignore00_TSI28A", "Friends List"), new(cx, cy, contentWidth - head, head), new(1, .85f, .3f), title);
        ModernCloseButton("friends/Close", new(x + width - pad - head, cy, head, head));
        _tips["friends/Close"] = _friendTips?.GetValueOrDefault("exit", "") is { Length: > 0 } closeTip ? closeTip : "Close";
        cy += head + gap;
        // Column headings: a click sorts by that column (the retail Name / Location buttons).
        double nameX = cx + 8 * u, locationX = nameX + nameWidth + 12 * u, locationWidth = contentWidth - (locationX - cx) - 8 * u - (rows.Length > visible ? ModernScrollbarRoom : 0);
        Heading("Name", FriendString("NAME_TSI28A", "Name"), new(nameX, cy, nameWidth, head), !_friendsByLocation);
        Heading("Location", FriendString("STATUS_TSI28A", "Location"), new(locationX, cy, locationWidth, head), _friendsByLocation);
        cy += head;
        var list = new Rect(cx, cy, contentWidth, visible * rowHeight);
        ModernFrame("DIALOG_BOX_INFO", list);
        Flush();
        Hit("friends/List", FromPixels(list));
        if (rows.Length == 0) ModernText("friends/none", "No friends in the list.", new(nameX, cy, contentWidth - 16 * u, rowHeight), new(.7f, .75f, .88f), text);
        for (int n = 0; n < visible && _friendsPage + n < rows.Length; n++)
        {
            int index = _friendsPage + n; var friend = rows[index];
            var row = new Rect(cx, Snap(cy + n * rowHeight), contentWidth, rowHeight);
            string id = "friends/Row/" + index.ToString(CultureInfo.InvariantCulture);
            Hit(id, FromPixels(row));
            bool selected = friend.Identity == _friendSelected;
            if (n % 2 == 1) _images.Add(new(EnbModernIcons.Fill(new(0, 0, 0), 56), new(row.X + 2 * u, row.Y, row.Width - 4 * u, row.Height)));
            if (selected) _images.Add(new(EnbModernIcons.Fill(new(.45f, .65f, 1), 46), new(row.X + 2 * u, row.Y, row.Width - 4 * u, row.Height)));
            if ((selected || _hover == id) && !editing) _images.Add(new(EnbModernIcons.SlotHighlight((int)Math.Round(row.Width * _scale), (int)Math.Round(row.Height * _scale)), row));
            // Online friends in white, offline friends dimmed (the retail list's two colours).
            EnbRgb nameColor = friend.IsOffline ? new(.55f, .58f, .66f) : new(1, 1, 1), placeColor = friend.IsOffline ? new(.5f, .53f, .6f) : new(.88f, .91f, .97f);
            ModernText(id + "/name", ModernEllipsis(friend.Identity, nameWidth, text), new(nameX, row.Y, nameWidth, row.Height), nameColor, text);
            ModernText(id + "/place", ModernEllipsis(friend.IsOffline ? "Offline" : friend.Presence, locationWidth, text), new(locationX, row.Y, locationWidth, row.Height), placeColor, text);
            _tips[id] = friend.Identity + (friend.IsOffline ? "\nOffline" : "\n" + friend.Presence) + "\n\nDouble-click to send a message";
        }
        cy += visible * rowHeight + gap;
        ModernScrollbar("friends/List", list, rows.Length, visible, _friendsPage, v => _friendsPage = v);
        cy += gap;
        foreach (var (id, label, offset, row, w) in placed)
        {
            bool enabled = FriendEnabled(id);
            string tipKey = id switch { "Add" => "add", "Remove" => "remove", "AddTarget" => "add_target", "AddGroup" => "add_group", _ => "send_message" };
            ModernButton("friends/" + id, label, new(cx + offset, cy + row * (head + gap), w, head), text, _friendTips?.GetValueOrDefault(tipKey, "") ?? "", enabled);
        }
        cy += buttonRows * (head + gap) + gap;
        // Who can see this character's connection status (the retail check pair).
        Choice("Unrestricted", FriendString("ANYONE_TSI28A", "Anyone can see my connection status"), FriendsData?.RestrictedStatus == false);
        Choice("Restricted", FriendString("FRIENDS_TSI28A", "Only Friends can see my status"), FriendsData?.RestrictedStatus == true);
        if (editing)
        {
            // The name to add, typed here; Enter adds it.
            cy += gap;
            string add = FriendString("ADD_TSI28A", "Add New"), cancel = "Cancel";
            double addWidth = Math.Ceiling(ModernMeasure(add, text) + buttonPad * 2), cancelWidth = Math.Ceiling(ModernMeasure(cancel, text) + buttonPad * 2);
            var box = new Rect(cx, cy, contentWidth - addWidth - cancelWidth - gap * 2, head);
            ModernFrame("DIALOG_BOX_INFO", box, "friends/Input");
            string typed = _friendInput?.Text ?? "";
            bool focused = _friendInput is { IsFocused: true };
            string shown = focused ? typed.Insert(Math.Clamp(_friendInput!.CaretIndex, 0, typed.Length), "|") : typed;
            ModernText("friends/inputText", shown.Length > 0 ? shown : "Player name", new(box.X + 8 * u, cy, box.Width - 16 * u, head), shown.Length > 0 ? new(1, 1, 1) : new(.55f, .6f, .7f), text);
            if (_friendInput is not null) _friendInput.Bounds = box;
            _tips["friends/Input"] = "Type the name of the player to add.";
            ModernButton("friends/Submit", add, new(box.Right + gap, cy, addWidth, head), text, "Add this player to your friends list.");
            ModernButton("friends/Cancel", cancel, new(box.Right + gap * 2 + addWidth, cy, cancelWidth, head), text, "Do not add a player.");
            cy += head + gap * 2;
        }
        if (_message is { } message) ModernText("friends/Error", message, new(cx, cy, contentWidth, rowHeight), new(1, .5f, .5f), small);
        Flush();

        void Heading(string id, string label, Rect r, bool active)
        {
            Hit("friends/" + id, FromPixels(r));
            ModernText("friends/" + id + "/text", label, r, active ? new(1, .85f, .3f) : _hover == "friends/" + id ? new(1, 1, .45f) : new(.7f, .75f, .88f), text);
            _tips["friends/" + id] = _friendTips?.GetValueOrDefault(id == "Name" ? "name" : "status", "") is { Length: > 0 } tip ? tip : "Sort by " + label.ToLowerInvariant();
        }
        void Choice(string id, string label, bool on)
        {
            var r = new Rect(cx, cy, contentWidth, rowHeight);
            Hit("friends/" + id, FromPixels(r));
            int box = (int)Math.Round(14 * u * _scale);
            _images.Add(new(EnbModernIcons.Check(box, on, _hover == "friends/" + id, true), new(Snap(r.X + 4 * u), Snap(r.Center.Y - box / _scale / 2), box / _scale, box / _scale)));
            ModernText("friends/" + id + "/text", label, new(r.X + 4 * u + box / _scale + 8 * u, r.Y, r.Width, r.Height), _hover == "friends/" + id ? new(1, 1, .45f) : new(.9f, .93f, 1), text);
            cy += rowHeight;
        }
    }
}
