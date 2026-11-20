namespace EnbNext.Engine;

internal static class RetailSplashSelection
{
    // Retail client.exe 006A9BB4-006A9BC6: time(NULL), signed remainder by 7,
    // then format "install%02da".
    public static string ModelAt(long epochSeconds) => $"install{epochSeconds % 7:00}a";
}
