namespace MidiPlayer.Config;

/// <summary>
/// UTSL：配置目录不再从 TShock.SavePath 推，而是由 InitializeAsync 注入
/// （IPluginConfigRegistrar.Directory -> config/MidiPlayer/）。
/// 上游布局是 &lt;SavePath&gt;/MidiPlayer/...，这里 config/MidiPlayer/ 就等价于那个 MidiPlayer 目录。
/// </summary>
public static class MidiPlayerPaths
{
    public static string BaseDirectory { get; private set; } = "";

    /// <summary>上游的 &lt;SavePath&gt;（即 config/），用于兼容旧版配置文件的迁移查找</summary>
    public static string LegacyDirectory { get; private set; } = "";

    public static void Initialize(string directory)
    {
        BaseDirectory = directory;
        LegacyDirectory = Path.GetDirectoryName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                          ?? directory;
        Directory.CreateDirectory(directory);
    }
}
