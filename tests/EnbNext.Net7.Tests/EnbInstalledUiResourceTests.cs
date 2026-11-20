using System.Reflection;
using EnbNext.Assets;
using EnbNext.Engine;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

[Xunit.Trait("Category", "Heavy")] // builds every fixed retail UI resource from the installed client
public sealed class EnbInstalledUiResourceTests
{
    private static readonly string[] SessionResources =
    [
        "OptionsPanels", "UtilityHud", "Information", "Frontend", "Gutter", "ShipName", "BrowserPanel",
        "Confirmations", "VerticalScrollbar", "SettingsFontMetrics", "SliderPanels", "HorizontalSlider",
        "Controls", "TooltipResources", "FontEffects",
    ];

    [Fact]
    public void EveryFixedRetailUiResourceLoadsFromTheInstalledClient()
    {
        // The fixed-name UI art loaders throw only when an authored retail file is
        // absent or malformed; with the installed client none of them is reached.
        var assets = EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var session = EnbRenderSession.Of(assets);
        var failures = new List<string>();
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        foreach (string name in SessionResources)
        {
            object? owner = Get(session, typeof(EnbRenderSession).GetProperty(name, instance)!, name, failures);
            if (owner is null) continue;
            foreach (PropertyInfo property in owner.GetType().GetProperties(instance))
                if (property.GetIndexParameters().Length == 0 && property.GetMethod is not null)
                    Get(owner, property, name + "." + property.Name, failures);
        }
        foreach (string prefix in new[] { "log", "help", "petition", "player", "complaint" })
            Run(() => session.Information.Get(prefix), "Information.Get(" + prefix + ")", failures);
        using (var inventory = new EnbInventoryUi(() => 1))
        {
            Run(() => typeof(EnbInventoryUi).GetMethod("Load", instance)!.Invoke(inventory, [assets]), "EnbInventoryUi.Load", failures);
            var art = (System.Collections.ICollection)typeof(EnbInventoryUi).GetField("_art", instance)!.GetValue(inventory)!;
            Assert.True(art.Count > 100, $"inventory art {art.Count}");
            // invitem.w3d supplies the shortcut drag geometry PreviewCursor requires.
            object drag = ((System.Collections.IDictionary)art)["dragItem"]!;
            object design = drag.GetType().GetProperty("Design")!.GetValue(drag)!;
            Assert.True(Convert.ToDouble(design.GetType().GetProperty("Width")!.GetValue(design)) > 0);
            // Station terminal art (LoadTerminal / UpdateTerminalPose throw only for
            // a missing or malformed authored terminal model or animation).
            typeof(EnbInventoryUi).GetField("_assets", instance)!.SetValue(inventory, assets);
            // Group formation art (EnbGroupFormationVisual names only these six models);
            // FormationArt's missing/empty branch is not reached with the installed client.
            MethodInfo formation = typeof(EnbInventoryUi).GetMethod("FormationArt", instance)!;
            foreach (string model in new[] { "tsi03a_form0", "tsi03a_form1", "tsi03a_form2", "tsi03a_form3", "tsi03a_ShipLrg", "tsi03a_ShipSml" })
                if (formation.Invoke(inventory, [model]) is null) failures.Add("FormationArt(" + model + ")");
            MethodInfo conversation = typeof(EnbInventoryUi).GetMethod("LoadConversationArt", instance)!;
            foreach (var (space, recovery) in new[] { (false, false), (true, false), (false, true) })
                Run(() => conversation.Invoke(inventory, [space, recovery]), $"LoadConversationArt({space},{recovery})", failures);
            MethodInfo load = typeof(EnbInventoryUi).GetMethod("LoadTerminal", instance)!;
            MethodInfo pose = typeof(EnbInventoryUi).GetMethod("UpdateTerminalPose", instance)!;
            foreach (string model in new[] { "tsm03a", "tsm02a", "tsm01a", "jnews00", "buy_ship01", "buy_ship02", "buy_ship03", "buy_ship04", "buy_avatar00", "jweb00" })
            {
                Run(() => load.Invoke(inventory, [model]), "LoadTerminal(" + model + ")", failures);
                for (long tick = 1; tick <= 20001; tick += 500)
                    Run(() => pose.Invoke(inventory, [model, true, Environment.TickCount64 + tick]), "UpdateTerminalPose(" + model + ")", failures);
            }
        }
        // blood.ini (008117D0): every BASE selection resolves to an authored section
        // with readable values, so the missing-section/invalid-value throws are unreached.
        var blood = assets.Blood!;
        for (int id = 0; id <= 32767; id++)
            foreach (bool organic in new[] { false, true })
                Run(() => blood.Resolve(id, organic), $"Blood.Resolve({id},{organic})", failures);
        // avatardef.ini creation defaults (004C0310/004C05D0) for every race/profession/gender.
        var avatarDefinitions = EnbRetailIniDocument.Load(Path.Combine(assets.Paths.CommonDataDirectory, "avatardef.ini"));
        for (uint race = 0; race < 3; race++)
            for (uint profession = 0; profession < 3; profession++)
                for (uint gender = 0; gender < 2; gender++)
                {
                    uint r = race, p = profession, g = gender;
                    Run(() => EnbNext.Client.EnbRetailCharacterCreationDefaults.Avatar(avatarDefinitions, r, p, g), $"CreationDefaults({r},{p},{g})", failures);
                }
        // Authored fontart.ini effects: the five implemented types admit; the other
        // authored types (PULSE, SCROLL, WIPE, GLOW, DROP, SCALE, REVERT, CYCLE, SHAKE,
        // TOGGLE, MOVE, LETTER, STRETCH, SWITCH, SCROLL_REVERT) and RANDLETTER other than
        // RANDOM reveal (Initial 0, Intensity 1) are an open gap recorded in the restriction-audit notebook.
        var fontArt = EnbRetailIniDocument.Load(Path.Combine(assets.Paths.ClientIniDirectory, "fontart.ini"));
        var gaps = new List<string>();
        int fontEffects = 0;
        foreach (var section in fontArt.EnumerateSections().Select(x => x.Value))
            if (section.GetValueOrDefault("EffectID") is { } effectId)
            {
                fontEffects++;
                var style = new EnbInterfaceTextDefinition("probe", default, "probe", new Dictionary<string, string> { ["EffectOn"] = effectId, ["EffectConst"] = effectId, ["EffectOff"] = effectId });
                string type = section.GetValueOrDefault("EffectType") ?? "";
                bool implemented = type is "FADE" or "STAY" or "ROTATE" or "BLINK";
                if (!EnbNext.Presentation.EnbMessageFontEffects.TryCreate(style, fontArt, out _, "probe text"))
                    (implemented ? failures : gaps).Add($"font effect {effectId} type {type} dir {section.GetValueOrDefault("Direction")}");
            }
        var lineLayout = EnbInterfaceLayoutCatalog.Load(Path.Combine(assets.Paths.ClientIniDirectory, "fc_line_display.ini"));
        var lineDefinitions = EnbRetailIniDocument.Load(Path.Combine(assets.Paths.ClientIniDirectory, "fc_line_display.ini"));
        for (int i = 1; lineDefinitions.TryGetSection($"STR-{i}", out var lineSection); i++)
            if (lineSection.TryGetValue("StringID", out var lineName) && lineLayout.TryResolve(lineName, out var lineStyle) &&
                !EnbNext.Presentation.EnbMessageFontEffects.TryCreate(lineStyle, fontArt, out _))
                gaps.Add($"message line style {lineName}: on={lineStyle.Properties.GetValueOrDefault("EffectOn")} const={lineStyle.Properties.GetValueOrDefault("EffectConst")} off={lineStyle.Properties.GetValueOrDefault("EffectOff")}");
        // The incoming-message dialog style (fa_tsi05b.ini message_TSI05B) must admit.
        Assert.True(EnbInterfaceLayoutCatalog.Load(Path.Combine(assets.Paths.ClientIniDirectory, "fa_tsi05b.ini")).TryResolve("message_TSI05B", out var incomingStyle));
        if (!EnbNext.Presentation.EnbMessageFontEffects.TryCreate(incomingStyle, fontArt, out _, "probe"))
            gaps.Add($"incoming message style: on={incomingStyle.Properties.GetValueOrDefault("EffectOn")} const={incomingStyle.Properties.GetValueOrDefault("EffectConst")} off={incomingStyle.Properties.GetValueOrDefault("EffectOff")}");
        // Damage text styles (00738970) resolve in the cockpit layout, admit their
        // effects (006CA0C0 FADE/SCROLL UP/SCALE/CYCLE) and name an authored colour.
        var colourIds = fontArt.EnumerateSections().Select(x => x.Value.GetValueOrDefault("ColorID")).OfType<string>().ToHashSet();
        foreach (string damageStyle in new[] { "VictimDamagePlasma", "VictimDamageEMP", "VictimDamageChemical", "VictimDamageCritical", "VictimDamageNormal",
                     "PlayerCriticalDamageHeader", "VictimCriticalDamageHeader", "PlayerEMPHeader", "VictimEMPHeader" })
        {
            if (assets.CockpitLayout?.TryResolve(damageStyle, out var resolvedDamage) != true) { failures.Add("damage style missing " + damageStyle); continue; }
            if (!EnbDamageTextEffects.TryCreate(resolvedDamage, fontArt, out _)) failures.Add("damage style effects " + damageStyle);
            if (!colourIds.Contains(resolvedDamage.Properties.GetValueOrDefault("Color", ""))) failures.Add("damage style colour " + damageStyle);
        }
        // Every installed font profile entry builds its glyph face (EnbRetailFontFactory).
        int faces = 0;
        foreach (var profile in assets.Fonts!.Profiles)
            foreach (var font in profile.Fonts)
            {
                if (string.IsNullOrWhiteSpace(font.Resource)) { failures.Add("font without resource " + font.FontId); continue; }
                if (EnbRetailFontFactory.TryCreate(assets, font, out _)) faces++; else failures.Add("font face " + font.FontId + " " + font.Resource);
            }
        Assert.True(faces > 0);
        Assert.True(EnbTargetReticleRasterFactory.TryCreate(assets, new EnbRgb(1, 1, 1), out _));
        Assert.True(EnbGutterFrameGeometry.TryCreate(assets, out _));
        File.WriteAllLines(Path.Combine(Path.GetTempPath(), "enbnext-font-effect-gaps.txt"), gaps);
        Assert.True(fontEffects > 0);
        using (var chat = new EnbChatUi(() => 1))
        {
            typeof(EnbChatUi).GetField("_assets", instance)!.SetValue(chat, assets);
            typeof(EnbChatUi).GetMethod("LoadChatCombo", instance)!.Invoke(chat, null);
            Assert.NotNull(typeof(EnbChatUi).GetField("_comboAsset", instance)!.GetValue(chat));
        }
        Assert.NotEmpty(session.Information.Get("log").Pieces);
        Assert.Empty(failures);
    }

    private static object? Get(object owner, PropertyInfo property, string name, List<string> failures)
    {
        object? value = null;
        Run(() => value = property.GetValue(owner), name, failures);
        return value;
    }

    private static void Run(Action action, string name, List<string> failures)
    {
        try { action(); }
        catch (Exception ex)
        {
            Exception inner = ex is TargetInvocationException { InnerException: { } cause } ? cause : ex;
            failures.Add(name + ": " + inner.GetType().Name + " " + inner.Message);
        }
    }
}
