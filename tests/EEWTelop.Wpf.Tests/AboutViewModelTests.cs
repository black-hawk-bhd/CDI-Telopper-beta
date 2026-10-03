using System.Reflection;
using EEWTelop.Wpf.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Wpf.Tests;

[TestClass]
public sealed class AboutViewModelTests
{
    [TestMethod]
    public void VersionUsesTheActualApplicationBuild()
    {
        var about = new AboutViewModel();
        string expected = typeof(AboutViewModel).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        Assert.AreEqual(expected, about.BuildVersion);
        Assert.AreEqual(AboutViewModel.FormatVersion(expected), about.Version);
    }

    [TestMethod]
    [DataRow("2.0.0-beta.54", "2.0.0-beta.54")]
    [DataRow("2.0.0-beta.54-oauth-preview.2+abcdef", "2.0.0-beta.54-oauth-preview.2")]
    public void VersionPreservesPreviewSuffixAndSeparatesBuildMetadata(string input, string expected) =>
        Assert.AreEqual(expected, AboutViewModel.FormatVersion(input));

    [TestMethod]
    public void ProductLinksIncludeTheWebsiteRepositoryPoliciesAndContact()
    {
        var about = new AboutViewModel();
        Assert.IsTrue(about.CanOpenLink(about.DmdataWebsite));
        Assert.IsTrue(about.CanOpenLink(about.DmdataControlPanel));
        Assert.IsTrue(about.Links.Any(link => link.Uri.AbsoluteUri == "https://github.com/black-hawk-bhd/CDI-Telopper-beta"));
        Assert.IsTrue(about.Links.Any(link => link.Uri.AbsoluteUri.EndsWith("/TERMS.md", StringComparison.Ordinal)));
        Assert.IsTrue(about.Links.Any(link => link.Uri.AbsoluteUri.EndsWith("/PRIVACY.md", StringComparison.Ordinal)));
        Assert.IsTrue(about.Links.Any(link => link.Uri.AbsoluteUri == "mailto:" + about.ContactEmail));
        foreach (AboutLink link in about.Links)
        {
            Assert.IsTrue(about.CanOpenLink(link.Uri));
            Assert.IsTrue(link.Uri.Scheme is "https" or "mailto");
        }
    }

    [TestMethod]
    [DataRow("file:///C:/Windows/System32/cmd.exe")]
    [DataRow("javascript:alert(1)")]
    [DataRow("https://example.com/")]
    [DataRow("mailto:someone@example.com")]
    [DataRow("relative/path")]
    [DataRow(null)]
    public void UnlistedLinksAreNotLaunched(string? value)
    {
        var about = new AboutViewModel();
        Assert.IsFalse(about.CanOpenLink(value is null ? null : new Uri(value, UriKind.RelativeOrAbsolute)));
    }
}
