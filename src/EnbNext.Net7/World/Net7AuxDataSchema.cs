using System.Collections.ObjectModel;
using System.Reflection;

namespace EnbNext.Net7.World;

public enum Net7AuxDataValueKind
{
    Aggregate,
    UInt64LittleEndian,
    Int32LittleEndian,
    Float32LittleEndian,
    LengthPrefixedString,
    BooleanByte,
    Raw4,
    Raw8TwoUInt32LittleEndian,
    Time32LittleEndianRelative,
    Raw12,
    Raw16,
    Raw20,
}

public sealed class Net7AuxDataProperty
{
    private readonly List<Net7AuxDataProperty> _children = [];

    internal Net7AuxDataProperty(int id, string path, Net7AuxDataValueKind kind, int declaredChildCount)
    {
        Id = id;
        Path = path;
        Kind = kind;
        DeclaredChildCount = declaredChildCount;
    }

    public int Id { get; }
    public string Path { get; }
    public Net7AuxDataValueKind Kind { get; }
    public int DeclaredChildCount { get; }
    public Net7AuxDataProperty? Parent { get; internal set; }
    public int ChildIndex { get; internal set; }
    public IReadOnlyList<Net7AuxDataProperty> Children => _children;
    private int _state;
    private object? _value;
    private ulong _updateRevision;
    // Local dependency invalidation, separate from the native callback count.
    // State-zero and rollback can change a projection without a new callback.
    public ulong MaterializedRevision {get;private set;}
    private void InvalidateProjection()
    {
        for(Net7AuxDataProperty? current=this;current is not null;current=current.Parent)
            current.MaterializedRevision++;
    }
    public int State {get=>_state;internal set{if(_state==value)return;_state=value;InvalidateProjection();}}
    public object? Value {get=>_value;internal set{if(ReferenceEquals(_value,value))return;_value=value;InvalidateProjection();}}
    // Local callback identity, never serialized. Repeated equal incoming values
    // can represent distinct presentation events (for example camera shake).
    public ulong UpdateRevision {get=>_updateRevision;internal set{if(_updateRevision==value)return;_updateRevision=value;InvalidateProjection();}}

    internal void AddChild(Net7AuxDataProperty child)
    {
        child.Parent = this;
        child.ChildIndex = _children.Count;
        _children.Add(child);
    }

    internal void ResetMaterializedState()
    {
        State = 0;
        Value = Net7AuxDataConstructorDefaults.Create(Path, Kind);
        foreach (Net7AuxDataProperty child in _children)
            child.ResetMaterializedState();
    }
}

/// <summary>
/// Constructor defaults used by the retail rebuild path for the two
/// long-lived local roots. RebuildLocalPlayerAuxDataDefaults constructs a
/// pristine temporary LocalPlayer/PlayerVar tree, copies those values into the
/// existing roots, then recursively state-zeros them. The object roots are not
/// part of that rebuild and intentionally keep the existing null-reset fallback.
/// </summary>
internal static class Net7AuxDataConstructorDefaults
{
    public static object? Create(string path, Net7AuxDataValueKind kind)
    {
        bool localPlayer = path.Equals("LocalPlayerAuxData", StringComparison.Ordinal)
            || path.StartsWith("LocalPlayerAuxData.", StringComparison.Ordinal);
        bool playerVar0 = path.Equals("PlayerVar[0]", StringComparison.Ordinal)
            || path.StartsWith("PlayerVar[0].", StringComparison.Ordinal);

        if (!localPlayer && !playerVar0)
            return null;
        if (kind == Net7AuxDataValueKind.Aggregate)
            return null;

        // Current InventoryItem constructor defaults. These apply to every
        // ordinary item embedded in the LocalPlayer inventories and PlayerVar
        // Cargo/Equipped/Ammo/Hull/PlayerTrade collections.
        if (path.EndsWith(".ItemTemplateID", StringComparison.Ordinal))
            return -1;
        if (path.EndsWith(".Price", StringComparison.Ordinal))
            return ulong.MaxValue;
        if (path.EndsWith(".Structure", StringComparison.Ordinal)
            || path.EndsWith(".Quality", StringComparison.Ordinal))
            return -1.0f;

