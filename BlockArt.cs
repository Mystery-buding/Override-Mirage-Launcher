using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OverrideMirageLauncher.Core;

namespace OverrideMirageLauncher;

/// <summary>
/// 方块图标：16×16 像素纹理逐格贴到等轴测立方体的三个面上，
/// 渲染成位图后缓存复用——等价于 PCL 的 Images/Blocks/*.png，
/// 只是纹理由程序生成。
/// 想换成自己的美术素材，把同名 PNG 放到 exe 同级的 images/blocks/ 即可覆盖。
/// </summary>
public static class BlockArt
{
    private const int Tex = 16;
    private const double CanvasSize = 192;

    /// <summary>自定义素材目录：images/blocks/{grass,command,dirt}.png</summary>
    public static string OverrideDir => Path.Combine(Paths.BaseDir, "images", "blocks");

    private static readonly Lazy<ImageSource> Grass = new(() =>
        Load("grass", "Grass", () => Render(BuildGrassTop(), BuildGrassSide(), 1.0, 0.86, 0.70)));

    private static readonly Lazy<ImageSource> Command = new(() =>
        Load("command", "CommandBlock", () => Render(BuildCommandTexture(), BuildCommandTexture(), 1.0, 0.84, 0.68)));

    private static readonly Lazy<ImageSource> Ancient = new(() =>
        Load("dirt", "CobbleStone", () => Render(BuildDirtTexture(), BuildDirtTexture(), 1.0, 0.84, 0.68)));

    /// <summary>读取内嵌素材（加载器图标等也走这里），缺失返回 null。</summary>
    public static ImageSource? LoaderAsset(string assetName) => LoadAsset(assetName);

    public static ImageSource GrassImage => Grass.Value;
    public static ImageSource CommandImage => Command.Value;
    public static ImageSource DirtImage => Ancient.Value;

    /// <summary>
    /// 把当前生效的图标导出成 PNG，方便二次编辑。
    /// 内嵌素材直接原样写出；没有素材时才渲染程序生成的纹理。
    /// </summary>
    public static void ExportAll(string dir, int pixelSize = 512)
    {
        Directory.CreateDirectory(dir);

        ExportOne("grass", "Grass", Path.Combine(dir, "grass.png"),
            () => Render(BuildGrassTop(), BuildGrassSide(), 1.0, 0.86, 0.70, pixelSize));
        ExportOne("command", "CommandBlock", Path.Combine(dir, "command.png"),
            () => Render(BuildCommandTexture(), BuildCommandTexture(), 1.0, 0.84, 0.68, pixelSize));
        ExportOne("dirt", "CobbleStone", Path.Combine(dir, "dirt.png"),
            () => Render(BuildDirtTexture(), BuildDirtTexture(), 1.0, 0.84, 0.68, pixelSize));
    }

    private static void ExportOne(string overrideName, string assetName, string path, Func<ImageSource> fallback)
    {
        var source = LoadOverride(overrideName) ?? LoadAsset(assetName);
        Save(source ?? fallback(), path);
    }

    /// <summary>优先级：images/blocks 覆盖 → 内嵌素材 → 程序生成。</summary>
    private static ImageSource Load(string overrideName, string assetName, Func<ImageSource> fallback) =>
        LoadOverride(overrideName) ?? LoadAsset(assetName) ?? fallback();

