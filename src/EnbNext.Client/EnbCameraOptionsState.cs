using EnbNext.Assets;

namespace EnbNext.Client;

/// <summary>OPT03A camera draft: 005C9C00 initializes, 005C9F20 commits.</summary>
public sealed class EnbCameraOptionsState
{
    private readonly bool[] values = new bool[7];
    public bool this[int index] => values[index];

    public void Begin(EnbRetailPlayerOptions options)
    {
        values[0] = options.CameraShip;
        values[1] = options.CameraWarp;
        values[2] = options.CameraLoot;
        values[3] = options.CameraGate;
        values[4] = options.CameraLand;
        values[5] = options.CameraDock;
        values[6] = options.CameraBridge;
    }

    public void Toggle(int index) => values[index] = !values[index];

    public bool TrySave(Action<EnbRetailCameraOptions>? save, out string? error)
    {
        try
        {
            if (save is null) throw new InvalidOperationException("Camera settings are unavailable.");
            save(new(values[0], values[1], values[2], values[3], values[4], values[5], values[6]));
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            error = "Could not save camera settings: " + ex.Message;
            return false;
        }
    }
}
