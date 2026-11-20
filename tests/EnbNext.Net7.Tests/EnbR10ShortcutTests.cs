using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using EnbNext.Assets;
using EnbNext.Net7.Gameplay;
using EnbNext.Net7.World;
using EnbNext.Presentation;

namespace EnbNext.Net7.Tests;

public sealed class EnbR10ShortcutTests
{
    [Fact]
    public void ShortcutRefreshRevisionsFollowBothRetailBindingsAndIgnoreUnrelatedData()
    {
        var schemas=new Net7AuxDataSchemaCatalog();
        var saved=Saved(Section("Skills",Entry(0,0,0,0,"<52/><Cloak/><Advanced Cloak/>")));
        var codec=new Net7AuxDataCodec();
        EnbSceneShortcutSnapshot Snapshot()=>EnbSceneShortcutSnapshotBuilder.Build(saved,Skills(),schemas.Ship,schemas.LocalPlayer,null);
        void Update(Net7AuxDataSchema schema,string path,object value)
        {
            Set(schema,path,value);
            var property=schema.Properties.Single(p=>p.Path==path);
            codec.Apply(schema,codec.Encode(schema,[new(property.Id,Net7AuxDataOperationKind.ValueUpdate)]));
        }
        var before=Snapshot();
        Update(schemas.Ship,"ShipAuxData.Inventory.Equipped.0.StackCount",1);
        var equipped=Snapshot();
        Assert.True(equipped.EquipmentRevision.Equipped>before.EquipmentRevision.Equipped);
        Assert.Equal(before.EquipmentRevision.Ammo,equipped.EquipmentRevision.Ammo);
        Update(schemas.Ship,"ShipAuxData.Inventory.Ammo.0.StackCount",1);
        var ammo=Snapshot();
        Assert.True(ammo.EquipmentRevision.Ammo>equipped.EquipmentRevision.Ammo);
        Update(schemas.LocalPlayer,"LocalPlayerAuxData.RPGInfo.Skills.7.Level",1);
        var skill=Snapshot();
        Assert.True(skill.SkillRevision.Skills>ammo.SkillRevision.Skills);
        Update(schemas.LocalPlayer,"LocalPlayerAuxData.RPGInfo.SkillAbilities.3.Range",1000);
        var ability=Snapshot();
        Assert.True(ability.SkillRevision.Abilities>skill.SkillRevision.Abilities);
        Update(schemas.LocalPlayer,"LocalPlayerAuxData.RPGInfo.SkillPowerupAbilityNumber",3);
        Update(schemas.Ship,"ShipAuxData.Inventory.Cargo.0.StackCount",1);
        Assert.Equal(ability.SkillRevision,Snapshot().SkillRevision);
        Assert.Equal(ability.EquipmentRevision,Snapshot().EquipmentRevision);
        Assert.Equal((0UL,0UL),before.SkillRevision);
        Assert.Equal((0UL,0UL),before.EquipmentRevision);
    }

    [Fact]
    public void RequirementCaptureKeepsHiddenSkillsAndHonorsActualAuxDataStates()
    {
        var source=new Net7AuxDataSchemaCatalog().LocalPlayer;
        var destination=new Net7AuxDataSchemaCatalog().LocalPlayer;
        Set(source,"LocalPlayerAuxData.RPGInfo.Skills.7.Level",0);
        Set(source,"LocalPlayerAuxData.RPGInfo.CombatLevel",10);
        var properties=source.Properties.Where(p=>p.Path is "LocalPlayerAuxData.RPGInfo.Skills.7.Level" or "LocalPlayerAuxData.RPGInfo.CombatLevel").ToArray();
        var codec=new Net7AuxDataCodec();
        codec.Apply(destination,codec.Encode(source,properties.Select(p=>new Net7AuxDataOperation(p.Id,Net7AuxDataOperationKind.ValueUpdate))));
        var snapshot=EnbSceneInventorySnapshotBuilder.Build(null,destination,null,Skills());
        Assert.True(snapshot.RequirementState.RpgAvailable);
        Assert.True(snapshot.RequirementState.SkillsAvailable);
        Assert.Equal(0,snapshot.RequirementState.SkillLevels["Cloak"]);
        Assert.Equal(10,snapshot.RequirementState.Fields["CombatLevel"]);
        Assert.DoesNotContain(snapshot.Skills,s=>s.Name=="Cloak");
        var aggregate=destination.Properties.Single(p=>p.Path=="LocalPlayerAuxData.RPGInfo.Skills");
        codec.Apply(destination,codec.Encode(destination,[new(aggregate.Id,Net7AuxDataOperationKind.StateZero)]));
        var next=EnbSceneInventorySnapshotBuilder.Build(null,destination,null,Skills());
        Assert.False(next.RequirementState.SkillsAvailable);
        Assert.True(snapshot.RequirementState.SkillsAvailable); // Detached, not a live AuxData reference.
    }