    /// <summary>内嵌的 PCL 方块图标（Assets/Blocks，轻度引用并署名）。</summary>
    private static ImageSource? LoadAsset(string name)
    {
        try
        {
            var assembly = typeof(BlockArt).Assembly.GetName().Name;
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(
                $"pack://application:,,,/{assembly};component/Assets/Blocks/{name}.png", UriKind.Absolute);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();
            LogBus.Info($"使用内嵌方块图标：{name}.png");
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private static void Save(ImageSource source, string path)
    {
        if (source is not BitmapSource bitmap) return;

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    // ---------------- 自定义素材 ----------------

    private static ImageSource? LoadOverride(string name)
    {
        // 同时接受小写与 PCL 风格的命名
        foreach (var file in new[] { name + ".png", name + ".jpg", Capitalize(name) + ".png" })
        {
            var path = Path.Combine(OverrideDir, file);
            if (!File.Exists(path)) continue;

            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(path, UriKind.Absolute);
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.EndInit();
                bitmap.Freeze();

                LogBus.Info($"使用自定义方块图标：{path}");
                return bitmap;
            }
            catch (Exception ex)
            {
                LogBus.Warn($"自定义方块图标读取失败（{file}）：" + ex.Message);
            }
        }

        return null;
    }

    private static string Capitalize(string name) =>
        name.Length == 0 ? name : char.ToUpperInvariant(name[0]) + name[1..];


    // ---------------- 纹理生成 ----------------

    private static Color[] BuildGrassTop()
    {
        var rnd = new Random(20261024);
        var pixels = new Color[Tex * Tex];

        for (var i = 0; i < pixels.Length; i++)
        {
            var n = rnd.Next(-14, 15);
            var dark = rnd.NextDouble() < 0.18;
            pixels[i] = Shift(dark ? Color.FromRgb(0x69, 0xAC, 0x4D) : Color.FromRgb(0x7C, 0xC2, 0x5B), n);
        }

        return pixels;
    }

    private static Color[] BuildGrassSide()
    {
        var rnd = new Random(20261025);
        var pixels = new Color[Tex * Tex];

        // 每一列草皮下沿的深度略有差异，形成参差的垂边
        var depth = new int[Tex];
        for (var x = 0; x < Tex; x++) depth[x] = 2 + rnd.Next(0, 3);

        for (var y = 0; y < Tex; y++)
        {
            for (var x = 0; x < Tex; x++)
            {
                var noise = rnd.Next(-12, 13);
                pixels[y * Tex + x] = y < depth[x]
                    ? Shift(Color.FromRgb(0x6F, 0xAF, 0x4E), noise)
                    : Shift(rnd.NextDouble() < 0.2
                        ? Color.FromRgb(0x79, 0x54, 0x39)
                        : Color.FromRgb(0x86, 0x60, 0x43), noise);
            }
        }

        return pixels;
    }

    private static Color[] BuildCommandTexture()
    {
        var rnd = new Random(20261026);
        var pixels = new Color[Tex * Tex];

        for (var y = 0; y < Tex; y++)
        {
            for (var x = 0; x < Tex; x++)
            {
                var noise = rnd.Next(-10, 11);
                Color baseColor;

                if (x is 0 or Tex - 1 || y is 0 or Tex - 1)
                    baseColor = Color.FromRgb(0x9A, 0x57, 0x22);        // 外框
                else if (x is >= 5 and <= 10 && y is >= 5 and <= 10)
                    baseColor = Color.FromRgb(0xEC, 0xA0, 0x55);        // 中央亮块
                else
                    baseColor = Color.FromRgb(0xC9, 0x77, 0x37);        // 主体

                pixels[y * Tex + x] = Shift(baseColor, noise);
            }
        }

        return pixels;
    }

    private static Color[] BuildDirtTexture()
    {
        var rnd = new Random(20261027);
        var pixels = new Color[Tex * Tex];

        for (var i = 0; i < pixels.Length; i++)
        {
            var noise = rnd.Next(-14, 15);
            var dark = rnd.NextDouble() < 0.22;
            pixels[i] = Shift(dark ? Color.FromRgb(0x74, 0x4C, 0x2E) : Color.FromRgb(0x8B, 0x5E, 0x3C), noise);
        }

        return pixels;
    }

    // ---------------- 等轴测渲染 ----------------

    private static ImageSource Render(Color[] topTexture, Color[] sideTexture,
        double topTint, double leftTint, double rightTint, int pixelSize = 192)
    {
        var canvas = new Canvas
        {
            Width = CanvasSize,
            Height = CanvasSize,
            Background = Brushes.Transparent
        };

        // 立方体三个面的顶点（192 逻辑像素画布）
        var topA = new Point(96, 10);
        var topB = new Point(184, 54);
        var topD = new Point(8, 54);

        var leftA = new Point(8, 54);
        var leftB = new Point(96, 98);
        var leftD = new Point(8, 138);

        var rightA = new Point(96, 98);
        var rightB = new Point(184, 54);
        var rightD = new Point(96, 182);

        DrawFace(canvas, leftA, leftB, leftD, sideTexture, leftTint);
        DrawFace(canvas, rightA, rightB, rightD, sideTexture, rightTint);
        DrawFace(canvas, topA, topB, topD, topTexture, topTint);

        canvas.Measure(new Size(CanvasSize, CanvasSize));
        canvas.Arrange(new Rect(0, 0, CanvasSize, CanvasSize));
        canvas.UpdateLayout();

        var dpi = 96 * (pixelSize / CanvasSize);
        var bitmap = new RenderTargetBitmap(pixelSize, pixelSize, dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(canvas);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>把一个面的 16×16 纹理逐格贴成平行四边形。</summary>
    private static void DrawFace(Canvas canvas, Point a, Point b, Point d, Color[] texture, double tint)
    {
        for (var j = 0; j < Tex; j++)
        {
            for (var i = 0; i < Tex; i++)
            {
                var p00 = PointAt(a, b, d, i, j);
                var p10 = PointAt(a, b, d, i + 1, j);
                var p11 = PointAt(a, b, d, i + 1, j + 1);
                var p01 = PointAt(a, b, d, i, j + 1);

                var brush = new SolidColorBrush(Tint(texture[j * Tex + i], tint));
                brush.Freeze();

                canvas.Children.Add(new System.Windows.Shapes.Polygon
                {
                    Points = new PointCollection { p00, p10, p11, p01 },
                    Fill = brush,
                    Stroke = brush,
                    StrokeThickness = 0.6,
                    StrokeLineJoin = PenLineJoin.Round
                });
            }
        }
    }

    private static Point PointAt(Point a, Point b, Point d, int i, int j)
    {
        var u = (double)i / Tex;
        var v = (double)j / Tex;
        return new Point(
            a.X + (b.X - a.X) * u + (d.X - a.X) * v,
            a.Y + (b.Y - a.Y) * u + (d.Y - a.Y) * v);
    }

    private static Color Shift(Color color, int delta) => Color.FromRgb(
        Clamp(color.R + delta),
        Clamp(color.G + delta),
        Clamp(color.B + delta));

    private static Color Tint(Color color, double factor) => Color.FromRgb(
        Clamp((int)(color.R * factor)),
        Clamp((int)(color.G * factor)),
        Clamp((int)(color.B * factor)));

    private static byte Clamp(int value) => (byte)Math.Clamp(value, 0, 255);
}
