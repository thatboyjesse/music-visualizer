using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using MusicVisualizer.Shared;

namespace MusicVisualizer.Setup
{
    /// <summary>Three pages: options → progress → done (or error).</summary>
    public partial class SetupWindow : Window
    {
        private bool _installing;
        private bool _succeeded;
        private readonly ExistingInstall _existing;

        public SetupWindow()
        {
            InitializeComponent();
            DrawArt();

            // WPF shows an .ico's first (smallest) frame by default, which looks blurry at 88 px.
            var icon = BitmapDecoder.Create(new Uri("pack://application:,,,/app.ico"), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            Logo.Source = icon.Frames.OrderByDescending(f => f.PixelWidth).First();

            var existing = _existing = Installer.FindExisting();
            DirBox.Text = existing?.Dir ?? InstallInfo.DefaultInstallDir;
            if (existing != null)
            {
                bool same = existing.Version == Installer.Version;
                Heading.Text = same ? "Reinstall Music Visualizer" : "Update Music Visualizer";
                SubHeading.Text = same
                    ? $"Version {existing.Version} is already installed. This reinstalls it and keeps your settings."
                    : $"Version {existing.Version ?? "?"} is installed. This replaces it with version {Installer.Version} and keeps your settings.";
                if (existing.IsLegacyInno)
                    SubHeading.Text = $"Version {existing.Version ?? "?"} is installed. This replaces it with version {Installer.Version}.";
                InstallBtn.Content = same ? "Reinstall" : "Update";
                UninstallBtn.Visibility = Visibility.Visible;
                FooterHint.Visibility = Visibility.Collapsed;
                if (!existing.IsLegacyInno)
                {
                    // Keep the app where it is, so shortcuts and the startup entry stay valid.
                    DirBox.IsEnabled = false;
                    BrowseBtn.IsEnabled = false;
                }
            }

            if (!Installer.HasPayload)
            {
                InstallBtn.IsEnabled = false;
                SubHeading.Text = "This build of Setup doesn't contain the app. Build it with build.ps1.";
            }
        }

        /// <summary>Decorative spectrum bars on the left panel, in the app's colours.</summary>
        private void DrawArt()
        {
            var rnd = new Random(7);
            const int bars = 14;
            const double width = 190, height = 404, gap = 3;
            double w = (width - gap * (bars + 1)) / bars;
            for (int i = 0; i < bars; i++)
            {
                double h = 40 + Math.Abs(Math.Sin(i * 0.55 + 0.8)) * 150 + rnd.Next(0, 40);
                var color = (Color)ColorConverter.ConvertFromString(i < bars / 2 ? "#7A5CFF" : "#3FD0C9");
                var rect = new Rectangle
                {
                    Width = w,
                    Height = h,
                    RadiusX = 2,
                    RadiusY = 2,
                    Fill = new LinearGradientBrush(Color.FromArgb(200, color.R, color.G, color.B), Color.FromArgb(30, color.R, color.G, color.B), 90),
                };
                Canvas.SetLeft(rect, gap + i * (w + gap));
                Canvas.SetTop(rect, height - h);
                Art.Children.Add(rect);
            }
        }

        private void OnTitleBarDrag(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        }

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            if (!_installing) Close();
        }

        private void OnBrowseClick(object sender, RoutedEventArgs e)
        {
            using (var dlg = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Choose where to install Music Visualizer. A \"Music Visualizer\" folder is created inside it.",
                SelectedPath = Directory.Exists(DirBox.Text) ? DirBox.Text : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            })
            {
                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                    DirBox.Text = dlg.SelectedPath.EndsWith(InstallInfo.AppName, StringComparison.OrdinalIgnoreCase)
                        ? dlg.SelectedPath
                        : System.IO.Path.Combine(dlg.SelectedPath, InstallInfo.AppName);
            }
        }

        private async void OnInstallClick(object sender, RoutedEventArgs e)
        {
            string dir = DirBox.Text.Trim();
            if (dir.Length == 0 || !System.IO.Path.IsPathRooted(dir))
            {
                MessageBox.Show(this, "Choose a full folder path to install to.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var options = new InstallOptions
            {
                InstallDir = dir,
                StartWithWindows = StartupBox.IsChecked == true,
                DesktopShortcut = DesktopBox.IsChecked == true,
            };

            _installing = true;
            CloseBtn.IsEnabled = false;
            OptionsPage.Visibility = Visibility.Collapsed;
            ProgressPage.Visibility = Visibility.Visible;

            try
            {
                await Task.Run(() => Installer.Install(options, (p, status) =>
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        Progress.Value = p;
                        StatusText.Text = status;
                    }))));
                _succeeded = true;
            }
            catch (Exception ex)
            {
                LaunchBox.IsChecked = false;
                DoneBadge.Background = new SolidColorBrush(Color.FromRgb(0xC6, 0x3B, 0x3B));
                DoneGlyph.Text = ""; // Segoe MDL2 "Cancel" (an X)
                DoneTitle.Text = "Setup couldn't finish";
                DoneText.Text = ex.Message;
                FinishBtn.Content = "Close";
            }

            _installing = false;
            CloseBtn.IsEnabled = true;
            ProgressPage.Visibility = Visibility.Collapsed;
            DonePage.Visibility = Visibility.Visible;
        }

        private async void OnUninstallClick(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show(this, "Remove Music Visualizer from this PC?\n\nThis removes the app, its shortcuts, the startup entry and your settings. Your normal wallpaper comes back.",
                    Title, MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
                return;

            _installing = true;
            CloseBtn.IsEnabled = false;
            LaunchBox.IsChecked = false;
            ProgressTitle.Text = "Removing…";
            OptionsPage.Visibility = Visibility.Collapsed;
            ProgressPage.Visibility = Visibility.Visible;

            try
            {
                await Task.Run(() => Installer.Uninstall(_existing, (p, status) =>
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        Progress.Value = p;
                        StatusText.Text = status;
                    }))));
                DoneTitle.Text = "Music Visualizer was removed";
                DoneText.Text = "Your normal wallpaper is back. You can install it again any time with this setup.";
            }
            catch (Exception ex)
            {
                DoneBadge.Background = new SolidColorBrush(Color.FromRgb(0xC6, 0x3B, 0x3B));
                DoneGlyph.Text = ""; // Segoe MDL2 "Cancel" (an X)
                DoneTitle.Text = "Couldn't remove everything";
                DoneText.Text = ex.Message;
            }

            FinishBtn.Content = "Close";
            _installing = false;
            CloseBtn.IsEnabled = true;
            ProgressPage.Visibility = Visibility.Collapsed;
            DonePage.Visibility = Visibility.Visible;
        }

        private void OnFinishClick(object sender, RoutedEventArgs e)
        {
            if (_succeeded && LaunchBox.IsChecked == true) // no arguments = run as the wallpaper
            {
                try { Installer.Launch(System.IO.Path.GetFullPath(DirBox.Text.Trim())); }
                catch (Exception ex) { MessageBox.Show(this, "Couldn't start Music Visualizer:\n\n" + ex.Message, Title); }
            }
            Close();
        }
    }
}
