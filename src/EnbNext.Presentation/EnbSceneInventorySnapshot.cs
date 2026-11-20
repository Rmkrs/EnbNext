using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using EnbNext.Assets;
using EnbNext.Net7.Gameplay;
using EnbNext.Net7.World;

namespace EnbNext.Presentation;

public sealed record EnbItemAttribute(uint Id, string Value);
public sealed record EnbSceneItemSnapshot(byte Inventory, int Slot, int TemplateId, string Name,
    uint Category, uint Subcategory, uint ItemType, int Level, int IconBaseAsset, int Count,
    float? Structure, float? Quality, uint? State, string Description, string Builder,
    ImmutableArray<EnbItemAttribute> Attributes, ImmutableArray<EnbItemSpecial> Effects, uint Flags)
{
    public bool IsAmmoLayer { get; init; }
    // 00521C10: definition +34 (template StringFieldC) names a corporation when BuilderName is empty.
    public string CorporationBuilder { get; init; } = "";
    public float? TargetRange { get; init; }
    public float? EffectRange { get; init; }
    public uint? EffectUsage { get; init; }
    public uint? EffectTargets { get; init; }
    public uint? EffectValidity { get; init; }
    public long? ReadyTime { get; init; }
    public bool IsWeapon => ItemType is 14 or 15 or 16;
    public ulong? Price { get; init; }
    public uint? MaxStack { get; init; }
    public float? AverageCost { get; init; }
    public bool HasAutoFire => Inventory == 2 && Attributes.Any(a => a.Id == 2);
    public bool AutoFire => (State.GetValueOrDefault() & 0x2000) != 0;
    public string Tooltip => Name + (Structure is >= 0 ? $"\nStructure {Structure * 100:0}%" : "")
        + (Quality is >= 0 ? $"\nQuality {Quality * 100:0}%" : "");
}
public sealed record EnbCharacterSkillSnapshot(int Id, string Name, string Icon, int Level, int Maximum, string Description)
{
    public Net7SkillAvailability? Availability { get; init; }
    public bool VisibleInSkillPanel => Availability is {} a && a.Raw0 is not (0 or 1);
    public bool ShowUpgradeIndicator => Availability is {Raw0:4,Raw1:0};
    public int QuestOnlyLevels { get; init; }
    public bool CanTrain => Availability is {Raw1:0} a && a.Raw0!=3 && Level<QuestOnlyLevels;
    public string Affiliations { get; init; } = "";
    public bool HideUpgradeCost => Availability is null || Availability.Raw0 == 3 || Level >= QuestOnlyLevels || (Availability.Raw1 & 0x40) != 0;
    // Retail 005490E3..00549266: flag priority is significant when several reasons coexist.
    public string UpgradeReason => Availability is not {} a ? "" :
        (a.Raw1 & 0x40) != 0 ? "Maximum Level Attained" :
        (a.Raw1 & 0x80) != 0 ? Affiliations + " Only." :
        a.Raw0 == 3 || Level >= QuestOnlyLevels ? "" :
        (a.Raw1 & 1) != 0 ? FormattableString.Invariant($"Upgrade requires level {a.RequiredLevel}") :
        (a.Raw1 & 8) != 0 ? FormattableString.Invariant($"Requires combat level {a.RequiredLevel}") :
        (a.Raw1 & 16) != 0 ? FormattableString.Invariant($"Requires trade level {a.RequiredLevel}") :
        (a.Raw1 & 32) != 0 ? FormattableString.Invariant($"Requires explore level {a.RequiredLevel}") :
        (a.Raw1 & 2) != 0 ? "Insufficient skill points" : "";

}
public sealed record EnbCharacterFactionSnapshot(string Name, float Reaction)
{
    // 0058E740 raw x87 branches, then 00669990 clamps each gauge independently.
    public (float Love,float Hate) GaugeFractions
    {
        get
        {
            float normalized=Math.Clamp(Reaction*.0001f,-1f,1f);
            return MathF.Abs(normalized)<.1f
                ? (Math.Clamp(.05f+normalized,0,1),Math.Clamp(.05f-normalized,0,1))
                : (Math.Max(normalized,0),Math.Max(-normalized,0));
        }
    }
}
public sealed record EnbEquipmentMountSnapshot(int Slot, uint ItemTypeMask);
public sealed record EnbCharacterMissionSnapshot(string Name, string Description)
{ public ulong Revision {get;init;}
  public string CurrentStep { get; init; } = "";
  public int Slot {get;init;} public int Id {get;init;} public bool CanForfeit {get;init;} public bool CanClear {get;init;}
  public long? ExpirationTime {get;init;}
  public string TimerText(uint clientTime)
  {
      if(ExpirationTime is not long expiry)return "";
      // 005C5C5E uses a signed 32-bit subtraction; positive sub-minute values
      // are seconds, otherwise hours:minutes (seconds are intentionally omitted).
      int remaining=unchecked((int)expiry-(int)clientTime);
      if(remaining<=0)return "";
      int seconds=remaining/1000,minutes=seconds/60;
      return minutes==0?FormattableString.Invariant($"{seconds} secs"):
          FormattableString.Invariant($"{minutes/60}:{minutes%60:00}");
  }
}
public sealed record EnbSceneInventorySnapshot(ulong? Credits, int CargoSpace,
    ImmutableArray<EnbSceneItemSnapshot> Cargo, ImmutableArray<EnbSceneItemSnapshot> Equipped,
    ImmutableArray<EnbSceneItemSnapshot> Ammo, ImmutableDictionary<string, string> ShipStats,
    ImmutableDictionary<string, string> CharacterStats, ImmutableArray<EnbCharacterSkillSnapshot> Skills,
    ImmutableArray<EnbCharacterFactionSnapshot> Factions, ImmutableArray<EnbCharacterMissionSnapshot> Missions)
{
    public EnbItemRequirementState RequirementState { get; init; } = EnbItemRequirementState.Empty;
    public ulong SkillPointsRevision { get; init; }
    public EnbPlayerTradeSnapshot? PlayerTrade { get; init; }
    public ImmutableArray<EnbSceneItemSnapshot> Vendor { get; init; } = [];
    public ImmutableArray<EnbSceneItemSnapshot> Vault { get; init; } = [];
    public int VaultSpace { get; init; }
    public ImmutableArray<int> CargoSlotKeys { get; init; } = [];
    public ImmutableArray<int> VaultSlotKeys { get; init; } = [];
    public uint? HulkId { get; init; }
    public ulong HulkOwner { get; init; }
    public string HulkName { get; init; } = "";
    public ImmutableArray<EnbSceneItemSnapshot> Loot { get; init; } = [];
    public ImmutableArray<int> LootSlotKeys { get; init; } = [];
    public uint? HarvestableId { get; init; }
    public ulong HarvestableOwner { get; init; }
    public ImmutableArray<EnbSceneItemSnapshot> Harvest { get; init; } = [];
    public ImmutableArray<int> HarvestSlotKeys { get; init; } = [];

    public ImmutableArray<EnbEquipmentMountSnapshot> Mounts { get; init; } = [];
    public int FutureWeapons { get; init; }
    public int FutureDevices { get; init; }
    public string OwnedText(int template)
    {
        var owned=Cargo.Concat(Equipped).Concat(Ammo).Concat(Vault).Where(x=>x.TemplateId==template).ToArray();
        long count=owned.Sum(x=>(long)x.Count);
        // 0068FE10 selects the multi-slot form before checking the total count.
        return owned.Length>1?$"Owned: {count} in {owned.Length} slots":count<1?"":$"Owned: {count}";
    }
    public EnbSceneItemSnapshot? Find(byte inventory, int slot, bool ammoLayer=false)
        => (ammoLayer && inventory==2 ? Ammo : inventory switch { 1=>Cargo, 2=>Equipped, 3=>Vault, 4=>Vendor, 16=>PlayerTrade?.Give??[], 17=>PlayerTrade?.Receive??[], 0x12=>Harvest, 5 or 6=>Loot.Where(i=>i.Inventory==inventory).ToImmutableArray(), _=>ImmutableArray<EnbSceneItemSnapshot>.Empty }).FirstOrDefault(i => i.Slot == slot);
}
public sealed record EnbCharacterActionRequest(uint Character, ulong ShipOwner, int Index, int Identity);
public sealed record EnbInventoryItemRequest(uint Character, ulong ShipOwner, byte Inventory, int Slot, int TemplateId)
{ public ulong? TradeRevision { get; init; } public bool AmmoLayer { get; init; } public uint? HulkId { get; init; } public ulong HulkOwner { get; init; } public uint? HarvestableId { get; init; } public ulong HarvestableOwner { get; init; } }
public sealed record EnbInventoryMoveRequest(EnbInventoryItemRequest Source,int SourceCount,byte Destination,int DestinationSlot,int? DestinationTemplate,int Count,bool DestroyConfirmed=false)
{
    public ulong? TradeRevision { get; init; }
    public int? ReloadAmmoTemplate { get; init; }
}
public sealed record EnbPlayerTradeSnapshot(ulong Revision,uint? PartnerId,bool Waiting,bool Confirmed,bool? Successful,
    uint OfferedCredits,ulong ReceivedCredits,ImmutableArray<EnbSceneItemSnapshot> Give,ImmutableArray<EnbSceneItemSnapshot> Receive)
{
    public Net7TradeStatusMessage StatusMessage {get;init;}
    public string StatusText=>StatusMessage switch
    {
        Net7TradeStatusMessage.Completed=>"Trading has successfully completed.",
        Net7TradeStatusMessage.Confirmed=>"Trade has been confirmed",
        Net7TradeStatusMessage.Waiting=>"Awaiting Trade Confirmation",
        _=>string.Empty
    };
}
public enum EnbPlayerTradeAction { Cancel, Confirm, Credits }
public sealed record EnbPlayerTradeRequest(uint Character,ulong ShipOwner,ulong Revision,uint PartnerId,EnbPlayerTradeAction Action,long Credits=0);

