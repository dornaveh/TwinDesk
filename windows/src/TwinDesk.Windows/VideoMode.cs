namespace TwinDesk;

public sealed record VideoMode(string Id, int Width, int Height, int Fps)
{
    public static readonly VideoMode Hd = new("720p30", 1280, 720, 30);
    public static readonly VideoMode FullHd = new("1080p15", 1920, 1080, 15);
    public static VideoMode FromId(string? id) => id == FullHd.Id ? FullHd : Hd;
    public override string ToString() => $"{Height}p · up to {Fps} fps";
}
