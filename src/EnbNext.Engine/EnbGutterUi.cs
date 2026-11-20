using System.Globalization;
using System.Numerics;
using Point = EnbNext.Presentation.EnbUiPoint;
using Rect = EnbNext.Presentation.EnbUiRect;
using Size = EnbNext.Presentation.EnbUiSize;
using EnbNext.Presentation;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Engine;

/// <summary>
/// Retail-shaped radar presentation: typed object icons are projected into the
/// view and clamped to the screen-edge gutter when off-screen. Icons are
/// clickable targets and expose Retail's name/distance hover information.
/// </summary>
internal sealed class EnbGutterUi
{
    private const int MarkerBitmapSize = 64;
    private const double GutterIconDesignSize = 12;
    private const double ReticleDesignLimitX = 308;
    private const double ReticleDesignLimitY = 228;
    private const double ReticleRadiusScale = 0.70710677;
    /// <summary>Design units from a screen edge that clamped gutter icons can cover; Next panels start beyond it.</summary>
    internal const double EdgeClearance = 320 - ReticleDesignLimitX + GutterIconDesignSize / 2 + 4;
    /// <summary>Design units of the gutter band itself (the reticle clamp's distance from a screen edge).</summary>
    internal const double Band = 320 - ReticleDesignLimitX;
    private const double MaximumReticleRadius = 0.15;
    private static readonly EnbRgb NameColor = new(1f, 1f, 1f);
    private static readonly EnbRgb GuildFallbackColor = new(0f, 1f, 0f);
    private static readonly EnbRgb DistanceColor = new(1f, 0.93333334f, 0f);

    private readonly Func<uint, CancellationToken, Task<bool>> _targetRequester;
    private readonly Func<uint?> _fixedSessionIdAccessor;
    private readonly GutterFrame _gutterFrame;
    private readonly GutterReticle _hoverReticle;
    private readonly GutterReticle _selectedReticle;
    private uint? _selectedTargetId;
    private readonly Dictionary<ulong, Marker> _markers = [];
    private EnbGutterResources? _resources;
    internal event Action<Marker>? MarkerCreated;
    public event Action? AssetsReleased;
    public Rect Bounds { get; set; }
    public double RenderScaling {get;set;}=1;
    internal GutterFrame Frame => _gutterFrame;
    internal GutterReticle HoverReticle => _hoverReticle;
    internal GutterReticle SelectedReticle => _selectedReticle;
    internal GutterLabel HoverLabel => _hoverLabel;
    /// <summary>
    /// Next option: every object in plain view (not clamped to the gutter, in front) keeps its label: the level
    /// (when the server sent one) before the name, the guild line and the distance. Off, labels are retail's.
    /// </summary>
    internal bool AlwaysShowLabels { get; set; }
    /// <summary>The label categories shown (EnbObjectLabels bits).</summary>
    internal int LabelKinds { get; set; } = EnbObjectLabels.All;
    /// <summary>
    /// Next Modern UI option: no gutter rail. Its band is not drawn, and an object off screen or behind (clamped to the
    /// rail) has no icon, reticle or label and cannot be hovered: the world shows only what is in view.
    /// </summary>
    internal bool RailHidden { get; set; }
    /// <summary>
    /// Next Modern UI option: label categories (EnbObjectLabels bits) whose icons are not drawn while their always-on
    /// label shows. The object keeps its label, its hover reticle and click on its body; with its label off it keeps
    /// its icon.
    /// </summary>
    internal int HiddenIconKinds { get; set; }
    /// <summary>
    /// Next Modern UI: an object attacking you keeps the label colour of its standing (hostile red, neutral yellow, ...)
    /// instead of retail's alternating attacker colours, slowly breathing to a lighter shade of it while it attacks. The
    /// gutter icons keep retail's attacker colours.
    /// </summary>
    internal bool SteadyAttackerLabels { get; set; }
    private bool OffRail(Marker marker) => RailHidden && (marker.IsClamped || !marker.IsInFront);
    private bool IconHidden(Marker marker) => HiddenIconKinds != 0 && AlwaysLabelled(marker) && marker.Contact is { } contact &&
        EnbObjectLabels.Shows(HiddenIconKinds, EnbObjectLabels.Classify(contact.ObjectType, contact.RelationshipRaw, contact.IsAttacking, contact.IsNavigation));
    // An object in plain view whose category is chosen keeps its label in place, also while hovered:
    // the hover label would otherwise move it to the pointer and back as the hovered object changes.
    private bool AlwaysLabelled(Marker marker) => AlwaysShowLabels && marker.Contact is { IsLocalShip: false } contact &&
        !marker.IsClamped && marker.IsInFront &&
        EnbObjectLabels.Shows(LabelKinds, EnbObjectLabels.Classify(contact.ObjectType, contact.RelationshipRaw, contact.IsAttacking, contact.IsNavigation));
    private readonly GutterLabel _hoverLabel;
    private readonly GutterLine _hoverName;
    private readonly GutterLine _hoverGuild;
    private readonly GutterLine _hoverDistance;
    private EnbRetailAssetBundle? _assets;
    private EnbGutterFrameGeometry? _frameGeometry;
    private Marker? _hoveredMarker;
    private Point? _hoverPointer;
    private bool? _hoverLabelRight;

    public EnbGutterUi(
        Func<uint, CancellationToken, Task<bool>> targetRequester,
        Func<uint?> fixedSessionIdAccessor)
    {
        _targetRequester = targetRequester ?? throw new ArgumentNullException(nameof(targetRequester));
        _fixedSessionIdAccessor = fixedSessionIdAccessor ?? throw new ArgumentNullException(nameof(fixedSessionIdAccessor));
        _gutterFrame = new GutterFrame();
        _hoverReticle = new GutterReticle();
        _selectedReticle = new GutterReticle();
        _hoverName = new GutterLine(); _hoverGuild = new GutterLine(); _hoverDistance = new GutterLine();
        _hoverLabel = new GutterLabel(_hoverName, _hoverGuild, _hoverDistance);
    }

