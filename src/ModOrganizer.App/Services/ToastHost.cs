using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ModOrganizer.App.Services;

/// <summary>
/// In-app snackbar/toast host: a singleton ObservableCollection that any UI
/// element can bind to. Items auto-expire after a few seconds.
/// </summary>
public sealed partial class ToastHost : ObservableObject
{
    public ObservableCollection<Toast> Toasts { get; } = new();
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;

    public void Show(string title, string body, Action? onClick = null,
        TimeSpan? duration = null, string? colorHex = null)
    {
        _dispatcher.BeginInvoke(() =>
        {
            var t = new Toast(title, body, onClick, colorHex);
            Toasts.Add(t);
            var dur = duration ?? TimeSpan.FromSeconds(8);
            var timer = new DispatcherTimer { Interval = dur };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                Toasts.Remove(t);
            };
            timer.Start();
        });
    }

    public void Dismiss(Toast t) => _dispatcher.BeginInvoke(() => Toasts.Remove(t));
}

public sealed partial class Toast : ObservableObject
{
    public string Title { get; }
    public string Body { get; }
    public Action? OnClick { get; }
    public Brush AccentBrush { get; }

    public Toast(string title, string body, Action? onClick, string? colorHex)
    {
        Title = title;
        Body = body;
        OnClick = onClick;
        try
        {
            var hex = !string.IsNullOrEmpty(colorHex) ? colorHex! : "#7A5CFA";
            var b = (SolidColorBrush)new BrushConverter().ConvertFrom(hex)!;
            b.Freeze();
            AccentBrush = b;
        }
        catch { AccentBrush = Brushes.MediumPurple; }
    }
}
