using EnbNext.Net7;

namespace EnbNext.Net7.Tests;

public sealed class Net7AssetDirectAuditTestsAsset05
{
    [Fact]
    public void Asset05IntegratedModeDoesNotRecreateLcHttpCertificateOrTouchCompatibility()
    {
        string production = AllProductionSource();

        foreach (string legacyLocalCompatibility in new[]
        {
            "SslStream",
            "HttpListener",
            "TcpListener",
            "X509Certificate",
            "X509Store",
            "StoreName.Root",
            "certificate.html",
            "touchsession.jsp",
            "sectorserver.cgi",
            "NewPlayer.cgi",
            "subsxml/subsxml.jsp",
            "InternetOpenUrlA",
            "UseLocalCert",
            "LKeyUrl",
            "local.net-7.org",
            "localhost.net-7.org",
            "/LC",
            "/SSL:",
        })
        {
            Assert.DoesNotContain(legacyLocalCompatibility, production, StringComparison.OrdinalIgnoreCase);
        }

        string repo = FindRepoRoot();
        string[] legacyCertificateArtifacts = Directory
            .EnumerateFiles(Path.Combine(repo, "src"), "*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".cer", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".crt", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".pem", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.Empty(legacyCertificateArtifacts);
    }

    [Fact]
    public void Asset05RequiredAccountLoginRemainsDirectServerTcpAndIgnoresLocalWebProfile()
    {
        Assert.Equal(3807, Net7ServerOptions.CurrentAccountTcpPort);

        string session = Source("src/EnbNext.Net7/Net7Session.cs");
        string tcp = Source("src/EnbNext.Net7/Net7TcpConnection.cs");
        string client = Source("src/EnbNext.Client/EnbClient.cs");
        string options = Source("src/EnbNext.Client/EnbClientOptions.cs");

        Assert.Contains("_options.Host", session, StringComparison.Ordinal);
        Assert.Contains("Net7TcpConnection.ConnectAsync(serverAddress, _options.AccountTcpPort", session, StringComparison.Ordinal);
        Assert.Contains("private readonly NetworkStream _stream", tcp, StringComparison.Ordinal);

        // The distinct auth-route profile stays native configuration,
        // but it is not a required local web/login bridge.
        Assert.Contains("AuthenticationRouteBaseUri", options, StringComparison.Ordinal);
        Assert.DoesNotContain("AuthenticationRouteBaseUri", session, StringComparison.Ordinal);
        Assert.DoesNotContain("AuthenticationRouteBaseUri", client, StringComparison.Ordinal);
        Assert.DoesNotContain("HttpClient", session, StringComparison.Ordinal);
        Assert.DoesNotContain("HttpRequestMessage", session, StringComparison.Ordinal);
        Assert.DoesNotContain("touchsession.jsp", session, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sectorserver.cgi", session, StringComparison.OrdinalIgnoreCase);
    }

    private static string AllProductionSource()
    {
        string repo = FindRepoRoot();
        return string.Join(
            "\n",
            Directory.EnumerateFiles(Path.Combine(repo, "src"), "*.cs", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.Ordinal)
                .Select(File.ReadAllText));
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
