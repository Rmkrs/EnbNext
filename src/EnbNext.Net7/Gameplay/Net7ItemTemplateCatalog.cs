namespace EnbNext.Net7.Gameplay;

/// <summary>Integrated EnbNext materialization equivalent of the retail item-definition registry.</summary>
public sealed class Net7ItemTemplateCatalog
{
    private readonly Dictionary<uint, Net7ItemTemplate> _items = [];

    public int Count => _items.Count;
    public IReadOnlyCollection<Net7ItemTemplate> Items => _items.Values;

    public bool TryGet(uint itemTemplateId, out Net7ItemTemplate? template)
        => _items.TryGetValue(itemTemplateId, out template);

    public Net7ItemTemplate? MaterializeCDataRecord(uint requestedRecordId, Net7CDataRecord? record)
    {
        if (record is null)
            return null;
        // Proxy 00404F30/0040F7D0 forwards the selected payload unchanged;
        // retail 008955C0/007371B0 keys the definition by its embedded ID.
        Net7ItemTemplate template = Net7ItemTemplateWire.Decode(record.Payload);
        return Materialize(template);
    }

    public Net7ItemTemplate Materialize(Net7ItemTemplate incoming)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        if (!_items.TryGetValue(incoming.ItemTemplateId, out Net7ItemTemplate? existing))
        {
            _items.Add(incoming.ItemTemplateId, incoming);
            return incoming;
        }

        // 007371B0 retains the first definition even when type/name differ.
        // Its assertions call 004E9410 (log and return), never reject the update.
        return existing;
    }
}
