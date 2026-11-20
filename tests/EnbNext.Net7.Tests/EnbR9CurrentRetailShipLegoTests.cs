using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

[Collection(LoadSensitiveCollection.Name)]
public sealed class EnbR9CurrentRetailShipLegoTests
{
    [Fact]
    public void FactoryAttachInverseRetainsItsLifetimeAndInvalidatesOnScaleOrHierarchyReplacement()
    {
        var hierarchy=Hierarchy("CHILD",Pivot(0,"ROOT",uint.MaxValue,Vector3.Zero,Quaternion.Identity),
            Pivot(1,"ATTACH",0,new(17,-41,8),Quaternion.CreateFromYawPitchRoll(.37f,-.83f,1.21f)));
        foreach(float scale in new[]{1f,1f,2f,0f,-0f,-2f,float.NaN,float.PositiveInfinity,1f})
        {
            bool expected=EnbCurrentRetailShipLegoRuntime.TryResolveChildAttachInverseLiteral(hierarchy,scale,out var literal,out bool fallback,out var error);
            bool actual=EnbCurrentRetailShipLegoRuntime.TryResolveChildAttachInverse(hierarchy,scale,out var retained,out bool retainedFallback,out var retainedError);
            Assert.Equal(expected,actual);Assert.Equal(literal,retained);Assert.Equal(fallback,retainedFallback);Assert.Equal(error,retainedError);
        }
        long before=GC.GetAllocatedBytesForCurrentThread();
        for(int n=0;n<128;n++)EnbCurrentRetailShipLegoRuntime.TryResolveChildAttachInverse(hierarchy,1,out _,out _,out _);
        Assert.Equal(0,GC.GetAllocatedBytesForCurrentThread()-before);
        var changed=Hierarchy("CHILD",Pivot(0,"ROOT",uint.MaxValue,Vector3.Zero,Quaternion.Identity),
            Pivot(1,"ATTACH",0,new(100,0,0),Quaternion.Identity));
        Assert.True(EnbCurrentRetailShipLegoRuntime.TryResolveChildAttachInverse(changed,1,out var next,out _,out _));
        Assert.Equal(-100,next.M41);
    }

    [Fact]
    public void FixedVisualQueriesSharePoseWithoutChangingMissingAndPivotZeroSemantics()
    {
        var hierarchy=Hierarchy("SHIP",Pivot(0,"ROOT",uint.MaxValue,Vector3.Zero,Quaternion.Identity),
            Pivot(1,"~FL",0,new(1,2,3),Quaternion.Identity),Pivot(2,"~FR",0,new(-1,2,3),Quaternion.Identity));
        var world=Matrix4x4.CreateRotationZ(.3f)*Matrix4x4.CreateTranslation(10,20,30);
        Assert.True(EnbCurrentRetailShipLegoRuntime.TryEvaluateScaledRigidHierarchy(hierarchy,world,3,out var pose,out var error),error);
        foreach(var name in new[]{"~FL","~FR","ROOT","absent"})
        {
            Assert.True(EnbCurrentRetailShipFixedVisualRuntime.TryResolveBoneWorld(hierarchy,world,3,name,out var literal,out bool found,out error),error);
            Assert.True(EnbCurrentRetailShipFixedVisualRuntime.TryResolveBoneWorld(hierarchy,pose,name,out var retained,out bool retainedFound,out error),error);
            Assert.Equal(found,retainedFound);Assert.Equal(literal,retained);
        }
    }