    [Fact]
    public void SkillFeedbackUsesResolvedFamilyAndAbilityAndDetachesRetainedAuxData()
    {
        var schemas=new Net7AuxDataSchemaCatalog();
        var saved=Saved(Section("Skills",Entry(0,0,0,0,"<52/><Cloak/><Advanced Cloak/>")));
        Set(schemas.LocalPlayer,"LocalPlayerAuxData.RPGInfo.Skills.7.RecycleTime",750,0);
        Set(schemas.LocalPlayer,"LocalPlayerAuxData.RPGInfo.Skills.7.LastActivation",new Net7AuxDataTime(1250),0);
        Set(schemas.LocalPlayer,"LocalPlayerAuxData.RPGInfo.SkillAbilities.3.Validity",new byte[]{0x70,1,0,0},0);
        Set(schemas.LocalPlayer,"LocalPlayerAuxData.RPGInfo.SkillAbilities.3.Targets",new byte[]{4,0,0,0},0);
        Set(schemas.LocalPlayer,"LocalPlayerAuxData.RPGInfo.SkillAbilities.3.Range",4500,0);
        Set(schemas.LocalPlayer,"LocalPlayerAuxData.RPGInfo.SkillPowerupAbilityNumber",3);
        Set(schemas.LocalPlayer,"LocalPlayerAuxData.RPGInfo.SkillPowerupStart",new Net7AuxDataTime(1000));
        Set(schemas.LocalPlayer,"LocalPlayerAuxData.RPGInfo.SkillPowerupEnd",new Net7AuxDataTime(2000));
        EnbSceneShortcutSnapshot BuildCurrent()=>EnbSceneShortcutSnapshotBuilder.Build(saved,Skills(),null,schemas.LocalPlayer,null);
        var snapshot=BuildCurrent();
        Assert.Equal(new EnbSceneSkillFeedbackSnapshot(0x170,4,4500,750,1250,3,1000,2000),snapshot.Slots[0].SkillFeedback);
        Set(schemas.LocalPlayer,"LocalPlayerAuxData.RPGInfo.SkillPowerupAbilityNumber",3,0);
        Set(schemas.LocalPlayer,"LocalPlayerAuxData.RPGInfo.SkillPowerupStart",new Net7AuxDataTime(1000),0);
        Set(schemas.LocalPlayer,"LocalPlayerAuxData.RPGInfo.SkillPowerupEnd",new Net7AuxDataTime(2000),0);
        Assert.Equal(new EnbSceneSkillFeedbackSnapshot(0x170,4,4500,750,1250,0,0,0),BuildCurrent().Slots[0].SkillFeedback);
        Assert.Equal(3,snapshot.Slots[0].SkillFeedback!.PowerupAbility);
        var request=EnbShortcutSkillActivation.Capture(snapshot,snapshot.Slots[0],91)!;
        Assert.True(EnbShortcutSkillActivation.TryResolve(request,snapshot,91,out uint ability));
        Assert.Equal(3u,ability); // Visual invalidity is not a locally invented activation gate.
    }

    [Theory]
    [InlineData(EnbRetailShortcutSource.Skill)]
    [InlineData(EnbRetailShortcutSource.Equipment)]
    [InlineData(EnbRetailShortcutSource.Cargo)]
    public void MovingShortcutsReplacesDestinationAndRemovalSurvivesReload(EnbRetailShortcutSource kind)
    {
        const uint character=123;
        var empty=EnbRetailShortcuts.Resolve(Ini(""),character);
        var from=new EnbRetailShortcutAddress(0,0,0);var to=new EnbRetailShortcutAddress(1,1,2);
        var source=new EnbRetailShortcutEntry(kind,from,kind==EnbRetailShortcutSource.Skill?"<7/><Cloak/><Cloak/>":"<3/>",kind==EnbRetailShortcutSource.Skill?7:3,
            kind==EnbRetailShortcutSource.Skill?"Cloak":null,kind==EnbRetailShortcutSource.Skill?"Cloak":null);
        var occupied=new EnbRetailShortcutEntry(EnbRetailShortcutSource.Cargo,to,"<2/>",2,null,null);
        var saved=empty with {Sections=empty.Sections.Select(s=>s with {Entries=new[]{source,occupied}.Where(e=>e.Source==s.Source).ToImmutableArray()}).ToImmutableArray()};
        Assert.True(saved.TryMove(source,from,out var same));Assert.Same(saved,same);
        Assert.False(saved.TryMove(source,new(2,0,0),out _));
        Assert.False(saved.TryMove(source with {Key="changed"},to,out _));
        Assert.True(saved.TryMove(source,to,out var moved));
        var entry=Assert.Single(moved.Sections.SelectMany(s=>s.Entries));Assert.Equal(source with {Address=to},entry);
        Assert.False(moved.TryMove(source,null,out _));
        string directory=Path.Combine(Path.GetTempPath(),"enb-shortcut-move-"+Guid.NewGuid().ToString("N"));
        try
        {
            moved.SaveOutput(directory,character);
            var loaded=EnbRetailShortcuts.LoadOutput(directory,character)!;
            Assert.Equal(entry,Assert.Single(loaded.Sections.SelectMany(s=>s.Entries)));
            Assert.True(loaded.TryMove(entry,null,out var removed));removed.SaveOutput(directory,character);
            Assert.Empty(EnbRetailShortcuts.LoadOutput(directory,character)!.Sections.SelectMany(s=>s.Entries));
        }
        finally {if(Directory.Exists(directory))Directory.Delete(directory,true);}
    }

