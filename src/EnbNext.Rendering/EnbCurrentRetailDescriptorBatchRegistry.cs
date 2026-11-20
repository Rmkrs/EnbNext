using System.Runtime.CompilerServices;

namespace EnbNext.Rendering;

/// <summary>
/// Retained descriptor selection within a container/pass (00978FD0).
/// Texture arguments are retained resource identities, never logical names.
/// Material state must already include native preprocessing and mapper ownership.
/// </summary>
public sealed class EnbCurrentRetailDescriptorBatchRegistry
{
    private sealed class Batch(object container, int pass,
        EnbCurrentRetailMaterialBatchIdentity? material, uint shader, object? texture0, object? texture1) : EnbCurrentRetailMeshCategory
    {
        public object Container { get; } = container;
        public int Pass { get; } = pass;
        public EnbCurrentRetailMaterialBatchIdentity? Material { get; } = material;
        public uint Shader { get; } = shader;
        public object? Texture0 { get; } = texture0;
        public object? Texture1 { get; } = texture1;
        public HashSet<object> Models { get; } = new(ReferenceEqualityComparer.Instance);
    }

    private readonly record struct Key(object Container, int Pass, EnbCurrentRetailMaterialBatchIdentity? Material,
        uint Shader, object? Texture0, object? Texture1);
    private sealed class KeyComparer : IEqualityComparer<Key>
    {
        public bool Equals(Key a, Key b) => ReferenceEquals(a.Container, b.Container) && a.Pass == b.Pass &&
            Equals(a.Material, b.Material) && a.Shader == b.Shader &&
            ReferenceEquals(a.Texture0, b.Texture0) && ReferenceEquals(a.Texture1, b.Texture1);
        public int GetHashCode(Key key) => HashCode.Combine(RuntimeHelpers.GetHashCode(key.Container), key.Pass,
            key.Material, key.Shader, key.Texture0 is null ? 0 : RuntimeHelpers.GetHashCode(key.Texture0),
            key.Texture1 is null ? 0 : RuntimeHelpers.GetHashCode(key.Texture1));
    }
    private readonly Dictionary<Key, Batch> _batches = new(new KeyComparer());
    private readonly Dictionary<object, HashSet<Batch>> _models = new(ReferenceEqualityComparer.Instance);

    public bool HasModelLinks(object model) => _models.ContainsKey(model);

    public object GetOrAdd(object model, object container, int pass,
        EnbCurrentRetailMaterialBatchIdentity? material, uint shader, object? texture0, object? texture1)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(container);
        var key = new Key(container, pass, material, shader, texture0, texture1);
        if (!_batches.TryGetValue(key, out var batch))
        {
            batch = new(container, pass, material, shader, texture0, texture1);
            _batches.Add(key, batch);
        }
        if (batch.Models.Add(model))
        {
            if (!_models.TryGetValue(model, out var links)) _models.Add(model, links = []);
            links.Add(batch);
        }
        return batch;
    }

    public void RemoveModel(object model)
    {
        if (!_models.Remove(model, out var links)) return;
        foreach (var batch in links)
        {
            batch.Models.Remove(model);
            // 00977830 immediately detaches the last-link batch from selection;
            // managed tokens already queued for a flush remain alive independently.
            if (batch.Models.Count == 0)
                _batches.Remove(new(batch.Container, batch.Pass, batch.Material, batch.Shader, batch.Texture0, batch.Texture1));
        }
    }

    public void RemoveContainer(object container)
    {
        foreach (var pair in _batches.Where(pair => ReferenceEquals(pair.Key.Container, container)).ToArray())
        {
            _batches.Remove(pair.Key);
            foreach (var model in pair.Value.Models)
                if (_models.TryGetValue(model, out var links))
                {
                    links.Remove(pair.Value);
                    if (links.Count == 0) _models.Remove(model);
                }
        }
    }

    public void Clear() { _batches.Clear(); _models.Clear(); }
}
