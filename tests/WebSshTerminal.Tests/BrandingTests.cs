using WebSshTerminal.Configuration;

namespace WebSshTerminal.Tests;

public class BrandingTests
{
    private static WebSshOptions Options(string dir) => new() { DataDirectory = dir };

    [Fact]
    public void Defaults_use_the_bundled_salesians_branding()
    {
        var b = new BrandingService(Options(Path.Combine(Path.GetTempPath(), "no-existeix-" + Guid.NewGuid())));
        Assert.Equal("/branding/salesians-logo-white.png", b.LogoUrl);
        Assert.Equal("/branding/salesians-symbol.png", b.SymbolUrl);
        Assert.Equal("/branding/salesians-background.jpg", b.BackgroundUrl);
        Assert.Contains("--brand:#CC0000", b.CssVariables(), StringComparison.Ordinal);
        Assert.Contains("--nav:#1e293b", b.CssVariables(), StringComparison.Ordinal);
    }

    [Fact]
    public void Files_in_the_data_directory_replace_the_bundled_ones_without_rebuilding()
    {
        var dir = Directory.CreateTempSubdirectory("brand-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "branding"));
            File.WriteAllBytes(Path.Combine(dir, "branding", "logo.png"), [1]);
            File.WriteAllBytes(Path.Combine(dir, "branding", "background.jpg"), [1]);
            var b = new BrandingService(Options(dir));
            Assert.Equal("/instance-branding/logo.png", b.LogoUrl);
            Assert.Equal("/instance-branding/background.jpg", b.BackgroundUrl);
            Assert.Equal("/branding/salesians-symbol.png", b.SymbolUrl); // el símbol no s'ha substituït
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Theory]
    [InlineData("red")]
    [InlineData("#12345")]
    [InlineData("#CC0000; } body { display:none")]
    public void Invalid_colors_are_rejected_so_they_cannot_inject_css(string color)
    {
        var o = new WebSshOptions();
        o.Branding.PrimaryColor = color;
        o.Branding.NavColor = color;
        var errors = WebSshOptionsValidator.Validate(o, requireHttps: false);
        Assert.Contains(errors, e => e.Contains("PrimaryColor", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("NavColor", StringComparison.Ordinal));
    }

    [Fact]
    public void Shade_darkens_each_channel()
    {
        Assert.Equal("#A70000", BrandingService.Shade("#CC0000", 0.82));
    }
}
