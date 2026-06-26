using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.FinTube.Configuration;

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PluginConfiguration"/> class.
    /// </summary>
    public PluginConfiguration()
    {
        exec_YTDL = "/usr/local/bin/yt-dlp";
        exec_ID3 = "/usr/bin/id3v2";
        cookies = string.Empty;
        preferFreeFormat = false;
    }

    /// <summary>
    /// Executable for youtube-dl/youtube-dlp
    /// </summary>
    public string exec_YTDL { get; set; }

    /// <summary>
    /// Executable for ID3v2
    /// </summary>
    public string exec_ID3 { get; set; }

    /// <summary>
    /// YouTube cookies in Netscape cookie-file format, passed to yt-dlp via
    /// <c>--cookies</c>. Used to download age-restricted, private or
    /// members-only videos. Easiest filled with the FinTube browser extension.
    /// </summary>
    public string cookies { get; set; }

    /// <summary>
    /// Prefer free/open formats when downloading (yt-dlp <c>--prefer-free-format</c>).
    /// Usually yields audio as .opus and video as .webm instead of .mp3/.mp4.
    /// Applies to every download; set from the FinTube settings page.
    /// </summary>
    public bool preferFreeFormat { get; set; }
}
