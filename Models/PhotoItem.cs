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

    // ---- 批次 0 扩展：真实数据层字段（manifest 路径保持默认值） ----
    public int ImageId { get; init; }              // images.id；manifest 示例图为 0
    public int RootId { get; init; }
    public string DirRel { get; init; } = "";
    public string RelPath { get; init; } = "";
    public double? TakenAtUnix { get; init; }      // EXIF 拍摄时间
    public double? CreatedAtUnix { get; init; }    // 文件创建时间
    public double? ModifiedAtUnix { get; init; }   // 文件修改时间
    public int Rating { get; init; }
    public string? Nsfw { get; init; }
    public IReadOnlyList<(string Category, string Name)> Tags { get; init; } = [];
    public bool Missing { get; init; }

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