public static class EnbSceneInventorySnapshotBuilder
{
    // This projection runs inside the existing presentation mutation boundary. Never expose live AuxData or byte arrays to UI.
    public static EnbSceneInventorySnapshot Build(Net7AuxDataSchema? ship, Net7AuxDataSchema player,
        Net7ItemTemplateCatalog? templates, EnbRetailSkillCatalog? skills, Net7WorldObject? hulk = null,
        Net7WorldObject? harvestable = null, Net7TradeDomain? trade = null, Net7WorldObject? tradePartner = null, EnbInventoryPublicationOwner? owner = null)
    {
        owner ??= new();
        if(trade is not {SessionRevision:>0})owner.ClearTrade();
        var inv = Child(ship?.Root, "Inventory");
        var rpg = Child(player.Root, "RPGInfo");
        var shipStats = ImmutableDictionary.CreateBuilder<string, string>();
        foreach (var p in Child(ship?.Root, "CurrentStats")?.Children ?? []) shipStats[Name(p)] = Value(p);
        foreach (var p in Child(ship?.Root, "BaseStats")?.Children ?? []) shipStats["Base."+Name(p)] = Value(p);
        foreach (string key in new[] { "Name", "Owner", "HullPoints", "MaxHullPoints" }) shipStats[key] = Value(Child(ship?.Root, key));
        var character = ImmutableDictionary.CreateBuilder<string, string>();
        foreach (string key in new[] { "CombatLevel", "TradeLevel", "ExploreLevel", "SkillPoints", "TotalSkillPoints", "HullUpgradeLevel", "Race", "Profession" })
            character[key] = Value(Child(rpg, key));
        character["Name"] = Value(Child(ship?.Root, "Owner"));
        character["Rank"] = Value(Child(ship?.Root, "Rank"));
        character["Guild"] = Value(Child(ship?.Root, "GuildName"));
        character["Affiliation"]=Value(Child(Child(player.Root,"Reputation"),"Affiliation"));
        character["XPDebt"] = Value(Child(player.Root, "XPDebt"));
        return new(Child(player.Root, "Money")?.Value as ulong?, Int(Child(inv, "CargoSpace")) ?? 0,
            Items("Cargo", 1), Items("Equipped", 2), Items("Ammo", 2), shipStats.ToImmutable(), character.ToImmutable(),
            (skills?.Families ?? []).Select(f =>
            {
                var p = Child(rpg, "Skills")?.Children.ElementAtOrDefault(f.Id);
                int level=Int(Child(p,"Level"))??0;
                return new EnbCharacterSkillSnapshot(f.Id, f.Name, f.IconResource, level,
                    Int(Child(p, "MaxSkillLevels")) ?? 0,
                    f.Name+"\n\n"+f.Description+"\n\n"+(f.LevelDescriptions.TryGetValue(level,out var current)?$"Current level {level}: {current}\n\n":"")+
                    (f.LevelDescriptions.TryGetValue(level+1,out var next)?$"Next level {level+1}: {next}":""))
                    {QuestOnlyLevels=Int(Child(p,"QuestOnlyLevels"))??0,Affiliations=Value(Child(p,"Affiliations")),Availability=Child(p,"Availability")?.Value is byte[] {Length:16} av
                       ?new(BinaryPrimitives.ReadInt32LittleEndian(av.AsSpan(0)),BinaryPrimitives.ReadInt32LittleEndian(av.AsSpan(4)),BinaryPrimitives.ReadInt32LittleEndian(av.AsSpan(8)),BinaryPrimitives.ReadInt32LittleEndian(av.AsSpan(12))):null};
            }).Where(s => s.Level > 0 || s.Maximum > 0 || s.VisibleInSkillPanel).ToImmutableArray(),
            (Child(Child(player.Root, "Reputation"), "Faction")?.Children ?? [])
                .GroupBy(p=>Int(Child(p,"Order"))??0).Where(g=>g.Key is >=0 and <=21).OrderBy(g=>g.Key)
                .Select(g=>g.First()).Select(p => new EnbCharacterFactionSnapshot(
                Value(Child(p, "Name")), Float(Child(p, "Reaction")) ?? 0)).Where(f => f.Name.Length > 0).ToImmutableArray(),
            (Child(player.Root, "Missions")?.Children ?? []).OrderByDescending(p=>Int(Child(p,"StartTime"))??0)
                .Where(p => Value(Child(p,"Name")).Length > 0).Select(ProjectMission).ToImmutableArray()) {
                    RequirementState=EnbItemRequirementState.Capture(player,skills),
                    SkillPointsRevision=Child(rpg,"SkillPoints")?.UpdateRevision??0,
                    PlayerTrade=trade is {SessionRevision:>0}?new(trade.SessionRevision,trade.ActivePartnerId,trade.WaitingForConfirmation,trade.Confirmed,trade.LastCloseSuccessful,
                        trade.LocalOfferedCredits,Child(tradePartner?.AuxData?.Root,"TradeMoney")?.Value as ulong? ?? 0,Items("PlayerTrade",16),Items("PlayerTrade",17)){StatusMessage=trade.StatusMessage}:null,
                    HulkId=hulk?.GameId, HulkOwner=hulk?.CreationOrdinal??0,
                    HulkName=Value(Child(hulk?.AuxData?.Root,"Name")),
                    Loot=Items("Equipped",5).AddRange(Items("Cargo",6)),
                    LootSlotKeys=InventoryKeys(Child(hulk?.AuxData?.Root,"Cargo")),
                    HarvestableId=harvestable?.GameId, HarvestableOwner=harvestable?.CreationOrdinal??0,
                    Harvest=Items("HarvestableResources",0x12),
                    HarvestSlotKeys=InventoryKeys(Child(harvestable?.AuxData?.Root,"HarvestableResources")),
                    CargoSlotKeys=InventoryKeys(Child(inv,"Cargo")), VaultSlotKeys=InventoryKeys(Child(player.Root,"SecureInventory")),
                    Vendor = Items("VendorInventory", 4), Vault=Items("SecureInventory",3),VaultSpace=Child(player.Root,"SecureInventory")?.Children.Count ?? 0,
                    // Current retail 005D2C50 / 005D2210: first Mounts word is the accepted item-type mask;
                    // array ordinal remains the equipment transaction slot, independent of visual position.
                    Mounts=(Child(inv,"Mounts")?.Children ?? []).Where(p=>p.State!=0 && p.Value is byte[] {Length:12})
                        .Select(p=>new EnbEquipmentMountSnapshot(p.ChildIndex,BinaryPrimitives.ReadUInt32LittleEndian((byte[])p.Value!)))
                        .Where(m=>m.ItemTypeMask!=0).ToImmutableArray(),
                    FutureWeapons=Int(Child(inv,"FutureWeapons"))??0, FutureDevices=Int(Child(inv,"FutureDevices"))??0
                };

        // 00688410 defaults to the unfiltered list. 00688B90 preserves empty
        // (-1) keys and removes only unavailable (-2) entries; 006A7A00 maps
        // visual indices back to those original keys. Unreceived values retain
        // the InventoryItem constructor's -1 semantics.
        ImmutableArray<int> InventoryKeys(Net7AuxDataProperty? collection) =>
            (collection?.Children ?? []).Where(p => Int(Child(p,"ItemTemplateID")) != -2)
                .Select(p => p.ChildIndex).ToImmutableArray();

        ImmutableArray<EnbSceneItemSnapshot> Items(string collection, byte inventory)
        {
            var source=Child(inventory == 17 ? Child(tradePartner?.AuxData?.Root,"Inventory") : inventory == 0x12 ? harvestable?.AuxData?.Root : inventory is 5 or 6 ? hulk?.AuxData?.Root : inventory is 3 or 4 ? player.Root : inv, collection);
            return owner.Sample(source,inventory,collection=="Ammo",templates);
        }
    }
    private static EnbCharacterMissionSnapshot ProjectMission(Net7AuxDataProperty mission)
    {
        // Current retail 005C5500 / 005C5DB0: independent summary and detail paths.
        string Read(string key) => Value(Child(mission, key));
        bool complete=Bool(Child(mission,"IsComplete")), failed=Bool(Child(mission,"IsFailed")), expired=Bool(Child(mission,"IsExpired"));
        int stage=Int(Child(mission,"Stage"))??1, count=Int(Child(mission,"StageCount"))??0;
        var stages=Child(mission,"Stages")?.Children;
        var current=stage is >=1 and <=20 ? stages?.ElementAtOrDefault(stage-1) : null;
        string step=complete?"Mission Complete!":failed?"Mission Failed!":expired?"Mission Expired!":
            (count==0?$"({stage}) ":$"({stage}/{count}) ")+Value(Child(current,"Text"));
        var details=new System.Text.StringBuilder();
        if(Read("Reward") is {Length:>0} reward)details.Append("Reward: ").Append(reward).Append("\n\n");
        if(Read("FailureConsequence") is {Length:>0} failure)details.Append("Failure: ").Append(failure).Append("\n\n");
        if(Read("Summary") is {Length:>0} summary)details.Append(summary).Append("\n\n");
        details.Append("Mission Steps:\n");
        // The authored array has twenty slots. Stop on its first empty text, as retail does.
        for(int i=0;i<Math.Min(count,20);i++)
        {
            string text=Value(Child(stages?.ElementAtOrDefault(i),"Text"));
            if(text.Length==0)break;
            details.Append((i+1).ToString(CultureInfo.InvariantCulture)).Append(". ").Append(text).Append('\n');
        }
        bool timed=Bool(Child(current,"IsTimed"))||Bool(Child(mission,"IsTimed"));
        var expiry=Child(mission,Bool(Child(current,"IsTimed"))?"StageExpirationTime":"ExpirationTime");
        return new(Read("Name"),Net7PlayerSemanticDomain.StripMissionMarkup(details.ToString()))
        {
            Revision=mission.UpdateRevision,Slot=mission.ChildIndex,Id=Int(Child(mission,"ID"))??-1,
            CanClear=complete||failed||expired,
            CanForfeit=!complete&&!failed&&!expired&&Bool(Child(mission,"IsForfeitable")),
            CurrentStep=Net7PlayerSemanticDomain.StripMissionMarkup(step),
            ExpirationTime=timed&&expiry?.Value is Net7AuxDataTime time?Math.Clamp(time.RelativeValue,int.MinValue,int.MaxValue):null
        };
    }
    /// <summary>An item definition as the inventory would show it with no instance values (the Next item catalogue).</summary>
    public static EnbSceneItemSnapshot CatalogueItem(Net7ItemTemplate template)
    {
        var (attributes, effects) = ProjectItemInformation(template, "");
        return new(0, -1, unchecked((int)template.ItemTemplateId), template.Name.EffectiveText, template.Category, template.Subcategory, template.ItemType,
            template.TechLevel, template.IconBaseAsset, 1, null, null, null, template.StringFieldB.EffectiveText, "", attributes, effects, template.Flags)
        { MaxStack = template.MaxStack, EffectUsage = template.EffectUsage };
    }
    internal static (ImmutableArray<EnbItemAttribute> Attributes, ImmutableArray<EnbItemSpecial> Effects) ProjectItemInformation(Net7ItemTemplate? t, string instanceInfo, string activatedEffects="", string equipEffects="")
    {
        // 00840030 retains repeated fields and updates its ID lookup to the last entry.
        // 00522860 enumerates those entries; numeric lookups use that last value,
        // while ammunition text (field 1) is read directly from each entry.
        var attributes = new Dictionary<uint, EnbItemAttribute>();
        static EnbItemAttribute Project(Net7ItemTemplateAttribute a) => new(a.AttributeId, a.Type switch
        {
            Net7ItemTemplateAttributeType.Int => a.Int32Value.ToString(CultureInfo.InvariantCulture),
            Net7ItemTemplateAttributeType.Float => a.FloatValue.ToString("R", CultureInfo.InvariantCulture),
            _ => a.StringValue
        });
        if (t is not null)
            foreach (var a in t.Info.Attributes) attributes[a.AttributeId] = Project(a);
        // 008417B0 converts numeric prefixes with the CRT, rather than requiring
        // the whole field to parse. 00522860 applies overlays only to these rows.
        foreach (var field in instanceInfo.Split('^'))
        {
            int colon = field.IndexOf(':');
            if (colon < 0) continue;
            uint key = unchecked((uint)ParseItemInteger(field.AsSpan(0, colon)));
            if (!attributes.TryGetValue(key, out var original)) continue;
            var value = field.AsSpan(colon + 1);
            string replacement;
            switch (key)
            {
                case 5 or 7 or 8 or 15 or 16 or 19 or 22 or 24 or 29 or 31 or 33:
                    int integer = ParseItemInteger(value);
                    if (integer == 0) continue;
                    replacement = integer.ToString(CultureInfo.InvariantCulture);
                    break;
                case 9 or 10 or 20 or 21 or 23 or 25 or 26 or 34 or 36 or 37:
                    double number = ParseItemFloat(value);
                    float scalar = (float)number;
                    // 008419EC/00841A00 compare against the authored +/-0.0001f.
                    if (!(number <= -0.0001f || scalar >= 0.0001f)) continue;
                    replacement = scalar.ToString("R", CultureInfo.InvariantCulture);
                    break;
                default:
                    continue;
            }
            attributes[key] = original with { Value = replacement };
        }
        var effects = ImmutableArray.CreateBuilder<EnbItemSpecial>();
        if (t is not null)
        {
            EnbItemEffectInformation.Append(effects,t.Info.VectorA,activatedEffects);
            EnbItemEffectInformation.Append(effects,t.Info.VectorB,equipEffects);
        }
        var rows = ImmutableArray.CreateBuilder<EnbItemAttribute>();
        if (t is not null)
            foreach (var a in t.Info.Attributes)
                rows.Add(a.AttributeId == 1 ? Project(a) : attributes[a.AttributeId]);
        return (rows.ToImmutable(), effects.ToImmutable());
    }
    // CRT 00A276D7: whitespace/sign, decimal prefix, and wrapping 32-bit accumulation.
    private static int ParseItemInteger(ReadOnlySpan<char> text)
    {
        text = TrimItemNumber(text);
        bool negative = !text.IsEmpty && text[0] == '-';
        if (!text.IsEmpty && text[0] is '+' or '-') text = text[1..];
        int value = 0;
        foreach (char c in text)
        {
            if (!char.IsAsciiDigit(c)) break;
            value = unchecked(value * 10 + c - '0');
        }
        return negative ? unchecked(-value) : value;
    }
    private static ReadOnlySpan<char> TrimItemNumber(ReadOnlySpan<char> text)
    {
        int start = 0;
        while (start < text.Length && text[start] is ' ' or '\t' or '\n' or '\r' or '\v' or '\f') start++;
        return text[start..];
    }
    // 00A278F6 -> 00A3D932 accepts decimal prefixes, including D exponents.
    private static double ParseItemFloat(ReadOnlySpan<char> text)
    {
        text = TrimItemNumber(text);
        int end = 0;
        if (end < text.Length && text[end] is '+' or '-') end++;
        int digits = end;
        while (end < text.Length && char.IsAsciiDigit(text[end])) end++;
        bool any = end > digits;
        if (end < text.Length && text[end] == '.')
        {
            digits = ++end;
            while (end < text.Length && char.IsAsciiDigit(text[end])) end++;
            any |= end > digits;
        }
        if (!any) return 0;
        int exponent = -1;
        if (end < text.Length && text[end] is 'e' or 'E' or 'd' or 'D')
        {
            exponent = end++;
            if (end < text.Length && text[end] is '+' or '-') end++;
            digits = end;
            while (end < text.Length && char.IsAsciiDigit(text[end])) end++;
            if (end == digits) { end = exponent; exponent = -1; }
        }
        var prefix = text[..end];
        if (exponent >= 0 && text[exponent] is 'd' or 'D')
            return double.TryParse(prefix.ToString().Replace('d', 'e').Replace('D', 'E'), NumberStyles.Float,
                CultureInfo.InvariantCulture, out double d) ? d : 0;
        return double.TryParse(prefix, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : 0;
    }
    private static string Name(Net7AuxDataProperty p) => p.Path[(p.Path.LastIndexOf('.') + 1)..];
    internal static Net7AuxDataProperty? Child(Net7AuxDataProperty? p, string name)
    {
        if(p is null)return null;
        // The schema owns these path strings. Compare their leaf spans rather
        // than allocating a substring and predicate for every visited child.
        var children=p.Children;
        for(int i=0;i<children.Count;i++)
        {
            var child=children[i];
            if(child.Path.AsSpan(child.Path.LastIndexOf('.')+1).SequenceEqual(name.AsSpan()))return child;
        }
        return null;
    }
    private static bool Bool(Net7AuxDataProperty? p)=>p?.Value is byte b&&b!=0;
    internal static int? Int(Net7AuxDataProperty? p) => p?.Value is int i ? i : p is { State: not 0 } ? 0 : null;
    internal static float? Float(Net7AuxDataProperty? p) => p?.Value is float f && float.IsFinite(f) ? f : null;
    internal static uint? Raw(Net7AuxDataProperty? p) => p?.Value is byte[] { Length: 4 } b ? BinaryPrimitives.ReadUInt32LittleEndian(b) : null;
    internal static string Value(Net7AuxDataProperty? p) => p?.Value switch
    { Net7AuxDataString s => s.Text, string s => s, byte[] { Length: 4 } b => BinaryPrimitives.ReadInt32LittleEndian(b).ToString(CultureInfo.InvariantCulture),
      IFormattable f => f.ToString(null, CultureInfo.InvariantCulture), null when p is { State: not 0, Kind: Net7AuxDataValueKind.Int32LittleEndian or Net7AuxDataValueKind.Float32LittleEndian or Net7AuxDataValueKind.Raw4 } => "0", _ => "" };
}
