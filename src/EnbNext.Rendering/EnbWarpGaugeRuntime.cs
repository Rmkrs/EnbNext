using System.Numerics;
using EnbNext.Net7.Gameplay;
using EnbNext.Presentation;

namespace EnbNext.Rendering;

public readonly record struct EnbWarpGaugeVisual(float Fill, float Red, Vector3? ButtonEmission);

/// <summary>00676190/006762B0, driven by 005821C0 and 00581670.</summary>
public sealed class EnbWarpGaugeRuntime
{
    private ulong? _owner;
    private (int? State, long? End, bool Incapacitated, int? PresentationMode) _key;
    private long _time;
    private int _mode;
    private float _phase, _duration, _rate, _fill, _red = 1, _flash;
    private bool _running;
    private ulong _commandSequence;

    public EnbWarpGaugeVisual UpdateVisual(ulong owner, Net7WarpStatus? status, long now, EnbSceneCockpitWarpCommand? command = null)
    {
        bool fresh = _owner != owner;
        if (fresh)
        {
            _owner = owner; _time = now; _mode = 0;
            _phase = _duration = _rate = _fill = _flash = 0;
            _red = 1; _running = false; _key = default;
            _commandSequence = 0;
        }
        // Advance the retained controller before observing the next command.
        float elapsed = unchecked((uint)(now - _time)) * .001f;
        _time = now;
        Vector3? flashColor = null;
        if (_flash > 0)
        {
            flashColor = new Vector3(1, 1 - _flash, 1 - _flash);
            _flash = Math.Max(0, _flash - elapsed);
            if (_flash == 0) flashColor = null;
        }
        if (_running)
        {
            if (_mode is 1 or 4)
            {
                _phase += elapsed;
                float fraction = Math.Min(_phase / _duration, 1);
                _fill = _mode == 1 ? fraction : 1 - fraction;
                if (_mode == 4) _red = 1;
            }
            else
            {
                double phase = _phase + (double)elapsed * _rate * 8;
                _phase = (float)phase;
                _red = (float)((Math.Cos(phase) + 1) * .5);
            }
        }
        var key = (status?.State, status?.TriggerTime, status?.Incapacitated == true, status?.PresentationMode);
        bool callback = command is null ? fresh || key != _key : command.Sequence != _commandSequence;
        if (callback || fresh || key.Item3 != _key.Incapacitated)
        {
            bool recovered = _key.Incapacitated && !key.Item3;
            if (key.Item3 && (fresh || !_key.Incapacitated)) SetMode(5, 2);
            else if (recovered) SetMode(0, 0);
            // Other PrivateWarpState cases only update enablement/tooltips. They
            // do not reset this controller. Planet/gas branches skip this switch.
            else if (!key.Item3 && callback && (command is not null || status?.PresentationMode == 0))
            {
                int? state = command?.State ?? status?.State;
                if (state == 1 && command?.HasTriggerTime != false) SetMode(1, 0);
                else if (state == 2)
                {
                    SetMode(2, 1); _flash = 1; flashColor = Vector3.UnitX;
                }
                else if (state is 3 or 6 or 7 or 8 or 9 or 11 && command?.HasTriggerTime != false) SetMode(4, 0);
            }
            _key = key;
        }
        if (command is not null) _commandSequence = command.Sequence;
        return new(_fill, _red, flashColor);

        void SetMode(int mode, float rate)
        {
            _mode = mode; _phase = 0; _rate = rate; _running = true;
            if (mode is 1 or 4)
            {
                long deadline = Math.Clamp((command is null ? status?.TriggerTime : command.TriggerTime) ?? 0, int.MinValue, int.MaxValue);
                if (deadline < 0 || (uint)now >= deadline) _mode = 0;
                else _duration = (deadline - (uint)now) * .001f;
            }
            if (_mode == 0) _fill = 0;
            else if (_mode == 5) _fill = 1;
            else if (_mode is 1 or 4) _fill = _mode == 1 ? 0 : 1;
            if (_mode is not 1) _red = 1;
        }
    }
}
