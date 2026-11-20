using EnbNext.Client;

namespace EnbNext.Net7.Tests;

public sealed class Net7AssetDirectAuditTestsAsset04
{
    [Fact]
    public void Asset04NativeProfileKeepsGameAndAuthenticationRoutesIndependent()
    {
        var profile = new EnbClientOptions
        {
            ServerHost = "game.example.invalid",
            AuthenticationHost = "auth.example.invalid",
            AuthenticationPort = 8443,
            AuthenticationSecureTransport = true,
        };

        Assert.Equal("game.example.invalid", profile.ServerHost);
        Assert.Equal("auth.example.invalid", profile.AuthenticationHost);
        Assert.Equal(8443, profile.AuthenticationPort);
        Assert.True(profile.AuthenticationSecureTransport);
        Assert.Equal("https", profile.AuthenticationRouteBaseUri.Scheme);
        Assert.Equal("auth.example.invalid", profile.AuthenticationRouteBaseUri.Host);
        Assert.Equal(8443, profile.AuthenticationRouteBaseUri.Port);

        EnbClientOptions insecure = profile with { AuthenticationSecureTransport = false };
        Assert.Equal("http", insecure.AuthenticationRouteBaseUri.Scheme);
        Assert.Equal(profile.AuthenticationHost, insecure.AuthenticationRouteBaseUri.Host);
        Assert.Equal(profile.AuthenticationPort, insecure.AuthenticationRouteBaseUri.Port);

        // Hostname and RegistrationHostname are distinct launcher
        // inputs. The native model must therefore not silently couple them.
        EnbClientOptions changedGameServer = profile with { ServerHost = "other-game.example.invalid" };
        Assert.Equal("auth.example.invalid", changedGameServer.AuthenticationHost);
    }

    [Fact]
    public void Asset04NormalIntegratedStartupDoesNotRequireLegacyLauncherArtifacts()
    {
        string options = Source("src/EnbNext.Client/EnbClientOptions.cs");
        string client = Source("src/EnbNext.Client/EnbClient.cs");
        string mainWindow = Source("src/EnbNext.App/SdlApplication.Session.cs");
        string normalStartup = string.Concat(options, "\n", client, "\n", mainWindow);

        Assert.Contains("AuthenticationHost", options, StringComparison.Ordinal);
        Assert.Contains("AuthenticationPort", options, StringComparison.Ordinal);
        Assert.Contains("AuthenticationSecureTransport", options, StringComparison.Ordinal);
        Assert.Contains("AuthenticationRouteBaseUri", options, StringComparison.Ordinal);
        Assert.DoesNotContain("AuthenticationHost = ServerHost", options, StringComparison.Ordinal);

        // The current integrated account/session path remains the already-closed
        // direct Net7 transport. This preserves the launcher-era auth-route
        // semantics as native profile state without recreating a second active
        // retail/AuthLogin transport or mutating physical compatibility assets.
        Assert.Contains("Host = _options.ServerHost", client, StringComparison.Ordinal);
        Assert.DoesNotContain("AuthenticationRouteBaseUri", client, StringComparison.Ordinal);

        foreach (string legacy in new[]
        {
            "Network.ini",
            "Auth.ini",
            "ReporterServer",
            "rg_regdata",
            "regserverurl",
            "LKeyUrl",
            "localhost.net-7.org",
            "Character and Starship Creator",
            "Microsoft.Win32",
            "Registry.GetValue(",
            "Registry.SetValue(",
            "Registry.CurrentUser",
            "Registry.LocalMachine",
            "Registry.ClassesRoot",
            "Registry.Users",
            "Registry.CurrentConfig",
        })
        {
            Assert.DoesNotContain(legacy, normalStartup, StringComparison.OrdinalIgnoreCase);
        }

        // The current AuthLogin bytes at raw +0x8292 are executable EB 0A
        // control flow, not a configuration timeout. Keep that false semantic
        // out of the native profile.
        Assert.DoesNotContain("0x8292", options, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AuthenticationTimeout", options, StringComparison.OrdinalIgnoreCase);
    }

    private static string Source(string relativePath)
    {
        string repo = FindRepoRoot();
        return File.ReadAllText(Path.Combine(repo, relativePath.Replace('/', Path.DirectorySeparatorChar)))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "EnbNext.slnx")))
                return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the EnbNext repository root.");
    }
}
