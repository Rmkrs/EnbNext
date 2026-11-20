using System.Globalization;
using EnbNext.Assets;
using EnbNext.Presentation;
using Rect = EnbNext.Presentation.EnbUiRect;

namespace EnbNext.Engine;

/// <summary>
/// Next Modern UI skills window: every skill on one line, all visible at once: its name, its ranks as pips,
/// the abilities it gives (dragged to the action bar exactly as from the retail skill panel) and the skill-up
/// marker when training is available. Tooltips carry the retail skill, rank and ability descriptions. As in
/// retail, a click selects a skill and opens its details beside the window; only that pane's Upgrade button
/// trains (the retail Upgrade request), so a stray click never spends skill points.
/// </summary>
internal sealed partial class EnbInventoryUi
{
    private bool _modernSkills;
    /// <summary>The skill whose details are open beside the window (-1: none).</summary>
    private int _modernSkillSelected = -1;
    private Rect _modernSkillsBounds;
    /// <summary>Saved top-left as a fraction of the screen; null centres the window.</summary>
    internal EnbUiPoint? ModernSkillsPosition { get; set; }
    internal Action<EnbUiPoint>? ModernSkillsMoved { get; set; }
    public bool ModernSkillsOpen => _modernSkills;
    /// <summary>The Modern UI action bar's first slot on screen: an ability dragged from the skills window takes its size.</summary>
    internal Func<Rect?>? ModernShortcutBounds { get; set; }
    private int? ModernAbilityDragPixels()
    {
        if (!_modernSkills || !_modernSkillsBounds.Contains(_pressOrigin)) return null;
        double size = ModernShortcutBounds?.Invoke() is { } slot ? Math.Min(slot.Width, slot.Height) : 40 * ModernUnit;
        return Math.Max(8, (int)Math.Round(size * _scale));
    }
    /// <summary>A skill family's colour, as the retail detail title uses it (combat green, trade purple, explore blue, else cyan).</summary>
    private EnbRgb ModernSkillColor(EnbCharacterSkillSnapshot skill) => _assets?.ResolveShortcutSkills()?.FindFamily(skill.Name)?.Category switch
    { "Combat" => new(.5f, 1, .5f), "Trade" => new(.75f, .25f, 1), "Explore" => new(.2f, .5f, 1), _ => new(0, 1, 1) };

    public bool ToggleModernSkills()
    {
        if (!_interactive || !ModernUi) return false;
        _modernSkills = !_modernSkills;
        _modernSkillSelected = -1;
        if (_modernSkills) { RaiseModern("mskill"); Opened?.Invoke(); }
        InteractionSoundRequested?.Invoke("TextClick03.wav");
        Publish(); return true;
    }

    /// <summary>The activated abilities a skill gives this character class, in the retail panel's order.</summary>
    private EnbRetailSkillAbility[] ModernSkillAbilities(EnbCharacterSkillSnapshot skill, uint race, uint profession)
    {
        if (_assets?.ResolveShortcutSkills() is not { } catalog) return [];
        int classMaximum = catalog.FindFamily(skill.Name)?.ClassMaximum(race, profession) ?? 0;
        return catalog.Abilities.Where(a => a.Activated && a.FamilyName.Equals(skill.Name, StringComparison.OrdinalIgnoreCase) && a.MinimumLevel <= classMaximum)
            .OrderBy(a => a.MinimumLevel).ThenBy(a => a.Id).ToArray();
    }

