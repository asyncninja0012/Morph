using System.Windows;
using System.Windows.Interop;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Morph.App.Toasts;

/// <summary>What one toast shows. <paramref name="Fraction"/>: null hides the bar, NaN makes it indeterminate.</summary>
public sealed record ToastView(string Glyph, string Title, string Message, double? Fraction,
    bool CanCancel = false, bool CanShowFolder = false, bool CanCopyDetails = false);

/// <summary>The bottom-right card (PLAN §6, JB-2). View only: <see cref="ToastHost"/> decides what it shows.</summary>
public sealed partial class ToastWindow : Window
{
    public ToastWindow()
    {
        InitializeComponent();
        CloseButton.Click += (_, _) => CloseClicked?.Invoke();
        CancelButton.Click += (_, _) => CancelClicked?.Invoke();
        ShowFolderButton.Click += (_, _) => ShowFolderClicked?.Invoke();
        CopyDetailsButton.Click += (_, _) => CopyDetailsClicked?.Invoke();

        // Clicking a button must not pull focus from the app the user is working in.
        var hwnd = new HWND(new WindowInteropHelper(this).EnsureHandle());
        var exStyle = (WINDOW_EX_STYLE)PInvoke.GetWindowLongPtr(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE)
            | WINDOW_EX_STYLE.WS_EX_NOACTIVATE | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW;
        PInvoke.SetWindowLongPtr(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, (nint)exStyle);
    }

    public event Action? CloseClicked;

    public event Action? CancelClicked;

    public event Action? ShowFolderClicked;

    public event Action? CopyDetailsClicked;

    public void Render(ToastView view)
    {
        GlyphText.Text = view.Glyph;
        TitleText.Text = view.Title;
        MessageText.Text = view.Message;
        Bar.Visibility = view.Fraction is null ? Visibility.Collapsed : Visibility.Visible;
        Bar.IsIndeterminate = view.Fraction is double.NaN;
        Bar.Value = view.Fraction is { } f && !double.IsNaN(f) ? f : 0;
        CancelButton.Visibility = view.CanCancel ? Visibility.Visible : Visibility.Collapsed;
        ShowFolderButton.Visibility = view.CanShowFolder ? Visibility.Visible : Visibility.Collapsed;
        CopyDetailsButton.Visibility = view.CanCopyDetails ? Visibility.Visible : Visibility.Collapsed;
    }
}
