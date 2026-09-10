using System.Globalization;
using System.Xml.Serialization;
using FluentFlyout.Core;
using Xunit;

namespace FluentFlyout.Tests;

public sealed class UserSettingsTests
{
    [Fact]
    public void DefaultSettings_AreXmlSerializable_AndPreserveExpectedDefaults()
    {
        var serializer = new XmlSerializer(typeof(UserSettings));
        var settings = new UserSettings();

        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        serializer.Serialize(writer, settings);

        using var reader = new StringReader(writer.ToString());
        var roundTripped = (UserSettings)serializer.Deserialize(reader)!;

        Assert.False(roundTripped.AppFilteringEnabled);
        Assert.Equal(0, roundTripped.AppFilteringMode);
        Assert.True(roundTripped.Startup);
        Assert.True(roundTripped.MediaFlyoutEnabled);
        Assert.False(roundTripped.TaskbarWidgetEnabled);
        Assert.Equal("system", roundTripped.AppLanguage);
        Assert.Empty(roundTripped.AllowedApps);
        Assert.Empty(roundTripped.BlockedApps);
    }
}
