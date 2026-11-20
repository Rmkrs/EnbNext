using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbShadowTargetAllocationTests
{
    [Fact]
    public void SourceRetainsBlobOnlyWhenTheSingleAllocationFails()
    {
        var supported = Enum.GetValues<EnbCurrentRetailShadowTargetFormat>().ToHashSet();
        object target = new(), blob = new();
        int allocations = 0, loads = 0;
        var captured = EnbCurrentRetailShadowTargetAllocation.CreateSource(supported,
            _ => { allocations++; return target; }, () => { loads++; return blob; });
        Assert.Same(target, captured.CaptureTarget);
        Assert.Null(captured.Blob);
        Assert.Equal(0, loads);
        var failed = EnbCurrentRetailShadowTargetAllocation.CreateSource<object, object>(supported,
            _ => { allocations++; return null; }, () => { loads++; return blob; });
        Assert.Null(failed.CaptureTarget);
        Assert.Same(blob, failed.Blob);
        Assert.Equal(2, allocations);
        Assert.Equal(1, loads);
        var missing = EnbCurrentRetailShadowTargetAllocation.CreateSource<object, object>(supported,
            _ => null, () => null);
        Assert.Null(missing.CaptureTarget);
        Assert.Null(missing.Blob);
    }

    [Fact]
    public void AllocationFailureDoesNotTryLowerPriorityFormats()
    {
        var calls = new List<EnbCurrentRetailShadowTargetFormat>();
        var supported = Enum.GetValues<EnbCurrentRetailShadowTargetFormat>().ToHashSet();
        Assert.Null(EnbCurrentRetailShadowTargetAllocation.Create<object>(supported, format =>
        {
            calls.Add(format);
            return null;
        }));
        Assert.Equal([EnbCurrentRetailShadowTargetFormat.R3G3B2], calls);
    }

    [Fact]
    public void MissingCapabilitiesSelectTheNextNativeFormatThenDisplay()
    {
        var supported = Enum.GetValues<EnbCurrentRetailShadowTargetFormat>().ToHashSet();
        foreach (var expected in new[] { EnbCurrentRetailShadowTargetFormat.R3G3B2,
            EnbCurrentRetailShadowTargetFormat.R5G6B5, EnbCurrentRetailShadowTargetFormat.A4R4G4B4,
            EnbCurrentRetailShadowTargetFormat.X1R5G5B5, EnbCurrentRetailShadowTargetFormat.Display })
        {
            var resource = new object();
            int allocations = 0;
            Assert.Same(resource, EnbCurrentRetailShadowTargetAllocation.Create(supported, format =>
            {
                allocations++;
                Assert.Equal(expected, format);
                return resource;
            }));
            Assert.Equal(1, allocations);
            supported.Remove(expected);
        }
        Assert.NotNull(EnbCurrentRetailShadowTargetAllocation.Create(new HashSet<EnbCurrentRetailShadowTargetFormat>(), format =>
        {
            Assert.Equal(EnbCurrentRetailShadowTargetFormat.Display, format);
            return new object();
        }));
    }
}