    [Fact]
    public void RoundedAttachRotationRetainsRetailTransposedBasis()
    {
        var hierarchy=Hierarchy("CHILD",
            Pivot(0,"ROOT",uint.MaxValue,Vector3.Zero,Quaternion.Identity),
            Pivot(1,"ATTACH",0,new Vector3(17.3f,-41.2f,8.7f),Quaternion.CreateFromYawPitchRoll(.37f,-.83f,1.21f)));
        Assert.True(EnbCurrentRetailShipLegoRuntime.TryEvaluateScaledRigidHierarchy(hierarchy,
            Matrix4x4.Identity,1,out var pose,out var diagnostic),diagnostic);
        Assert.True(EnbCurrentRetailShipLegoRuntime.TryResolveChildAttachInverse(hierarchy,1,
            out var inverse,out var fallback,out diagnostic),diagnostic);
        Assert.False(fallback);
        var attach=pose[1];
        //72A7C0 ->8C2BD0 copies these nine words verbatim in transposed order.
        Assert.Equal(new[]{attach.M11,attach.M21,attach.M31,attach.M12,attach.M22,attach.M32,attach.M13,attach.M23,attach.M33},
            new[]{inverse.M11,inverse.M12,inverse.M13,inverse.M21,inverse.M22,inverse.M23,inverse.M31,inverse.M32,inverse.M33});
        Assert.True(Matrix4x4.Invert(attach,out var general));
        Assert.NotEqual(general,inverse);
    }
    [Fact]
    public void BaseAssetScaleChangesHierarchyTranslationsWithoutScalingPivotBasis()
    {
        EnbW3dRenderHierarchy hierarchy = Hierarchy(
            "SHIP",
            Pivot(0, "ROOT", uint.MaxValue, Vector3.Zero, Quaternion.Identity),
            Pivot(1, "MOUNT", 0, new Vector3(2, 3, 4), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f)));

        Assert.True(EnbCurrentRetailShipLegoRuntime.TryEvaluateScaledRigidHierarchy(
            hierarchy,
            Matrix4x4.Identity,
            3f,
            out Matrix4x4[] pose,
            out string? diagnostic), diagnostic);