    internal bool HasHoveredMarker => _hoveredMarker is not null;
    internal IEnumerable<Marker> Markers => _markers.Values;
    public int VisibleMarkerCount => _markers.Values.Count(static marker => marker.HitTarget.IsVisible);

    public int ClampedMarkerCount => _markers.Values.Count(
        static marker => marker.HitTarget.IsVisible && marker.IsClamped);

    private long _reticleTime;
    private LiveCompletedRenderQueries _bodyQueries=LiveCompletedRenderQueries.Empty;

    public void Update(
        EnbRadarFrame frame,
        EnbResolvedRenderCamera camera,
        EnbRetailAssetBundle? assets,
        double logicalWidth,
        double logicalHeight,
        LiveCompletedRenderQueries renderedObjectBounds,
        long currentClientTime = 0)
    {
        uint elapsed=unchecked((uint)Math.Max(0,currentClientTime-_reticleTime));
        _reticleTime = currentClientTime;
        ArgumentNullException.ThrowIfNull(frame);
        if (!ReferenceEquals(_assets, assets))
            ResetAssets(assets);
        if (assets is not null)
        {
            _gutterFrame.SetOpacity(
                assets.ResolvePlayerOptions(_fixedSessionIdAccessor()).Options.RadarGutterOpacity);
        }

        _gutterFrame.Width = logicalWidth;
        _gutterFrame.Height = logicalHeight;
        SetLeft(_gutterFrame, 0);
        SetTop(_gutterFrame, 0);
        _hoverReticle.Width = logicalWidth;
        _hoverReticle.Height = logicalHeight;
        SetLeft(_hoverReticle, 0);
        SetTop(_hoverReticle, 0);
        _selectedReticle.Width = logicalWidth;
        _selectedReticle.Height = logicalHeight;

        if (!frame.IsAvailable || assets is null || logicalWidth <= 0 || logicalHeight <= 0)
        {
            ClearMarkers();
            return;
        }

        renderedObjectBounds.Retain();
        var previousQueries=_bodyQueries;
        _bodyQueries=renderedObjectBounds;
        var retained = new HashSet<ulong>();
        _selectedTargetCategory = null;
        foreach (EnbRadarContact contact in frame.Contacts)
        {
            if (contact.GameId == _selectedTargetId) _selectedTargetCategory = LabelCategory(contact);
            if (!EnbGutterProjectionRuntime.TryResolve(
                    camera,
                    contact.WorldPosition,
                    contact.WorldRadius,
                    contact.SurfaceDistance,
                    out EnbGutterProjection projection))
            {
                continue;
            }

            retained.Add(contact.RenderIdentity);
            if (!_markers.TryGetValue(contact.RenderIdentity, out Marker? marker))
            {
                marker = CreateMarker(contact.RenderIdentity);
                _markers.Add(contact.RenderIdentity, marker);
                MarkerCreated?.Invoke(marker);
            }

            marker.Contact = contact;
            // 7B50D0 constructs the ordinary name first; later updates replace
            // it with B9EC7C while the object speaking timer is positive.
            marker.DisplayName=marker.NameInitialized && contact.SpeakingColor is not null
                ? EnbSpaceSpeakerRuntime.Text : contact.Name;
            marker.NameInitialized=true;
            // 7B63C0 overwrites the earlier speaking flash flag with 700860.
            bool nameVisible=ReferenceEquals(marker,_hoveredMarker) || contact.GameId==_selectedTargetId ||
                (contact.ObjectType==1 && !projection.IsClampedToGutter);
            if(nameVisible)marker.FlashAlpha=contact.ObjectType is 0 or 1 && contact.IsRescueBeaconActive
                ? EnbStationSpeakerLabel.AdvanceFlash(marker.FlashAlpha,elapsed):1;
            (marker.X, marker.Y) = (projection.LogicalX, projection.LogicalY);
            (marker.ReticleX, marker.ReticleY) = (projection.ReticleLogicalX, projection.ReticleLogicalY);
            (marker.ObjectX, marker.ObjectY) = (projection.ObjectLogicalX, projection.ObjectLogicalY);
            marker.ObjectRadiusNormalized = projection.ObjectRadiusNormalized;
            marker.ReticleRadiusNormalized = projection.ReticleRadiusNormalized;
            marker.HoverRadiusNormalized = projection.HoverRadiusNormalized;
            marker.IsClamped = projection.IsClampedToGutter;
            marker.IsInFront = projection.IsInFront;
            marker.RenderedBounds = renderedObjectBounds.TryGetValue(
                contact.RenderIdentity,
                out LiveRenderedObjectQuery bounds)
                    ? bounds
                    : null;
            marker.Image.Source = ResolveIconBitmap(contact, ReferenceEquals(marker, _hoveredMarker));
            if (_resources?.IconOpacity is float opacity) marker.Image.Opacity = opacity;
            marker.HitTarget.IsVisible = marker.Image.Source is not null && !OffRail(marker);
            marker.IconHidden = IconHidden(marker);
            // gutter_icon.w3d is a 12x12 quad in Retail's 640x480 HUD view.
            // FUN_007B7810 registers the matching 12/640 by 12/480 mouse box;
            // only the render object receives the distance scale.
            double baseWidth = GutterIconDesignSize * logicalWidth /
                EnbGutterFrameGeometry.DesignWidth;
            double baseHeight = GutterIconDesignSize * logicalHeight /
                EnbGutterFrameGeometry.DesignHeight;
            marker.Image.Width = baseWidth * projection.IconScale;
            marker.Image.Height = baseHeight * projection.IconScale;
            marker.HitTarget.Width = baseWidth;
            marker.HitTarget.Height = baseHeight;
            SetLeft(marker.HitTarget, marker.X - marker.HitTarget.Width * 0.5);
            SetTop(marker.HitTarget, marker.Y - marker.HitTarget.Height * 0.5);
            UpdatePersistentPlayerLabel(marker, logicalWidth, logicalHeight);
        }

        foreach (ulong identity in _markers.Keys.Where(identity => !retained.Contains(identity)).ToArray())
            RemoveMarker(identity);

        previousQueries.Release();
        UpdateSelectedReticle(logicalWidth, logicalHeight);

        if (_hoveredMarker is not null)
        {
            // A stationary pointer must not retain a body that has moved out of its hit area.
            if (_hoverPointer is { } pointer && ResolveHoveredMarker(pointer) is { } current)
                SetHoveredMarker(current, pointer);
            else
                ClearHoveredMarker();
        }
    }

