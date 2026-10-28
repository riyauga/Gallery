using Gallery.Models;

namespace Gallery.Services;

public sealed class Library
{
    public List<PhotoItem> All { get; } = new();

    public static Library Load()
    {
        return new Library();
    }
}