        Matrix4x4 mount = pose[1];
        AssertVectorClose(new Vector3(6, 9, 12), Translation(mount));
        Assert.Equal(1f, RowLength(mount, 1), 5);
        Assert.Equal(1f, RowLength(mount, 2), 5);
        Assert.Equal(1f, RowLength(mount, 3), 5);
    }

    [Fact]
    public void ScaledParentMountDoesNotRescaleChildGeometryOrItsAttachInverse()
    {
        EnbW3dRenderHierarchy parent = Hierarchy(
            "PARENT",
            Pivot(0, "ROOT", uint.MaxValue, Vector3.Zero, Quaternion.Identity),
            Pivot(1, "HARDPOINT", 0, new Vector3(10, 0, 0), Quaternion.Identity),
            Pivot(2, "DESCENDANT", 1, new Vector3(0, 4, 0), Quaternion.Identity),
            Pivot(3, "SIBLING", 0, new Vector3(0, 0, 6), Quaternion.Identity));
        EnbW3dRenderHierarchy child = Hierarchy(
            "CHILD",
            Pivot(0, "ROOT", uint.MaxValue, Vector3.Zero, Quaternion.Identity),
            Pivot(1, "ATTACH", 0, new Vector3(0, 5, 0), Quaternion.Identity));
        EnbClientBaseAssetMount mount = ResolveMount(100, "HARDPOINT", boneIndex: 1);

        Assert.True(EnbCurrentRetailShipLegoRuntime.TryCreateParentHierarchyOverride(
            mount,
            child,
            childCreationScale: EnbCurrentRetailShipLegoRuntime.LegoControllerCreationScale,
            out EnbCurrentRetailShipLegoRuntime.HierarchyPivotOverride hierarchyOverride,
            out bool usedFallback,
            out string? diagnostic), diagnostic);
        Assert.False(usedFallback);

        Assert.True(EnbCurrentRetailShipLegoRuntime.TryEvaluateScaledRigidHierarchy(
            parent,
            Matrix4x4.Identity,
            baseAssetScale: 2f,
            animations: null,
            hierarchyOverrides: [hierarchyOverride],
            out Matrix4x4[] parentPose,
            out diagnostic), diagnostic);

        // The child RenderObject receives the evaluated parent state record as
        // its own transform. The override also affects descendants of that
        // record, while an unrelated sibling remains on the original pose.
        Matrix4x4 childRenderObjectTransform = parentPose[1];
        AssertVectorClose(new Vector3(20, -5, 0), Translation(childRenderObjectTransform));
        AssertVectorClose(new Vector3(20, 3, 0), Translation(parentPose[2]));
        AssertVectorClose(new Vector3(0, 0, 12), Translation(parentPose[3]));

        Matrix4x4 childDrawWorld = EnbCurrentRetailShipLegoRuntime.CreateAttachedChildDrawWorld(
            childRenderObjectTransform);
        Assert.Equal(1f, RowLength(childDrawWorld, 1), 5);
        Assert.Equal(1f, RowLength(childDrawWorld, 2), 5);
        Assert.Equal(1f, RowLength(childDrawWorld, 3), 5);

        Assert.True(EnbCurrentRetailShipLegoRuntime.TryEvaluateScaledRigidHierarchy(
            child,
            childRenderObjectTransform,
            EnbCurrentRetailShipLegoRuntime.LegoControllerCreationScale,
            out Matrix4x4[] childPose,
            out diagnostic), diagnostic);
        // Child ATTACH returns exactly to the scaled parent's hardpoint.
        AssertVectorClose(new Vector3(20, 0, 0), Translation(childPose[1]));
    }

    [Fact]
    public void MissingAttachNameUsesCurrentRetailPivotZeroFallback()
    {
        EnbW3dRenderHierarchy parent = Hierarchy(
            "PARENT",
            Pivot(0, "ROOT", uint.MaxValue, Vector3.Zero, Quaternion.Identity),
            Pivot(1, "HARDPOINT", 0, new Vector3(7, 8, 9), Quaternion.Identity));
        EnbW3dRenderHierarchy child = Hierarchy(
            "CHILD",
            Pivot(0, "ROOT", uint.MaxValue, Vector3.Zero, Quaternion.Identity),
            Pivot(1, "NOT_ATTACH", 0, new Vector3(100, 0, 0), Quaternion.Identity));
        EnbClientBaseAssetMount mount = ResolveMount(100, "HARDPOINT", boneIndex: 1);

        Assert.True(EnbCurrentRetailShipLegoRuntime.TryCreateParentHierarchyOverride(
            mount,
            child,
            childCreationScale: EnbCurrentRetailShipLegoRuntime.LegoControllerCreationScale,
            out EnbCurrentRetailShipLegoRuntime.HierarchyPivotOverride hierarchyOverride,
            out bool usedFallback,
            out string? diagnostic), diagnostic);

        Assert.True(usedFallback);
        Assert.True(EnbCurrentRetailShipLegoRuntime.TryResolveParentMountWorld(
            parent,
            Matrix4x4.Identity,
            parentScale: 1f,
            mount,
            animations: null,
            hierarchyOverrides: [hierarchyOverride],
            out Matrix4x4 childRenderObjectTransform,
            out diagnostic), diagnostic);
        AssertVectorClose(new Vector3(7, 8, 9), Translation(childRenderObjectTransform));
    }

    [Fact]
    public void ModeZeroOverrideRunsAfterAnimationAndBeforeDescendantEvaluation()
    {
        EnbW3dRenderHierarchy hierarchy = Hierarchy(
            "SHIP",
            Pivot(0, "ROOT", uint.MaxValue, Vector3.Zero, Quaternion.Identity),
            Pivot(1, "MOUNT", 0, new Vector3(2, 0, 0), Quaternion.Identity),
            Pivot(2, "TIP", 1, new Vector3(0, 3, 0), Quaternion.Identity));
        EnbW3dRenderHierarchyAnimation animation = TranslationAnimation("LIVE", "SHIP", 4f);
        EnbW3dResolvedWeightedAnimationSample[] animations = [new(animation, 0f, 1f)];
        var hierarchyOverride = new EnbCurrentRetailShipLegoRuntime.HierarchyPivotOverride(
            PivotIndex: 1,
            Transform: Matrix4x4.CreateTranslation(0, 10, 0));

        Assert.True(EnbCurrentRetailShipLegoRuntime.TryEvaluateScaledRigidHierarchy(
            hierarchy,
            Matrix4x4.CreateTranslation(100, 0, 0),
            baseAssetScale: 3f,
            animations,
            hierarchyOverrides: [hierarchyOverride],
            out Matrix4x4[] pose,
            out string? diagnostic), diagnostic);

        //0098A0A0 scales authored X (2*3) and the stored animation scalar;
        //00989BC0 therefore adds HAnim X (4*3), then the record
        // override adds Y=10. The child TIP consumes that overridden record.
        AssertVectorClose(new Vector3(118, 10, 0), Translation(pose[1]));
        AssertVectorClose(new Vector3(118, 19, 0), Translation(pose[2]));
    }

    [Fact]
    public void HierarchyNameLookupIsCaseInsensitive()
    {
        EnbW3dRenderHierarchy hierarchy = Hierarchy(
            "CHILD",
            Pivot(0, "ROOT", uint.MaxValue, Vector3.Zero, Quaternion.Identity),
            Pivot(1, "Attach", 0, Vector3.One, Quaternion.Identity));

        Assert.True(EnbCurrentRetailShipLegoRuntime.TryResolveRetailHierarchyPivot(
            hierarchy,
            "ATTACH",
            out int pivot,
            out bool fallback,
            out string? diagnostic), diagnostic);
        Assert.Equal(1, pivot);
        Assert.False(fallback);
    }

    [Fact]
    public void HierarchyNameLookupUsesFirstDuplicateAndZeroForMissingName()
    {
        var hierarchy = Hierarchy("CHILD",
            Pivot(0, "ROOT", uint.MaxValue, Vector3.Zero, Quaternion.Identity),
            Pivot(1, "Emitter", 0, Vector3.One, Quaternion.Identity),
            Pivot(2, "EMITTER", 0, new Vector3(100), Quaternion.Identity));
        Assert.True(EnbCurrentRetailShipLegoRuntime.TryResolveRetailHierarchyPivot(
            hierarchy, "emitter", out int pivot, out bool fallback, out _));
        Assert.Equal(1, pivot);
        Assert.False(fallback);
        Assert.True(EnbCurrentRetailShipLegoRuntime.TryResolveRetailHierarchyPivot(
            hierarchy, "absent", out pivot, out fallback, out _));
        Assert.Equal(0, pivot);
        Assert.True(fallback);
    }

    [Fact]
    public void CbassetBoneIndexOutsideResolvedHierarchyFailsClosed()
    {
        EnbW3dRenderHierarchy parent = Hierarchy(
            "PARENT",
            Pivot(0, "ROOT", uint.MaxValue, Vector3.Zero, Quaternion.Identity),
            Pivot(1, "HARDPOINT", 0, Vector3.Zero, Quaternion.Identity));
        EnbClientBaseAssetMount mount = ResolveMount(100, "HARDPOINT", boneIndex: 9);

        Assert.False(EnbCurrentRetailShipLegoRuntime.TryResolveParentMountWorld(
            parent,
            Matrix4x4.Identity,
            1f,
            mount,
            out _,
            out string? diagnostic));
        Assert.Contains("BoneIndex 9", diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void AttachedChildDrawWorldPreservesRigidPlacementAndFactoryGeometry()
    {
        Matrix4x4 rigid = Matrix4x4.CreateFromAxisAngle(Vector3.UnitY, 0.4f) *
                          Matrix4x4.CreateTranslation(10, 20, 30);

        Matrix4x4 draw = EnbCurrentRetailShipLegoRuntime.CreateAttachedChildDrawWorld(
            rigid);

        AssertVectorClose(new Vector3(10, 20, 30), Translation(draw));
        Assert.Equal(1f, RowLength(draw, 1), 5);
        Assert.Equal(1f, RowLength(draw, 2), 5);
        Assert.Equal(1f, RowLength(draw, 3), 5);
    }

    [Fact]
    public void AnimatedRslParentUsesScaledAuthoredPivotAndCurrentHAnimPoseForAttachments()
    {
        EnbW3dRenderHierarchy hierarchy = Hierarchy(
            "SHIP",
            Pivot(0, "ROOT", uint.MaxValue, Vector3.Zero, Quaternion.Identity),
            Pivot(1, "HARDPOINT", 0, new Vector3(2, 0, 0), Quaternion.Identity));
        EnbW3dRenderHierarchyAnimation animation = TranslationAnimation("LIVE", "SHIP", 4f);
        EnbW3dResolvedWeightedAnimationSample[] animations = [new(animation, 0f, 1f)];
        EnbClientBaseAssetMount mount = ResolveMount(100, "HARDPOINT", boneIndex: 1);
        Matrix4x4 rigid = Matrix4x4.CreateTranslation(100, 0, 0);

        Assert.True(EnbCurrentRetailShipLegoRuntime.TryResolveParentMountWorld(
            hierarchy,
            rigid,
            parentScale: 3f,
            mount,
            animations,
            out Matrix4x4 mountWorld,
            out string? diagnostic), diagnostic);
        AssertVectorClose(new Vector3(118, 0, 0), Translation(mountWorld));

        Assert.True(EnbCurrentRetailShipFixedVisualRuntime.TryResolveBoneWorld(
            hierarchy,
            rigid,
            parentScale: 3f,
            "HARDPOINT",
            animations,
            out Matrix4x4 fixedWorld,
            out bool found,
            out diagnostic), diagnostic);
        Assert.True(found);
        AssertVectorClose(new Vector3(118, 0, 0), Translation(fixedWorld));
    }

    [Fact]
    public void FixedShipVisualSlotsMatchCurrentRetailAssetAndBoneMapping()
    {
        EnbCurrentRetailShipFixedVisualSpec[] specs = EnbCurrentRetailShipFixedVisualRuntime.Specs.ToArray();
        Assert.Equal(6, specs.Length);
        Assert.Collection(
            specs,
            static spec => Assert.Equal((0, 0x0618, "~FL"), (spec.Slot, spec.BaseAsset, spec.BoneName)),
            static spec => Assert.Equal((1, 0x0617, "~FR"), (spec.Slot, spec.BaseAsset, spec.BoneName)),
            static spec => Assert.Equal((2, 0x0618, "~BL"), (spec.Slot, spec.BaseAsset, spec.BoneName)),
            static spec => Assert.Equal((3, 0x0617, "~BR"), (spec.Slot, spec.BaseAsset, spec.BoneName)),
            static spec => Assert.Equal((4, 0x073F, "~FL"), (spec.Slot, spec.BaseAsset, spec.BoneName)),
            static spec => Assert.Equal((5, 0x073F, "~FR"), (spec.Slot, spec.BaseAsset, spec.BoneName)));
    }

    [Fact]
    public void FixedShipVisualBoneRequiresNonZeroExactHierarchyMatchAndUsesScaledPivotTranslation()
    {
        EnbW3dRenderHierarchy hierarchy = Hierarchy(
            "SHIP",
            Pivot(0, "ROOT", uint.MaxValue, Vector3.Zero, Quaternion.Identity),
            Pivot(1, "~FL", 0, new Vector3(2, 3, 4), Quaternion.Identity));
        Matrix4x4 rigid = Matrix4x4.CreateTranslation(10, 20, 30);

        Assert.True(EnbCurrentRetailShipFixedVisualRuntime.TryResolveBoneWorld(
            hierarchy, rigid, 2f, "~fl", out Matrix4x4 boneWorld, out bool found, out string? diagnostic), diagnostic);
        Assert.True(found);
        AssertVectorClose(new Vector3(14, 26, 38), Translation(boneWorld));

        Assert.True(EnbCurrentRetailShipFixedVisualRuntime.TryResolveBoneWorld(
            hierarchy, rigid, 2f, "~BR", out _, out found, out diagnostic), diagnostic);
        Assert.False(found);

        EnbW3dRenderHierarchy pivotZeroNamed = Hierarchy(
            "ODD",
            Pivot(0, "~FL", uint.MaxValue, Vector3.Zero, Quaternion.Identity));
        Assert.True(EnbCurrentRetailShipFixedVisualRuntime.TryResolveBoneWorld(
            pivotZeroNamed, Matrix4x4.Identity, 1f, "~FL", out _, out found, out diagnostic), diagnostic);
        Assert.False(found);
    }

    [Fact]
    public void FixedShipVisualSearchReadsTheOverriddenParentRecord()
    {
        EnbW3dRenderHierarchy hierarchy = Hierarchy(
            "SHIP",
            Pivot(0, "ROOT", uint.MaxValue, Vector3.Zero, Quaternion.Identity),
            Pivot(1, "~FL", 0, new Vector3(2, 0, 0), Quaternion.Identity));
        var hierarchyOverride = new EnbCurrentRetailShipLegoRuntime.HierarchyPivotOverride(
            PivotIndex: 1,
            Transform: Matrix4x4.CreateTranslation(0, 7, 0));

        Assert.True(EnbCurrentRetailShipFixedVisualRuntime.TryResolveBoneWorld(
            hierarchy,
            Matrix4x4.CreateTranslation(10, 0, 0),
            parentScale: 3f,
            "~FL",
            animations: null,
            hierarchyOverrides: [hierarchyOverride],
            out Matrix4x4 boneWorld,
            out bool found,
            out string? diagnostic), diagnostic);

        Assert.True(found);
        AssertVectorClose(new Vector3(16, 7, 0), Translation(boneWorld));
    }


    private static EnbW3dRenderHierarchyAnimation TranslationAnimation(
        string name,
        string hierarchyName,
        float x)
        => new(
            Ordinal: 0,
            Version: 0x0003_0000u,
            Name: name,
            HierarchyName: hierarchyName,
            NumFrames: 2,
            FrameRate: 30,
            HasLegacySyntheticRootChannelShift: false,
            Channels:
            [
                new EnbW3dRenderAnimationChannel(
                    FirstFrame: 0,
                    LastFrame: 1,
                    VectorLength: 1,
                    Flags: (ushort)EnbW3dAnimationChannelKind.TranslationX,
                    KnownKind: EnbW3dAnimationChannelKind.TranslationX,
                    SourcePivotIndex: 1,
                    PivotIndex: 1,
                    Data: [x, x]),
            ],
            BitChannels: Array.Empty<EnbW3dRenderAnimationBitChannel>());

    private static EnbClientBaseAssetMount ResolveMount(int baseAsset, string boneName, int boneIndex)
    {
        EnbClientBaseAssetMountCatalog catalog = EnbClientBaseAssetMountCatalog.ParseText(
            $"[BASE-{baseAsset}/Mount00]\nBoneName={boneName}\nBoneIndex={boneIndex}\n");
        Assert.True(catalog.TryResolveMount(baseAsset, boneName, out EnbClientBaseAssetMount mount));
        return mount;
    }

    private static EnbW3dRenderHierarchy Hierarchy(string name, params EnbW3dRenderPivot[] pivots)
        => new(
            Version: 0x00040001,
            Name: name,
            Center: Vector3.Zero,
            HasSyntheticRetailRoot: false,
            Pivots: pivots,
            PivotFixups: Array.Empty<EnbW3dRenderPivotFixup>());

    private static EnbW3dRenderPivot Pivot(
        int ordinal,
        string name,
        uint parent,
        Vector3 translation,
        Quaternion rotation)
        => new(
            Ordinal: ordinal,
            Name: name,
            ParentIndex: parent,
            Translation: translation,
            EulerAngles: Vector3.Zero,
            Rotation: rotation,
            LocalTransform: Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(translation));

    private static Vector3 Translation(Matrix4x4 value)
        => new(value.M41, value.M42, value.M43);

    private static float RowLength(Matrix4x4 value, int row)
        => row switch
        {
            1 => new Vector3(value.M11, value.M12, value.M13).Length(),
            2 => new Vector3(value.M21, value.M22, value.M23).Length(),
            3 => new Vector3(value.M31, value.M32, value.M33).Length(),
            _ => throw new ArgumentOutOfRangeException(nameof(row)),
        };

    private static void AssertVectorClose(Vector3 expected, Vector3 actual, float epsilon = 1e-4f)
    {
        Assert.InRange(MathF.Abs(expected.X - actual.X), 0f, epsilon);
        Assert.InRange(MathF.Abs(expected.Y - actual.Y), 0f, epsilon);
        Assert.InRange(MathF.Abs(expected.Z - actual.Z), 0f, epsilon);
    }
}
