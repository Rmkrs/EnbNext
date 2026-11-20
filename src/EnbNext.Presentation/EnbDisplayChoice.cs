namespace EnbNext.Presentation;

public enum EnbDisplayScope { System, Character }

public enum EnbDisplayMode
{
    Windowed,
    BorderlessFullscreen,
}

public sealed record EnbDisplayChoice(int Width, int Height, EnbDisplayMode Mode)
{
    public string Label => Mode == EnbDisplayMode.BorderlessFullscreen
        ? "Borderless fullscreen"
        : $"Windowed {Width} x {Height}";

    // OPT04A's authored Resolution button is only about 102 design units wide.
    // The full mode description belongs in the expanded list, not on that button.
    public string ButtonLabel => Mode == EnbDisplayMode.BorderlessFullscreen
        ? "Borderless"
        : $"{Width} x {Height}";
}
