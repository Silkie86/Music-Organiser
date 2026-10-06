namespace MusicOrganiser.Core.Parsers;

/// <summary>The supported project formats.</summary>
public static class Readers
{
    public static readonly BitwigReader Bitwig = new();
    public static readonly FlStudioReader FlStudio = new();
    public static readonly StudioOneReader StudioOne = new();

    public static readonly IReadOnlyList<IProjectReader> All = [Bitwig, FlStudio, StudioOne];

    public static IProjectReader? For(string path)
    {
        var ext = Path.GetExtension(path);
        return All.FirstOrDefault(r => r.Extensions.Contains(ext, StringComparer.OrdinalIgnoreCase));
    }
}
