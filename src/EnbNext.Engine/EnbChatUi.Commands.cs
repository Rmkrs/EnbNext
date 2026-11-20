using Point = EnbNext.Presentation.EnbUiPoint;
using Vector = EnbNext.Presentation.EnbUiPoint;
using Size = EnbNext.Presentation.EnbUiSize;
using Rect = EnbNext.Presentation.EnbUiRect;
using EnbNext.Presentation;
using System.Globalization;
using EnbNext.Assets;
using EnbNext.Presentation;

namespace EnbNext.Engine;

internal sealed partial class EnbChatUi
{
    private IReadOnlyList<EnbRetailSlashCommand> _commands = [];
    private double _commandsScroll;
    private int _commandsSelection = -1;
    private Vector _commandsOffset;
    private Rect _listTrack, _listThumb;
    private double _listGrab;
    private double _listScrollAtGrab;
    private long _listRepeatAt;
    private int _listRepeat;
    private bool _listCaptured;
    private readonly Dictionary<string, EnbAttachedLabelWipe> _commandOpening = [];
    private double _commandsWipeSpeed;
    internal Func<long> CommandsClock { get; set; } = () => Environment.TickCount64;
    private int CommandsFirst => (int)_commandsScroll;

    private void LoadCommands()
    {
        _commands = []; _wrappedLabels.Clear(); _commandOpening.Clear();
        if (_chatDialog == ChatDialog.Commands) CloseChatDialog();
        if (_assets is null) return;
        try
        {
            if (EnbAuthoredUiAsset.Load(_assets, "tsi29a.w3d") is not { } asset) return;
            var layout = EnbInterfaceLayoutCatalog.Load(Path.Combine(_assets.Paths.ClientIniDirectory, "fa_tsi29a.ini"));
            _dialogAssets["commands"] = asset; _dialogLayouts["commands"] = layout;
            foreach (var mesh in asset.Resource.Meshes)
            {
                string key = "commands/" + mesh.Name;
                _dialogPieces[key] = asset with { Resource = asset.Resource.Copy().SetMeshes([mesh]) };
                _dialogDesign[key] = EnbCockpitUi.MeshBounds(asset, mesh.Name);
            }
            _commands = EnbRetailSlashCommand.Parse(EnbRetailIniDocument.Load(Path.Combine(_assets.Paths.ClientIniDirectory, "slash_commands.ini")));
            var fontArt = EnbRetailIniDocument.Load(Path.Combine(_assets.Paths.ClientIniDirectory, "fontart.ini"));
            double Speed(string id) => fontArt.EnumerateSections().Select(s => s.Value)
                .Where(s => s.GetValueOrDefault("EffectID") == id)
                .Select(s => double.TryParse(s.GetValueOrDefault("Speed"), CultureInfo.InvariantCulture, out var value) ? value : 0).FirstOrDefault();
            _commandsWipeSpeed = Speed("FA_WIPE_ON_RIGHT");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private void OpenCommands()
    {
        if (!_enabled || _scene?.IsTransitioning == true || !_dialogAssets.ContainsKey("commands")) return;
        _commandsScroll = 0; _commandsSelection = -1; _commandsOffset = default;
        _wrappedLabels.Clear(); _commandOpening.Clear();
        OpenChatDialog(ChatDialog.Commands);
    }

    private void ScrollCommands(int amount)
    {
        SetCommandsScroll(_commandsScroll + amount);
        Publish();
    }

    private void SetCommandsScroll(double value)
    {
        double next = Math.Clamp(value, 0, Math.Max(0, _commands.Count - 7));
        if (Math.Round(next) != Math.Round(_commandsScroll)) InteractionSoundRequested?.Invoke("slider_bar_1.wav");
        _commandsScroll = next;
    }

    private bool IsChatList => _chatDialog is ChatDialog.Commands or ChatDialog.PrivateMembers or ChatDialog.Ignore;
    private string ChatListPrefix => _chatDialog == ChatDialog.Commands ? "commands" : _chatDialog == ChatDialog.Ignore ? "ignore" : "privatemembers";
    private double ChatListScroll => _chatDialog == ChatDialog.Commands ? _commandsScroll : _chatDialog == ChatDialog.Ignore ? _ignoreScroll : _privateMembersScroll;
    private int ChatListCount => _chatDialog == ChatDialog.Commands ? _commands.Count : _chatDialog == ChatDialog.Ignore ? IgnoreRows.Length : PrivateMembersCount();
    private static bool IsChatListSlider(string id) => id.StartsWith("chatdialog/commands/slider/", StringComparison.Ordinal) ||
        id.StartsWith("chatdialog/privatemembers/slider/", StringComparison.Ordinal) || id.StartsWith("chatdialog/ignore/slider/", StringComparison.Ordinal);
    private void SetChatListScroll(double value)
    {
        if (_chatDialog == ChatDialog.Commands) SetCommandsScroll(value);
        else if (_chatDialog == ChatDialog.Ignore) SetIgnoreScroll(value);
        else SetPrivateMembersScroll(value);
    }

    private bool AdvanceChatList()
    {
        if (!IsChatList || _listRepeat == 0 || _drag is null || CommandsClock() < _listRepeatAt) return false;
        if (!TryRegion(_drag, out var region) || !region.Contains(_last) ||
            _drag.EndsWith("/track") && (_listRepeat < 0 ? _last.Y >= _listThumb.Top : _last.Y <= _listThumb.Bottom)) return false;
        _listRepeatAt += 100;
        SetChatListScroll(ChatListScroll + _listRepeat);
        return true;
    }

    private void SelectCommand(int row)
    {
        int selected = CommandsFirst + row;
        if ((uint)selected >= _commands.Count) return;
        PlayFontClick(); _commandsSelection = selected;
        _wrappedLabels.Remove("commands/WRAP_DESC_TSI29A");
        Publish();
    }

    private void DragCommands(Vector delta)
    {
        var bounds = CommandsBounds();
        _commandsOffset = new(Math.Clamp(Math.Round(_commandsOffset.X + delta.X * 640 / _size.Width), -bounds.Left, 640 - bounds.Right),
            Math.Clamp(Math.Round(_commandsOffset.Y + delta.Y * 480 / _size.Height), -bounds.Top, 480 - bounds.Bottom));
    }

    private Rect CommandsBounds() => _dialogDesign.Where(p => p.Key.StartsWith("commands/", StringComparison.Ordinal))
        .Select(p => p.Value).Aggregate((a, b) => a.Union(b));

    private void DragChatListThumb(Point point)
    {
        var bounds = Region("chatdialog/" + ChatListPrefix + "/slider/up").Union(Region("chatdialog/" + ChatListPrefix + "/slider/down"));
        // 00670440 restores the press value outside the slider's 32-unit margin.
        if (!bounds.Inflate(32 * _size.Width / 640, 32 * _size.Height / 480).Contains(point))
        { SetChatListScroll(_listScrollAtGrab); Publish(); return; }
        SetChatListScroll(Math.Clamp((point.Y - _listGrab - _listTrack.Top) /
            Math.Max(1, _listTrack.Height - _listThumb.Height), 0, 1) * Math.Max(0, ChatListCount - 7));
        Publish();
    }

    private void PublishCommandLabels(EnbAuthoredUiAsset asset, EnbInterfaceLayoutCatalog layout)
    {
        var transforms = EnbCockpitUi.ResolveTransforms(asset);
        long now = CommandsClock();
        foreach (var definition in layout.Definitions)
        {
            var p = definition.Properties;
            if (!p.TryGetValue("Attach", out var attach) || !p.TryGetValue("FontType", out var fontId) || Font(fontId) is not { } font) continue;
            var pivot = asset.Hierarchy.Pivots.FirstOrDefault(v => v.Name.Equals(attach, StringComparison.OrdinalIgnoreCase));
            if (pivot is null) continue;
            bool row = attach.StartsWith("name", StringComparison.OrdinalIgnoreCase) && int.TryParse(attach.AsSpan(4), out _);
            int rowIndex = row ? int.Parse(attach.AsSpan(4), CultureInfo.InvariantCulture) : -1;
            bool wrap = definition.StringId == "WRAP_DESC_TSI29A";
            string text = row ? CommandsFirst + rowIndex < _commands.Count ? _commands[CommandsFirst + rowIndex].Command : ""
                : wrap ? (uint)_commandsSelection < _commands.Count ? _commands[_commandsSelection].Description : ""
                : p.GetValueOrDefault("String0", "");
            var point = transforms[pivot.Ordinal].Translation;
            var anchor = DialogScreen(new(point.X + 320, 240 - point.Y, 0, 0));
            if (!wrap && text.Length == 0 && p.GetValueOrDefault("EffectOn", "").Contains("FA_WIPE_ON_RIGHT", StringComparison.Ordinal))
            {
                if (!_commandOpening.TryGetValue(definition.StringId, out var blankOpening)) _commandOpening[definition.StringId] = blankOpening = new();
                blankOpening.Update(now, 0, (float)_commandsWipeSpeed);
            }
            double height = font.CellHeight * font.Scale;
            double width = font.MeasureWidth(text) * font.Scale;
            string id = "chatdialog/commands/" + attach;
            bool hovered = _hover.Equals(id, StringComparison.OrdinalIgnoreCase);
            bool selected = row && CommandsFirst + rowIndex == _commandsSelection;
            var color = _chatLabelColors.GetValueOrDefault(p.GetValueOrDefault(selected ? "AltColor" : "Color", ""), new(1, 1, 1));
            if (hovered && p.GetValueOrDefault("MouseOver") == "FA_CYCLE" && _chatLabelColors.TryGetValue(p.GetValueOrDefault("AltColor", ""), out var alt))
                color = EnbAttachedLabelCycle.Color(color, alt, now - _hoverStarted, _chatCycleSpeed);
            if (wrap)
            {
                PublishWrappedDialogLabel("commands", definition.StringId, p, text, point, font, color, 1);
                if (text.Length > 0 && _dialogDesign.TryGetValue("commands/" + attach, out var wrappedBounds))
                    Hit(id, DialogScreen(wrappedBounds), () => { }, wheel: delta => ScrollCommands(delta > 0 ? -1 : 1));
                continue;
            }
            double x = anchor.X, y = anchor.Y - height / 2;
            if (p.GetValueOrDefault("Justify", "").EndsWith(",CENTER", StringComparison.OrdinalIgnoreCase)) x -= width / 2;
            // 006CFA7C passes Attach (definition +10) to virtual 70. The
            // queried bounds are DETAIL00's mesh, not the whole TSI29A panel.
            if (!_dialogDesign.TryGetValue("commands/" + attach, out var textBounds)) continue;
            var boxOffset = p.GetValueOrDefault("BoxOffset", "0,0").Split(',');
            double Offset(int axis) => axis < boxOffset.Length && double.TryParse(boxOffset[axis], CultureInfo.InvariantCulture, out var n) ? n : 0;
            {
                var area = new Rect(x, y, Math.Max(1, width + 2), height);
                Rect clip = area;
                // 006D5D40 derives all four clipping edges from Attach's bounds.
                double left = 320 + Math.Clamp((int)(textBounds.Left + _commandsOffset.X - 320 + Offset(0)), -320, 320);
                double right = 320 + Math.Clamp((int)(textBounds.Right + _commandsOffset.X - 320 - Offset(0)), -320, 320);
                double top = 240 - Math.Clamp((int)(240 - textBounds.Top - _commandsOffset.Y - Offset(1)), -240, 240);
                double bottom = 240 - Math.Clamp((int)(240 - textBounds.Bottom - _commandsOffset.Y + Offset(1)), -240, 240);
                clip = clip.Intersect(new Rect(left * _size.Width / 640, top * _size.Height / 480,
                    Math.Max(0, right - left) * _size.Width / 640, Math.Max(0, bottom - top) * _size.Height / 480));
                if (text.Length > 0)
                {
                    int imageStart = _images.Count;
                    Text(text, area, color, fontId, clip: clip);
                    if (p.GetValueOrDefault("EffectOn", "").Contains("FA_WIPE_ON_RIGHT", StringComparison.Ordinal) && _images.Count > imageStart)
                    {
                        if (!_commandOpening.TryGetValue(definition.StringId, out var opening)) _commandOpening[definition.StringId] = opening = new();
                        var image = _images[imageStart];
                        int extent = opening.Update(now, (int)Math.Round(image.Bounds.Width * _scale), (float)_commandsWipeSpeed);
                        _images[imageStart] = image with { Clip = (clip.Intersect(new Rect(image.Bounds.X, image.Bounds.Y, extent / _scale, image.Bounds.Height))) };
                    }
                }
            }
            if (row)
                Hit(id, DialogScreen(textBounds), () => SelectCommand(rowIndex), wheel: delta => ScrollCommands(delta > 0 ? -1 : 1));
        }
    }
}
