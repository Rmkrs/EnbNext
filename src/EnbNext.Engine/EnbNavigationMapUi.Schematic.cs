using Point = EnbNext.Presentation.EnbUiPoint;
using Rect = EnbNext.Presentation.EnbUiRect;
using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Net7.Protocol;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

/// <summary>
/// Next Modern UI galaxy map, schematic view: every sector flat, as a named box inside its system's block, with a
/// line for each gate. Systems keep their rough place in the galaxy (rows top to bottom, left to right), packed to
/// fill the map area. Drag to move, wheel to zoom; a click plots a route to the sector, as on the 3D map.
/// </summary>
internal sealed partial class EnbNavigationMapUi
{
    private readonly record struct SchematicSector(uint Sector, uint System, string Name, Rect Box);
    private readonly record struct SchematicSystem(uint System, string Name, Rect Block);
    private sealed record SchematicBlock(Net7GalaxySystemRecord System, (Net7GalaxySectorRecord Sector, int Row, int Column)[] Grid, double Width, double Height, Vector3 Real);
    private SchematicSector[] _schematicSectors = [];
    private SchematicSystem[] _schematicSystems = [];
    private Rect _schematicBounds;
    private object? _schematicFor;
    private double _schematicAspect;
    private Vector2 _schematicCenter;
    private float _schematicScale = 1, _schematicFit = 1;
    private bool _schematicFitted;
    private uint? _schematicHover;
    private const double BoxWidth = 128, BoxHeight = 20, BoxGap = 4, BlockPad = 6, BlockHeader = 18, BlockGap = 16;

    private bool Schematic => _modern && _galaxy && ModernGalaxySchematic;

    /// <summary>Lays the sectors out (again when the galaxy data or the map area's shape changes).</summary>
    private void BuildSchematic()
    {
        double aspect = _modernContentLogical.Width / Math.Max(1, _modernContentLogical.Height);
        if (ReferenceEquals(_schematicFor, Galaxy) && Math.Abs(aspect - _schematicAspect) < .05) return;
        _schematicFor = Galaxy; _schematicAspect = aspect; _schematicFitted = false;
        var sectors = EnbGalaxyMapPresentation.VisibleSectors(Galaxy).Where(s => _galaxySectors.ContainsKey(s.SectorHashId)).ToArray();
        var systems = Galaxy.Systems.Where(s => _galaxySystems.ContainsKey(s.SystemHashId) && sectors.Any(x => x.Field2 == s.SystemHashId)).ToArray();
        // Each system's block: its sectors in a small grid in their real arrangement (top rows first, then left to right).
        var blocks = systems.Select(system =>
        {
            var own = sectors.Where(s => s.Field2 == system.SystemHashId).OrderByDescending(s => _galaxySectors[s.SectorHashId].Y).ToArray();
            int columns = own.Length <= 4 ? 1 : own.Length <= 10 ? 2 : 3, rows = (own.Length + columns - 1) / columns;
            var grid = own.Chunk(columns).SelectMany((chunk, row) => chunk.OrderBy(s => _galaxySectors[s.SectorHashId].X).Select((s, column) => (s, row, column))).ToArray();
            double width = 2 * BlockPad + columns * BoxWidth + (columns - 1) * BoxGap, height = BlockHeader + rows * BoxHeight + (rows - 1) * BoxGap + BlockPad;
            return new SchematicBlock(system, grid, width, height, _galaxySystems[system.SystemHashId]);
        }).ToArray();
        if (blocks.Length == 0) { _schematicSectors = []; _schematicSystems = []; _schematicBounds = default; return; }
        // Rows of systems, top of the galaxy first, each row left to right; the row count that fills the area best.
        var byHeight = blocks.OrderByDescending(b => b.Real.Y).ToArray();
        (int Rows, double Score) best = (1, 0);
        for (int rowCount = 1; rowCount <= blocks.Length; rowCount++)
        {
            var rows = Split(rowCount);
            double width = rows.Max(r => r.Sum(b => b.Width) + (r.Length - 1) * BlockGap), height = rows.Sum(r => r.Max(b => b.Height)) + (rows.Length - 1) * BlockGap;
            double score = Math.Min(_modernContentLogical.Width / width, _modernContentLogical.Height / height);
            if (score > best.Score) best = (rowCount, score);
        }
        var placedRows = Split(best.Rows);
        double totalWidth = placedRows.Max(r => r.Sum(b => b.Width) + (r.Length - 1) * BlockGap), y = 0;
        var placedSectors = new List<SchematicSector>(); var placedSystems = new List<SchematicSystem>();
        foreach (var row in placedRows)
        {
            double rowWidth = row.Sum(b => b.Width) + (row.Length - 1) * BlockGap, x = (totalWidth - rowWidth) / 2, rowHeight = row.Max(b => b.Height);
            foreach (var block in row)
            {
                placedSystems.Add(new(block.System.SystemHashId, ModernName(block.System.Name), new(x, y, block.Width, block.Height)));
                foreach (var (sector, r, c) in block.Grid)
                    placedSectors.Add(new(sector.SectorHashId, block.System.SystemHashId, ModernName(sector.Name),
                        new(x + BlockPad + c * (BoxWidth + BoxGap), y + BlockHeader + r * (BoxHeight + BoxGap), BoxWidth, BoxHeight)));
                x += block.Width + BlockGap;
            }
            y += rowHeight + BlockGap;
        }
        _schematicSectors = [.. placedSectors]; _schematicSystems = [.. placedSystems];
        _schematicBounds = new(0, 0, totalWidth, y - BlockGap);
        _schematicFit = (float)(best.Score * .96);

        SchematicBlock[][] Split(int rowCount)
        {
            int per = (byHeight.Length + rowCount - 1) / rowCount;
            return byHeight.Chunk(per).Select(r => r.OrderBy(b => b.Real.X).ToArray()).ToArray();
        }
    }

