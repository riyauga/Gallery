using System.Text;
using Gallery.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Gallery;

public partial class App
{
    /// <summary>
    /// --verify-collage：沙箱内验证拼图布局引擎与合成导出（不出窗口，不碰真实图库）。
    /// 覆盖统一/原始两种尺寸策略、四种对齐、基准切换、上限整体缩放与像素落点。
    /// </summary>
    private async Task RunVerifyCollageAsync()
    {
        var lines = new List<string>();
        var folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ptv", "collage-src");
        System.IO.Directory.CreateDirectory(folder);

        string Make(string name, int width, int height, Rgba32 color)
        {
            var path = System.IO.Path.Combine(folder, name);
            using var image = new Image<Rgba32>(width, height, color);
            image.Save(path);
            return path;
        }

        var red = Make("a.png", 1000, 1000, new Rgba32(255, 0, 0));
        var green = Make("b.png", 2000, 1000, new Rgba32(0, 255, 0));
        var blue = Make("c.png", 500, 1500, new Rgba32(0, 0, 255));

        List<CollageEntry> Build() =>
        [
            new() { Path = red, PixelWidth = 1000, PixelHeight = 1000 },
            new() { Path = green, PixelWidth = 2000, PixelHeight = 1000 },
            new() { Path = blue, PixelWidth = 500, PixelHeight = 1500 },
        ];

        void Check(string label, double actual, double expected, double tolerance = 0.5)
        {
            bool ok = Math.Abs(actual - expected) <= tolerance;
            lines.Add($"{(ok ? "PASS" : "FAIL")} {label} actual={actual:0.##} expected={expected:0.##}");
        }

        var horizontalUniform = Build();
        var r1 = CollageLayout.Compute(horizontalUniform, CollageAxis.Horizontal, CollageScaleMode.Uniform, CollageAlign.Start);
        Check("横向统一/首图为基准 宽", r1.Width, 3333);
        Check("横向统一/首图为基准 高", r1.Height, 1000);
        Check("横向统一 第二张宽", horizontalUniform[1].Width, 2000);
        Check("横向统一 第三张宽", horizontalUniform[2].Width, 333);

        var horizontalOriginal = Build();
        var r3 = CollageLayout.Compute(horizontalOriginal, CollageAxis.Horizontal, CollageScaleMode.Original, CollageAlign.Center);
        Check("横向原始 宽", r3.Width, 3500);
        Check("横向原始 高", r3.Height, 1500);
        Check("横向原始 首图 y", horizontalOriginal[0].Y, 250);
        Check("横向原始 第三张 y", horizontalOriginal[2].Y, 0);

        var verticalUniform = Build();
        var r4 = CollageLayout.Compute(verticalUniform, CollageAxis.Vertical, CollageScaleMode.Uniform, CollageAlign.Start);
        Check("纵向统一 宽", r4.Width, 1000);
        Check("纵向统一 高", r4.Height, 4500);

        var free = Build();
        free[1].Offset = 900;
        CollageLayout.Compute(free, CollageAxis.Horizontal, CollageScaleMode.Original, CollageAlign.Free, CollageTarget.First);
        Check("自由对齐/首图参照 合法偏移保留", free[1].Y, 900);

        var clamped = Build();
        clamped[1].Offset = -2000;
        CollageLayout.Compute(clamped, CollageAxis.Horizontal, CollageScaleMode.Original, CollageAlign.Free, CollageTarget.First);
        Check("自由对齐/首图参照 越界钳制", clamped[1].Y, -900);

        // 上一张参照：Offset 是增量，第二张 = 第一张位置 + 增量。
        var chained = Build();
        chained[0].Offset = 200;
        chained[1].Offset = 150;
        chained[2].Offset = 100;
        CollageLayout.Compute(chained, CollageAxis.Horizontal, CollageScaleMode.Original, CollageAlign.Free, CollageTarget.Previous);
        Check("自由对齐/上一张参照 首张位置", chained[0].Y, 200);
        Check("自由对齐/上一张参照 第二张累积", chained[1].Y, 350);
        Check("自由对齐/上一张参照 第三张累积", chained[2].Y, 450);

        var oversized = new List<CollageEntry>
        {
            new() { Path = red, PixelWidth = 10000, PixelHeight = 10000 },
            new() { Path = green, PixelWidth = 10000, PixelHeight = 10000 },
        };
        var r5 = CollageLayout.Compute(oversized, CollageAxis.Horizontal, CollageScaleMode.Uniform, CollageAlign.Start);
        Check("超上限 画布宽", r5.Width, CollageLayout.MaxEdge);
        Check("超上限 画布高", r5.Height, 8192);
        lines.Add($"{(r5.Clamped ? "PASS" : "FAIL")} 超上限 标记 Clamped");

        var render = Build();
        var r6 = CollageLayout.Compute(render, CollageAxis.Horizontal, CollageScaleMode.Original, CollageAlign.Center);
        using (var image = await CollageService.RenderAsync(render, r6.Width, r6.Height))
        {
            if (image is null)
            {
                lines.Add("FAIL 合成返回空");
            }
            else
            {
                Check("合成 宽", image.Width, 3500);
                Check("合成 高", image.Height, 1500);
                var a = image[500, 500];
                var b = image[1500, 500];
                var c = image[3300, 100];
                var blank = image[500, 100];
                lines.Add($"{(a.R > 200 && a.G < 40 ? "PASS" : "FAIL")} 首张像素={a}");
                lines.Add($"{(b.G > 200 && b.R < 40 ? "PASS" : "FAIL")} 第二张像素={b}");
                lines.Add($"{(c.B > 200 ? "PASS" : "FAIL")} 第三张像素={c}");
                lines.Add($"{(blank.R < 16 && blank.G < 16 && blank.B < 16 ? "PASS" : "FAIL")} 留黑像素={blank}");
            }
        }

        // 目录区实际可读尺寸（EXIF 方向后）也要对得上。
        var size = CollageService.ReadSize(blue);
        Check("读尺寸 宽", size.Width, 500);
        Check("读尺寸 高", size.Height, 1500);

        var report = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ptv", "verify_collage.txt");
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(report)!);
        var builder = new StringBuilder();
        foreach (var line in lines) builder.AppendLine(line);
        builder.AppendLine(lines.All(line => line.StartsWith("PASS")) ? "RESULT PASS" : "RESULT FAIL");
        System.IO.File.WriteAllText(report, builder.ToString());
    }
}