    private void ModernSkillsWindow()
    {
        if (Data is not { } data) return;
        var stats = data.CharacterStats;
        int Stat(string key) => int.TryParse(stats.GetValueOrDefault(key, ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
        uint race = (uint)Stat("Race"), profession = (uint)Stat("Profession");
        var skills = data.Skills.Where(s => s.VisibleInSkillPanel).ToArray();
        var abilities = skills.Select(s => ModernSkillAbilities(s, race, profession)).ToArray();
        double u = ModernUnit, text = 14 * u, small = 13 * u, title = 16 * u, gap = 4 * u, pad = 10 * u, head = 24 * u;
        double rowHeight = Math.Round(26 * u), pip = Math.Round(11 * u), pipGap = Math.Round(4 * u), icon = Math.Round(22 * u), iconGap = Math.Round(3 * u), arrow = Math.Round(16 * u);
        // Columns measured from the widest content, so every row lines up and nothing is squeezed.
        double nameWidth = skills.Length == 0 ? 0 : skills.Max(s => ModernMeasure(s.Name, text));
        int pips = skills.Length == 0 ? 0 : skills.Max(s => Math.Min(11, s.Maximum)), icons = abilities.Length == 0 ? 0 : abilities.Max(a => Math.Min(7, a.Length));
        double pipsWidth = pips * pip + Math.Max(0, pips - 1) * pipGap, iconsWidth = icons * icon + Math.Max(0, icons - 1) * iconGap;
        double column = 14 * u;
        double rowWidth = Math.Ceiling(8 * u + nameWidth + column + pipsWidth + (icons > 0 ? column + iconsWidth : 0) + column + arrow + 8 * u);
        string pointsLabel = "Skill points available: " + Stat("SkillPoints").ToString(CultureInfo.InvariantCulture);
        double contentWidth = Math.Max(rowWidth, Math.Max(ModernMeasure("Skills", title) + head + 40 * u, ModernMeasure(pointsLabel, text) + 16 * u));
        double width = pad * 2 + contentWidth, height = pad * 2 + head + gap + skills.Length * rowHeight + gap + head;
        double x, y;
        if (ModernSkillsPosition is { } at) { x = at.X * _size.Width; y = at.Y * _size.Height; }
        else { x = (_size.Width - width) / 2; y = (_size.Height - height) * .4; }
        x = Snap(Math.Clamp(x, 0, Math.Max(0, _size.Width - width))); y = Snap(Math.Clamp(y, 0, Math.Max(0, _size.Height - height)));
        _modernSkillsBounds = new(x, y, width, height);
        ModernFrame("DIALOG_BOX_WINDOW", _modernSkillsBounds, "mskill/frame");
        Flush();
        double cx = x + pad, cy = y + pad;
        ModernText("mskill/title", "Skills", new(cx, cy, contentWidth - head, head), new(1, .85f, .3f), title);
        ModernCloseButton("mskill/close", new(x + width - pad - head, cy, head, head));
        cy += head + gap;
        var list = new Rect(cx, cy, contentWidth, skills.Length * rowHeight);
        ModernFrame("DIALOG_BOX_INFO", list);
        Flush();
        double nameX = cx + 8 * u, pipsX = nameX + nameWidth + column, iconsX = pipsX + pipsWidth + column, arrowX = cx + contentWidth - 8 * u - arrow;
        var catalog = _assets?.ResolveShortcutSkills();
        for (int i = 0; i < skills.Length; i++)
        {
            var skill = skills[i];
            var row = new Rect(cx, Snap(cy + i * rowHeight), contentWidth, rowHeight);
            string id = "mskill/row/" + skill.Id.ToString(CultureInfo.InvariantCulture), prefix = id + "/";
            Hit(id, FromPixels(row));
            // Every other row slightly darker, so a row reads across from its name to its skill-up marker.
            if (i % 2 == 1) _images.Add(new(EnbModernIcons.Fill(new(0, 0, 0), 56), new(row.X + 2 * u, row.Y, row.Width - 4 * u, row.Height)));
            bool selected = skill.Id == _modernSkillSelected;
            if (selected) _images.Add(new(EnbModernIcons.Fill(new(.45f, .65f, 1), 46), new(row.X + 2 * u, row.Y, row.Width - 4 * u, row.Height)));
            bool hot = selected || _hover is { } hovered && (hovered == id || hovered.StartsWith(prefix, StringComparison.Ordinal) || abilities[i].Any(a => hovered == "ability/" + a.Id.ToString(CultureInfo.InvariantCulture)));
            if (hot) _images.Add(new(EnbModernIcons.SlotHighlight((int)Math.Round(row.Width * _scale), (int)Math.Round(row.Height * _scale)), row));
            // 00663BE0: an untrained skill is darkened.
            bool trained = skill.Level > 0;
            var nameColor = ModernSkillColor(skill);
            if (!trained) nameColor = new(nameColor.R * .5f + .1f, nameColor.G * .5f + .1f, nameColor.B * .5f + .12f);
            ModernText(id + "/name", skill.Name, new(nameX, row.Y, nameWidth, row.Height), nameColor, text);
            string description = catalog?.FindFamily(skill.Name)?.Description ?? skill.Description;
            _tips[id] = $"{(trained ? "Level " + skill.Level.ToString(CultureInfo.InvariantCulture) : "Unavailable")} {(abilities[i].Length > 0 ? "Activated" : "Passive")} Skill\n\n"
                + string.Concat(EnbRetailDialogText.Wrap(description, 40, _ => 1).Select(line => line + "\n")) + "\nClick for details";
            // Ranks: a filled pip per trained rank, an empty one per rank still to train (the retail notches).
            int pipPixels = (int)Math.Round(pip * _scale);
            for (int rank = 0; rank < Math.Min(11, skill.Maximum); rank++)
            {
                var r = new Rect(Snap(pipsX + rank * (pip + pipGap)), Snap(row.Center.Y - pip / 2), pip, pip);
                _images.Add(new(EnbModernIcons.Pip(pipPixels, rank < skill.Level), new(r.X, r.Y, pipPixels / _scale, pipPixels / _scale)));
                string notch = prefix + "rank/" + (rank + 1).ToString(CultureInfo.InvariantCulture);
                Hit(notch, FromPixels(new(r.X - pipGap / 2, row.Y, pip + pipGap, row.Height)));
                _tips[notch] = catalog?.FindFamily(skill.Name)?.LevelDescriptions.GetValueOrDefault(rank + 1, "") ?? "";
            }
            // Abilities: the retail ability ids, so a drag lands on the action bar exactly as from the retail panel.
            Flush();
            for (int n = 0; n < Math.Min(7, abilities[i].Length); n++)
            {
                var ability = abilities[i][n];
                var r = new Rect(Snap(iconsX + n * (icon + iconGap)), Snap(row.Center.Y - icon / 2), icon, icon);
                // Only an ability the skill already gives can go on the action bar; a later one is shown, not dragged.
                bool unlocked = skill.Level >= ability.MinimumLevel;
                string abilityId = (unlocked ? "ability/" : prefix + "locked/") + ability.Id.ToString(CultureInfo.InvariantCulture);
                ImageTexture(skill.Icon, FromPixels(r), unlocked && _hover == abilityId ? System.Numerics.Vector3.One : ability.IconTint(skill.Level));
                Hit(abilityId, FromPixels(r));
                _tips[abilityId] = (catalog?.Describe(ability, skill.Level) ?? "") + (unlocked
                    ? "\nDrag this icon to your shortcut bar to activate or configure."
                    : $"\nTrain {skill.Name} to level {ability.MinimumLevel} to use this ability.");
            }
            Flush();
            // Skill-up: shown when the server reports the next rank is available (retail UP00).
            if (skill.ShowUpgradeIndicator)
            {
                // An indicator only: a click selects the skill, like any other part of its row.
                string up = prefix + "up";
                var r = new Rect(Snap(arrowX), Snap(row.Center.Y - arrow / 2), arrow, arrow);
                int arrowPixels = (int)Math.Round(arrow * _scale);
                _images.Add(new(EnbModernIcons.UpArrow(arrowPixels, _hover == up || selected), new(r.X, r.Y, arrowPixels / _scale, arrowPixels / _scale)));
                Hit(up, FromPixels(r));
                var cost = skill.Availability?.UpgradeCost ?? 0;
                _tips[up] = skill.CanTrain
                    ? $"Level {skill.Level + 1} available: {cost} skill point{(cost == 1 ? "" : "s")}\nClick for details and the Upgrade button."
                    : skill.UpgradeReason.Length > 0 ? skill.UpgradeReason : "Upgrade available.";
            }
        }
        cy += skills.Length * rowHeight + gap;
        ModernText("mskill/points", pointsLabel, new(cx + 8 * u, cy, contentWidth - 16 * u, head), new(.75f, .8f, .92f), text);
        Flush();
        if (skills.FirstOrDefault(s => s.Id == _modernSkillSelected) is { } chosen) ModernSkillDetails(chosen, Stat("SkillPoints"), race, profession);
        else _modernSkillSelected = -1;
    }

    /// <summary>
    /// The selected skill's details, beside the window (the retail TSI12C pane's content): description, this level,
    /// the next level, the cost and the Upgrade button, or the reason it cannot be trained yet.
    /// </summary>
    private void ModernSkillDetails(EnbCharacterSkillSnapshot skill, int points, uint race, uint profession)
    {
        double u = ModernUnit, text = 14 * u, small = 13 * u, title = 16 * u, gap = 4 * u, pad = 12 * u, head = 24 * u, line = Math.Round(18 * u);
        var window = _modernSkillsBounds;
        double width = Math.Round(320 * u), height = window.Height;
        double x = window.Right + gap <= _size.Width - width ? window.Right + gap : Math.Max(0, window.X - gap - width);
        var bounds = new Rect(Snap(x), window.Y, width, height);
        ModernFrame("DIALOG_BOX_WINDOW", bounds, "mskill/detail");
        Flush();
        var family = _assets?.ResolveShortcutSkills()?.FindFamily(skill.Name);
        var titleColor = ModernSkillColor(skill);
        double cx = bounds.X + pad, cw = width - pad * 2, cy = bounds.Y + pad;
        ModernText("mskill/detail/title", skill.Name, new(cx, cy, cw - head, head), titleColor, title);
        ModernCloseButton("mskill/detail/close", new(bounds.Right - pad - head, cy, head, head));
        cy += head;
        var muted = new EnbRgb(.75f, .8f, .92f);
        ModernText("mskill/detail/level", skill.Level > 0 ? $"Level {skill.Level} of {skill.Maximum}" : "Not trained", new(cx, cy, cw, line), muted, small);
        cy += line + gap * 2;
        // The footer (points, cost, the Upgrade button or its reason) keeps its place at the bottom.
        double footerTop = bounds.Bottom - pad - head - gap - line * 2;
        int n = 0;
        void Section(string heading, string body)
        {
            if (body.Length == 0 || cy + line * 2 > footerTop) return;
            ModernText("mskill/detail/h" + n, heading, new(cx, cy, cw, line), new(1, .85f, .3f), small);
            cy += line;
            foreach (string wrapped in ModernWrap(body.Replace("\\n", "\n"), cw, text))
            {
                if (cy + line > footerTop) break;
                ModernText("mskill/detail/t" + n++, wrapped, new(cx, cy, cw, line), new(1, 1, 1), text);
                cy += line;
            }
            cy += gap * 2;
        }
        Section("Description", family?.Description ?? skill.Description);
        if (skill.Level == 0) Section("Not acquired", family?.AcquisitionDescription(race, profession) ?? "");
        else Section("Current level", family?.LevelDescriptions.GetValueOrDefault(skill.Level, "") ?? "");
        Section("Next level", ((skill.Availability?.Raw1 ?? 0) & 0x40) != 0 ? "You have already attained the maximum level for this skill." : family?.LevelDescriptions.GetValueOrDefault(skill.Level + 1, "") ?? "");
        double fy = footerTop;
        ModernText("mskill/detail/points", "Skill points available: " + points.ToString(CultureInfo.InvariantCulture), new(cx, fy, cw, line), muted, small);
        fy += line;
        if (skill.Availability is { } availability && !skill.HideUpgradeCost)
            ModernText("mskill/detail/cost", $"Upgrade costs {availability.UpgradeCost} skill point{(availability.UpgradeCost == 1 ? "" : "s")}", new(cx, fy, cw, line), muted, small);
        fy += line + gap;
        // 00548610: Upgrade is offered only when training is possible; otherwise the reason takes its place.
        if (skill.CanTrain)
        {
            double buttonWidth = Math.Ceiling(ModernMeasure("Upgrade", text) + 40 * u);
            ModernButton("mskill/upgrade", "Upgrade", new(bounds.Right - pad - buttonWidth, fy, buttonWidth, head), text, $"Train {skill.Name} to level {skill.Level + 1}.");
        }
        else if (skill.UpgradeReason.Length > 0)
            ModernText("mskill/detail/reason", skill.UpgradeReason, new(cx, fy, cw, head), new(1, .55f, .3f), text);
        Flush();
    }

    /// <summary>Splits text into lines that fit a width at a Modern UI text size, keeping its own line breaks.</summary>
    private List<string> ModernWrap(string text, double width, double size)
    {
        var lines = new List<string>();
        foreach (string paragraph in text.Replace("\r", "").Split('\n'))
        {
            string current = "";
            foreach (string word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                string candidate = current.Length == 0 ? word : current + " " + word;
                if (current.Length > 0 && ModernMeasure(candidate, size) > width) { lines.Add(current); current = word; }
                else current = candidate;
            }
            lines.Add(current);
        }
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    private async Task<bool> ActivateModernSkills(string id, bool right)
    {
        if (!id.StartsWith("mskill/", StringComparison.Ordinal)) return false;
        // Retail Skills Upgrade and close buttons also take an ordinary right press (SKILL-D07); other rows stay left-only.
        if (right && id is not ("mskill/close" or "mskill/upgrade")) return true;
        if (id == "mskill/close") { _modernSkills = false; _modernSkillSelected = -1; InteractionSoundRequested?.Invoke("TextClick03.wav"); return true; }
        if (id == "mskill/detail/close") { _modernSkillSelected = -1; InteractionSoundRequested?.Invoke("Roll_Select02b.wav"); return true; }
        // A click anywhere on a row (name, pips, marker) selects that skill and opens its details; again closes them.
        if (id.StartsWith("mskill/row/", StringComparison.Ordinal))
        {
            var rest = id.AsSpan(11);
            int slash = rest.IndexOf('/');
            if (int.TryParse(slash < 0 ? rest : rest[..slash], NumberStyles.Integer, CultureInfo.InvariantCulture, out int clicked))
            {
                _modernSkillSelected = _modernSkillSelected == clicked ? -1 : clicked;
                InteractionSoundRequested?.Invoke("fontclick.wav");
            }
            return true;
        }
        // Only the details pane's Upgrade button trains: the retail Upgrade request (00548610 -> action2).
        if (id == "mskill/upgrade" && !_busy && _owner is uint owner && Data?.Skills.FirstOrDefault(s => s.Id == _modernSkillSelected) is { CanTrain: true } skill && TrainRequested is { } train)
        {
            InteractionSoundRequested?.Invoke("skill_train_1.wav");
            var request = new EnbCharacterActionRequest(owner, _ship, skill.Id, skill.Level);
            await Send(() => train(request));
        }
        return true;
    }
}
