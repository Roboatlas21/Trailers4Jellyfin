using System.IO;
using System.Xml.Serialization;
using Jellyfin.Plugin.Trailers4Jellyfin.Configuration;
using Xunit;

namespace Jellyfin.Plugin.Trailers4Jellyfin.Tests.Services;

public sealed class ConfigurationDefaultsTests
{
    [Theory]
    [InlineData("", true)]
    [InlineData("false", false)]
    [InlineData("true", true)]
    public void UnwatchedDefaults_ApplyOnlyToMissingSerializedValues(string saved, bool expected)
    {
        var fields = new[] { "PreferUnwatchedTrailers", "PreferUnwatchedTrailerPreRolls", "PreferUnwatchedFeaturePreRolls", "PreferUnwatchedEpisodePreRolls" };
        var xml = "<PluginConfiguration>";
        if (saved.Length > 0)
            foreach (var field in fields) xml += $"<{field}>{saved}</{field}>";
        xml += "<YtDlpPath>/config/bin/yt-dlp</YtDlpPath></PluginConfiguration>";
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        using var reader = new StringReader(xml);
        var config = (PluginConfiguration)serializer.Deserialize(reader)!;
        foreach (var field in fields) Assert.Equal(expected, typeof(PluginConfiguration).GetProperty(field)!.GetValue(config));
        Assert.Equal("/config/bin/yt-dlp", config.YtDlpPath);
        Assert.True(config.SkipCurrentMovieTrailers);
        Assert.True(config.SkipWatchedMovieTrailers);
    }
}
