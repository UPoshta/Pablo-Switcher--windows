using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Pablo_Switcher.Services;

public static class TrayIconService
{
    // =========================================================================
    // КОНФІГУРАЦІЯ ІКОНКИ ТРЕЮ
    // =========================================================================
    public const string IconText = "PB";
    public const int IconSizePx = 32;
    private const int SupersampleFactor = 4;
    private const string FontFamilyName = "Segoe UI";

    private const float PaddingRatio = 0.08f;
    private const float StrokeWidthRatio = 0.075f;
    private const float CornerRadiusRatio = 0.22f;

    private static readonly Color LightTaskbarForeground = Color.FromArgb(255, 24, 24, 28);
    private static readonly Color DarkTaskbarForeground = Color.FromArgb(255, 245, 247, 250);

    private const string PersonalizeKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>
    /// Визначає, чи використовує панель завдань Windows 11 світлу тему.
    /// </summary>
    public static bool IsSystemLightTheme()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(PersonalizeKeyPath, writable: false);
            object? val = key?.GetValue("SystemUsesLightTheme");
            return val is int intVal && intVal == 1;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Генерує іконку для системного трею згідно з налаштуваннями у цьому класі.
    /// </summary>
    public static Icon CreateTrayIcon(string? customText = null)
    {
        string text = customText ?? IconText;
        Color foreground = IsSystemLightTheme() ? LightTaskbarForeground : DarkTaskbarForeground;

        int hiSize = IconSizePx * SupersampleFactor;
        using var hiBitmap = new Bitmap(hiSize, hiSize, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(hiBitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);

            float pad = hiSize * PaddingRatio;
            float strokeWidth = hiSize * StrokeWidthRatio;
            float cornerRadius = hiSize * CornerRadiusRatio;

            var rect = new RectangleF(
                pad + strokeWidth / 2f,
                pad + strokeWidth / 2f,
                hiSize - (pad * 2) - strokeWidth,
                hiSize - (pad * 2) - strokeWidth);

            using var path = CreateRoundedRectPath(rect, cornerRadius);
            using var pen = new Pen(foreground, strokeWidth);
            g.DrawPath(pen, path);

            float fontSize = hiSize * (text.Length > 2 ? 0.34f : 0.42f);
            using var font = new Font(FontFamilyName, fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(foreground);
            using var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoWrap
            };

            var textRect = new RectangleF(0, hiSize * 0.03f, hiSize, hiSize);
            g.DrawString(text, font, brush, textRect, format);
        }

        using var finalBitmap = new Bitmap(IconSizePx, IconSizePx, PixelFormat.Format32bppArgb);
        using (Graphics gFinal = Graphics.FromImage(finalBitmap))
        {
            gFinal.InterpolationMode = InterpolationMode.HighQualityBicubic;
            gFinal.SmoothingMode = SmoothingMode.HighQuality;
            gFinal.DrawImage(hiBitmap, 0, 0, IconSizePx, IconSizePx);
        }

        IntPtr hIcon = finalBitmap.GetHicon();
        try
        {
            using Icon tempIcon = Icon.FromHandle(hIcon);
            return (Icon)tempIcon.Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    private static GraphicsPath CreateRoundedRectPath(RectangleF bounds, float radius)
    {
        float diameter = radius * 2f;
        var path = new GraphicsPath();

        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();

        return path;
    }
}