    public void Clear()
    {
        ClearMarkers();
        _hoverLabel.IsVisible = false;
    }

    public void TrackPointer(Point pointer)
        => OnOverlayPointerMoved(pointer);

    public void ClearPointer()
        => ClearHoveredMarker();

    public void SetSelectedTarget(uint? gameId)
    {
        _selectedTargetId = gameId;
        UpdateSelectedReticle(Bounds.Width, Bounds.Height);
        foreach(var marker in _markers.Values)UpdatePersistentPlayerLabel(marker,Bounds.Width,Bounds.Height);
        if(_hoveredMarker is {} hovered)UpdateHoverLabel(hovered,Bounds.Width,Bounds.Height);
    }

    private void UpdateSelectedReticle(double width, double height)
    {
        Marker? selected = _selectedTargetId is uint id
            ? _markers.Values.FirstOrDefault(marker => marker.Contact?.GameId == id)
            : null;
        if (selected is null || OffRail(selected)) _selectedReticle.IsVisible = false;
        else UpdateReticle(_selectedReticle, selected, width, height);
        if (_hoveredMarker is { } hovered) UpdateHoverReticle(hovered, width, height);
    }

    public void ReleaseAssets() => ResetAssets(null);

    private Marker CreateMarker(ulong renderIdentity)
    {
        _markersAdded++;
        var name = new GutterLine(); var guild = new GutterLine(); var distance = new GutterLine();
        return new Marker(renderIdentity, new GutterElement(), new GutterImage(), new GutterLabel(name, guild, distance), name, guild, distance);
    }

    internal void OnMarkerExited(Marker marker, Point pointer)
    {
        if (!ReferenceEquals(_hoveredMarker, marker))
            return;
        Marker? replacement = double.IsFinite(pointer.X) && double.IsFinite(pointer.Y)
            ? ResolveHoveredMarker(pointer)
            : null;
        if (replacement is null)
            ClearHoveredMarker();
        else
            SetHoveredMarker(replacement, pointer);
    }

    private void OnOverlayPointerMoved(Point pointer)
    {
        Marker? marker = ResolveHoveredMarker(pointer);
        if (marker is null)
        {
            ClearHoveredMarker();
            return;
        }

        SetHoveredMarker(marker, pointer);
    }

    private void SetHoveredMarker(Marker marker, Point pointer)
    {
        if (!ReferenceEquals(_hoveredMarker, marker))
        {
            _hoverChanges++;
            Marker? previous = _hoveredMarker;
            _hoveredMarker = marker;
            _hoverLabelRight = pointer.X >= Bounds.Width * 0.5;
            if (previous?.Contact is { } previousContact)
            {
                previous.Image.Source = ResolveIconBitmap(previousContact, hovered: false);
                UpdatePersistentPlayerLabel(previous, Bounds.Width, Bounds.Height);
            }
        }

        _hoverPointer = pointer;
        if (marker.Contact is { } contact)
            marker.Image.Source = ResolveIconBitmap(contact, hovered: true);
        UpdateHoverReticle(marker, Bounds.Width, Bounds.Height);
        UpdateHoverLabel(marker, Bounds.Width, Bounds.Height);
        UpdatePersistentPlayerLabel(marker, Bounds.Width, Bounds.Height);
    }

    private void ClearHoveredMarker()
    {
        Marker? marker = _hoveredMarker;
        if (marker is not null) _hoverChanges++;
        _hoveredMarker = null;
        _hoverPointer = null;
        _hoverLabel.IsVisible = false;
        _hoverReticle.IsVisible = false;
        _hoverLabelRight = null;
        if (marker?.Contact is { } contact)
            marker.Image.Source = ResolveIconBitmap(contact, hovered: false);
        if (marker is not null)
            UpdatePersistentPlayerLabel(marker, Bounds.Width, Bounds.Height);
    }

    internal uint? TargetAt(Point pointer, Marker? fallback = null)
    {
        var marker = ResolveHoveredMarker(pointer) ?? fallback ?? _hoveredMarker;
        return marker?.Contact is { IsLocalShip: false } contact ? contact.GameId : null;
    }

    internal async Task RequestTargetAsync(uint target)
    {
        try { await _targetRequester(target, CancellationToken.None); }
        catch (InvalidOperationException) { /* Sector authority can retire between frame and click. */ }
    }

    private EnbUiRaster? ResolveIconBitmap(EnbRadarContact contact, bool hovered)
    {
        EnbRetailAssetBundle? assets = _assets;
        if (assets is null)
            return null;

        string category = ResolveColorCategory(contact);
        string state = hovered ? "Mouse" : contact.IsCurrentTarget ? "Target" : contact.ObjectType is 0 or 1 && contact.IsRescueBeaconActive ? "JumpStart" : "NotTarget";
        EnbRgb first = ResolveColor(assets.GutterColors, category, state + "1");
        EnbRgb second = ResolveColor(assets.GutterColors, category, state + "2");
        EnbRgb color = !hovered && contact.IsCurrentTarget
            ? EnbCurrentRetailGutterColorRuntime.SelectedColor(first, second, _reticleTime)
            : EnbCurrentRetailGutterColorRuntime.AlternatingColor(first, second, _reticleTime);
        return _resources?.Icon(contact.IconAssetName, color, MarkerBitmapSize);
    }

