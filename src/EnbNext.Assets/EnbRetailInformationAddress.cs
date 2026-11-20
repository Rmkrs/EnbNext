namespace EnbNext.Assets;

/// <summary>Installed BrowserLinkManager (007C97B0) information destinations.</summary>
public static class EnbRetailInformationAddress
{
    public static Uri Load(string clientExecutable, int action) => EnbRetailNewsAddress.LoadLink(clientExecutable,
        action switch
        {
            0 => 0x007A005C,
            1 => 0x0079FFBC,
            2 => 0x0079FF78,
            5 => 0x0079FE90,
            6 => 0x0079FE54,
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        });
}
