namespace EnbNext.Net7.World;

/// <summary>
/// Current retail object-AuxData factory schemas that are smaller than the
/// retained LocalPlayer/Ship/Hulk census resources. The topology and value
/// widths come from the retail factory
/// and constructor dossiers.
/// </summary>
internal static class Net7ObjectAuxDataSchemas
{
    public static Net7AuxDataSchema CreatePlanet()
        => Build("Planet", Aggregate("Planet", String("Name")), 2);

    public static Net7AuxDataSchema CreateComponent()
        => Build("Component", Aggregate("Component", String("Name")), 2);

    public static Net7AuxDataSchema CreateStargate()
        => Build("Stargate", Aggregate("Stargate", String("Name")), 2);

    public static Net7AuxDataSchema CreateStarbase()
        => Build("Starbase", Aggregate("Starbase", String("Name")), 2);

    public static Net7AuxDataSchema CreateLight()
        => Build(
            "Light",
            Aggregate(
                "Light",
                String("Name"),
                Float("Intensity"),
                Raw("DiffuseRGB", Net7AuxDataValueKind.Raw12),
                Raw("SpecularRGB", Net7AuxDataValueKind.Raw12),
                Raw("AmbientRGB", Net7AuxDataValueKind.Raw12),
                Raw("Attenuation", Net7AuxDataValueKind.Raw4),
                Raw("AttenuationFactors", Net7AuxDataValueKind.Raw12)),
            8);

    public static Net7AuxDataSchema CreateDecoration()
        => Build(
            "Decoration",
            Aggregate(
                "Decoration",
                String("Name"),
                Bool("AppearsInRadar"),
                Float("RadarRange")),
            4);

    public static Net7AuxDataSchema CreateHarvestable()
    {
        Node[] resources = Enumerable.Range(0, 40)
            .Select(index => InventoryItem(index.ToString()))
            .ToArray();

        return Build(
            "Harvestable",
            Aggregate(
                "Harvestable",
                String("Name"),
                Aggregate("HarvestableResources", resources),
                Float("PercentFull"),
                Int32("TechLevel")),
            445);
    }

    public static Net7AuxDataSchema CreateManufacturingLab()
    {
        Node[] manufactureTargets = Enumerable.Range(0, 2)
            .Select(index => InventoryItem(index.ToString()))
            .ToArray();
        Node[] manufactureComponents = Enumerable.Range(0, 6)
            .Select(index => InventoryItem(index.ToString()))
            .ToArray();
        Node[] manufactureOverride = [InventoryItem("0")];
        Node[] primaryCategories = Enumerable.Range(0, 2)
            .Select(index => PrimaryCategory(index.ToString()))
            .ToArray();
        Node[] knownFormula = Enumerable.Range(0, 500)
            .Select(index => Formula(index.ToString()))
            .ToArray();
        Node[] previousAttempts = Enumerable.Range(0, 15)
            .Select(index => Formula(index.ToString()))
            .ToArray();

        return Build(
            "ManufacturingLab",
            Aggregate(
                "ManufacturingLab",
                String("Name"),
                Int32("Mode"),
                Int32("Validity"),
                String("FailureMessage"),
                Int32("Difficulty"),
                Aggregate("ManufactureTarget", manufactureTargets),
                Aggregate("ManufactureComponents", manufactureComponents),
                Aggregate("ManufactureOverride", manufactureOverride),
                UInt64("NegotiatedCost"),
                UInt64("BaseCost"),
                Aggregate("PrimaryCategories", primaryCategories),
                Aggregate("KnownFormula", knownFormula),
                Aggregate("PreviousAttempts", previousAttempts),
                Int32("CurrentItemCategory"),
                Float("SuccessProbability"),
                Float("CriticalSuccessProbability"),
                Float("ExpectedQuality"),
                Float("MinimumQuality"),
                Float("MaximumQuality"),
                Int32("AdditionalIterations"),
                Int32("TechLevelFilterBitfield")),
            2427);
    }

    private static Node InventoryItem(string name)
        => Aggregate(
            name,
            Int32("ItemTemplateID"),
            Int32("StackCount"),
            UInt64("Price"),
            Float("AveCost"),
            Float("Structure"),
            Float("Quality"),
            String("InstanceInfo"),
            String("InstanceActivatedEffectInfo"),
            String("InstanceEquipEffectInfo"),
            String("BuilderName"));

    private static Node Formula(string name)
        => Aggregate(
            name,
            String("ItemName"),
            Int32("ItemID"),
            Int32("TechLevel"));

    private static Node PrimaryCategory(string name)
    {
        Node[] categories = Enumerable.Range(0, 5)
            .Select(index => Category(index.ToString()))
            .ToArray();
        return Aggregate(name, String("Name"), Aggregate("Categories", categories));
    }

    private static Node Category(string name)
    {
        Node[] subcategories = Enumerable.Range(0, 5)
            .Select(index => Subcategory(index.ToString()))
            .ToArray();
        // Retail ConstructManufacturingPrimaryCategoryEntryAuxData constructs
        // ID before Subcategories in memory, but registers children in the wire-
        // significant order Name, Subcategories, ID. Property IDs and
        // compressed child masks follow registration order, not member offset.
        return Aggregate(
            name,
            String("Name"),
            Aggregate("Subcategories", subcategories),
            Int32("ID"));
    }

    private static Node Subcategory(string name)
        => Aggregate(name, String("Name"), Int32("ID"), Bool("Visible"));

    private static Net7AuxDataSchema Build(string schemaName, Node root, int expectedCount)
    {
        List<Net7AuxDataProperty> properties = new(expectedCount);
        _ = Flatten(root, parentPath: null, properties);
        if (properties.Count != expectedCount)
        {
            throw new InvalidDataException(
                $"Generated AuxData schema '{schemaName}' contains {properties.Count} properties; expected {expectedCount}.");
        }
        return new Net7AuxDataSchema(schemaName, properties);
    }

    private static Net7AuxDataProperty Flatten(Node node, string? parentPath, List<Net7AuxDataProperty> properties)
    {
        string path = parentPath is null ? node.Name : $"{parentPath}.{node.Name}";
        var property = new Net7AuxDataProperty(properties.Count, path, node.Kind, node.Children.Length);
        properties.Add(property);
        foreach (Node child in node.Children)
            property.AddChild(Flatten(child, path, properties));
        return property;
    }

    private static Node Aggregate(string name, params Node[] children)
        => new(name, Net7AuxDataValueKind.Aggregate, children);

    private static Node UInt64(string name)
        => new(name, Net7AuxDataValueKind.UInt64LittleEndian, []);

    private static Node Int32(string name)
        => new(name, Net7AuxDataValueKind.Int32LittleEndian, []);

    private static Node Float(string name)
        => new(name, Net7AuxDataValueKind.Float32LittleEndian, []);

    private static Node String(string name)
        => new(name, Net7AuxDataValueKind.LengthPrefixedString, []);

    private static Node Bool(string name)
        => new(name, Net7AuxDataValueKind.BooleanByte, []);

    private static Node Raw(string name, Net7AuxDataValueKind kind)
        => new(name, kind, []);

    private sealed record Node(string Name, Net7AuxDataValueKind Kind, Node[] Children);
}