    [Fact]
    public void RetailShortcutsSurviveReloadAndRemainCharacterIsolated()
    {
        string directory=Path.Combine(Path.GetTempPath(),"enb-shortcuts-"+Guid.NewGuid().ToString("N"));
        const uint character=123;
        try
        {
            var empty=EnbRetailShortcuts.Resolve(Ini(""),character);
            var entry=new EnbRetailShortcutEntry(EnbRetailShortcutSource.Skill,new(1,1,2),"<7/><Cloak/><Advanced Cloak/>",7,"Cloak","Advanced Cloak");
            var saved=empty with {Sections=empty.Sections.Select(s=>s.Source==EnbRetailShortcutSource.Skill?s with {Entries=[entry]}:s).ToImmutableArray()};
            Directory.CreateDirectory(directory);
            string shared=Path.Combine(directory,"shortcut.ini");
            const string unrelated="; retail settings\r\n[OtherCharacter_Skills]\r\nNum=1\r\n0_Key=<1/><Other/><Other/>\r\n[Options]\r\nCustom=retained\r\n";
            File.WriteAllText(shared,unrelated);
            saved.SaveOutput(directory,character);
            var loaded=EnbRetailShortcuts.LoadOutput(directory,character)!;
            Assert.Equal(entry,Assert.Single(loaded.Sections.SelectMany(s=>s.Entries)));
            Assert.Empty(EnbRetailShortcuts.LoadOutput(directory,character+1).Sections.SelectMany(s=>s.Entries));
            Assert.Throws<ArgumentException>(()=>empty.SaveOutput(directory,character+1));
            Assert.StartsWith(unrelated,File.ReadAllText(shared));
            // A later save must merge the latest other-character data, not a
            // cached copy captured on first load.
            File.AppendAllText(shared,"[LaterCharacter_Skills]\r\nNum=0\r\n");
            saved.SaveOutput(directory,character);
            var merged=EnbRetailIniDocument.Load(shared);
            Assert.True(merged.TryGetSection("LaterCharacter_Skills",out _));
            Assert.Equal(6,merged.SectionCount);
            Assert.Single(Directory.GetFiles(directory));
        }
        finally { if(Directory.Exists(directory))Directory.Delete(directory,true); }
    }
    [Fact]
    public void ShortcutKeysUseSavedChordsIncludingSecondaryAndAltVariants()
    {
        var definitions = Ini("[Use Slot 1]\nPSW_1=1\n[Use Slot 4]\nPSW_1=4\n[Target Self]\nPSW_1=S");
        var bindings = EnbRetailKeyBindings.Resolve(definitions,
            Ini("[Default]\nUser=Test\n[Test]\n1_Use Slot 1=Q\n2_Use Slot 1=^F7\n1_Use Slot 4=\n1_Target Self=@Q"),null);
        string? Resolve(string key,EnbKeyModifiers mods=0) => bindings.ResolveAction(k=>k==key,mods);
        Assert.Equal("Use Slot 1",Resolve("Q"));
        Assert.Equal("Use Slot 1",Resolve("F7",EnbKeyModifiers.Control));
        Assert.Equal("Use Slot 1",Resolve("F7",EnbKeyModifiers.Control|EnbKeyModifiers.Alt));
        Assert.Equal("Target Self",Resolve("Q",EnbKeyModifiers.Alt)); // explicit chord wins
        Assert.Null(Resolve("1")); Assert.Null(Resolve("4"));
        Assert.Null(Resolve("Q",EnbKeyModifiers.Shift)); Assert.Null(Resolve("F7"));
        // Slots 7-12 are the Modern UI bar's second row.
        for (int i=1;i<=12;i++) Assert.Equal(i-1,EnbRetailKeyBindings.ShortcutSlot("Use Slot "+i));
        Assert.Equal(-1,EnbRetailKeyBindings.ShortcutSlot("Use Slot 13"));
    }

    [Fact]
    public void HeldBankShiftIsRelativeAndIdempotentAndKeepsMousePageChanges()
    {
        var snapshot=Build(Saved(Section("Skills",Entry(0,0,1,0,"<7/><Cloak/><Advanced Cloak/>")+Entry(1,1,1,0,"<7/><Cloak/><Advanced Cloak/>"))));
        var pages=new EnbShortcutPages(); pages.Update(snapshot); pages.Toggle(0);
        Assert.True(pages.SetShift(true)); Assert.Equal(0,pages.Group(0)); Assert.Equal(1,pages.Group(1));
        Assert.False(pages.SetShift(true));
        pages.Update(snapshot); Assert.True(pages.Shifted);
        pages.Toggle(1); // a click during the hold must remain effective after release
        Assert.True(pages.SetShift(false)); Assert.Equal(1,pages.Group(0)); Assert.Equal(1,pages.Group(1));
        Assert.False(pages.SetShift(false));
        pages.SetShift(true); pages.Update(Build(Saved("")));
        Assert.Equal(0,pages.Group(0)); Assert.Equal(0,pages.Group(1));
        pages.SetShift(false); Assert.Equal(1,pages.Group(0)); Assert.Equal(1,pages.Group(1));
        pages.Update(snapshot); pages.SetShift(true);
        pages.Update(snapshot with { Saved=snapshot.Saved with { CharacterKey="new pilot" } });
        Assert.False(pages.Shifted); Assert.Equal(0,pages.Group(0)); Assert.Equal(0,pages.Group(1));
    }

    [Fact]
    public void AuthoredSkillTooltipIncludesRankIndexedRangeCostAndWrappedDescription()
    {
        var catalog = EnbRetailSkillCatalog.Parse(EnbRetailIniDocument.Parse("[All Skills]\n7=Cloak\n[Cloak]\nRange=1000,2000,3000"),
            EnbRetailIniDocument.Parse(""), EnbRetailIniDocument.Parse("[Activated Abilities]\n3=Cloak\n4=Advanced Cloak\n[Cloak]\nSkill=Cloak\nMinLevel=1\nCost=9\nDesc=When activated, completely hides the ship from sensor detection.\n[Advanced Cloak]\nSkill=Cloak\nMinLevel=3\nCost=-1\nDesc=Advanced description."));
        Assert.Equal(new[] {0,1000,2000,3000},catalog.Families[0].Ranges);
        Assert.Equal("Cloak\n\nEnergy cost: 9\nRange: 1.00k units\n\nWhen activated, completely hides the\nship from sensor detection.\n",catalog.Describe(catalog.Abilities[0],1));
        Assert.Contains("Range: 2.00k units",catalog.Describe(catalog.Abilities[0],2));
        Assert.Contains("Range: 0.00k units",catalog.Describe(catalog.Abilities[0],4));
        Assert.Contains("Energy cost: Variable\nRange: 3.00k units",catalog.Describe(catalog.Abilities[1],1));
        Assert.Contains("Requires Level 3 Skill Upgrade",catalog.Describe(catalog.Abilities[1],1));
        Assert.Single(catalog.Choices("Cloak",1));
        Assert.Equal(2,catalog.Choices("Cloak",3).Length);
        Assert.Equal(new[] {"abcd","ef","","last"},EnbRetailSkillCatalog.Wrap("abcdef\n\nlast",4));
    }