    private void UpdateHoverLabel(Marker marker, double width, double height)
    {
        if (marker.Contact is not { } contact || width <= 0 || height <= 0 || AlwaysLabelled(marker))
        {
            _hoverLabel.IsVisible = false;
            return;
        }

        EnbRetailFontFace? font = ResolveRadarFont(width, height);
        if (font is null)
        {
            _hoverLabel.IsVisible = false;
            return;
        }

        // 7B50D0 writes the 006FF7A0 name verbatim ("Unknown" only when Name/Owner are unset).
        string name = LabelName(marker, contact) ?? "";
        var labelColor=ResolveLabelColor(contact,true);
        _hoverName.SetText(name, font, contact.SpeakingColor??labelColor);
        _hoverLabel.Scale=AttackerScale(contact);
        _hoverName.Opacity=Math.Clamp(marker.FlashAlpha,0,1);
        _hoverGuild.SetText(contact.GuildTag, font, contact.GuildTagColor is null?labelColor:ResolveGuildColor(contact.GuildTagColor));
        _hoverDistance.SetText(FormatHoverDistance(contact), font, DistanceColor);
        Point anchor = _hoverPointer is { } pointer &&
            double.IsFinite(pointer.X) &&
            double.IsFinite(pointer.Y)
                ? pointer
                : new Point(marker.X, marker.Y);
        bool right = ResolveHystereticSide(anchor.X / width, _hoverLabelRight);
        _hoverLabelRight = right;
        SetLabelLineAlignment(right, _hoverName, _hoverGuild, _hoverDistance);

        _hoverLabel.Width = double.NaN;
        _hoverLabel.Measure();
        double labelWidth = Math.Max(1, _hoverLabel.DesiredSize.Width);
        double labelHeight = Math.Max(1, _hoverLabel.DesiredSize.Height);
        _hoverLabel.Width = labelWidth;

        // FUN_007B63C0 anchors hover text to the selected mouse view. Its
        // left-side branch uses the authored 0.011 HUD offset; the right-side
        // branch uses -0.001 with right justification.
        double labelX = right
            ? anchor.X - labelWidth - (width * 0.001)
            : anchor.X + (width * 0.011);
        SetLeft(_hoverLabel, Math.Clamp(labelX, 0, Math.Max(0, width - labelWidth)));
        SetTop(_hoverLabel, Math.Clamp(anchor.Y, 0, Math.Max(0, height - labelHeight)));
        _hoverLabel.IsVisible = true;
    }

    // Running totals for the presentation diagnostics: label show/hide transitions, marker churn and hover changes.
    private long _labelShows, _labelHides, _markersAdded, _markersRemoved, _hoverChanges;
    internal string LabelDiagnostics
    {
        get
        {
            int shown = 0; foreach (var marker in _markers.Values) if (marker.PersistentLabel.IsVisible) shown++;
            return $" · labels {(AlwaysShowLabels ? "always" : "retail")} {shown}/{_markers.Count} shown, shows {_labelShows} hides {_labelHides}, markers +{_markersAdded} -{_markersRemoved}, hover changes {_hoverChanges}";
        }
    }

    private void UpdatePersistentPlayerLabel(Marker marker, double width, double height)
    {
        bool was = marker.PersistentLabel.IsVisible;
        UpdatePersistentLabel(marker, width, height);
        if (marker.PersistentLabel.IsVisible != was) { if (was) _labelHides++; else _labelShows++; }
    }

    private void UpdatePersistentLabel(Marker marker, double width, double height)
    {
        // 007B63C0: the name/guild pair shows for the selected object (also at the gutter) or an
        // on-screen player; the hovered object's pair is anchored at the mouse (hover label).
        // The pair is shown whatever its text, so an empty name does not hide the guild line.
        bool always = AlwaysLabelled(marker);
        if (marker.Contact is not {} contact ||
            (contact.GameId!=_selectedTargetId && (contact.ObjectType!=1 || marker.IsClamped) && !always) ||
            (ReferenceEquals(marker, _hoveredMarker) && !always) ||
            OffRail(marker) ||
            width <= 0 || height <= 0)
        {
            marker.PersistentLabel.IsVisible = false;
            return;
        }

        EnbRetailFontFace? font = ResolveRadarFont(width, height);
        if (font is null)
        {
            marker.PersistentLabel.IsVisible = false;
            return;
        }

        var labelColor=ResolveLabelColor(contact,false);
        marker.PersistentName.SetText(
            LabelName(marker, contact),
            font,
            contact.SpeakingColor??labelColor);
        marker.PersistentLabel.Scale=AttackerScale(contact);
        marker.PersistentName.Opacity=Math.Clamp(marker.FlashAlpha,0,1);
        marker.PersistentGuild.SetText(contact.GuildTag, font, contact.GuildTagColor is null?labelColor:ResolveGuildColor(contact.GuildTagColor));
        // The distance text is drawn again at most five times a second, not for every object in every frame.
        long now = Environment.TickCount64;
        if (!AlwaysShowLabels) marker.PersistentDistance.SetText(null, font, DistanceColor);
        else if (!marker.PersistentDistance.IsVisible || now - marker.DistanceUpdated >= 200)
        { marker.PersistentDistance.SetText(FormatHoverDistance(contact), font, DistanceColor); marker.DistanceUpdated = now; }

        marker.PersistentLabel.Width = double.NaN;
        marker.PersistentLabel.Measure();
        double labelWidth = Math.Max(1, marker.PersistentLabel.DesiredSize.Width);
        double labelHeight = Math.Max(1, marker.PersistentLabel.DesiredSize.Height);
        marker.PersistentLabel.Width = labelWidth;

        const double horizontalOffset = 0.011;
        const double verticalOffset = 0.011;
        marker.LabelRight = ResolveHystereticSide(marker.X / width, marker.LabelRight);
        marker.LabelBelow = ResolveHystereticNearSide(marker.Y / height, marker.LabelBelow);
        SetLabelLineAlignment(marker.LabelRight == true, marker.PersistentName, marker.PersistentGuild, marker.PersistentDistance);
        double labelX = marker.LabelRight == true
            ? marker.X - labelWidth - (width * horizontalOffset)
            : marker.X + (width * horizontalOffset);
        double labelY = marker.LabelBelow == true
            ? marker.Y + (height * verticalOffset)
            : marker.Y - labelHeight - (height * verticalOffset);
        SetLeft(marker.PersistentLabel, Math.Clamp(labelX, 0, Math.Max(0, width - labelWidth)));
        SetTop(marker.PersistentLabel, Math.Clamp(labelY, 0, Math.Max(0, height - labelHeight)));
        marker.PersistentLabel.IsVisible = true;
    }