        // LocalPlayer scalar constructor defaults that are deliberately not the
        // zero value of their physical wire type.
        if (path is "LocalPlayerAuxData.MusicID" or "LocalPlayerAuxData.PIPAvatarID")
            return -1;

        if (path.StartsWith("LocalPlayerAuxData.Missions.", StringComparison.Ordinal))
        {
            if (path.EndsWith(".ID", StringComparison.Ordinal))
                return -1;
            if (path.EndsWith(".IsTimed", StringComparison.Ordinal)
                && !path.Contains(".Stages.", StringComparison.Ordinal))
                return (byte)1;
            if (path.EndsWith(".IsForfeitable", StringComparison.Ordinal))
                return (byte)1;
            if (path.EndsWith(".StageCount", StringComparison.Ordinal)
                || path.EndsWith(".Stage", StringComparison.Ordinal))
                return 1;
        }

        if (path.StartsWith("LocalPlayerAuxData.GroupInfo.", StringComparison.Ordinal))
        {
            if (path.EndsWith(".LookingForGroup", StringComparison.Ordinal)
                || path.EndsWith(".AllowGroupInvite", StringComparison.Ordinal)
                || path.EndsWith(".ShowNonCombatActivities", StringComparison.Ordinal)
                || path.EndsWith(".ForceAutoSplit", StringComparison.Ordinal)
                || path.EndsWith(".RestrictedLootingRights", StringComparison.Ordinal)
                || path.EndsWith(".AutoReleaseLootingRestrictions", StringComparison.Ordinal))
                return (byte)1;
            if (path.EndsWith(".Position", StringComparison.Ordinal))
                return -1;
        }

        // The shared PlayerVar inventory constructor initializes every mount to
        // {-1,-1,-1}. Extended equipped items additionally start with Validity
        // raw value 0x00000100.
        if (playerVar0
            && path.Contains(".Inventory.Mounts.", StringComparison.Ordinal)
            && kind == Net7AuxDataValueKind.Raw12)
            return Enumerable.Repeat((byte)0xFF, 12).ToArray();

        if (playerVar0
            && path.Contains(".Inventory.Equipped.", StringComparison.Ordinal)
            && path.EndsWith(".Effects.Validity", StringComparison.Ordinal))
            return new byte[] { 0x00, 0x01, 0x00, 0x00 };

        return kind switch
        {
            Net7AuxDataValueKind.UInt64LittleEndian => 0UL,
            Net7AuxDataValueKind.Int32LittleEndian => 0,
            Net7AuxDataValueKind.Float32LittleEndian => 0.0f,
            Net7AuxDataValueKind.LengthPrefixedString => new Net7AuxDataString(Array.Empty<byte>()),
            Net7AuxDataValueKind.BooleanByte => (byte)0,
            Net7AuxDataValueKind.Raw4 => new byte[4],
            Net7AuxDataValueKind.Raw8TwoUInt32LittleEndian => new byte[8],
            Net7AuxDataValueKind.Time32LittleEndianRelative => new Net7AuxDataTime(0),
            Net7AuxDataValueKind.Raw12 => new byte[12],
            Net7AuxDataValueKind.Raw16 => new byte[16],
            Net7AuxDataValueKind.Raw20 => new byte[20],
            Net7AuxDataValueKind.Aggregate => null,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }
}

public sealed class Net7AuxDataSchema
{
    private readonly ReadOnlyCollection<Net7AuxDataProperty> _properties;

    internal Net7AuxDataSchema(string name, List<Net7AuxDataProperty> properties)
    {
        if (properties.Count == 0 || properties[0].Id != 0)
            throw new InvalidDataException($"AuxData schema '{name}' does not begin with root PropertyId 0.");
        Name = name;
        _properties = properties.AsReadOnly();
        Root = properties[0];
    }

    public string Name { get; }
    public Net7AuxDataProperty Root { get; }
    public IReadOnlyList<Net7AuxDataProperty> Properties => _properties;

    public Net7AuxDataProperty Resolve(int propertyId)
    {
        if ((uint)propertyId >= (uint)_properties.Count)
            throw new InvalidDataException($"AuxData schema '{Name}' has no PropertyId {propertyId}.");
        return _properties[propertyId];
    }

