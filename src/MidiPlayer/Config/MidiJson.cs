using System.Text.Encodings.Web;
using System.Text.Json;

namespace MidiPlayer.Config;

/// <summary>STJ 序列化选项（替代上游 Newtonsoft 的 Formatting.Indented）</summary>
internal static class MidiJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        // 中文原样输出，不转义
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
}
