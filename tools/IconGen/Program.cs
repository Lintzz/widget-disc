using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

// ---------------------------------------------------------------------------
// Gera assets/DiscordVoiceWidget.ico: icone do executavel, do atalho e do
// instalador: microfone branco sobre circulo azul.
//
//   dotnet run --project tools/IconGen -- assets/DiscordVoiceWidget.ico
//
// Tamanhos ate 64px vao como bitmap (DIB) e o de 256px como PNG: e o formato que
// Explorer, compilador de recursos e Inno Setup aceitam sem surpresa.
// ---------------------------------------------------------------------------

var output = args.Length > 0 ? args[0] : "DiscordVoiceWidget.ico";
int[] sizes = [16, 20, 24, 32, 40, 48, 64, 256];

var thread = new Thread(() =>
{
    var entries = sizes.Select(size => (Size: size, Data: size >= 256 ? Png(Render(size)) : Dib(Render(size)))).ToList();
    WriteIco(output, entries);
});

// RenderTargetBitmap exige thread STA.
thread.SetApartmentState(ApartmentState.STA);
thread.Start();
thread.Join();

Console.WriteLine($"icone gerado: {Path.GetFullPath(output)} ({string.Join(", ", sizes)} px)");

static BitmapSource Render(int size)
{
    var s = size / 16.0;
    var white = Brushes.White;
    var stroke = new Pen(white, 1.3 * s) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };

    var visual = new DrawingVisual();
    using (var dc = visual.RenderOpen())
    {
        var radius = (size / 2.0) - 0.5;
        dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x58, 0x65, 0xF2)), null, new Point(size / 2.0, size / 2.0), radius, radius);
        dc.DrawRoundedRectangle(white, null, new Rect(6.3 * s, 3.2 * s, 3.4 * s, 6 * s), 1.7 * s, 1.7 * s);

        var arc = new PathFigure { StartPoint = new Point(11.6 * s, 8 * s) };
        arc.Segments.Add(new ArcSegment(new Point(4.4 * s, 8 * s), new Size(3.6 * s, 3.2 * s), 0, false, SweepDirection.Clockwise, true));
        dc.DrawGeometry(null, stroke, new PathGeometry([arc]));

        dc.DrawLine(stroke, new Point(8 * s, 11.3 * s), new Point(8 * s, 12.8 * s));
    }

    var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
    bitmap.Render(visual);

    // O ICO espera alfa nao pre-multiplicado.
    return new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
}

static byte[] Png(BitmapSource bitmap)
{
    var encoder = new PngBitmapEncoder();
    encoder.Frames.Add(BitmapFrame.Create(bitmap));
    using var stream = new MemoryStream();
    encoder.Save(stream);
    return stream.ToArray();
}

/// <summary>BITMAPINFOHEADER + pixels BGRA de baixo para cima + mascara AND vazia.</summary>
static byte[] Dib(BitmapSource bitmap)
{
    int w = bitmap.PixelWidth, h = bitmap.PixelHeight;
    var pixels = new byte[w * h * 4];
    bitmap.CopyPixels(pixels, w * 4, 0);

    var maskRowBytes = ((w + 31) / 32) * 4;
    using var stream = new MemoryStream();
    using var writer = new BinaryWriter(stream);

    writer.Write(40);          // biSize
    writer.Write(w);           // biWidth
    writer.Write(h * 2);       // biHeight: cor + mascara
    writer.Write((short)1);    // biPlanes
    writer.Write((short)32);   // biBitCount
    writer.Write(0);           // biCompression = BI_RGB
    writer.Write(pixels.Length + (maskRowBytes * h));
    writer.Write(0);
    writer.Write(0);
    writer.Write(0);
    writer.Write(0);

    for (var row = h - 1; row >= 0; row--) writer.Write(pixels, row * w * 4, w * 4);
    writer.Write(new byte[maskRowBytes * h]);

    return stream.ToArray();
}

static void WriteIco(string path, List<(int Size, byte[] Data)> entries)
{
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

    using var stream = File.Create(path);
    using var writer = new BinaryWriter(stream);

    writer.Write((short)0);                // reservado
    writer.Write((short)1);                // tipo: icone
    writer.Write((short)entries.Count);

    var offset = 6 + (16 * entries.Count);
    foreach (var (size, data) in entries)
    {
        writer.Write((byte)(size >= 256 ? 0 : size));  // 0 significa 256
        writer.Write((byte)(size >= 256 ? 0 : size));
        writer.Write((byte)0);             // cores na paleta
        writer.Write((byte)0);             // reservado
        writer.Write((short)1);            // planos
        writer.Write((short)32);           // bits por pixel
        writer.Write(data.Length);
        writer.Write(offset);
        offset += data.Length;
    }

    foreach (var (_, data) in entries) writer.Write(data);
}
