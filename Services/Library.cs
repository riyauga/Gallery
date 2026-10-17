using System.Text.Json;
using Gallery.Models;

namespace Gallery.Services;

public sealed class Library
{
    private sealed record Entry(string file, int w, int h, string date, bool favorite);

    public List<PhotoItem> All { get; } = new();

    public static Library Load()
    {
        var lib = new Library();
        var root = AppContext.BaseDirectory;
        var manifest = System.IO.Path.Combine(root, "assets", "manifest.json");
        if (!System.IO.File.Exists(manifest)) return lib;

        var json = System.IO.File.ReadAllText(manifest);
        var entries = JsonSerializer.Deserialize<List<Entry>>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();

        foreach (var e in entries)
        {
            var path = System.IO.Path.Combine(root, "assets", e.file);
            if (!System.IO.File.Exists(path)) continue;
            lib.All.Add(new PhotoItem
            {
                File = e.file,
                FullPath = path,
                PixelWidth = e.w,
                PixelHeight = e.h,
                Date = DateTime.TryParse(e.date, out var d) ? d : DateTime.Now,
                Favorite = e.favorite
            });
        }
        return lib;
    }
}