    // With the Next labels on, the level stands before the name as on the Modern UI frames.
    private string? LabelName(Marker marker, EnbRadarContact contact)
    {
        string? name = marker.DisplayName ?? contact.Name;
        return AlwaysShowLabels && contact.Level is int level && name is { Length: > 0 } ? level.ToString(System.Globalization.CultureInfo.InvariantCulture) + "  " + name : name;
    }

    private void UpdateHoverReticle(Marker marker, double width, double height)
    {
        // One set of identical glyphs when hover and selection coincide.
        if (_selectedReticle.IsVisible && marker.Contact?.GameId == _selectedTargetId)
            _hoverReticle.IsVisible = false;
        else UpdateReticle(_hoverReticle, marker, width, height);
    }

    private void UpdateReticle(GutterReticle reticle, Marker marker, double width, double height)
    {
        if (marker.Contact is not { } contact ||
            width <= 0 || height <= 0 ||
            !double.IsFinite(marker.ObjectX) ||
            !double.IsFinite(marker.ObjectY) ||
            !double.IsFinite(marker.ReticleRadiusNormalized))
        {
            reticle.IsVisible = false;
            return;
        }

        double scaleX = width / EnbGutterFrameGeometry.DesignWidth;
        double scaleY = height / EnbGutterFrameGeometry.DesignHeight;
        Point anchor = ResolveHoverReticleAnchor(marker);
        double centerX = anchor.X;
        double centerY = anchor.Y;
        double normalizedRadius = Math.Min(MaximumReticleRadius, marker.ReticleRadiusNormalized);
        double offsetX = Math.Round(Math.Max(normalizedRadius * ReticleRadiusScale * EnbGutterFrameGeometry.DesignWidth, 2) + 4) * scaleX;
        double offsetY = Math.Round(Math.Max(normalizedRadius * ReticleRadiusScale * EnbGutterFrameGeometry.DesignHeight, 2) + 4) * scaleY;
        ReduceReticleOffsetsToRetailBounds(centerX, centerY, scaleX, scaleY, ref offsetX, ref offsetY);
        EnbTargetReticleGlyphSet? glyphs = ResolveTargetReticleGlyphs(contact);
        if (glyphs is null)
        {
            reticle.IsVisible = false;
            return;
        }

        reticle.Update(
            glyphs,
            centerX,
            centerY,
            offsetX,
            offsetY,
            scaleX,
            scaleY);
    }

    private Point ResolveHoverReticleAnchor(Marker marker)
        => new(marker.ReticleX, marker.ReticleY);

    private static void ReduceReticleOffsetsToRetailBounds(
        double centerX,
        double centerY,
        double scaleX,
        double scaleY,
        ref double offsetX,
        ref double offsetY)
    {
        double maxX = (EnbGutterFrameGeometry.DesignWidth * 0.5 + ReticleDesignLimitX) * scaleX;
        double minX = (EnbGutterFrameGeometry.DesignWidth * 0.5 - ReticleDesignLimitX) * scaleX;
        double maxY = (EnbGutterFrameGeometry.DesignHeight * 0.5 + ReticleDesignLimitY) * scaleY;
        double minY = (EnbGutterFrameGeometry.DesignHeight * 0.5 - ReticleDesignLimitY) * scaleY;
        double minimumX = 4 * scaleX;
        double minimumY = 4 * scaleY;

        while (offsetX > minimumX || offsetY > minimumY)
        {
            double excess = Math.Max(
                Math.Max(centerX + offsetX - maxX, minX - (centerX - offsetX)),
                Math.Max(centerY + offsetY - maxY, minY - (centerY - offsetY)));
            if (excess <= 0.0001)
                break;

            offsetX = Math.Max(minimumX, offsetX - excess);
            offsetY = Math.Max(minimumY, offsetY - excess);
        }
    }

