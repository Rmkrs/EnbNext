using System.Globalization;
using EnbNext.Assets;

namespace EnbNext.Presentation;

/// <summary>Authored screen-message effects. Retail 006CA0C0 cases 2/3/4/5/13,
/// with completion queried by 005A69B0.</summary>
public sealed class EnbMessageFontEffects
{
    private sealed record Effect(string Type, float Initial, float Intensity, float Speed, float Time);
    private readonly Effect[][] _phases;
    private int _phase;
    private long _phaseTime;
    private long _fadeTime;
    private long _rotateTime;
    private readonly string _text;
    private readonly bool[] _revealed;
    private readonly Func<int> _random15;
    private int _revealCount;
    public float Opacity { get; private set; } = 1;
    public float VerticalScale { get; private set; } = 1;
    public string? DisplayText { get; private set; }
    public bool Complete { get; private set; }

    private EnbMessageFontEffects(Effect[][] phases, string text, Func<int>? random15)
    {
        _phases = phases; _phase = phases[0].Length == 0 ? 1 : 0;
        _text = text; _revealed = new bool[text.Length];
        _random15 = random15 ?? (() => Random.Shared.Next(32768));
    }

    public static bool TryCreate(EnbInterfaceTextDefinition style, EnbRetailIniDocument fontArt,
        out EnbMessageFontEffects effects, string text = "", Func<int>? random15 = null)
    {
        effects = null!;
        var definitions = fontArt.EnumerateSections().Select(s => s.Value)
            .Where(s => s.ContainsKey("EffectID"))
            .ToDictionary(s => s["EffectID"], StringComparer.OrdinalIgnoreCase);
        var phases = new List<Effect[]>();
        foreach (string key in new[] { "EffectOn", "EffectConst", "EffectOff" })
        {
            var phase = new List<Effect>();
            foreach (string id in style.Properties.GetValueOrDefault(key, "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                // 006C2E10 returns null for absent names (including authored NONE).
                if (!definitions.TryGetValue(id, out var d)) continue;
                if (!d.TryGetValue("EffectType", out var type) ||
                    type is not ("FADE" or "STAY" or "ROTATE" or "BLINK" or "RANDLETTER")) return false;
                // 006BFD00: all numeric effect defaults are zero.
                float initial = Read(d, "Initial", 0), intensity = Read(d, "Intensity", 0),
                    speed = Read(d, "Speed", 0), time = Read(d, "Time", 0);
                if (!float.IsFinite(initial) || !float.IsFinite(intensity) || !float.IsFinite(speed) || !float.IsFinite(time) ||
                    type != "STAY" && speed <= 0 ||
                    type == "FADE" && (intensity < 0 || intensity > 1) ||
                    type == "BLINK" && 500 / speed < 1) return false;
                if (type == "RANDLETTER" && (d.GetValueOrDefault("Direction") != "RANDOM" || initial != 0 || intensity != 1)) return false;
                phase.Add(new(type, initial, intensity, speed, time));
            }
            phases.Add(phase.ToArray());
        }
        effects = new(phases.ToArray(), text, random15);
        return true;

        static float Read(IReadOnlyDictionary<string, string> d, string key, float fallback) =>
            d.TryGetValue(key, out var value) && float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float n) ? n : fallback;
    }

    public void Advance(long elapsedMilliseconds)
    {
        if (Complete) return;
        long elapsed = Math.Max(0, elapsedMilliseconds);
        _phaseTime += elapsed;
        var phase = _phases[_phase];
        // 006D89C0: a phase with no authored effects is already complete.
        if (phase.Length == 0) { Complete = true; return; }
        bool finished = false;
        foreach (var effect in phase)
        {
            if (effect.Type == "FADE")
            {
                // The current binary's 00AFEB7C is .0005f, not .001f.
                // Explicit Initial samples the previous fade counter, then
                // advances it; implicit Initial moves the existing alpha.
                float start = effect.Initial < 0 ? Opacity : effect.Initial;
                float amount = effect.Speed * .0005f * (effect.Initial < 0 ? elapsed : _fadeTime);
                Opacity = Math.Clamp(start < effect.Intensity ? Math.Min(effect.Intensity, start + amount)
                    : Math.Max(effect.Intensity, start - amount), 0, 1);
                _fadeTime += elapsed;
                finished = Opacity == effect.Intensity;
            }
            else if (effect.Type == "ROTATE")
            {
                (VerticalScale,finished)=EnbNext.RetailMath.EnbRetailTextRotation.Sample(
                    unchecked((int)_rotateTime),effect.Speed,effect.Initial,effect.Intensity);
                _rotateTime += elapsed;
            }
            else if (effect.Type == "BLINK")
            {
                long period = (long)(500f / effect.Speed);
                long position = _phaseTime % period;
                Opacity = (float)(position <= period * .5 ? position / (period * .5) : (period - position) / (period * .5));
                finished = true;
            }
            else if (effect.Type == "RANDLETTER")
            {
                // 006CA3C2 samples the phase clock, multiplies Speed, truncates,
                // then divides by 200. Completion is strictly past text length.
                long steps = (long)(_phaseTime * (double)effect.Speed) / 200;
                int count = (int)Math.Min(_text.Length, steps);
                while (_revealCount < count)
                {
                    int remaining = _text.Length - _revealCount;
                    int divisor = 32768 / remaining;
                    // The renderer wraps lines well below 32768 glyphs.
                    if (divisor == 0) throw new InvalidOperationException("Message reveal exceeds retail random range.");
                    int choice = (_random15() & 32767) / divisor;
                    for (int i = 0; i < _revealed.Length; i++)
                        if (!_revealed[i] && choice-- == 0) { _revealed[i] = true; break; }
                    _revealCount++;
                }
                char[] glyphs = _text.ToCharArray();
                for (int i = 0; i < glyphs.Length; i++) if (!_revealed[i]) glyphs[i] = ' ';
                DisplayText = new(glyphs);
                finished = steps > _text.Length;
            }
            else if (effect.Time < 0 ? finished : _phaseTime >= (long)(effect.Time * 1000))
            {
                if (_phase == 2) { Complete = true; return; }
                _phase++;
                _phaseTime = effect.Time < 0 ? 0 : _phaseTime - (long)(effect.Time * 1000);
                _fadeTime = 0;
                _rotateTime = 0;
                _revealCount = 0; Array.Clear(_revealed); DisplayText = null;
                if (effect.Time >= 0) VerticalScale = 1;
                return;
            }
        }
        if (_phase == 2 && finished) Complete = true;
    }
}