    public void ResetMaterializedState() => Root.ResetMaterializedState();
}

public sealed class Net7AuxDataSchemaCatalog
{
    private const string ResourcePrefix = "EnbNext.Net7.World.Schemas.";
    private readonly Lazy<Net7AuxDataSchema> _localPlayer;
    private readonly Lazy<Net7AuxDataSchema> _playerVar0;
    private readonly Lazy<Net7AuxDataSchema> _ship;
    private readonly Lazy<Net7AuxDataSchema> _planet;
    private readonly Lazy<Net7AuxDataSchema> _component;
    private readonly Lazy<Net7AuxDataSchema> _light;
    private readonly Lazy<Net7AuxDataSchema> _stargate;
    private readonly Lazy<Net7AuxDataSchema> _starbase;
    private readonly Lazy<Net7AuxDataSchema> _hulk;
    private readonly Lazy<Net7AuxDataSchema> _decoration;
    private readonly Lazy<Net7AuxDataSchema> _harvestable;
    private readonly Lazy<Net7AuxDataSchema> _manufacturingLab;

    public Net7AuxDataSchemaCatalog()
    {
        _localPlayer = new(() => LoadEmbedded("LocalPlayer.csv", "LocalPlayerAuxData", 5596));
        _playerVar0 = new(() => LoadEmbedded("PlayerVar0.csv", "PlayerVar[0]", 1176));
        _ship = new(() => LoadEmbedded("Ship.csv", "ShipAuxData", 1933));
        _planet = new(Net7ObjectAuxDataSchemas.CreatePlanet);
        _component = new(Net7ObjectAuxDataSchemas.CreateComponent);
        _light = new(Net7ObjectAuxDataSchemas.CreateLight);
        _stargate = new(Net7ObjectAuxDataSchemas.CreateStargate);
        _starbase = new(Net7ObjectAuxDataSchemas.CreateStarbase);
        _hulk = new(() => LoadEmbedded("Hulk.csv", "HulkAuxData", 703));
        _decoration = new(Net7ObjectAuxDataSchemas.CreateDecoration);
        _harvestable = new(Net7ObjectAuxDataSchemas.CreateHarvestable);
        _manufacturingLab = new(Net7ObjectAuxDataSchemas.CreateManufacturingLab);
    }

    public Net7AuxDataSchema LocalPlayer => _localPlayer.Value;
    public Net7AuxDataSchema PlayerVar0 => _playerVar0.Value;
    public Net7AuxDataSchema Ship => _ship.Value;
    public Net7AuxDataSchema Planet => _planet.Value;
    public Net7AuxDataSchema Component => _component.Value;
    public Net7AuxDataSchema Light => _light.Value;
    public Net7AuxDataSchema Stargate => _stargate.Value;
    public Net7AuxDataSchema Starbase => _starbase.Value;
    public Net7AuxDataSchema Hulk => _hulk.Value;
    public Net7AuxDataSchema Decoration => _decoration.Value;
    public Net7AuxDataSchema Harvestable => _harvestable.Value;
    public Net7AuxDataSchema ManufacturingLab => _manufacturingLab.Value;

    public Net7AuxDataSchema? CreateObjectSchema(byte objectType)
    {
        // Current retail CreateObjectAuxDataByType @ 0x006F6000 is the
        // controlling factory. The complete supported family must be kept
        // together here so an already-proven object type cannot silently fall
        // through to a null schema during sector materialization.
        return objectType switch
        {
            0 or 1 => Clone(Ship),
            3 => Clone(Planet),
            4 => Clone(Component),
            8 => Clone(Light),
            0x0B => Clone(Stargate),
            0x0C => Clone(Starbase),
            0x19 => Clone(Hulk),
            0x22 or 0x25 or 0x29 => Clone(Decoration),
            0x26 => Clone(Harvestable),
            0x27 => Clone(ManufacturingLab),
            _ => null,
        };
    }

    public static Net7AuxDataSchema Clone(Net7AuxDataSchema source)
    {
        ArgumentNullException.ThrowIfNull(source);
        List<Net7AuxDataProperty> copy = new(source.Properties.Count);
        foreach (Net7AuxDataProperty property in source.Properties)
            copy.Add(new Net7AuxDataProperty(property.Id, property.Path, property.Kind, property.DeclaredChildCount));
        RebuildTopology(copy, source.Name);
        return new Net7AuxDataSchema(source.Name, copy);
    }

