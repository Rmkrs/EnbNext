namespace EnbNext.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args is ["--validate-runtime-cpu"])
        {
            // Packaging smoke test: load managed project dependencies without
            // constructing a window, audio output or a GL context.
            foreach (var path in Directory.EnumerateFiles(AppContext.BaseDirectory, "EnbNext*.dll"))
            {
                using var stream = File.OpenRead(path);
                using var pe = new System.Reflection.PortableExecutable.PEReader(stream);
                if (!pe.HasMetadata) continue; // Native libraries are not managed assemblies.
                var name = System.Reflection.AssemblyName.GetAssemblyName(path);
                _ = System.Reflection.Assembly.Load(name).GetTypes();
            }
            Console.WriteLine("CPU-only runtime loading passed");
            return 0;
        }
        if(OperatingSystem.IsWindows())EnbRetailCrtRandomSource.InitializeClientThread();
        else EnbRetailCrtRandomSource.Current.SeedClientStartup(unchecked((uint)Environment.TickCount64));
        bool hidden=Environment.GetEnvironmentVariable("ENB_DIAG_OFFSCREEN_REPLAY")=="1";
        // Offscreen analysis is a developer run: it keeps its receipts.
        args=DiagnosticFiles.Take(args,hidden);
        if(hidden)
        {
            string manifest=Environment.GetEnvironmentVariable("ENBNEXT_FULL_CLIENT_BENCHMARK")??throw new InvalidOperationException("Offscreen analysis requires a replay manifest.");
            using var document=System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifest));
            if(document.RootElement.GetProperty("Mode").GetString()!="replay")throw new InvalidOperationException("Offscreen analysis cannot log in or record live.");
        }
        return SdlApplication.Run(args,hidden);
    }
}
