using RentalServer.Core.TabletApps;

namespace RentalServer.Core.Tests.TabletApps;

public sealed class TabletAppClassifierTests
{
    [Theory]
    [InlineData("Mobile Legends", "com.mobile.legends", TabletAppCategories.Games)]
    [InlineData("YouTube", "com.google.android.youtube", TabletAppCategories.VideoSocial)]
    [InlineData("Brave", "com.brave.browser", TabletAppCategories.Browser)]
    [InlineData("Google Maps", "com.google.android.apps.maps", TabletAppCategories.Tools)]
    [InlineData("Unknown App", "com.example.unknown", TabletAppCategories.Other)]
    public void InferCategory_groups_common_apps(string name, string packageName, string expected)
    {
        Assert.Equal(expected, TabletAppClassifier.InferCategory(name, packageName));
    }

    [Theory]
    [InlineData("com.rentalsystem.android")]
    [InlineData("com.android.settings")]
    [InlineData("com.google.android.gms")]
    [InlineData("com.miui.securitycenter")]
    public void IsProtectedPackage_blocks_unsafe_packages(string packageName)
    {
        Assert.True(TabletAppClassifier.IsProtectedPackage(packageName));
    }

    [Fact]
    public void Normalize_defaults_unknown_category_to_other()
    {
        Assert.Equal(TabletAppCategories.Other, TabletAppCategories.Normalize("bad-category"));
    }
}