    private static Net7AuxDataSchema LoadEmbedded(string fileName, string schemaName, int expectedCount)
    {
        Assembly assembly = typeof(Net7AuxDataSchemaCatalog).Assembly;
        string resourceName = ResourcePrefix + fileName;
        using Stream stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Missing embedded AuxData schema resource '{resourceName}'.");
        using var reader = new StreamReader(stream);

        string? header = reader.ReadLine();
        if (!string.Equals(header, "property_id,path,kind,child_count", StringComparison.Ordinal))
            throw new InvalidDataException($"Unexpected header in embedded AuxData schema '{resourceName}'.");

        List<Net7AuxDataProperty> properties = new(expectedCount);
        string? line;
        int expectedId = 0;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0)
                continue;
            string[] columns = line.Split(',', 4);
            if (columns.Length != 4 || !int.TryParse(columns[0], out int id) || !int.TryParse(columns[3], out int childCount))
                throw new InvalidDataException($"Malformed row in embedded AuxData schema '{resourceName}': {line}");
            if (id != expectedId)
                throw new InvalidDataException($"AuxData schema '{resourceName}' expected PropertyId {expectedId}, found {id}.");
            properties.Add(new Net7AuxDataProperty(id, columns[1], ParseKind(columns[2]), childCount));
            expectedId++;
        }

        if (properties.Count != expectedCount)
            throw new InvalidDataException($"AuxData schema '{resourceName}' contains {properties.Count} properties; expected {expectedCount}.");

        RebuildTopology(properties, schemaName);
        return new Net7AuxDataSchema(schemaName, properties);
    }

    private static void RebuildTopology(List<Net7AuxDataProperty> properties, string schemaName)
    {
        var stack = new Stack<(Net7AuxDataProperty Property, int RemainingChildren)>();
        foreach (Net7AuxDataProperty property in properties)
        {
            while (stack.Count > 0 && stack.Peek().RemainingChildren == 0)
                stack.Pop();

            if (property.Id != 0)
            {
                if (stack.Count == 0)
                    throw new InvalidDataException($"AuxData schema '{schemaName}' lost its parent before PropertyId {property.Id}.");
                (Net7AuxDataProperty parent, int remaining) = stack.Pop();
                parent.AddChild(property);
                stack.Push((parent, remaining - 1));
            }

            if (property.DeclaredChildCount > 0)
            {
                if (property.Kind != Net7AuxDataValueKind.Aggregate)
                    throw new InvalidDataException($"AuxData PropertyId {property.Id} declares children but is not aggregate.");
                stack.Push((property, property.DeclaredChildCount));
            }
        }

        while (stack.Count > 0 && stack.Peek().RemainingChildren == 0)
            stack.Pop();
        if (stack.Count != 0)
            throw new InvalidDataException($"AuxData schema '{schemaName}' ended before all declared children were present.");
    }

    private static Net7AuxDataValueKind ParseKind(string kind)
        => kind switch
        {
            "aggregate" => Net7AuxDataValueKind.Aggregate,
            "uint64le" => Net7AuxDataValueKind.UInt64LittleEndian,
            "int32le" => Net7AuxDataValueKind.Int32LittleEndian,
            "f32le" => Net7AuxDataValueKind.Float32LittleEndian,
            "lp_string_u16le" => Net7AuxDataValueKind.LengthPrefixedString,
            "bool_u8" => Net7AuxDataValueKind.BooleanByte,
            "raw4" => Net7AuxDataValueKind.Raw4,
            "raw8_two_u32le" => Net7AuxDataValueKind.Raw8TwoUInt32LittleEndian,
            "time_u32le_wire_i64_internal" => Net7AuxDataValueKind.Time32LittleEndianRelative,
            "raw12" => Net7AuxDataValueKind.Raw12,
            "raw16" => Net7AuxDataValueKind.Raw16,
            "raw20" => Net7AuxDataValueKind.Raw20,
            _ => throw new InvalidDataException($"Unsupported WORLD-11 AuxData kind '{kind}'."),
        };
}
