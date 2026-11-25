using System.Text.Json;
using VNotch.Models;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public class SettingsMigratorTests
{
    [Fact]
    public void NewBannerSettingsDefaultOffWithoutASchemaBump()
    {
        var (settings, _) = SettingsMigrator.Migrate($$"""{ "SettingsVersion": {{SettingsMigrator.CurrentVersion}} }""");
        Assert.False(settings.HideWindowsBanners);
        Assert.Empty(settings.BannerRestore);
    }

    [Fact]
    public void BannerRestoreRoundTripsAndClonesIndependently()
    {
        var original = new NotchSettings { HideWindowsBanners = true };
        original.BannerRestore["Test.App"] = 1;
        original.BannerRestore["Missing.App"] = null;
        var restored = JsonSerializer.Deserialize<NotchSettings>(JsonSerializer.Serialize(original))!;
        Assert.True(original.ValueEquals(restored));
        var clone = restored.Clone();
        Assert.True(restored.ValueEquals(clone));
        clone.BannerRestore.Clear();
        Assert.Equal(2, restored.BannerRestore.Count);
        Assert.False(restored.ValueEquals(clone));
    }

    [Fact]
    public void CanvasIsOffOnNewInstall()
    {
        var settings = new NotchSettings();
        Assert.False(settings.EnableSpotifyCanvas);
        Assert.False(settings.AllowOnlineCanvas);
        Assert.False(SpotifyCanvasConsent.HasAccepted(settings));
    }

    [Theory]
    [InlineData(13, 1)] // Even an injected consent field in a legacy file is reset.
    [InlineData(14, 0)]
    [InlineData(14, 2)]
    [InlineData(15, 0)]
    [InlineData(15, 2)]
    public void MigrationNeverInfersConsentFromEnabledFlags(int version, int consentVersion)
    {
        string json = $$"""
            { "SettingsVersion": {{version}}, "EnableSpotifyCanvas": true,
              "AllowOnlineCanvas": true, "SpotifyCanvasConsentVersion": {{consentVersion}} }
            """;
        var (settings, migrated) = SettingsMigrator.Migrate(json);
        Assert.True(migrated);
        Assert.False(settings.EnableSpotifyCanvas);
        Assert.False(settings.AllowOnlineCanvas);
        Assert.Equal(0, settings.SpotifyCanvasConsentVersion);
    }

    [Theory]
    [InlineData("Media,Secondary,Timer,AudioMixer,Camera", "Media,Secondary,Timer,Claude,Pi,School,Legion,AudioMixer,Camera")]
    [InlineData("AudioMixer,Media", "AudioMixer,Media,Claude,Pi,School,Legion")]
    [InlineData("Media,Claude,Timer", "Media,Claude,Pi,School,Legion,Timer")]
    public void Migrate_Version14_AddsTheClaudeTabNextToTheControls(string saved, string expected)
    {
        string json = $$"""{ "SettingsVersion": 14, "NavTabOrder": "{{saved}}", "VisibleNavTabs": "{{saved}}" }""";
        var (settings, migrated) = SettingsMigrator.Migrate(json);
        Assert.True(migrated);
        Assert.Equal(expected + ",AiUsage", settings.NavTabOrder);
        Assert.Equal(expected, settings.VisibleNavTabs);
    }

    [Theory]
    [InlineData("Media,Secondary,Timer,Claude,AudioMixer,Camera", "Media,Secondary,Timer,Claude,Pi,School,Legion,AudioMixer,Camera")]
    [InlineData("Media,AudioMixer", "Media,AudioMixer,Pi,School,Legion")]
    [InlineData("Pi,Media,Claude", "Pi,School,Legion,Media,Claude")]
    public void Migrate_Version15_AddsThePiTabNextToClaude(string saved, string expected)
    {
        string json = $$"""{ "SettingsVersion": 15, "NavTabOrder": "{{saved}}", "VisibleNavTabs": "{{saved}}" }""";
        var (settings, migrated) = SettingsMigrator.Migrate(json);
        Assert.True(migrated);
        Assert.Equal(expected + ",AiUsage", settings.NavTabOrder);
        Assert.Equal(expected, settings.VisibleNavTabs);
    }

    [Theory]
    [InlineData("Media,Secondary,Timer,Claude,Pi,AudioMixer,Camera", "Media,Secondary,Timer,Claude,Pi,School,Legion,AudioMixer,Camera")]
    [InlineData("Media,AudioMixer", "Media,AudioMixer,Legion,School")]
    [InlineData("Weather,Media,Pi", "Legion,Media,Pi,School")]
    public void Migrate_Version16_RemovesTheFormerWeatherTab(string saved, string expected)
    {
        string json = $$"""{ "SettingsVersion": 16, "NavTabOrder": "{{saved}}", "VisibleNavTabs": "{{saved}}" }""";
        var (settings, migrated) = SettingsMigrator.Migrate(json);
        Assert.True(migrated);
        Assert.Equal(expected + ",AiUsage", settings.NavTabOrder);
        Assert.Equal(expected, settings.VisibleNavTabs);
    }

    [Theory]
    [InlineData("Media,Secondary,Timer,Claude,Pi,Weather,AudioMixer,Camera", "Media,Secondary,Timer,Claude,Pi,School,Legion,AudioMixer,Camera")]
    [InlineData("Media,AudioMixer", "Media,AudioMixer,Legion,School")]
    [InlineData("Legion,Media,Weather", "Legion,Media,School")]
    public void Migrate_Version17_AddsTheLegionTabNextToWeather(string saved, string expected)
    {
        string json = $$"""{ "SettingsVersion": 17, "NavTabOrder": "{{saved}}", "VisibleNavTabs": "{{saved}}" }""";
        var (settings, migrated) = SettingsMigrator.Migrate(json);
        Assert.True(migrated);
        Assert.Equal(expected + ",AiUsage", settings.NavTabOrder);
        Assert.Equal(expected, settings.VisibleNavTabs);
    }

    [Theory]
    [InlineData("Media,Secondary,Timer,Claude,Pi,Weather,Legion,AudioMixer,Camera", "Media,Secondary,Timer,Claude,Pi,School,Legion,AudioMixer,Camera")]
    [InlineData("Media,AudioMixer", "Media,AudioMixer,School")]
    [InlineData("School,Media,Pi", "School,Media,Pi")]
    public void Migrate_Version18_AddsTheSchoolTabNextToHome(string saved, string expected)
    {
        string json = $$"""{ "SettingsVersion": 18, "NavTabOrder": "{{saved}}", "VisibleNavTabs": "{{saved}}" }""";
        var (settings, migrated) = SettingsMigrator.Migrate(json);
        Assert.True(migrated);
        Assert.Equal(expected + ",AiUsage", settings.NavTabOrder);
        Assert.Equal(expected, settings.VisibleNavTabs);
    }

    [Fact]
    public void MigrationDropsWeatherFromBothSavedTabLists()
    {
        const string json = """{ "SettingsVersion": 19, "NavTabOrder": " Weather,Media,wEATHER,Pi ", "VisibleNavTabs": "Media,weather" }""";
        var (settings, migrated) = SettingsMigrator.Migrate(json);
        Assert.True(migrated);
        Assert.Equal("Media,Pi,AiUsage", settings.NavTabOrder);
        Assert.Equal("Media", settings.VisibleNavTabs);
    }

    [Fact]
    public void AcceptedCanvasSettingsSurviveLocalSaveAndReload()
    {
        var settings = new NotchSettings { SettingsVersion = SettingsMigrator.CurrentVersion };
        Assert.True(SpotifyCanvasConsent.TryEnable(settings, () => true));
        var (loaded, migrated) = SettingsMigrator.Migrate(JsonSerializer.Serialize(settings));
        Assert.False(migrated);
        Assert.True(NetworkPrivacy.Allows(loaded, NetworkFeature.Canvas));
    }

    [Fact]
    public void Migrate_Version3_AddsDynamicIslandWidthFromExistingWidth()
    {
        const string rawJson = """
            {
              "SettingsVersion": 3,
              "Width": 230
            }
            """;

        var (settings, migrated) = SettingsMigrator.Migrate(rawJson);

        Assert.True(migrated);
        Assert.Equal(SettingsMigrator.CurrentVersion, settings.SettingsVersion);
        Assert.Equal(260, settings.DynamicIslandWidth);
    }

    [Fact]
    public void Migrate_PreservesExistingDynamicIslandWidth()
    {
        const string rawJson = """
            {
              "SettingsVersion": 3,
              "Width": 230,
              "DynamicIslandWidth": 320
            }
            """;

        var (settings, _) = SettingsMigrator.Migrate(rawJson);

        Assert.Equal(320, settings.DynamicIslandWidth);
    }

    [Fact]
    public void NewDefaults_UseSliderAlignedDynamicIslandWidth()
    {
        var settings = new NotchSettings();

        Assert.Equal(220, settings.DynamicIslandWidth);
    }

    [Fact]
    public void NewDefaults_UseReadableSpotifyCanvasBrightness()
    {
        var settings = new NotchSettings();

        Assert.Equal(0.7, settings.SpotifyCanvasBrightness);
    }

    [Fact]
    public void NewDefaults_AllowHighRefreshDisplays()
    {
        var settings = new NotchSettings();

        Assert.Equal(240, settings.AnimationFps);
    }

    [Fact]
    public void NewDefaults_EnableGpuGlassWithoutHidingNotchFromCapture()
    {
        var settings = new NotchSettings();

        Assert.True(settings.LiquidGlass.UseGpuRefraction);
        Assert.False(settings.LiquidGlass.HideFromScreenCapture);
    }

    [Fact]
    public void CurrentSettings_PreserveExplicitGpuOptOut()
    {
        string rawJson = $$"""
            {
              "SettingsVersion": {{SettingsMigrator.CurrentVersion}},
              "LiquidGlass": {
                "UseGpuRefraction": false
              }
            }
            """;

        var (settings, migrated) = SettingsMigrator.Migrate(rawJson);

        Assert.False(migrated);
        Assert.False(settings.LiquidGlass.UseGpuRefraction);
    }

    [Fact]
    public void Migrate_Version4_AddsPerformanceDefaults()
    {
        const string rawJson = """
            {
              "SettingsVersion": 4
            }
            """;

        var (settings, migrated) = SettingsMigrator.Migrate(rawJson);

        Assert.True(migrated);
        Assert.Equal(SettingsMigrator.CurrentVersion, settings.SettingsVersion);
        Assert.True(settings.EnableBlurEffects);
        Assert.Equal(AnimationConfig.MaxFps, settings.AnimationFps);
    }

    [Fact]
    public void Migrate_Version10_PreservesSupportedHighRefreshRate()
    {
        const string rawJson = """
            {
              "SettingsVersion": 10,
              "AnimationFps": 144
            }
            """;

        var (settings, migrated) = SettingsMigrator.Migrate(rawJson);

        Assert.True(migrated);
        Assert.Equal(SettingsMigrator.CurrentVersion, settings.SettingsVersion);
        Assert.Equal(144, settings.AnimationFps);
    }

    [Fact]
    public void Migrate_Version11_EnablesSpotlightByDefault()
    {
        const string rawJson = """
            {
              "SettingsVersion": 11
            }
            """;

        var (settings, migrated) = SettingsMigrator.Migrate(rawJson);

        Assert.True(migrated);
        Assert.Equal(SettingsMigrator.CurrentVersion, settings.SettingsVersion);
        Assert.True(settings.EnableSpotlight);
    }

    [Fact]
    public void Migrate_Version11_PreservesExplicitSpotlightOptOut()
    {
        const string rawJson = """
            {
              "SettingsVersion": 11,
              "EnableSpotlight": false
            }
            """;

        var (settings, _) = SettingsMigrator.Migrate(rawJson);

        Assert.False(settings.EnableSpotlight);
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(-1)]
    public void Migrate_InvalidVersion_ThrowsJsonException(int invalidVersion)
    {
        string rawJson = $$"""
            {
              "SettingsVersion": {{invalidVersion}}
            }
            """;
        var ex = Assert.Throws<JsonException>(() => SettingsMigrator.Migrate(rawJson));
        Assert.Contains($"Unsupported settings version: {invalidVersion}", ex.Message);
    }
    [Fact]
    public void Migrate_FutureVersion_LoadsForwardCompatibleSettingsWithoutThrowing()
    {
        string rawJson = $$"""
            {
              "SettingsVersion": {{SettingsMigrator.CurrentVersion + 1}},
              "DynamicIslandWidth": 310
            }
            """;
        var (settings, migrated) = SettingsMigrator.Migrate(rawJson);
        Assert.False(migrated);
        Assert.Equal(310, settings.DynamicIslandWidth);
    }

    [Fact]
    public void Migrate_Version20_AppendsAiUsageTabAndKeepsAiUsageWidget()
    {
        const string rawJson = """
            {
              "SettingsVersion": 20,
              "NavTabOrder": "Media,Secondary,Timer,AudioMixer,Camera",
              "VisibleNavTabs": "Media,Secondary,Timer,AudioMixer,Camera",
              "ExpandedWidget": "aiusage"
            }
            """;

        var (settings, migrated) = SettingsMigrator.Migrate(rawJson);

        Assert.True(migrated);
        Assert.Equal(SettingsMigrator.CurrentVersion, settings.SettingsVersion);
        Assert.Equal("Media,Secondary,Timer,AudioMixer,Camera,AiUsage", settings.NavTabOrder);
        Assert.Equal("Media,Secondary,Timer,AudioMixer,Camera", settings.VisibleNavTabs);
        Assert.Equal("aiusage", settings.ExpandedWidget);
    }

    [Fact]
    public void CurrentSettings_PreserveExplicitAiUsageTabOptOut()
    {
        string rawJson = $$"""
            {
              "SettingsVersion": {{SettingsMigrator.CurrentVersion}},
              "NavTabOrder": "Media,Secondary,Timer,AudioMixer,Camera,AiUsage",
              "VisibleNavTabs": "Media,Secondary,Timer"
            }
            """;

        var (settings, migrated) = SettingsMigrator.Migrate(rawJson);

        Assert.False(migrated);
        Assert.Equal("Media,Secondary,Timer", settings.VisibleNavTabs);
    }
}
