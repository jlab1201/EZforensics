namespace EzForensics.Core.Setup;

/// <summary>One third-party component the setup may download, with its licence (shown behind the wizard's licence "?").</summary>
public sealed record ThirdPartyLicense(string Component, string License, string Url);

/// <summary>
/// The third-party software the setup downloads (nothing is bundled with EZ Forensics). The wizard asks for ONE acceptance of these
/// terms before Install.
/// </summary>
public static class ThirdPartyLicenses
{
    // The Explore hex viewer is in-house (no third-party hex control is used).
    public static readonly IReadOnlyList<ThirdPartyLicense> All = new[]
    {
        new ThirdPartyLicense("Eric Zimmerman's tools", "MIT License", "https://github.com/EricZimmerman"),
        new ThirdPartyLicense("log2timeline / plaso", "Apache License 2.0", "https://github.com/log2timeline/plaso/blob/main/LICENSE"),
        new ThirdPartyLicense("Volatility 3", "Volatility Software License 1.0", "https://github.com/volatilityfoundation/volatility3/blob/develop/LICENSE.txt"),
        new ThirdPartyLicense("Zeek", "BSD 3-Clause License", "https://github.com/zeek/zeek/blob/master/COPYING"),
        new ThirdPartyLicense("RITA (and ClickHouse)", "GPL-3.0 (ClickHouse: Apache License 2.0)", "https://github.com/activecm/rita/blob/main/LICENSE"),
        new ThirdPartyLicense("Ubuntu Base (EZ Forensics Linux environment)", "Ubuntu licensing (free software licences)", "https://ubuntu.com/legal/intellectual-property-policy"),
        new ThirdPartyLicense("Microsoft .NET 9 runtime", "MIT License", "https://github.com/dotnet/runtime/blob/main/LICENSE.TXT"),
        new ThirdPartyLicense("Detect It Easy (Explore)", "MIT License", "https://github.com/horsicq/Detect-It-Easy/blob/master/LICENSE"),
        new ThirdPartyLicense("ExifTool (Explore)", "Perl Artistic License / GPL", "https://exiftool.org/#license"),
        new ThirdPartyLicense("CyberChef (Explore)", "Apache License 2.0", "https://github.com/gchq/CyberChef/blob/master/LICENSE"),
        new ThirdPartyLicense("PeNet (Explore, compiled in)", "Apache License 2.0", "https://github.com/secana/PeNet/blob/master/LICENSE"),
        new ThirdPartyLicense("Microsoft Edge WebView2 (Explore, compiled in)", "Microsoft Software License Terms (BSD-style)", "https://www.nuget.org/packages/Microsoft.Web.WebView2/#license-body-tab"),
        new ThirdPartyLicense("Python (private copy)", "Python Software Foundation License", "https://docs.python.org/3/license.html"),
    };

    /// <summary>One line per component ("Zeek: BSD 3-Clause License"), for the "?" tooltip and logs.</summary>
    public static string Text => string.Join(Environment.NewLine, All.Select(l => $"{l.Component}: {l.License}"));
}
