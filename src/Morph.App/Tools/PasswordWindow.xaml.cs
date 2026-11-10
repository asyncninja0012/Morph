using System.IO;
using System.Windows;

using Morph.Overlay.Theme;

namespace Morph.App.Tools;

/// <summary>
/// The password popover (IT-5), opened by a drop on the Protect or Unlock wedge: one password box, nothing else.
/// Protecting a ZIP also picks the lock: File Explorer's ZipCrypto or AES-256. Raises <see cref="RunClicked"/> with the
/// password and whether AES-256 was picked; nothing runs if the window is closed.
/// </summary>
public sealed partial class PasswordWindow : Window
{
    public PasswordWindow(IReadOnlyList<string> files, bool unlock)
    {
        InitializeComponent();
        var dark = MorphGlass.IsDark();
        MorphTheme.Apply(this, dark, glass: false);
        SourceInitialized += (_, _) => MorphTheme.Apply(this, dark, MorphGlass.TryApplyBackdrop(this, dark));

        Title = unlock ? "Unlock" : "Protect";
        RunButton.Content = Title;
        FilesText.Text = files is [var one] ? Path.GetFileName(one) : $"{files.Count} files";
        FilesText.ToolTip = string.Join(Environment.NewLine, files);
        HintText.Text = unlock
            ? "Saves a copy without the password. The original keeps it."
            : "Saves a copy that asks for this password to open. Morph can't recover a forgotten password; the original stays unlocked.";

        ZipPanel.Visibility = !unlock && files.Any(f => Path.GetExtension(f).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            ? Visibility.Visible : Visibility.Collapsed;
        Hidden.PasswordChanged += (_, _) => Changed(Hidden.Password);
        Shown.TextChanged += (_, _) => Changed(Shown.Text);
        ShowBox.Click += (_, _) =>
        {
            var show = ShowBox.IsChecked == true;
            Shown.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            Hidden.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
            if (show)
            {
                Shown.Focus();
                Shown.CaretIndex = Shown.Text.Length;
            }
            else
            {
                Hidden.Focus();
            }
        };
        RunButton.Click += (_, _) =>
        {
            RunClicked?.Invoke(Hidden.Password, StrongLock.IsChecked == true);
            Close();
        };
        Loaded += (_, _) => Hidden.Focus();
    }

    public event Action<string, bool>? RunClicked;

    // The two boxes mirror each other; the PasswordBox holds the value.
    private void Changed(string password)
    {
        if (Hidden.Password != password)
        {
            Hidden.Password = password;
        }

        if (Shown.Text != password)
        {
            Shown.Text = password;
        }

        RunButton.IsEnabled = password.Length > 0;
    }
}
