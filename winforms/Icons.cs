using System.Drawing.Drawing2D;

namespace MxZip.Viewer;

/// <summary>
/// MxZip's own line icons, drawn at any size in the theme's colours (no image files): rounded 1.8 px strokes on
/// a 24-unit grid, the zip-tab motif of the MxZip logo where an archive is shown.
/// </summary>
static class Icons
{
    public delegate void Painter(Graphics g, Pen pen, Brush accent);

    public static Bitmap Render(Painter paint, int size, Color stroke, Color accent)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.ScaleTransform(size / 24f, size / 24f);
        using var pen = new Pen(stroke, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        using var brush = new SolidBrush(accent);
        paint(g, pen, brush);
        return bmp;
    }

    static GraphicsPath Rounded(float x, float y, float w, float h, float r)
    {
        var p = new GraphicsPath();
        p.AddArc(x, y, r * 2, r * 2, 180, 90);
        p.AddArc(x + w - r * 2, y, r * 2, r * 2, 270, 90);
        p.AddArc(x + w - r * 2, y + h - r * 2, r * 2, r * 2, 0, 90);
        p.AddArc(x, y + h - r * 2, r * 2, r * 2, 90, 90);
        p.CloseFigure();
        return p;
    }

    static void Folder(Graphics g, Pen pen)
    {
        using var p = new GraphicsPath();
        p.AddLines(new PointF[] { new(3, 18), new(3, 6), new(9, 6), new(11, 8), new(21, 8), new(21, 18) });
        p.CloseFigure();
        g.DrawPath(pen, p);
    }

    static void Page(Graphics g, Pen pen)
    {
        using var p = new GraphicsPath();
        p.AddLines(new PointF[] { new(6, 3), new(14, 3), new(19, 8), new(19, 21), new(6, 21) });
        p.CloseFigure();
        g.DrawPath(pen, p);
        g.DrawLines(pen, new PointF[] { new(14, 3), new(14, 8), new(19, 8) });
    }

    /// <summary>열기: an open folder.</summary>
    public static void Open(Graphics g, Pen pen, Brush accent)
    {
        g.DrawLines(pen, new PointF[] { new(3, 18), new(3, 6), new(9, 6), new(11, 8), new(19, 8), new(19, 11) });
        g.DrawLines(pen, new PointF[] { new(3, 18), new(6.5f, 11), new(22, 11), new(18.5f, 18), new(3, 18) });
    }

    /// <summary>풀기: an archive box with an arrow out of it.</summary>
    public static void Extract(Graphics g, Pen pen, Brush accent)
    {
        using (var box = Rounded(3, 9, 18, 12, 2))
            g.DrawPath(pen, box);
        g.DrawLine(pen, 3, 13, 21, 13);
        g.DrawLine(pen, 12, 2, 12, 10);
        g.DrawLines(pen, new PointF[] { new(8.5f, 6.5f), new(12, 10), new(15.5f, 6.5f) });
        g.FillRectangle(accent, 10.5f, 14.5f, 3, 2.2f);  // the zip tab
    }

    /// <summary>새로 압축: a folder with a plus.</summary>
    public static void NewArchive(Graphics g, Pen pen, Brush accent)
    {
        Folder(g, pen);
        g.DrawLine(pen, 12, 10.5f, 12, 15.5f);
        g.DrawLine(pen, 9.5f, 13, 14.5f, 13);
    }

    /// <summary>파일 추가: a page with a plus.</summary>
    public static void AddFile(Graphics g, Pen pen, Brush accent)
    {
        Page(g, pen);
        g.DrawLine(pen, 12.5f, 12, 12.5f, 17);
        g.DrawLine(pen, 10, 14.5f, 15, 14.5f);
    }

    /// <summary>파일 삭제: a page with a minus.</summary>
    public static void DeleteFile(Graphics g, Pen pen, Brush accent)
    {
        Page(g, pen);
        g.DrawLine(pen, 10, 14.5f, 15, 14.5f);
    }

    /// <summary>오류 검사: a page with a check mark.</summary>
    public static void Test(Graphics g, Pen pen, Brush accent)
    {
        Page(g, pen);
        g.DrawLines(pen, new PointF[] { new(9.5f, 14.5f), new(11.8f, 16.8f), new(16, 12) });
    }