    private Marker? ResolveHoveredMarker(Point pointer)
    {
        double width = Bounds.Width;
        double height = Bounds.Height;
        if (width <= 0 || height <= 0)
            return null;

        Marker? best = null;
        HoverCandidateKind bestKind = HoverCandidateKind.None;
        double bestScore = double.PositiveInfinity;
        double bestDepthScore = double.PositiveInfinity;
        foreach (Marker marker in _markers.Values)
        {
            if (!marker.HitTarget.IsVisible || marker.Contact is null)
                continue;

            HoverCandidateKind candidateKind = HoverCandidateKind.None;
            double candidateScore = double.PositiveInfinity;
            double candidateDepthScore = ResolveHoverDepthScore(marker);

            // Several edge-clamped contacts may share an icon hit rectangle.
            // Returning the dictionary's first match made only whichever
            // contact happened to be inserted first selectable. Resolve every
            // icon candidate and prefer the icon whose center was clicked.
            // A hidden icon is no click box: the object is hovered and clicked on its body only.
            if (!marker.IconHidden && IsInsideIconHitTarget(marker, pointer))
            {
                candidateKind = HoverCandidateKind.Icon;
                double dx = pointer.X - marker.X;
                double dy = pointer.Y - marker.Y;
                candidateScore = Math.Abs(dx / width) + Math.Abs(dy / height);
            }
            else if (!marker.IsInFront)
            {
                continue;
            }
            // 007B7880: raw projected radius is only a prefilter. A body hit
            // requires render-object collision, never a bounds rectangle/ellipse.
            else if (IsInsideRetailHoverRadius(marker, pointer, marker.ObjectRadiusNormalized,
                         width, height, out _) && IsInsideRenderedObject(marker, pointer, width, height))
            {
                candidateKind = HoverCandidateKind.RenderedObject;
                candidateScore = candidateDepthScore;
            }
            else if (IsInsideRetailHoverRadius(
                         marker,
                         pointer,
                         marker.HoverRadiusNormalized,
                         width,
                         height,
                         out double hoverDistanceSquared))
            {
                candidateKind = HoverCandidateKind.ProjectedRadius;
                candidateScore = hoverDistanceSquared;
            }

            if (candidateKind == HoverCandidateKind.None || candidateKind < bestKind ||
                (candidateKind == bestKind &&
                 (candidateScore > bestScore ||
                  (Math.Abs(candidateScore - bestScore) <= 0.0001 &&
                   candidateDepthScore >= bestDepthScore))))
            {
                continue;
            }

            best = marker;
            bestKind = candidateKind;
            bestScore = candidateScore;
            bestDepthScore = candidateDepthScore;
        }

        // 7BCA70 discards a resolved mouse candidate whose ID is the local
        // avatar's. The visible self tag must not become a mouse target.
        return best?.Contact?.IsLocalShip==true?null:best;
    }

    private static double ResolveHoverDepthScore(Marker marker)
        => marker.Contact is { } contact && float.IsFinite(contact.CenterDistance)
            ? contact.CenterDistance
            : double.PositiveInfinity;

    private static bool IsInsideIconHitTarget(Marker marker, Point pointer)
    {
        double halfWidth = marker.HitTarget.Width * 0.5;
        double halfHeight = marker.HitTarget.Height * 0.5;
        return Math.Abs(pointer.X - marker.X) <= halfWidth &&
               Math.Abs(pointer.Y - marker.Y) <= halfHeight;
    }

    private static bool IsInsideRetailHoverRadius(
        Marker marker,
        Point pointer,
        double radius,
        double width,
        double height,
        out double distanceSquared)
    {
        if (!double.IsFinite(radius) || radius <= 0 ||
            !double.IsFinite(marker.ReticleX) ||
            !double.IsFinite(marker.ReticleY))
        {
            distanceSquared = double.PositiveInfinity;
            return false;
        }

        double dx = (pointer.X - marker.ReticleX) / width;
        double dy = (pointer.Y - marker.ReticleY) / height;
        distanceSquared = dx * dx + dy * dy;
        return distanceSquared <= radius * radius;
    }

    private static bool IsInsideRenderedObject(
        Marker marker,
        Point pointer,
        double width,
        double height)
    {
        if (marker.RenderedBounds is not {} published || !published.TryResolveProjection(out var bounds))
            return false;

        if (pointer.X < bounds.MinimumX || pointer.X > bounds.MaximumX ||
            pointer.Y < bounds.MinimumY || pointer.Y > bounds.MaximumY)
        {
            return false;
        }

        return bounds.BodyQueries?.Contains(pointer.X / width,pointer.Y / height)==true;
    }

    private void ResetAssets(EnbRetailAssetBundle? assets)
    {
        ClearMarkers();
        AssetsReleased?.Invoke();
        _resources = assets is null ? null : EnbRenderSession.Of(assets).Gutter;
        _hoverName.Clear();
        _hoverGuild.Clear();
        _hoverDistance.Clear();
        _assets = assets;
        _frameGeometry = _resources?.Frame;
        _gutterFrame.SetGeometry(_frameGeometry);
    }

    private void ClearMarkers()
    {
        _bodyQueries.Release();
        _bodyQueries=LiveCompletedRenderQueries.Empty;
        _selectedReticle.IsVisible = false;
        _selectedTargetId = null;
        foreach (ulong identity in _markers.Keys.ToArray())
            RemoveMarker(identity);
        _hoveredMarker = null;
        _hoverPointer = null;
        _hoverLabel.IsVisible = false;
        _hoverReticle.IsVisible = false;
        _hoverLabelRight = null;
    }

    private void RemoveMarker(ulong identity)
    {
        if (!_markers.Remove(identity, out Marker? marker))
            return;
        _markersRemoved++;
        if (ReferenceEquals(_hoveredMarker, marker))
        {
            _hoveredMarker = null;
            _hoverPointer = null;
            _hoverLabel.IsVisible = false;
            _hoverReticle.IsVisible = false;
            _hoverLabelRight = null;
        }
        marker.Retiring?.Invoke();
        marker.Retiring = null;
        marker.PersistentName.Dispose();
        marker.PersistentGuild.Dispose();
        marker.PersistentDistance.Dispose();
    }

    private static string ResolveColorCategory(EnbRadarContact contact) => ResolveColorCategory(contact, contact.IsAttacking);
    private static string ResolveColorCategory(EnbRadarContact contact, bool attacking)
        => EnbCurrentRetailGutterColorRuntime.ResolveCategory(
            contact.ObjectType,
            contact.RelationshipRaw,
            attacking,
            contact.IsGroupMember,
            contact.IsPvpAttackable);
    /// <summary>The category a label (and the Modern target name) takes its colour from.</summary>
    private string LabelCategory(EnbRadarContact contact) => ResolveColorCategory(contact, contact.IsAttacking && !SteadyAttackerLabels);

