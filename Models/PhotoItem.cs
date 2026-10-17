using Microsoft.UI.Xaml.Media.Imaging;

namespace Gallery.Models;

public sealed class PhotoItem
{
    public required string File { get; init; }
    public required string FullPath { get; init; }
    public required int PixelWidth { get; init; }
    public required int PixelHeight { get; init; }
    public required DateTime Date { get; init; }
    public bool Favorite { get; set; }

    public string Name => System.IO.Path.GetFileNameWithoutExtension(File);
    public string Extension => System.IO.Path.GetExtension(File).TrimStart('.').ToUpperInvariant();
    public double AspectRatio => PixelHeight <= 0 ? 1.0 : (double)PixelWidth / PixelHeight;

    private BitmapImage? _thumb;
    public BitmapImage Thumbnail
    {
        get
        {
            if (_thumb is null)
            {
                _thumb = new BitmapImage
                {
                    DecodePixelWidth = 560,
                    UriSource = new Uri(FullPath)
                };
            }
            return _thumb;
        }
    }
}