    [Fact]
    public void SkillLevelChoiceRequiresCurrentIdentityAndRankButNeverActivationPermission()
    {
        var snapshot = Build(Saved(Section("Skills",Entry(0,0,0,0,"<52/><Cloak/><Advanced Cloak/>"))));
        snapshot = snapshot with { Slots=snapshot.Slots.SetItem(0,snapshot.Slots[0] with { SkillLevel=7 }), IsIncapacitated=true };
        var request = EnbShortcutSkillActivation.Capture(snapshot,snapshot.Slots[0],91)!;
        Assert.NotNull(EnbShortcutSkillActivation.ResolveSelection(request,3,snapshot,91,Skills()));
        Assert.Null(EnbShortcutSkillActivation.ResolveSelection(request,3,snapshot,92,Skills()));
        Assert.Null(EnbShortcutSkillActivation.ResolveSelection(request,3,snapshot with { Slots=snapshot.Slots.SetItem(0,snapshot.Slots[0] with { SkillLevel=null }) },91,Skills()));
        Assert.Null(EnbShortcutSkillActivation.ResolveSelection(request,169,snapshot,91,Skills()));
        Assert.Null(EnbShortcutSkillActivation.ResolveSelection(request,3,snapshot with { Saved=snapshot.Saved with { CharacterKey="other" } },91,Skills()));
    }

    [Fact]
    public void SkillClickResolvesIdentityWithoutInventingReadinessChecks()
    {
        var saved = Saved(Section("Skills", Entry(0,0,0,0,"<52/><Cloak/><Advanced Cloak/>")));
        var snapshot = Build(saved);
        var request = EnbShortcutSkillActivation.Capture(snapshot,snapshot.Slots[0],91)!;
        Assert.Equal(7,request.FamilyId); Assert.Equal(3,request.AbilityId);
        // Missing ownership/level are presentation information, not send predicates.
        Assert.Null(snapshot.Slots[0].HasAbility); Assert.Null(snapshot.Slots[0].SkillLevel);
        Assert.True(EnbShortcutSkillActivation.TryResolve(request,snapshot,91,out uint ability));
        Assert.Equal(3u,ability);
        Assert.Equal(Convert.FromHexString("443322110000000003000000"),
            Net7GameplayWire.BuildSkillAbility(0x11223344,ability));
        Assert.False(EnbShortcutSkillActivation.TryResolve(request,snapshot,92,out _));
        Assert.False(EnbShortcutSkillActivation.TryResolve(request,snapshot with { Saved = saved with { CharacterKey = "other" } },91,out _));
        Assert.False(EnbShortcutSkillActivation.TryResolve(request,Build(Saved("")),91,out _));
        foreach (var replacement in new[] {
            snapshot.Slots[0] with { AbilityId = 4 },
            snapshot.Slots[0] with { FamilyId = 4 },
            snapshot.Slots[0] with { Assignment = request.Assignment with { Key = "changed" } },
            snapshot.Slots[0] with { Resolution = EnbSceneShortcutResolution.UnknownAbility } })
            Assert.False(EnbShortcutSkillActivation.TryResolve(request,snapshot with { Slots = snapshot.Slots.SetItem(0,replacement) },91,out _));
        foreach (var invalid in new[] {
            snapshot.Slots[0] with { AbilityId = -1 },
            snapshot.Slots[0] with { AbilityId = 170 },
            snapshot.Slots[0] with { FamilyId = 64 },
            snapshot.Slots[0] with { Assignment = request.Assignment with { Source = EnbRetailShortcutSource.Equipment } },
            snapshot.Slots[0] with { Assignment = null } })
            Assert.Null(EnbShortcutSkillActivation.Capture(snapshot,invalid,91));
        Assert.Null(EnbShortcutSkillActivation.Capture(snapshot,snapshot.Slots[0],0));
    }

    [Fact]
    public void IncapacitationAndAbilityExceptionAreDetachedAndRecheckedAtDispatch()
    {
        var schemas = new Net7AuxDataSchemaCatalog();
        var saved = Saved(Section("Skills",Entry(0,0,0,0,"<7/><Cloak/><Advanced Cloak/>")));
        EnbSceneShortcutSnapshot Current() => EnbSceneShortcutSnapshotBuilder.Build(saved,Skills(),schemas.Ship,schemas.LocalPlayer,null);
        var request = EnbShortcutSkillActivation.Capture(Current(),Current().Slots[0],91)!;
        Set(schemas.Ship,"ShipAuxData.IsIncapacitated",(byte)1,0);
        var incapacitated = Current();
        Assert.True(incapacitated.IsIncapacitated);
        Assert.False(EnbShortcutSkillActivation.TryResolve(request,incapacitated,91,out _));
        Set(schemas.LocalPlayer,"LocalPlayerAuxData.RPGInfo.SkillAbilities.3.CanBeUsedWhileIncapacitated",(byte)1,0);
        Assert.True(EnbShortcutSkillActivation.TryResolve(request,Current(),91,out _));
        Assert.False(incapacitated.Slots[0].CanBeUsedWhileIncapacitated);
        Set(schemas.LocalPlayer,"LocalPlayerAuxData.RPGInfo.SkillAbilities.3.CanBeUsedWhileIncapacitated",(byte)0);
        Set(schemas.Ship,"ShipAuxData.IsIncapacitated",(byte)0);
        Assert.True(EnbShortcutSkillActivation.TryResolve(request,Current(),91,out _));
    }

