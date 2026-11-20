using System.Reflection;
using EnbNext.Net7.Gameplay;

namespace EnbNext.Net7.Tests;

[Xunit.Trait("Category", "Heavy")] // decodes every installed launcher cdata item template
public sealed class EnbInstalledItemEffectFormatCorpusTests
{
    [Fact]
    public void EveryInstalledEffectValueFormatHasADefinedRetailResult()
    {
        // Retail 00833880 extracts "%value<spec>%" and 008335D0 calls the CRT formatter with
        // "%"+spec into a 32-byte stack buffer. The installed corpus includes "2.2", "2.0" and
        // "2.2F", which the VS2003 CRT formats as an empty field.
        string root = EnbNext.Installation.EnbInstallDiscovery.RequireLauncher();
        var catalog = Net7CDataCatalog.Decode(File.ReadAllBytes(Path.Combine(root, "bin", "cdata.dat")));
        var ids = ((System.Collections.IDictionary)typeof(Net7CDataCatalog)
            .GetField("_records", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(catalog)!).Keys.Cast<uint>().ToArray();
        var specs = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var problems = new List<string>();
        int templates = 0, entries = 0;
        foreach (uint id in ids)
        {
            if (!catalog.TryGetRecord(id, out var record) || record is null) continue;
            Net7ItemTemplate template = Net7ItemTemplateWire.Decode(record.Payload);
            templates++;
            foreach (var vector in new[] { template.Info.VectorA, template.Info.VectorB })
            foreach (var entry in vector.Entries)
            {
                entries++;
                Scan(id, entry.StringB.EffectiveText, entry.ListA);
                Scan(id, entry.StringC.EffectiveText, entry.ListB);
            }
        }
        void Scan(uint id, string text, IReadOnlyList<uint> values)
        {
            int start = text.IndexOf("%value", StringComparison.Ordinal), index = 0;
            while (start >= 0)
            {
                int end = text.IndexOf('%', start + 1);
                if (end < 0) { problems.Add($"{id}: unterminated %value in '{text}'"); return; }
                string spec = text[(start + 6)..end];
                specs[spec] = specs.GetValueOrDefault(spec) + 1;
                float value = index < values.Count ? BitConverter.UInt32BitsToSingle(values[index]) : 0;
                string? result = EnbNext.Assets.EnbRetailCrtFormat.Format("%" + spec, value, 32);
                if (result is null) problems.Add($"{id}: spec '{spec}' value {value} has no defined native result");
                index++; start = text.IndexOf("%value", end + 1, StringComparison.Ordinal);
            }
        }
        File.WriteAllLines(Path.Combine(Path.GetTempPath(), "enbnext-item-effect-formats.txt"),
            new[] { $"templates {templates} entries {entries}" }.Concat(specs.Select(p => $"'{p.Key}' {p.Value}")).Concat(problems));
        Assert.True(templates > 0);
        Assert.Empty(problems);
    }
}
