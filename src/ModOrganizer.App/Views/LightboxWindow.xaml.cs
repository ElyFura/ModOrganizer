using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace ModOrganizer.App.Views;

public partial class LightboxWindow : Window
{
    private readonly IReadOnlyList<string> _images;
    private int _index;
    private Point _dragStart;
    private Point _panStart;
    private bool _dragging;

    public LightboxWindow(IReadOnlyList<string> images, int startIndex)
    {
        InitializeComponent();
        _images = images;
        _index = Math.Clamp(startIndex, 0, Math.Max(0, images.Count - 1));
        Load();
    }

    private void Load()
    {
        if (_images.Count == 0 || _index >= _images.Count)
        {
            Img.Source = null;
            TitleText.Text = "";
            CounterText.Text = "0 / 0";
            return;
        }
        var path = _images[_index];
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();
            Img.Source = bmp;
        }
        catch { Img.Source = null; }
        TitleText.Text = Path.GetFileName(path);
        CounterText.Text = $"{_index + 1} / {_images.Count}";
        Scale.ScaleX = Scale.ScaleY = 1;
        Pan.X = Pan.Y = 0;
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape: Close(); break;
            case Key.Right or Key.PageDown or Key.Space:
                if (_index < _images.Count - 1) { _index++; Load(); }
                break;
            case Key.Left or Key.PageUp or Key.Back:
                if (_index > 0) { _index--; Load(); }
                break;
            case Key.Home: _index = 0; Load(); break;
            case Key.End: _index = Math.Max(0, _images.Count - 1); Load(); break;
            case Key.R:
                Scale.ScaleX = Scale.ScaleY = 1;
                Pan.X = Pan.Y = 0;
                break;
        }
    }

    private void Host_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { Close(); return; }
        _dragStart = e.GetPosition(Host);
        _panStart = new Point(Pan.X, Pan.Y);
        _dragging = true;
        Host.CaptureMouse();
    }

    private void Host_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _dragging = false;
        Host.ReleaseMouseCapture();
    }

    private void Host_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var cur = e.GetPosition(Host);
        Pan.X = _panStart.X + (cur.X - _dragStart.X);
        Pan.Y = _panStart.Y + (cur.Y - _dragStart.Y);
    }

    private void Host_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        var factor = e.Delta > 0 ? 1.15 : 1 / 1.15;
        var newScale = Math.Clamp(Scale.ScaleX * factor, 0.25, 12);
        Scale.ScaleX = Scale.ScaleY = newScale;
    }

    private void Host_Close(object sender, MouseButtonEventArgs e) => Close();
}