    /// <summary>정보: an i in a circle.</summary>
    public static void Info(Graphics g, Pen pen, Brush accent)
    {
        g.DrawEllipse(pen, 3, 3, 18, 18);
        g.DrawLine(pen, 12, 11, 12, 16.5f);
        using var dot = new SolidBrush(pen.Color);
        g.FillEllipse(dot, 10.9f, 6.7f, 2.2f, 2.2f);
    }

    /// <summary>전체 보기: a flat list.</summary>
    public static void FlatList(Graphics g, Pen pen, Brush accent)
    {
        using var dot = new SolidBrush(pen.Color);
        foreach (float y in new[] { 6f, 12f, 18f })
        {
            g.DrawLine(pen, 9, y, 21, y);
            g.FillEllipse(dot, 3.6f, y - 1.2f, 2.4f, 2.4f);
        }
    }

    /// <summary>환경설정: a gear.</summary>
    public static void Settings(Graphics g, Pen pen, Brush accent)
    {
        using var p = new GraphicsPath();
        const int teeth = 8;
        for (int i = 0; i < teeth * 2; i++)
        {
            double a0 = Math.PI * 2 * i / (teeth * 2), a1 = Math.PI * 2 * (i + 1) / (teeth * 2);
            float r = i % 2 == 0 ? 9.5f : 7.4f;
            p.AddLine(12 + r * (float)Math.Cos(a0), 12 + r * (float)Math.Sin(a0),
                      12 + r * (float)Math.Cos(a1), 12 + r * (float)Math.Sin(a1));
        }
        p.CloseFigure();
        g.DrawPath(pen, p);
        g.DrawEllipse(pen, 9, 9, 6, 6);
    }

    /// <summary>다크 모드: a crescent moon.</summary>
    public static void Moon(Graphics g, Pen pen, Brush accent)
    {
        using var p = new GraphicsPath();
        // outer arc of the moon, then the inner arc of the bite back to its start
        p.AddArc(3.5f, 3.5f, 17, 17, 300, 300);
        p.AddArc(8.5f, 1.5f, 14, 14, 165, -150);
        p.CloseFigure();
        g.DrawPath(pen, p);
    }

    /// <summary>위로: an arrow up to the parent folder.</summary>
    public static void Up(Graphics g, Pen pen, Brush accent)
    {
        g.DrawLine(pen, 12, 19, 12, 5);
        g.DrawLines(pen, new PointF[] { new(6.5f, 10.5f), new(12, 5), new(17.5f, 10.5f) });
    }

    /// <summary>
    /// The empty-state picture: a storage box with its lid, a hand hole and a label plate in the accent, on a soft
    /// floor shadow; drawn on a 100-unit grid at size px.
    /// </summary>
    public static Bitmap StorageBox(int size, Color stroke, Color body, Color lid, Color shadow, Color accent, Color accentText)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.ScaleTransform(size / 100f, size / 100f);
        using var pen = new Pen(stroke, 2.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        using (var b = new SolidBrush(shadow))
            g.FillEllipse(b, 12, 83, 76, 9);
        using (var b = new SolidBrush(body))
        using (var p = Rounded(16, 37, 68, 49, 3))
        {
            g.FillPath(b, p);
            g.DrawPath(pen, p);
        }
        using (var b = new SolidBrush(lid))
        using (var p = Rounded(11, 23, 78, 16, 3))
        {
            g.FillPath(b, p);
            g.DrawPath(pen, p);
        }
        using (var p = Rounded(42, 45, 16, 5.5f, 2.75f))
            g.DrawPath(pen, p);
        using (var b = new SolidBrush(accent))
        using (var p = Rounded(32, 57, 36, 19, 2.5f))
            g.FillPath(b, p);
        using var lines = new Pen(Color.FromArgb(210, accentText), 2.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(lines, 38.5f, 63.5f, 61.5f, 63.5f);
        g.DrawLine(lines, 38.5f, 69.5f, 52, 69.5f);
        return bmp;
    }

    /// <summary>An encrypted entry: a padlock.</summary>
    public static void Lock(Graphics g, Pen pen, Brush accent)
    {
        using (var body = Rounded(5, 10.5f, 14, 10.5f, 2))
            g.DrawPath(pen, body);
        g.DrawArc(pen, 8, 4, 8, 9, 180, 180);
        g.DrawLine(pen, 8, 8.5f, 8, 10.5f);
        g.DrawLine(pen, 16, 8.5f, 16, 10.5f);
    }

    public static void Close(Graphics g, Pen pen, Brush accent)
    {
        g.DrawLine(pen, 7, 7, 17, 17);
        g.DrawLine(pen, 17, 7, 7, 17);
    }
}
