using System;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace copperInspection
{
    /// <summary>Binds a PNG byte[] (as stored in Mongo) to an Image.Source thumbnail.</summary>
    public sealed class BytesToImageConverter : IValueConverter
    {
        public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not byte[] data || data.Length == 0) return null;

            var bmp = new BitmapImage();
            using var ms = new MemoryStream(data);
            bmp.BeginInit();
            bmp.CacheOption      = BitmapCacheOption.OnLoad;
            bmp.StreamSource     = ms;
            bmp.DecodePixelWidth = 360; // decode small — many cards on screen at once
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
