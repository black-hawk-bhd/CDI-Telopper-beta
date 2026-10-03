using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace EEWTelop.Wpf.ViewModels;

public sealed class AboutViewModel
{
    private const string RepositoryUrl = "https://github.com/black-hawk-bhd/CDI-Telopper-beta";

    public string ProductName { get; } = "CDI-Telopper";
    public string FormalName { get; } = "Comprehensive Disaster Information Telopper";
    public string ContactEmail { get; } = "cdi.telopper.contact@proton.me";
    public string Copyright { get; } = "Copyright (c) 2026 black-hawk-bhd / MIT License";
    public Uri DmdataWebsite { get; } = new("https://dmdata.jp/");
    public Uri DmdataControlPanel { get; } = new("https://control.dmdata.jp/");
    public string BuildVersion { get; } = typeof(AboutViewModel).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
    public string Version => FormatVersion(BuildVersion);

    public IReadOnlyList<AboutLink> Links { get; } = Array.AsReadOnly<AboutLink>(
    [
        new("DMDATA.JP 公式サイト", new("https://dmdata.jp/")),
        new("DMDATA.JP コントロールパネル", new("https://control.dmdata.jp/")),
        new("GitHub（ソースコード・説明）", new(RepositoryUrl)),
        new("配布版・更新履歴（Releases）", new(RepositoryUrl + "/releases")),
        new("利用規約・免責事項", new(RepositoryUrl + "/blob/main/TERMS.md")),
        new("プライバシーポリシー", new(RepositoryUrl + "/blob/main/PRIVACY.md")),
        new("ライセンス", new(RepositoryUrl + "/blob/main/LICENSE")),
        new("データ出典・提供元", new(RepositoryUrl + "/blob/main/docs/data-sources.md")),
        new("外部API仕様", new(RepositoryUrl + "/blob/main/docs/external-api-v1.md")),
        new("お問い合わせ（メール）", new("mailto:cdi.telopper.contact@proton.me")),
    ]);

    // Only the explicitly listed product links can be launched from this screen.
    public bool CanOpenLink(Uri? uri) => uri is not null && uri.IsAbsoluteUri &&
        Links.Any(link => link.Uri.Equals(uri));

    internal static string FormatVersion(string version)
    {
        int separator = version.IndexOf('+', StringComparison.Ordinal);
        return separator < 0 ? version : version[..separator];
    }
}

public sealed record AboutLink(string Label, Uri Uri);