    private static EnbRgb ResolveColor(EnbGutterColorCatalog? colors, string category, string state)
        => colors?.TryResolve(category, state, out EnbRgb authored) == true
            ? authored
            : state == "NotTarget1" || state == "NotTarget2"
                ? new EnbRgb(0.43f, 0.65f, 0.69f)
                : new EnbRgb(0.99f, 0.99f, 0.99f);

    private string? _selectedTargetCategory;
    /// <summary>The selected target's radar colour (its gutter category's normal colour), for the Modern UI target name.</summary>
    internal EnbRgb? SelectedTargetColor => _selectedTargetCategory is { } category ? ResolveColor(_assets?.GutterColors, category, "NotTarget1") : null;

    /// <summary>A contact's radar colour (its gutter category's normal colour), for the Modern UI Local window.</summary>
    internal EnbRgb ContactColor(EnbRadarContact contact) => ResolveContactColor(contact, "NotTarget1");
    private EnbRgb ResolveContactColor(EnbRadarContact contact, string state)
        => ResolveColor(_assets?.GutterColors, ResolveColorCategory(contact),
            state == "NotTarget1" && contact.ObjectType is 0 or 1 && contact.IsRescueBeaconActive ? "JumpStart1" : state);

    private EnbRgb ResolveLabelColor(EnbRadarContact contact,bool hovered)
    {
        bool selected=contact.GameId==_selectedTargetId;
        string state=hovered?"Mouse":selected?"Target":contact.ObjectType is 0 or 1 && contact.IsRescueBeaconActive?"JumpStart":"NotTarget";
        string category=LabelCategory(contact);
        var first=ResolveColor(_assets?.GutterColors,category,state+"1");
        var second=ResolveColor(_assets?.GutterColors,category,state+"2");
        var color=!hovered && selected?EnbCurrentRetailGutterColorRuntime.SelectedColor(first,second,_reticleTime)
            :EnbCurrentRetailGutterColorRuntime.AlternatingColor(first,second,_reticleTime);
        return SteadyAttackerLabels && contact.IsAttacking ? AttackerPulse(color) : color;
    }
    /// <summary>Modern UI: an attacker's label breathes slowly between its colour and a lighter shade of it (never another
    /// colour): 1.8 seconds a breath, at most 40% of the way to white.</summary>
    private EnbRgb AttackerPulse(EnbRgb color)
    {
        float lift=.4f*AttackerBreath;
        return new(color.R+(1-color.R)*lift,color.G+(1-color.G)*lift,color.B+(1-color.B)*lift);
    }
    /// <summary>Where an attacker's label is in its breath: 0 at rest, 1 at its fullest (1.8 seconds a breath).</summary>
    private float AttackerBreath=>.5f-.5f*MathF.Cos(MathF.Tau*(_reticleTime%1800)/1800f);
    /// <summary>Modern UI: an attacker's name line (level and name) also grows a little with its breath (at most 7.5%
    /// larger), about its centre; the distance under it keeps its size.</summary>
    private float AttackerScale(EnbRadarContact contact)=>SteadyAttackerLabels && contact.IsAttacking ? 1+.075f*AttackerBreath : 1;

    private EnbRetailFontFace? ResolveRadarFont(double width, double height)
    {
        EnbRetailAssetBundle? assets = _assets;
        if (assets is null)
            return null;

        string fontId = ResolveRadarFontId(assets);
        int displayWidth = Math.Max(1, (int)Math.Round(width*RenderScaling));
        int displayHeight = Math.Max(1, (int)Math.Round(height*RenderScaling));
        if (!EnbRetailFontFactory.TryResolveDefinition(
                assets,
                fontId,
                displayWidth,
                displayHeight,
                out EnbFontDefinition definition))
        {
            return null;
        }

        return _resources?.Font(definition);
    }

    private static string ResolveRadarFontId(EnbRetailAssetBundle assets)
    {
        if (assets.RadarLayout?.TryResolve("RADAR_NAME", out EnbInterfaceTextDefinition definition) == true &&
            definition.Properties.TryGetValue("FontType", out string? fontId) &&
            !string.IsNullOrWhiteSpace(fontId))
        {
            return fontId.Trim();
        }

        return "FONT_TINY";
    }

    private EnbTargetReticleGlyphSet? ResolveTargetReticleGlyphs(EnbRadarContact contact)
    {
        EnbRetailAssetBundle? assets = _assets;
        if (assets is null)
            return null;

        string category = ResolveColorCategory(contact);
        EnbRgb color = contact.GameId == _selectedTargetId
            ? EnbCurrentRetailGutterColorRuntime.SelectedColor(
                ResolveColor(assets.GutterColors, category, "Target1"),
                ResolveColor(assets.GutterColors, category, "Target2"), _reticleTime)
            : ResolveColor(assets.GutterColors, category, "Mouse1");
        return _resources?.Reticle(color);
    }

    private static bool ResolveHystereticSide(double coordinate, bool? previousRight)
        => previousRight switch
        {
            true => coordinate >= 0.4,
            false => coordinate > 0.6,
            null => coordinate >= 0.5,
        };

    private static bool ResolveHystereticNearSide(double coordinate, bool? previousNear)
        => previousNear switch
        {
            true => coordinate <= 0.6,
            false => coordinate < 0.4,
            null => coordinate < 0.5,
        };

    internal static string FormatDistance(float distance)
    {
        if (!float.IsFinite(distance) || distance < 0f)
            return string.Empty;
        if (distance < 1f)
            return string.Empty;
        return string.Create(CultureInfo.InvariantCulture, $"{distance / 1_000f:0.00}k");
    }

