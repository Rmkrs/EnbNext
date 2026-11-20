using EnbNext.Installation;
using Xunit;

namespace EnbNext.Net7.Tests;

public sealed class InstallSettingsTests
{
    [Fact]
    public void SavedRootPrecedesEnvironmentAndDetection()
    {
        Assert.Equal(new[] { "saved", "environment", "detected" },
            EnbInstallDiscovery.RetailCandidates(new("saved"), "environment", ["detected"]));
        Assert.Equal(new[] { "environment", "detected" },
            EnbInstallDiscovery.RetailCandidates(new(), "environment", ["detected"]));
    }

    [Fact]
    public void ProfileRoundTripsBothRootsAndReplacesExistingSettings()
    {
        string directory = Path.Combine(Path.GetTempPath(), "EnbNext-install-" + Guid.NewGuid());
        string path = Path.Combine(directory, "install.json");
        try
        {
            Assert.Equal(new(), EnbInstallSettings.Load(path));
            var settings = new EnbInstallSettings("/games/Earth & Beyond", "/games/Net-7");
            settings.Save(path);
            Assert.Equal(settings, EnbInstallSettings.Load(path));
            settings = settings with { Net7Folder = "/moved/Net-7" };
            settings.Save(path);
            Assert.Equal(settings, EnbInstallSettings.Load(path));
            Assert.Single(Directory.GetFiles(directory));
            File.WriteAllText(path, "broken");
            Assert.Throws<InvalidDataException>(() => EnbInstallSettings.Load(path));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void LauncherConfigUsesRecordedClientPathAndIgnoresBrokenXml()
    {
        string file = Path.GetTempFileName();
        try
        {
            string root = Path.Combine(Path.GetTempPath(), "game");
            new System.Xml.Linq.XDocument(new System.Xml.Linq.XElement("configuration",
                new System.Xml.Linq.XElement("setting", new System.Xml.Linq.XAttribute("name", "ClientPath"),
                    new System.Xml.Linq.XElement("value", Path.Combine(root, "release", "client.exe"))))).Save(file);
            Assert.Equal(root, EnbInstallDiscovery.ReadLauncherClientRoot(file));
            File.WriteAllText(file, "broken");
            Assert.Null(EnbInstallDiscovery.ReadLauncherClientRoot(file));
        }
        finally { File.Delete(file); }
    }
}