    [Fact]
    public void VisiblePagesAreIndependentAndEmptyBanksRemainAvailable()
    {
        var saved = Saved(Section("Skills", Entry(0,0,1,0,"<7/><Cloak/><Advanced Cloak/>")+Entry(1,1,1,0,"<7/><Cloak/><Advanced Cloak/>")));
        var snapshot = Build(saved);
        var pages = new EnbShortcutPages(); pages.Update(snapshot);
        Assert.True(pages.CanToggle(0)); Assert.True(pages.CanToggle(1));
        pages.Toggle(0); Assert.Equal(1,pages.Group(0)); Assert.Equal(0,pages.Group(1));
        pages.Update(snapshot with { }); Assert.Equal(1,pages.Group(0));
        pages.Toggle(1); pages.Toggle(0); Assert.Equal(0,pages.Group(0)); Assert.Equal(1,pages.Group(1));
        pages.Update(snapshot with { Saved = saved with { CharacterKey = "other" } });
        Assert.Equal(0,pages.Group(0)); Assert.Equal(0,pages.Group(1));
        pages.Update(snapshot); pages.Toggle(0); pages.Update(Build(Saved("")));
        Assert.True(pages.CanToggle(0)); Assert.True(pages.CanToggle(1));
        Assert.Equal(1,pages.Group(0));
        pages.SetShift(true); Assert.Equal(0,pages.Group(0)); Assert.Equal(1,pages.Group(1));
        pages.SetShift(false); Assert.Equal(1,pages.Group(0)); Assert.Equal(0,pages.Group(1));
        pages.Toggle(0); Assert.Equal(0,pages.Group(0));
        pages.Toggle(0); Assert.Equal(1,pages.Group(0));
        pages.Update(null);
        Assert.False(pages.CanToggle(0)); Assert.Equal(0,pages.Group(0));
    }

    [Fact]
    public void SkillTintUsesMinimumLevelWithAuthoredOverridesNotOwnership()
    {
        var catalog = EnbRetailSkillCatalog.Parse(Ini("[All Skills]\n7=Cloak"),Ini("[Cloak]\nIcon=cloak.tga"),
            Ini("[Activated Abilities]\n3=Advanced Cloak\n[Advanced Cloak]\nSkill=Cloak\nMinLevel=4"),
            Ini("[AbilityColors]\nLevel_4=.3,.8,1"));
        var ability = catalog.FindAbility("Advanced Cloak")!;
        Assert.Equal(new System.Numerics.Vector3(.2f),ability.IconTint(3));
        Assert.Equal(new System.Numerics.Vector3(.2f),ability.IconTint(null));
        Assert.Equal(new System.Numerics.Vector3(.3f,.8f,1),ability.IconTint(4));
        var overridden = EnbRetailSkillCatalog.Parse(Ini(""),Ini(""),
            Ini("[Activated Abilities]\n3=Advanced Cloak\n[Advanced Cloak]\nMinLevel=4\nColor=1,0,0"),
            Ini("[AbilityColors]\nLevel_4=.3,.8,1\n[Advanced Cloak]\nColor=0,1,0"));
        Assert.Equal(new System.Numerics.Vector3(0,1,0),overridden.Abilities[0].IconTint(4));
        Assert.Equal(System.Numerics.Vector3.One,Skills().Abilities[0].Color);
    }

    private const uint Session = 0x01020304;
    private static readonly string Character = BinaryPrimitives.ReverseEndianness(Session).ToString();
    private static EnbRetailIniDocument Ini(string text) => EnbRetailIniDocument.Parse(text);
    private static string Section(string suffix, string entries, string created = "yes")
        => $"[{Character}_{suffix}]\nShortcutBeenCreated={created}\nNum=12\n{entries}\n";
    private static string Entry(int i, int bar, int group, int button, string key)
        => $"{i}_Bar={bar}\n{i}_Group={group}\n{i}_Button={button}\n{i}_Key={key}\n";
    private static EnbRetailShortcuts Saved(string text) => EnbRetailShortcuts.Resolve(Ini(text), Session);
    private static EnbRetailSkillCatalog Skills() => EnbRetailSkillCatalog.Parse(
        Ini("[All Skills]\n7=Cloak\n4=Other Skill"), Ini("[Cloak]\nIcon=skill_cloak.tga"),
        Ini("[Activated Abilities]\n3=Advanced Cloak\n[Advanced Cloak]\nSkill=Cloak\n[Passive Abilities]\n5=Passive Cloak\n[Passive Cloak]\nSkill=Cloak"));

    [Fact]
    public void SavedSectionsUseSessionIdentityAndNeverBorrowAnotherCharacter()
    {
        string text = Section("PDA_EQUIP", Entry(0, 0, 0, 0, "<3/>")) +
            "[123_Skills]\nNum=1\n" + Entry(0, 1, 0, 0, "<7/><Cloak/><Advanced Cloak/>");
        var saved = Saved(text);
        Assert.Equal(Character, saved.CharacterKey);
        Assert.Single(saved.Sections.SelectMany(s => s.Entries));
        Assert.True(saved.Sections[0].Exists);
        Assert.False(saved.Sections[2].Exists);
        Assert.All(EnbRetailShortcuts.Resolve(Ini(text), null).Sections, s => Assert.False(s.Exists));
        Assert.All(EnbRetailShortcuts.Resolve(Ini(text), 0).Sections, s => Assert.Empty(s.Entries));
    }

    [Fact]
    public void MissingAndIntentionallyEmptySectionsStayDistinctWithoutFirstRunInvention()
    {
        var saved = Saved(Section("PDA_EQUIP", "") + Section("Skills", "", "no"));
        Assert.True(saved.Sections[0].PreviouslyCreated);
        Assert.True(saved.Sections[2].Exists);
        Assert.False(saved.Sections[2].PreviouslyCreated);
        Assert.False(saved.Sections[1].Exists);
        Assert.All(saved.Sections, s => Assert.Empty(s.Entries));
    }

