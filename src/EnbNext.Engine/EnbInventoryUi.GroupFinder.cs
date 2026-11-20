using Point = EnbNext.Presentation.EnbUiPoint;
using Vector = EnbNext.Presentation.EnbUiPoint;
using Size = EnbNext.Presentation.EnbUiSize;
using Rect = EnbNext.Presentation.EnbUiRect;
using KeyModifiers = EnbNext.Assets.EnbKeyModifiers;
using EnbNext.Engine;
using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Net7.Gameplay;
using EnbNext.Net7.Social;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbInventoryUi
{
    private bool _finderOpen, _finderList;
    private readonly Dictionary<string, EnbAuthoredUiAsset> _finderAssets = [];
    private readonly Dictionary<string, EnbInterfaceLayoutCatalog> _finderLayouts = [];
    private readonly Dictionary<string, Point> _finderAnchors = [];
    private IReadOnlyDictionary<string, string>? _finderTips;
    private EnbAuthoredUiAsset? _finderThumbAsset;
    private Net7GroupFinderSettings _finderSettings = new(false, false, false, false, false);
    private ulong _finderAuxRevision;
    private long _finderRevision;
    private Guid _finderStream;
    private uint? _finderPlayer;
    private Point? _finderHoverPoint;
    private Net7GroupFinderCandidate[] _finderRows = [];
    private int _finderSort = 2;
    private double _finderScroll, _finderGrab, _finderScrollAtGrab;
    private Rect _finderTrack, _finderThumb, _finderSliderBounds;
    private int _finderRepeat;
    private long _finderRepeatAt;
    private long _finderGeneration;
    internal Func<long> FinderClock { get; set; } = () => Environment.TickCount64;
    public Func<EnbGroupFinderRequest, Task<bool>>? GroupFinderRequested { get; set; }

    private void LoadGroupFinder(EnbRetailAssetBundle assets)
    {
        ClearFinderAssets();
        try
        {
            foreach (var (prefix, name) in new[] { ("finder", "tsi24a"), ("finderList", "tsi25a") })
            {
                var asset = EnbAuthoredUiAsset.Load(assets, name + ".w3d") ?? throw new InvalidDataException(name);
                _finderAssets[prefix] = asset;
                _finderLayouts[prefix] = EnbInterfaceLayoutCatalog.Load(Path.Combine(assets.Paths.ClientIniDirectory, "fa_" + name + ".ini"));
                var transforms = EnbCockpitUi.ResolveTransforms(asset);
                foreach (var pivot in asset.Hierarchy.Pivots)
                {
                    var p = transforms[pivot.Ordinal].Translation;
                    _finderAnchors[prefix + "/" + pivot.Name.ToUpperInvariant()] = new(p.X + 320, 240 - p.Y);
                }
                foreach (var mesh in asset.Resource.Meshes)
                    _art[prefix + "/" + mesh.Name.ToUpperInvariant()] = new(asset with { Resource = asset.Resource.Copy().SetMeshes([mesh]) },
                        EnbCockpitUi.MeshBounds(asset, mesh.Name).Translate(new(-320, -240)));
                foreach (var definition in _finderLayouts[prefix].Definitions)
                {
                    string key = prefix + "/" + definition.Properties.GetValueOrDefault("Attach", "").ToUpperInvariant();
                    if (!_art.ContainsKey(key) || !_finderAnchors.ContainsKey(key)) throw new InvalidDataException("Group finder font attachment unavailable.");
                }
            }
            foreach (string mesh in new[] { "EXIT", "LOOKING", "FIND", "LEAVE", "DISBAND" }.Concat(FinderChecks.Select(c=>c.Mesh)))
                if (!_art.ContainsKey("finder/" + mesh)) throw new InvalidDataException("Group finder control unavailable.");
            if (!_art.ContainsKey("finderList/EXIT") || !_finderAnchors.ContainsKey("finderList/SLIDER_00_MIN") || !_finderAnchors.ContainsKey("finderList/SLIDER_00_MAX"))
                throw new InvalidDataException("Group finder list controls unavailable.");
            EnbRetailIniDocument.Load(Path.Combine(assets.Paths.ClientIniDirectory, "tooltip.ini")).TryGetSection("UI_FIND_LEAVE", out _finderTips);
            var catalog = assets.RetailMixResources;
            if (catalog is not null && EnbAuthoredUiAsset.Load(assets, "slider_vert.w3d") is {} thumb &&
                catalog.TryReadResource("slider_vert.w3d", out var bytes, out var location) &&
                catalog.TryReadResource("slider_vert-1.w3d", out var animation, out _) &&
                EnbW3dModel.TryDecode([.. bytes, .. animation], out var model) &&
                EnbAuthoredUiAsset.TryCompile(catalog, location, model, "slider_vert", out var resource))
                _finderThumbAsset = thumb with { Resource = resource };
            if (_finderThumbAsset is null || _finderThumbAsset.Resource.Meshes.Count != 1 ||
                _finderThumbAsset.Resource.Hierarchies.Count != 1 ||
                _finderThumbAsset.Resource.Animations.Count(a=>a.Name.Equals("slider_vert-1",StringComparison.OrdinalIgnoreCase)) != 1)
                throw new InvalidDataException("Group finder slider unavailable.");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        { ClearFinderAssets(); }
    }

    private void ClearFinderAssets()
    { _finderAssets.Clear(); _finderLayouts.Clear(); _finderAnchors.Clear(); _finderThumbAsset = null; _finderTips = null; }

    internal bool GroupFinderOpen => _finderOpen;

    internal Task OpenGroupFinder()
    {
        if (!_interactive || _finderOpen || GroupFinderRequested is null || _finderAssets.Count != 2 || Group is not {} group) return Task.CompletedTask;
        CloseAll(); _finderGeneration++; _finderOpen = true;
        _finderSettings = group.AuxDataState != 0 ? group.FinderSettings : new(false,false,false,false,false); _finderAuxRevision = group.AuxRevision;
        _finderStream = group.SymbolStream; _finderPlayer = _scene?.LocalAvatarObjectId;
        Opened?.Invoke(); Publish(); return Task.CompletedTask;
    }

    private void CloseFinderList()
    {
        bool open = _finderList; _finderGeneration++; _finderList = false; _finderRows = []; _finderScroll = 0; _finderRepeat = 0; _finderHoverPoint = null;
        if (open && _owner is uint owner && GroupFinderRequested is {} send)
            _ = send(new(owner, _ship, EnbGroupFinderAction.CloseList));
    }
    private void CloseGroupFinder() { _finderGeneration++; _finderOpen = false; CloseFinderList(); }

    private void UpdateGroupFinder()
    {
        if (!_finderOpen || Group is not {} group) return;
        if (GroupConfirmationOpen) _finderRepeat = 0;
        if (_finderStream != group.SymbolStream) { CloseGroupFinder(); return; }
        if (_finderPlayer != _scene?.LocalAvatarObjectId) { CloseFinderList(); _finderPlayer = _scene?.LocalAvatarObjectId; }
        if (group.AuxDataState > 1 && _finderAuxRevision != group.AuxRevision)
        { _finderAuxRevision = group.AuxRevision; _finderSettings = group.FinderSettings; }
        if (_finderList && _finderRevision != group.FinderRevision)
        {
            _finderRevision = group.FinderRevision; _finderSort = 2;
            _finderRows = EnbGroupFinder.Sort(group.FinderRows, _finderSort);
            _finderScroll = Math.Clamp(_finderScroll, 0, Math.Max(0, _finderRows.Length - EnbGroupFinder.VisibleRows));
        }
        if (_finderRepeat != 0 && FinderClock() >= _finderRepeatAt && _pressed is {} pressed &&
            (pressed != "finderList/Track" || (_finderRepeat < 0 ? _point.Y < Screen(_finderThumb).Top : _point.Y > Screen(_finderThumb).Bottom)))
        { _finderRepeatAt += 100; SetFinderScroll(_finderScroll + _finderRepeat); }
    }

    private Rect FinderRect(string key) => Native(key);
    private bool FinderOptionAllowed(uint option) => Group is {} g && EnbGroupFinder.AllowsOption(g.IsInGroup, g.IsLeader, option);
    private static readonly (uint Option, string Mesh, string Label)[] FinderChecks =
    [ (1, "INVITES_CHECK", "INVITES_opt00"), (3, "NON COMBAT_CHEC", "AUTO_SPLIT_opt00"),
      (4, "QUESTION MARK_C", "LOOTING_opt00"), (5, "AUTO SPLIT_CHEC", "AUTO_RELEASE_LOOTING_opt00") ];
    private bool FinderValue(uint option) => option switch
    { 0 => _finderSettings.LookingForGroup, 1 => _finderSettings.AllowGroupInvite, 3 => _finderSettings.ForceAutoSplit,
      4 => _finderSettings.RestrictedLootingRights, 5 => _finderSettings.AutoReleaseLootingRestrictions, _ => false };

    private void GroupFinderPanel()
    {
        if (!_finderOpen || Group is not {} group) return;
        UpdateGroupFinder(); if (!_finderOpen) return;
        // Next Modern UI: the same actions, options and search results in a Modern UI window.
        if (ModernUi) { ModernGroupFinderPanel(group); return; }
        int first = _draws.Count;
        FinderBackground("finder");
        FinderButton("finder", "EXIT", "Close", null, true, "exit");
        FinderLabel("finder", "title00");
        FinderButton("finder", "LOOKING", "Looking", "LOOKING_opt00", !group.IsInGroup,
            _finderSettings.LookingForGroup ? "STOP_LOOKING" : "LOOK_FOR_GROUP", _finderSettings.LookingForGroup ? "Stop Looking" : "Look for new Group");
        FinderButton("finder", "FIND", "Find", "FIND_opt00", true, _finderList ? "CLOSE_LIST" : "AVAILABLE_RECRUITS", _finderList ? "Close List" : "Available Recruits");
        FinderButton("finder", "LEAVE", "Leave", "LEAVE_opt00", group.IsInGroup && !group.IsLeader, "LEAVE_GROUP");
        FinderButton("finder", "DISBAND", "Disband", "DISBAND_opt00", group.IsInGroup && group.IsLeader, "DISBAND_GROUP");
        foreach (var check in FinderChecks)
        {
            if (check.Option is 3 or 4 && !group.IsInGroup) continue;
            string id = "finder/Option/" + check.Option;
            bool enabled = FinderOptionAllowed(check.Option);
            Draw(FinderValue(check.Option) ? "check/on" : "finder/" + check.Mesh, FinderRect("finder/" + check.Mesh));
            if (enabled && !_busy) Hit(id, FinderRect("finder/" + check.Mesh));
            FinderLabel("finder", check.Label);
        }
        if (_finderList)
        {
            FinderBackground("finderList");
            FinderButton("finderList", "EXIT", "Close", null, true, "exit");
            foreach (var definition in _finderLayouts["finderList"].Definitions)
            {
                string attach = definition.Properties.GetValueOrDefault("Attach", "").ToUpperInvariant();
                if (attach is "NAME" or "RC" or "LEVEL")
                    FinderButton("finderList", attach, "Sort/" + (attach == "NAME" ? 0 : attach == "RC" ? 1 : 2), definition.StringId, true);
                else if (attach.Length >= 2 && int.TryParse(attach.AsSpan(attach.Length - 2), out int row) && row is >= 1 and <= 8)
                {
                    int index = (int)_finderScroll + row - 1;
                    if (index >= _finderRows.Length) continue;
                    var candidate = _finderRows[index]; string id = "finderList/Row/" + index;
                    if (attach.StartsWith("NAME")) Hit(id, FinderRect("finderList/" + attach));
                    string text = attach.StartsWith("NAME") ? candidate.Name : attach.StartsWith("RC") ? candidate.CurrentClassLabel : unchecked((int)candidate.NumericField).ToString(CultureInfo.InvariantCulture);
                    // 0059A1D0 tests cursor geometry every update, including a
                    // response or scroll under a stationary pointer.
                    bool hovered = _finderHoverPoint is {} point && Screen(FinderRect("finderList/NAME" + row.ToString("00"))).Contains(point);
                    FinderLabel("finderList", definition.StringId, text, hovered);
                    _tips[id] = _finderTips?.GetValueOrDefault("CLICK_TARGET", "") ?? "";
                }
                else FinderLabel("finderList", definition.StringId);
            }
            FinderSlider();
        }
        if (_message is {} message) Text("finder/Error", message, new(120, 450, 400, 18), new(1, .5f, .5f));
        Flush();
        for (int i = first; i < _draws.Count; i++) _draws[i] = _draws[i] with { CompositeOpaqueCoverage = true };
    }

    private void FinderBackground(string prefix)
    {
        var asset = _finderAssets[prefix];
        var bounds = asset.Resource.Meshes.Select(m => FinderRect(prefix + "/" + m.Name.ToUpperInvariant())).Aggregate((a,b) => a.Union(b));
        Hit(prefix + "/Background", bounds);
        foreach (var mesh in asset.Resource.Meshes)
        {
            string name = mesh.Name.ToUpperInvariant();
            // Controls are emitted separately; slider placeholders are replaced by GDSlider.
            if (name is "EXIT" or "LOOKING" or "FIND" or "LEAVE" or "DISBAND" or "NAME" or "RC" or "LEVEL" or "LOOTING_CHECK" || FinderChecks.Any(c=>c.Mesh==name) || name.StartsWith("SLIDER")) continue;
            Draw(prefix + "/" + name, FinderRect(prefix + "/" + name));
        }
    }

    private void FinderButton(string prefix, string mesh, string action, string? label, bool enabled, string? tip = null, string? text = null)
    {
        string id = prefix + "/" + action; bool active = enabled && !_busy;
        Draw(prefix + "/" + mesh, FinderRect(prefix + "/" + mesh), active ? id : null, enabled);
        if (label is not null) FinderLabel(prefix, label, text, prefix == "finder" && (!enabled || _pressed == id));
        if (tip is not null) _tips[id] = _finderTips?.GetValueOrDefault(tip, "") ?? "";
    }

    private void FinderLabel(string prefix, string id, string? value = null, bool alternate = false)
    {
        if (!_finderLayouts[prefix].TryResolve(id, out var d)) return;
        var p = d.Properties; string attach = p.GetValueOrDefault("Attach", "").ToUpperInvariant();
        if (!_finderAnchors.TryGetValue(prefix + "/" + attach, out var anchor) || Font(p.GetValueOrDefault("FontType", "FONT_SMALL")) is not {} font) return;
        string text = value ?? p.GetValueOrDefault("String0", "");
        string key = prefix + "/label/" + id;
        if (!_labels.TryGetValue(key, out var label)) _labels[key] = label = new();
        label.SetText(text, font, _friendColors.GetValueOrDefault(p.GetValueOrDefault(alternate ? "AltColor" : "Color", "FA_WHITE"), new(1,1,1)));
        label.SetDisplayScale(1,1);
        if (label.Raster is not {} raster) return;
        var origin = Screen(new(anchor.X, anchor.Y, 0, 0));
        double x = origin.X, y = origin.Y - label.Height / 2;
        string justify = p.GetValueOrDefault("Justify", "");
        if (justify.EndsWith(",CENTER", StringComparison.OrdinalIgnoreCase)) x -= label.Width / 2;
        else if (justify.EndsWith(",RIGHT", StringComparison.OrdinalIgnoreCase)) x -= label.Width;
        var textBounds = FinderRect(prefix + "/" + attach);
        // 006D5D40 truncates attachment bounds in centered retail coordinates.
        double left = 320 + Math.Clamp((int)(textBounds.Left - 320), -320, 320);
        double right = 320 + Math.Clamp((int)(textBounds.Right - 320), -320, 320);
        double top = 240 - Math.Clamp((int)(240 - textBounds.Top), -240, 240);
        double bottom = 240 - Math.Clamp((int)(240 - textBounds.Bottom), -240, 240);
        var clip = Screen(new Rect(left, top, Math.Max(0, right-left), Math.Max(0, bottom-top)));
        _images.Add(new(raster, new(x, y, label.Width, label.Height)) { Clip = (clip) });
    }

    private void SetFinderScroll(double value)
    {
        double next = Math.Clamp(value, 0, Math.Max(0, _finderRows.Length - EnbGroupFinder.VisibleRows));
        if (Math.Round(next) != Math.Round(_finderScroll)) InteractionSoundRequested?.Invoke("slider_bar_1.wav");
        _finderScroll = next;
    }

    private void FinderSlider()
    {
        int count = _finderRows.Length;
        if (count <= EnbGroupFinder.VisibleRows || _finderThumbAsset is not {} thumb) return;
        var top = _finderAnchors["finderList/SLIDER_00_MIN"];
        var bottom = _finderAnchors["finderList/SLIDER_00_MAX"];
        var up = _art["up/CLICK_UP"].Design.Translate(new(top.X, top.Y));
        var down = _art["down/CLICK_DOWN"].Design.Translate(new(bottom.X, bottom.Y));
        Draw("up/CLICK_UP", up, "finderList/Up"); Draw("down/CLICK_DOWN", down, "finderList/Down");
        double start = up.Bottom, trackHeight = Math.Max(0, down.Top - start), height = Math.Max(16, trackHeight * EnbGroupFinder.VisibleRows / count);
        var animation = thumb.Resource.Animations.Single(a => a.Name.Equals("slider_vert-1", StringComparison.OrdinalIgnoreCase));
        var hierarchy = thumb.Resource.Hierarchies.Single(); var transforms = new Matrix4x4[hierarchy.Pivots.Count];
        foreach (var pivot in hierarchy.Pivots)
        {
            if (!EnbW3dAnimationSampler.TrySamplePivot(animation, pivot.Ordinal, (float)Math.Max(0, height - 16), out var sample, out _)) return;
            var local = Matrix4x4.CreateTranslation(sample.Translation) * pivot.LocalTransform;
            transforms[pivot.Ordinal] = pivot.IsRoot ? local : local * transforms[pivot.ParentIndex];
        }
        var pose = new EnbW3dHierarchyPoseOwner(transforms.ToImmutableArray(), Enumerable.Repeat(true, transforms.Length).ToImmutableArray(), null, null);
        if (!EnbW3dSkinDeformer.TryDeform(thumb.Resource.Meshes.Single(), pose, out var geometry, out _)) return;
        var min = geometry.Positions.Aggregate(Vector3.Min); var max = geometry.Positions.Aggregate(Vector3.Max);
        var source = new Rect(min.X, -max.Y, max.X-min.X, max.Y-min.Y);
        _finderTrack = new(top.X + source.X, start, source.Width, trackHeight);
        _finderThumb = new(_finderTrack.X, start + Math.Max(0, trackHeight-height)*_finderScroll/(count-EnbGroupFinder.VisibleRows), source.Width, height);
        _finderSliderBounds = up.Union(down);
        Hit("finderList/Track", _finderTrack);
        _draws.Add(new(thumb with { Geometry = geometry }, 0, (Screen(_finderThumb)), _hover == "finderList/Thumb") { LayoutGroup=_layoutGroup,DesignBounds = (source) });
        Hit("finderList/Thumb", _finderThumb);
    }

    private void FinderPress(string id)
    {
        if (!_finderList || _busy || GroupConfirmationOpen) return;
        _finderRepeat = id switch { "finderList/Up" => -1, "finderList/Down" => 1,
            "finderList/Track" => _point.Y < Screen(_finderThumb).Top ? -EnbGroupFinder.VisibleRows : EnbGroupFinder.VisibleRows, _ => 0 };
        if (_finderRepeat != 0) { SetFinderScroll(_finderScroll + _finderRepeat); _finderRepeatAt = FinderClock() + 500; }
        if (id == "finderList/Thumb") { _finderGrab = _point.Y - Screen(_finderThumb).Top; _finderScrollAtGrab = _finderScroll; }
    }

    private void FinderPointerAt(string id, Point point)
        => _finderHoverPoint = id.StartsWith("finder", StringComparison.Ordinal) ? point : null;

    private void FinderMove(Point point)
    {
        if (_pressed == "finderList/Thumb")
        {
            if (!Screen(_finderSliderBounds).Inflate(32*_size.Width/640,32*_size.Height/480).Contains(point)) SetFinderScroll(_finderScrollAtGrab);
            else SetFinderScroll((point.Y-_finderGrab-Screen(_finderTrack).Top)/Math.Max(1,Screen(_finderTrack).Height-Screen(_finderThumb).Height)*Math.Max(0,_finderRows.Length-EnbGroupFinder.VisibleRows));
        }
        _point = point; Publish();
    }

    private async Task<bool> ActivateGroupFinder(string id, bool right)
    {
        if (!id.StartsWith("finder/", StringComparison.Ordinal) && !id.StartsWith("finderList/", StringComparison.Ordinal)) return false;
        if (right || !_finderOpen || _busy || GroupConfirmationOpen || GroupFinderRequested is not {} send || _owner is not uint owner) return true;
        if (id is "finder/Close" or "finderList/Close")
        { InteractionSoundRequested?.Invoke("Roll_Select02b.wav"); if (id == "finder/Close") CloseGroupFinder(); else CloseFinderList(); return true; }
        if (id.StartsWith("finderList/Sort/"))
        { _finderSort = int.Parse(id.AsSpan(16)); _finderRows = EnbGroupFinder.Sort(Group?.FinderRows ?? [], _finderSort); InteractionSoundRequested?.Invoke("fontclick.wav"); return true; }
        EnbGroupFinderRequest? request = null;
        if (id == "finder/Find")
        {
            InteractionSoundRequested?.Invoke("Roll_Select02b.wav");
            if (_finderList) { CloseFinderList(); return true; }
            _finderList = true; _finderRows = []; _finderScroll = 0; _finderSort = 2; _finderRevision = Group?.FinderRevision ?? 0;
            request = new(owner, _ship, EnbGroupFinderAction.Search);
        }
        else if (id == "finder/Looking" || id.StartsWith("finder/Option/"))
        {
            uint option = id == "finder/Looking" ? 0 : uint.Parse(id.AsSpan(14));
            if (!FinderOptionAllowed(option)) return true;
            bool enabled = !FinderValue(option); _finderSettings = _finderSettings.WithCurrentRetailOption(option, enabled);
            InteractionSoundRequested?.Invoke(option == 0 ? "Roll_Select02b.wav" : "fontclick.wav");
            request = new(owner, _ship, EnbGroupFinderAction.Option, option, enabled);
        }
        else if (id is "finder/Leave" or "finder/Disband")
        {
            bool leader = id == "finder/Disband";
            if (Group is not {IsInGroup:true} g || g.IsLeader != leader) return true;
            InteractionSoundRequested?.Invoke("Roll_Select02b.wav");
            request = new(owner, _ship, leader ? EnbGroupFinderAction.Disband : EnbGroupFinderAction.Leave);
        }
        else if (_finderList && id.StartsWith("finderList/Row/") && int.TryParse(id.AsSpan(15), out int index) && (uint)index < _finderRows.Length)
        {
            InteractionSoundRequested?.Invoke("fontclick.wav");
            uint target = _finderRows[index].ObjectId;
            if (target == 0 || _scene?.Objects.Any(o=>o.GameId==target) != true) return true;
            request = new(owner, _ship, EnbGroupFinderAction.Target, CandidateId:target, Revision:_finderRevision);
        }
        long generation = _finderGeneration;
        if (request is not null) await Send(()=>send(request), ()=>_finderOpen && generation == _finderGeneration);
        return true;
    }
}