    private void FitSchematic()
    {
        _schematicScale = _schematicFit; _schematicCenter = new((float)(_schematicBounds.X + _schematicBounds.Width / 2), (float)(_schematicBounds.Y + _schematicBounds.Height / 2));
        _schematicFitted = true;
    }
    private Point SchematicScreen(double x, double y)
    {
        var c = _modernContentLogical.Center;
        return new(c.X + (x - _schematicCenter.X) * _schematicScale, c.Y + (y - _schematicCenter.Y) * _schematicScale);
    }
    private Rect SchematicScreen(Rect r) { var a = SchematicScreen(r.X, r.Y); return new(a.X, a.Y, r.Width * _schematicScale, r.Height * _schematicScale); }
    private void ClampSchematic()
    {
        _schematicScale = Math.Clamp(_schematicScale, _schematicFit * .8f, _schematicFit * 6);
        _schematicCenter = Vector2.Clamp(_schematicCenter, new((float)_schematicBounds.X, (float)_schematicBounds.Y), new((float)_schematicBounds.Right, (float)_schematicBounds.Bottom));
    }
    private void PanSchematic(double dx, double dy) { _schematicCenter -= new Vector2((float)dx, (float)dy) / _schematicScale; ClampSchematic(); }
    private void ZoomSchematic(float amount) { _schematicScale *= MathF.Pow(1.25f, amount * 10); ClampSchematic(); }
    private void CentreSchematicOn(uint id, bool system, bool zoom = true)
    {
        BuildSchematic();
        Rect? target = system ? _schematicSystems.FirstOrDefault(s => s.System == id).Block : _schematicSectors.FirstOrDefault(s => s.Sector == id).Box;
        if (target is not { Width: > 0 } r) return;
        _schematicFitted = true;
        _schematicCenter = new((float)r.Center.X, (float)r.Center.Y);
        if (zoom) _schematicScale = Math.Max(_schematicScale, _schematicFit * 2.5f);
        ClampSchematic();
    }
    private void CentreSchematicOnHome() { if (EnbSectorRouteGuidance.CurrentSector(_scene, Galaxy, Guidance.Place?.Invoke()) is uint home) CentreSchematicOn(home, false); else FitSchematic(); }

    /// <summary>A click on the schematic: a sector plots a route there (as on the 3D map); a right click fits the whole galaxy.</summary>
    private void ClickSchematic(bool right)
    {
        if (right) { FitSchematic(); return; }
        if (RoutePlannerEnabled && _schematicHover is uint sector && _scene is not null)
        {
            Guidance.Choose(sector); Guidance.Update(GalaxyMapAccessor?.Invoke(), _scene);
            InteractionSoundRequested?.Invoke("TextClick03.wav");
        }
    }