    [Fact]
    public void SparseEntriesKeepOrderAndRejectOutOfBoundsAddressesWithoutLoopingNum()
    {
        var saved = Saved(Section("Skills", Entry(9, 1, 1, 2, "<7/><Cloak/><Advanced Cloak/>") +
            Entry(2, 0, 0, 1, "<4/><Other Skill/><Absent/>") + Entry(3, 2, 0, 0, "<3/>"))
            .Replace("Num=12", "Num=2147483647"));
        Assert.Equal([1, 2], saved.Sections[2].Entries.Select(e => e.Address.Button));
        Assert.Single(saved.Diagnostics);
        Assert.All(saved.Sections[2].Entries, e => Assert.True(e.Address.IsValid));
    }

    [Theory]
    [InlineData("3")]
    [InlineData("<3/>junk")]
    [InlineData("<3/><extra/>")]
    [InlineData("<invalid/>")]
    public void MalformedKeysRemainAssignedButCannotResolve(string key)
    {
        var saved = Saved(Section("PDA_EQUIP", Entry(0, 0, 0, 0, key)));
        var snapshot = Build(saved);
        Assert.Equal(EnbSceneShortcutResolution.MalformedKey, snapshot.Slots[0].Resolution);
        Assert.Equal(key, snapshot.Slots[0].Assignment!.Key);
        Assert.Single(saved.Diagnostics);
    }

    [Fact]
    public void FamilyNameOverridesStaleIndexAndAbilityNameUsesExactCatalogLookup()
    {
        var local = new Net7AuxDataSchemaCatalog().LocalPlayer;
        Set(local, "LocalPlayerAuxData.RPGInfo.Skills.7.Level", 4);
        Set(local, "LocalPlayerAuxData.RPGInfo.SkillAbilities.3.HasAbility", (byte)1);
        var saved = Saved(Section("Skills", Entry(0, 1, 1, 2, "<52/><cLoAk/><Advanced Cloak/>")));
        var snapshot = EnbSceneShortcutSnapshotBuilder.Build(saved, Skills(), null, local, null);
        var slot = snapshot.Slots[11];
        Assert.Equal(EnbSceneShortcutResolution.Resolved, slot.Resolution);
        Assert.Equal(7, slot.FamilyId);
        Assert.Equal(3, slot.AbilityId);
        Assert.Equal(4, slot.SkillLevel);
        Assert.True(slot.HasAbility);
        Assert.Equal("skill_cloak.tga", slot.IconResource);
        Set(local, "LocalPlayerAuxData.RPGInfo.SkillAbilities.3.HasAbility", (byte)0);
        Assert.True(slot.HasAbility); // detached snapshot, not a live AuxData reference
        Assert.Equal(EnbSceneShortcutResolution.UnknownAbility,
            Build(Saved(Section("Skills", Entry(0, 0, 0, 0, "<7/><Cloak/><advanced cloak/>")))).Slots[0].Resolution);
    }

    [Fact]
    public void CatalogIdentityDoesNotInventRuntimeAbilityOwnership()
    {
        var slot = Build(Saved(Section("Skills", Entry(0, 0, 0, 0, "<7/><Cloak/><Advanced Cloak/>")))).Slots[0];
        Assert.Equal(EnbSceneShortcutResolution.Resolved, slot.Resolution);
        Assert.Null(slot.HasAbility);
        Assert.Null(slot.SkillLevel);
        Assert.Equal("skill_.tga", Skills().FindFamily("Other Skill")!.IconResource);
    }

    [Fact]
    public void EquipmentAndCargoWithSameSlotNumberResolveDifferentTemplates()
    {
        var schemas = new Net7AuxDataSchemaCatalog();
        Set(schemas.Ship, "ShipAuxData.Inventory.Equipped.3.ItemTemplateID", 101);
        Set(schemas.Ship, "ShipAuxData.Inventory.Equipped.3.StackCount", 1);
        Set(schemas.Ship, "ShipAuxData.Inventory.Cargo.3.ItemTemplateID", 202);
        Set(schemas.Ship, "ShipAuxData.Inventory.Cargo.3.StackCount", 12);
        var templates = new Net7ItemTemplateCatalog();
        templates.Materialize(Template(101, "Equipped beam", 701));
        templates.Materialize(Template(202, "Cargo device", 702));
        var saved = Saved(Section("PDA_EQUIP", Entry(0, 0, 0, 0, "<3/>")) +
            Section("PDA_CARGO", Entry(0, 1, 0, 0, "<3/>")));
        var snapshot = EnbSceneShortcutSnapshotBuilder.Build(saved, null, schemas.Ship, schemas.LocalPlayer, templates);
        Assert.Equal("Equipped beam", snapshot.Slots[0].Name);
        Assert.Equal(701, snapshot.Slots[0].IconBaseAsset);
        Assert.Equal("Cargo device", snapshot.Slots[6].Name);
        Assert.Equal(12, snapshot.Slots[6].StackCount);
        Assert.Equal(2, snapshot.ResolvedCount);
        Set(schemas.Ship, "ShipAuxData.Inventory.Cargo.3.StackCount", 0);
        Assert.Equal(12, snapshot.Slots[6].StackCount);
        Assert.Equal(EnbSceneShortcutResolution.EmptyInventorySlot,
            EnbSceneShortcutSnapshotBuilder.Build(saved, null, schemas.Ship, schemas.LocalPlayer, templates).Slots[6].Resolution);
    }

