namespace UTSL.VirtualWorld;

/// <summary>一个区块建筑模板（.utpl）的元信息 —— 玩法列举/确认时用，不必自己解析文件。</summary>
public readonly struct VirtualWorldTemplateInfo
{
    /// <summary>模板名（不含扩展名，可安全用作文件名）。</summary>
    public string Name { get; }

    /// <summary>模板宽度（图格）。</summary>
    public int Width { get; }

    /// <summary>模板高度（图格）。</summary>
    public int Height { get; }

    public VirtualWorldTemplateInfo(string name, int width, int height)
    {
        Name = name;
        Width = width;
        Height = height;
    }

    public override string ToString() => $"{Name}({Width}x{Height})";
}