    private void PublishSchematic()
    {
        BuildSchematic();
        if (!_schematicFitted) FitSchematic();
        double u = ModernUnit;
        var content = _modernContentLogical;
        var images = ImmutableArray.CreateBuilder<EnbUiTextDraw>();
        var connections = ImmutableArray.CreateBuilder<EnbMapConnection>();
        var pointer = ModernPointer;
        bool pointerInside = content.Contains(pointer) && _hovered is null;
        _schematicHover = null;
        var route = Guidance.Route;
        var onRoute = route.ToHashSet();
        var routeLegs = new HashSet<(uint, uint)>();
        for (int i = 1; i < route.Count; i++) routeLegs.Add((Math.Min(route[i - 1], route[i]), Math.Max(route[i - 1], route[i])));
        uint? here = EnbSectorRouteGuidance.CurrentSector(_scene, Galaxy, Guidance.Place?.Invoke());
        var records = Galaxy.Sectors.ToDictionary(s => s.SectorHashId);
        float textScale = _schematicScale;
        // Text follows the zoom, but stays readable when zoomed out (names are shortened to fit instead).
        double nameSize = Math.Clamp(11 * u * textScale, 9 * u, 15 * u), headerSize = Math.Clamp(12 * u * textScale, 10 * u, 16 * u);

        foreach (var system in _schematicSystems)
        {
            var r = SchematicScreen(system.Block);
            if (!r.Intersects(content)) continue;
            images.Add(new(EnbModernIcons.Fill(new(.04f, .07f, .16f), 150), r));
            Outline(r, new(.32f, .42f, .72f), 150, 1);
            if (ModernTextImage(system.Name, new(r.X + BlockPad * _schematicScale, r.Y, r.Width - 2 * BlockPad * _schematicScale, BlockHeader * _schematicScale), new(.72f, .8f, 1), headerSize) is { } label)
                images.Add(label);
        }
        // Gate lines first, under the boxes: one per connected pair; route legs bright and wider.
        var centres = _schematicSectors.ToDictionary(s => s.Sector, s => SchematicScreen(s.Box).Center);
        var drawn = new HashSet<(uint, uint)>();
        foreach (var gate in Galaxy.Gates)
        {
            if (!centres.TryGetValue(gate.SectorReferenceA, out var a) || !centres.TryGetValue(gate.SectorReferenceB, out var b)) continue;
            var key = (Math.Min(gate.SectorReferenceA, gate.SectorReferenceB), Math.Max(gate.SectorReferenceA, gate.SectorReferenceB));
            if (!drawn.Add(key)) continue;
            bool leg = routeLegs.Contains(key);
            var from = ToDesign(new(a.X, a.Y, 0, 0)).TopLeft; var to = ToDesign(new(b.X, b.Y, 0, 0)).TopLeft;
            float widthPixels = (float)((leg ? 3 : 1.4) * u);
            connections.Add(new(new((float)from.X, (float)from.Y), new((float)to.X, (float)to.Y),
                EnbModernIcons.Fill(leg ? new(1, .85f, .3f) : new(.5f, .65f, 1), (byte)(leg ? 255 : 150)), (float)(widthPixels * 480 / _size.Height), 1, 0));
        }
        foreach (var sector in _schematicSectors)
        {
            var r = SchematicScreen(sector.Box);
            if (!r.Intersects(content)) continue;
            if (pointerInside && r.Contains(pointer)) _schematicHover = sector.Sector;
            var color = records.TryGetValue(sector.Sector, out var record) ? EnbGalaxyMapPresentation.SectorColor(Galaxy, record) : new EnbRgb(.6f, .7f, 1);
            images.Add(new(EnbModernIcons.Fill(new(color.R * .28f, color.G * .28f, color.B * .28f), 225), r));
            bool isHere = sector.Sector == here, routed = onRoute.Contains(sector.Sector), found = sector.Sector == _modernFoundSector;
            EnbRgb rim = isHere ? new(.4f, 1, .45f) : found ? new(.3f, 1, 1) : routed ? new(1, .85f, .3f) : color;
            Outline(r, rim, 230, isHere || found || routed ? 2 : 1);
            if (_schematicHover == sector.Sector)
                images.Add(new(EnbModernIcons.SlotHighlight((int)Math.Round(r.Width * _scaling), (int)Math.Round(r.Height * _scaling)), r));
            if (ModernTextImage(sector.Name, new(r.X + 3 * u, r.Y, r.Width - 6 * u, r.Height), isHere ? new(.6f, 1, .6f) : new(1, 1, 1), nameSize, center: true) is { } name)
                images.Add(name);
        }
        HelpPresentation = null; _guidanceBounds = default;
        Presentation = new(_frame!, _state!.Frame, _state.Maximized, _size, _hovered, [])
        { ContentImages = images.ToImmutable(), Connections = connections.ToImmutable(), ContentBounds = Content };
        ApplyModern(null);
        ShowTooltip(); Changed?.Invoke();

        void Outline(Rect r, EnbRgb color, byte alpha, double thickness)
        {
            double edge = thickness / _scaling; var rim = EnbModernIcons.Fill(color, alpha);
            images.Add(new(rim, new(r.X, r.Y, r.Width, edge))); images.Add(new(rim, new(r.X, r.Bottom - edge, r.Width, edge)));
            images.Add(new(rim, new(r.X, r.Y, edge, r.Height))); images.Add(new(rim, new(r.Right - edge, r.Y, edge, r.Height)));
        }
    }
}