    private static string FormatHoverDistance(EnbRadarContact contact)
    {
        string distance = FormatDistance(contact.SurfaceDistance);
        if (contact.ObjectType != 0x01 || contact.ShieldPercent is not float shield || !float.IsFinite(shield))
            return distance;
        float percentage = shield <= 1.0001f ? shield * 100f : shield;
        string shieldText = string.Create(
            CultureInfo.InvariantCulture,
            $"({Math.Clamp((int)MathF.Round(percentage), 0, 100)}%)");
        return string.IsNullOrEmpty(distance)
            ? shieldText
            : string.Create(CultureInfo.InvariantCulture, $"{distance} {shieldText}");
    }

    private static EnbRgb ResolveGuildColor(Vector3? color)
    {
        if (color is not { } value ||
            !float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z))
        {
            return GuildFallbackColor;
        }
        return new EnbRgb(value.X, value.Y, value.Z);
    }

    private static void SetLabelLineAlignment(bool right, params GutterLine[] lines)
    {
        foreach (GutterLine line in lines)
        {
            line.AlignRight = right;
        }
    }

    internal sealed class Marker(
        ulong renderIdentity,
        GutterElement hitTarget,
        GutterImage image,
        GutterLabel persistentLabel,
        GutterLine persistentName,
        GutterLine persistentGuild,
        GutterLine persistentDistance)
    {
        public Action? Retiring;
        public ulong RenderIdentity { get; } = renderIdentity;
        public GutterElement HitTarget { get; } = hitTarget;
        public GutterImage Image { get; } = image;
        public GutterLabel PersistentLabel { get; } = persistentLabel;
        public GutterLine PersistentName { get; } = persistentName;
        public GutterLine PersistentGuild { get; } = persistentGuild;
        public GutterLine PersistentDistance { get; } = persistentDistance;
        public EnbRadarContact? Contact { get; set; }
        public bool NameInitialized;
        public string? DisplayName;
        public float FlashAlpha=1;
        public long DistanceUpdated;
        public double X { get; set; }
        public double Y { get; set; }
        public double ReticleX { get; set; }
        public double ReticleY { get; set; }
        public double ObjectX { get; set; }
        public double ObjectY { get; set; }
        public double ObjectRadiusNormalized { get; set; }
        public double ReticleRadiusNormalized { get; set; }
        public double HoverRadiusNormalized { get; set; }
        public bool IsClamped { get; set; }
        /// <summary>Next option: the icon is not drawn and is no click box (its label shows instead).</summary>
        public bool IconHidden { get; set; }
        public bool IsInFront { get; set; }
        public LiveRenderedObjectQuery? RenderedBounds { get; set; }
        public bool? LabelRight { get; set; }
        public bool? LabelBelow { get; set; }
    }

    private enum HoverCandidateKind
    {
        None,
        ProjectedRadius,
        RenderedObject,
        Icon,
    }

    internal class GutterElement
    {
        public double X, Y, Width, Height;
        public bool IsVisible;
    }
    internal sealed class GutterImage : GutterElement
    {
        public EnbUiRaster? Source;
        public float Opacity = 1;
    }
    internal sealed class GutterLine : EnbRetailText
    {
        public double Opacity = 1;
        public bool AlignRight;
    }
    internal sealed class GutterLabel(GutterLine first, GutterLine second, GutterLine? third = null) : GutterElement
    {
        public GutterLine First { get; } = first;
        public GutterLine Second { get; } = second;
        public GutterLine? Third { get; } = third;
        /// <summary>Next Modern UI: the name line drawn this much larger about its centre (an attacker's breath); 1 normally.</summary>
        public float Scale = 1;
        public Size DesiredSize;
        public void Measure()
        {
            if (!IsVisible) { DesiredSize = default; return; }
            double width = 0, height = 0; int count = 0;
            Include(First); Include(Second); if (Third is { } line) Include(line);
            DesiredSize = new(width, height + Math.Max(0, count - 1));
            void Include(GutterLine line)
            {
                if (!line.IsVisible) return;
                width = Math.Max(width, line.Width);
                height += line.Height; count++;
            }
        }
    }
    internal sealed class GutterFrame : GutterElement
    {
        public EnbGutterFrameGeometry? Geometry;
        public float Opacity = .75f;
        public void SetGeometry(EnbGutterFrameGeometry? value) { Geometry = value; IsVisible = value is not null; }
        public void SetOpacity(float value) => Opacity = value;
    }
    internal sealed class GutterReticle : GutterElement
    {
        public GutterImage UpperLeft { get; } = new();
        public GutterImage UpperRight { get; } = new();
        public GutterImage BottomRight { get; } = new();
        public GutterImage BottomLeft { get; } = new();
        public void Update(EnbTargetReticleGlyphSet glyphs, double centerX, double centerY,
            double offsetX, double offsetY, double scaleX, double scaleY)
        {
            Place(UpperLeft, glyphs.UpperLeft, centerX-offsetX, centerY-offsetY, scaleX, scaleY);
            Place(UpperRight, glyphs.UpperRight, centerX+offsetX, centerY-offsetY, scaleX, scaleY);
            Place(BottomRight, glyphs.BottomRight, centerX+offsetX, centerY+offsetY, scaleX, scaleY);
            Place(BottomLeft, glyphs.BottomLeft, centerX-offsetX, centerY+offsetY, scaleX, scaleY);
            IsVisible = true;
        }
        private static void Place(GutterImage image, EnbTargetReticleGlyph glyph, double x, double y, double sx, double sy)
        {
            image.Source = glyph.Raster;
            image.Width = Math.Max(1, glyph.DesignWidth*sx); image.Height = Math.Max(1, glyph.DesignHeight*sy);
            image.X = x+glyph.MinX*sx; image.Y = y-glyph.MaxY*sy; image.IsVisible = true;
        }
    }
    private static void SetLeft(GutterElement element, double x) => element.X = x;
    private static void SetTop(GutterElement element, double y) => element.Y = y;
}