    [Fact]
    public void PendingDataAndRetainedStateZeroValuesAreNotMistakenForEmptyButtons()
    {
        var schemas = new Net7AuxDataSchemaCatalog();
        var saved = Saved(Section("PDA_EQUIP", Entry(0, 0, 0, 0, "<3/>")));
        Assert.Equal(EnbSceneShortcutResolution.InventoryPending, Build(saved).Slots[0].Resolution);
        Set(schemas.Ship, "ShipAuxData.Inventory.Equipped.3.ItemTemplateID", 101, 0);
        Set(schemas.Ship, "ShipAuxData.Inventory.Equipped.3.StackCount", 1, 0);
        var pending = EnbSceneShortcutSnapshotBuilder.Build(saved, null, schemas.Ship, schemas.LocalPlayer, null);
        Assert.Equal(EnbSceneShortcutResolution.ItemTemplatePending, pending.Slots[0].Resolution);
        Assert.Equal(101, pending.Slots[0].ItemTemplateId);
        Assert.Equal(12, pending.Slots.Length);
        Assert.Equal(11, pending.Slots.Count(s => s.Resolution == EnbSceneShortcutResolution.Empty));
    }

    [Fact]
    public void CollisionsAreDiagnosedInsteadOfChoosingAnArbitraryAction()
    {
        var saved = Saved(Section("PDA_EQUIP", Entry(0, 0, 0, 0, "<3/>")) +
            Section("PDA_CARGO", Entry(0, 0, 0, 0, "<3/>")));
        var slot = Build(saved).Slots[0];
        Assert.Equal(EnbSceneShortcutResolution.ConflictingAssignments, slot.Resolution);
        Assert.Null(slot.Assignment);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0u)]
    [InlineData(0x12345678u)]
    public void EquipmentActivationUsesCurrentSelectionAndEquippedSource(uint? target)
    {
        var schema = new Net7AuxDataSchemaCatalog();
        Set(schema.Ship,"ShipAuxData.Inventory.Equipped.3.ItemTemplateID",101);
        Set(schema.Ship,"ShipAuxData.Inventory.Equipped.3.StackCount",1);
        var templates = new Net7ItemTemplateCatalog();
        templates.Materialize(Template(101,"Device",701)); // no fabricated effect/range requirement
        var saved = Saved(Section("PDA_EQUIP",Entry(0,0,0,0,"<3/>")));
        var snapshot = EnbSceneShortcutSnapshotBuilder.Build(saved,null,schema.Ship,schema.LocalPlayer,templates);
        var request = EnbShortcutEquipmentActivation.Capture(snapshot,snapshot.Slots[0],91)!;
        Assert.NotNull(request);
        Assert.True(EnbShortcutEquipmentActivation.TryResolve(request,snapshot,91,target,out var use));
        Assert.Equal(new Net7UseInventoryItemRequest(target ?? 0,2,3),use);
        var payload = Net7GameplayWire.BuildUseInventoryItem(use.TargetGameId,use.Inventory,use.Slot);
        Assert.Equal(use,Net7GameplayWire.DecodeUseInventoryItem(payload));
        Assert.Equal(target == 0x12345678u ? "785634120203" : "000000000203",Convert.ToHexString(payload));
        Assert.True(EnbShortcutEquipmentActivation.TryResolve(request,snapshot,91,99,out use));
        Assert.Equal(99u,use.TargetGameId); // selection at dispatch, not pointer-down
        Assert.False(EnbShortcutEquipmentActivation.TryResolve(request,snapshot,92,target,out _));
        Assert.False(EnbShortcutEquipmentActivation.TryResolve(request,snapshot with { IsIncapacitated=true },91,target,out _));
        Assert.False(EnbShortcutEquipmentActivation.TryResolve(request,snapshot with { Saved=saved with { CharacterKey="replacement" } },91,target,out _));
        foreach (var replacement in new[] {
            snapshot.Slots[0] with { ItemTemplateId=102 },
            snapshot.Slots[0] with { StackCount=0 },
            snapshot.Slots[0] with { Resolution=EnbSceneShortcutResolution.ItemTemplatePending },
            snapshot.Slots[0] with { Assignment=request.Assignment with { Source=EnbRetailShortcutSource.Cargo } },
            snapshot.Slots[0] with { Assignment=request.Assignment with { SavedIndex=4 } } })
            Assert.False(EnbShortcutEquipmentActivation.TryResolve(request,snapshot with { Slots=snapshot.Slots.SetItem(0,replacement) },91,target,out _));
        foreach (int bad in new[] {-1,256})
            Assert.Null(EnbShortcutEquipmentActivation.Capture(snapshot,snapshot.Slots[0] with {
                Assignment=request.Assignment with { SavedIndex=bad } },91));
        // Sending a request never locally consumes the item.
        Assert.Equal(1,snapshot.Slots[0].StackCount);
        Assert.Equal(1,schema.Ship.Properties.Single(p=>p.Path=="ShipAuxData.Inventory.Equipped.3.StackCount").Value);
    }

    private static EnbSceneShortcutSnapshot Build(EnbRetailShortcuts saved)
        => EnbSceneShortcutSnapshotBuilder.Build(saved, Skills(), null, new Net7AuxDataSchemaCatalog().LocalPlayer, null);
    private static void Set(Net7AuxDataSchema schema, string path, object value, int state = 2)
    {
        var p = schema.Properties.Single(p => p.Path == path); p.Value = value; p.State = state;
    }
    private static Net7ItemTemplate Template(uint id, string name, ushort icon)
    {
        var empty = new Net7ItemTemplateVector([], null);
        return new(id, 0, 0, 0, new([], empty, empty), 0, icon, 1, 0, 1, 0, 0,
            new(Encoding.Latin1.GetBytes(name)), new([]), new([]));
    }

    [Fact]
    public void ItemDropOnShortcutBarRecordsTheDraggedSlotEvenForALoadedWeapon()
    {
        // 005A0740 hands the dragged slot (00685610) to the panel's shortcut UI, which
        // 0059F470 creates only for SHIP/PDA cargo and equipment panels.
        var snapshot = Build(Saved(Section("Skills",Entry(0,0,0,0,"<52/><Cloak/><Advanced Cloak/>"))));
        var gun=new EnbSceneItemSnapshot(2,4,1002,"Gun",0,0,15,1,0,1,null,null,null,"","",[],[],0);
        var ammo=new EnbSceneItemSnapshot(2,4,2001,"Shell",0,0,10,1,0,40,null,null,null,"","",[],[],0){IsAmmoLayer=true};
        var inventory=new EnbSceneInventorySnapshot(null,0,[],[gun],[ammo],ImmutableDictionary<string,string>.Empty,ImmutableDictionary<string,string>.Empty,[],[],[]);
        using var cockpit=new EnbNext.Engine.EnbCockpitUi();
        void SetField(string name,object value)=>typeof(EnbNext.Engine.EnbCockpitUi).GetField(name,System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(cockpit,value);
        SetField("_interactive",true);SetField("_shortcuts",snapshot);SetField("_shortcutShipOwner",91UL);SetField("_inventory",inventory);
        SetField("_size",new EnbUiSize(640,480));
        var bounds=(Dictionary<string,EnbUiRect>)typeof(EnbNext.Engine.EnbCockpitUi).GetField("_hitBounds",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(cockpit)!;
        bounds["BTTN_0_00"]=new EnbUiRect(0,0,100,100);
        var request=cockpit.CaptureItemDrop(new EnbInventoryItemRequest(1,91,2,4,2001){AmmoLayer=true},new EnbUiPoint(10,10));
        Assert.NotNull(request);
        Assert.Equal(new EnbInventoryItemRequest(1,91,2,4,1002),request!.Item);
        Assert.Null(cockpit.CaptureItemDrop(new EnbInventoryItemRequest(1,91,3,4,1002),new EnbUiPoint(10,10)));
    }

    [Fact]
    public void IncapacitatedKeyboardShortcutPressReportsTheRetailLocalMessage()
    {
        // 006120E0: a keyboard press of an occupied slot while incapacitated adds
        // local type 0x11 "Cannot perform action while incapacitated." and
        // activates nothing.
        var snapshot = Build(Saved(Section("Skills",Entry(0,0,0,0,"<52/><Cloak/><Advanced Cloak/>"))));
        using var cockpit=new EnbNext.Engine.EnbCockpitUi();
        void SetField(string name,object value)=>typeof(EnbNext.Engine.EnbCockpitUi).GetField(name,System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(cockpit,value);
        SetField("_interactive",true);SetField("_shortcuts",snapshot with { IsIncapacitated=true });SetField("_shortcutShipOwner",91UL);
        int reported=0,activated=0;cockpit.IncapacitatedShortcutPressed+=()=>reported++;cockpit.SkillRequested+=_=>activated++;
        cockpit.PressShortcutKey(1,0);
        Assert.True(cockpit.ReleaseShortcutKey(1));
        Assert.Equal((1,0),(reported,activated));
        SetField("_shortcuts",snapshot);
        cockpit.PressShortcutKey(2,0);
        Assert.True(cockpit.ReleaseShortcutKey(2));
        Assert.Equal((1,1),(reported,activated));
    }

    [Fact]
    public void RemoteHiddenBankRefreshesRangeAndCooldownWithoutSoundOrInput()
    {
        var saved=Saved(Section("Skills",Entry(0,1,1,2,"<7/><Cloak/><Advanced Cloak/>")));
        var snapshot=Build(saved);
        snapshot=snapshot with {Slots=snapshot.Slots.Select(slot=>slot.Resolution==EnbSceneShortcutResolution.Resolved
            ?slot with {SkillFeedback=new(0,0x34,1000,1000,500,-1,0,0)}:slot).ToImmutableArray()};
        using var cockpit=new EnbNext.Engine.EnbCockpitUi();
        void SetField(string name,object value)=>typeof(EnbNext.Engine.EnbCockpitUi).GetField(name,System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(cockpit,value);
        SetField("_interactive",true);SetField("_shortcuts",snapshot);SetField("_shortcutShipOwner",91UL);SetField("_combatTime",1000L);
        var target=new EnbNext.Rendering.EnbRadarContact(1,99,1,"Target",default,0,"",default,500,500,1000,0,false,true,false,true,null,null,null);
        int events=0;cockpit.CombatSoundRequested+=_=>events++;cockpit.SkillRequested+=_=>events++;
        cockpit.SetCombatTarget(target);var ready=Assert.Single(cockpit.CaptureRemoteShortcuts());
        Assert.Equal(11,ready.Slot);Assert.Equal(15,ready.RangeFrame);Assert.True(ready.Cooling);
        Assert.Equal(0,ready.RangeAngle);Assert.Same(ready,Assert.Single(cockpit.CaptureRemoteShortcuts([ready])));
        cockpit.SetCombatTarget(target with {SurfaceDistance=1100});
        Assert.Equal(0,Assert.Single(cockpit.CaptureRemoteShortcuts([ready])).RangeFrame);
        cockpit.SetCombatTarget(target with {SurfaceDistance=2500});SetField("_combatTime",2000L);
        var far=Assert.Single(cockpit.CaptureRemoteShortcuts());Assert.Equal(0,far.RangeFrame);Assert.False(far.Cooling);
        cockpit.SetCombatTarget(null);Assert.Equal(-1,Assert.Single(cockpit.CaptureRemoteShortcuts()).RangeFrame);
        Assert.Equal(0,events);
    }
}
