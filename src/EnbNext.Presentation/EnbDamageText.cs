using System.Globalization;
using EnbNext.Assets;
using EnbNext.Net7.Gameplay;

namespace EnbNext.Presentation;

/// <summary>Current client 00738970: format and style selection before attaching
/// to the cockpit (00583DC0) or victim effect (006EF4C0).</summary>
public readonly record struct EnbDamageText(string Text, string Style, string? Header, string? HeaderStyle, bool Incoming, bool Reactor)
{
    public static EnbDamageText? Resolve(Net7DamagePresentation damage, uint localId)
    {
        bool incoming = damage.VictimId == localId;
        if (!incoming && damage.SourceId != localId) return null;
        int value = (int)(damage.Damage + .5), modifier = (int)damage.Modifier;
        string text = value.ToString(CultureInfo.InvariantCulture);
        if (modifier != 0) text += modifier > 0 ? $" (+{modifier.ToString(CultureInfo.InvariantCulture)})" : $" ({modifier.ToString(CultureInfo.InvariantCulture)})";
        bool critical = damage.Inflicted == 3;
        string style = damage.Type switch
        {
            2 => "VictimDamagePlasma", 4 => "VictimDamageEMP", 5 => "VictimDamageChemical",
            0 or 1 or 3 when critical => "VictimDamageCritical", _ => "VictimDamageNormal"
        };
        string? header = critical ? "Critical!" : damage.Type == 4 ? "EMP" : null;
        string? headerStyle = header is null ? null : (incoming ? "Player" : "Victim") + (critical ? "CriticalDamageHeader" : "EMPHeader");
        return new(text, style, header, headerStyle, incoming, damage.Type == 4);
    }
}

/// <summary>Damage styles use FADE, SCROLL, SCALE and CYCLE from 006CA0C0.
/// Completion is the sticky byte at +404, queried by cockpit vtable+1A8;
/// outgoing effects instead retain their authored 2000 ms owner lifetime.</summary>
public sealed class EnbDamageTextEffects
{
    private readonly Effect[] _effects;
    private long _fadeTime, _scaleTime;
    private float _scroll;
    public float Opacity { get; private set; } = 1;
    public float Scale { get; private set; } = 1;
    public float Scroll { get; private set; }
    public bool Complete { get; private set; }
    private readonly record struct Effect(string Type, float Initial, float Intensity, float Speed);
    private EnbDamageTextEffects(Effect[] effects) => _effects = effects;

    public static bool TryCreate(EnbInterfaceTextDefinition style, EnbRetailIniDocument fontArt, out EnbDamageTextEffects effects)
    {
        effects = null!;
        var list = new List<Effect>();
        foreach (var id in style.Properties.GetValueOrDefault("EffectOn", "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var definition = fontArt.EnumerateSections().Select(s => s.Value).FirstOrDefault(s => s.GetValueOrDefault("EffectID") == id);
            // Retail's name lookup returns null for NONE and unknown effect names.
            if (definition is null) continue;
            string type = definition.GetValueOrDefault("EffectType", "");
            if (type is not ("FADE" or "SCROLL" or "SCALE" or "CYCLE") || type == "SCROLL" && definition.GetValueOrDefault("Direction") != "UP") return false;
            list.Add(new(type, Read("Initial"), Read("Intensity"), Read("Speed")));
            float Read(string key) => float.TryParse(definition.GetValueOrDefault(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : 0;
        }
        effects = new(list.ToArray());
        return true;
    }

    public void Advance(uint elapsed)
    {
        // 006D64C0 builds the translation before executing this frame's effects.
        Scroll = _scroll;
        foreach (var effect in _effects)
        {
            switch (effect.Type)
            {
                case "FADE":
                    Opacity = Move(effect.Initial < 0 ? Opacity : effect.Initial, effect.Intensity,
                        effect.Speed * .0005f * (effect.Initial < 0 ? elapsed : _fadeTime));
                    _fadeTime += elapsed;
                    Complete |= Opacity == effect.Intensity;
                    break;
                case "SCALE":
                    Scale = Move(effect.Initial < 0 ? Scale : effect.Initial, effect.Intensity,
                        effect.Speed * .002f * (effect.Initial < 0 ? elapsed : _scaleTime));
                    _scaleTime += elapsed;
                    Complete |= Scale == effect.Intensity;
                    break;
                case "SCROLL": _scroll += elapsed * effect.Speed * .02f; break;
                case "CYCLE": Complete = true; break;
            }
        }
    }

    private static float Move(float start, float target, float amount)
        => start < target ? Math.Min(target, start + amount) : Math.Max(target, start - amount);
}
