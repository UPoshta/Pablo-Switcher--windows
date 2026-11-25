using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Pablo_Switcher.Models;
using Pablo_Switcher.Services;

namespace Pablo_Switcher.Views;

public partial class AboutWindow : Window
{
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);

    public AboutWindow(XmlI18nManager i18n)
    {
        InitializeComponent();

        Title = i18n.T("about_title");
        TxtTitle.Text = i18n.T("about_title");
        TxtBody.Text = i18n.T("about_body", ("version", AppInfo.Version));

        AppIcon.Source = LoadBestAppIconSource();
    }

    private static ImageSource LoadBestAppIconSource()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Resources/AppIcon.ico", UriKind.Absolute);
            var streamInfo = Application.GetResourceStream(uri);
            if (streamInfo is not null)
            {
                using var stream = streamInfo.Stream;
                var decoder = new IconBitmapDecoder(
                    stream,
                    BitmapCreateOptions.PreservePixelFormat,
                    BitmapCacheOption.OnLoad);

                // Беремо кадр із найбільшою роздільною здатністю (наприклад, 128x128 або 256x256)
                var bestFrame = decoder.Frames
                    .OrderByDescending(f => f.PixelWidth)
                    .FirstOrDefault();

                if (bestFrame is not null)
                    return bestFrame;
            }
        }
        catch
        {
            // Переходимо до програмного рендеру іконки
        }

        return RenderFallbackIcon(128);
    }

    /// <summary>
    /// Резервний рендер фірмової іконки в стилі AppIcon, якщо AppIcon.ico відсутній у ресурсах.
    /// </summary>
    private static ImageSource RenderFallbackIcon(int size)
    {
        using var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(System.Drawing.Color.Transparent);

            float pad = size * 0.08f;
            var outerRect = new RectangleF(pad, pad, size - pad * 2, size - pad * 2);
            float outerRadius = size * 0.22f;

            using var outerPath = CreateRoundedRect(outerRect, outerRadius);
            using var bgBrush = new System.Drawing.Drawing2D.LinearGradientBrush(
                outerRect,
                System.Drawing.Color.FromArgb(44, 48, 58),
                System.Drawing.Color.FromArgb(22, 24, 30),
                LinearGradientMode.Vertical);

            g.FillPath(bgBrush, outerPath);

            using var borderPen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(45, 255, 255, 255), size * 0.015f);
            g.DrawPath(borderPen, outerPath);

            // Внутрішня рамка клавіші та текст із TrayIconService.IconText
            var fgColor = System.Drawing.Color.FromArgb(245, 247, 250);
            float innerPadX = size * 0.22f;
            float innerPadY = size * 0.28f;
            var innerRect = new RectangleF(innerPadX, innerPadY, size - innerPadX * 2, size - innerPadY * 2);

            using var innerPath = CreateRoundedRect(innerRect, size * 0.11f);
            using var innerPen = new System.Drawing.Pen(fgColor, size * 0.035f);
            g.DrawPath(innerPen, innerPath);

            using var font = new Font("Segoe UI", size * 0.28f, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
            using var textBrush = new SolidBrush(fgColor);
            using var sf = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };

            g.DrawString(TrayIconService.IconText, font, textBrush, new RectangleF(0, size * 0.02f, size, size), sf);
        }

        IntPtr hBitmap = bmp.GetHbitmap();
        try
        {
            return Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap,
                IntPtr.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
        }
        finally
        {
            DeleteObject(hBitmap);
        }
    }

    private static GraphicsPath CreateRoundedRect(RectangleF bounds, float radius)
    {
        float d = radius * 2f;
        var path = new GraphicsPath();
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private void OnOkClicked(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
