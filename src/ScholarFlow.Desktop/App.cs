using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Material.Icons.Avalonia;
using ScholarFlow.Core.Models;
using ScholarFlow.Desktop.Theme;
using ScholarFlow.Desktop.ViewModels;
using ScholarFlow.Desktop.Views;

namespace ScholarFlow.Desktop;

/// <summary>
/// Pure C# Avalonia Application bootstrapper (zero .axaml files).
/// </summary>
public sealed class App : Application
{
    private MainWindowViewModel? _mainVm;
    private TrayIcon? _trayIcon;
    private bool _isExplicitExit;

    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        Styles.Add(new MaterialIconStyles(null));
        ThemeService.ApplyTheme(ThemeMode.Dark);

        // Pure C# global styles for Linux Skia ToolTip rendering & TextBox focus aesthetics
        var tooltipStyle = new Style(x => x.OfType<ToolTip>())
        {
            Setters =
            {
                new Setter(TemplatedControl.BackgroundProperty, new SolidColorBrush(Color.Parse("#22252B"))),
                new Setter(TemplatedControl.ForegroundProperty, new SolidColorBrush(Color.Parse("#ECEFF4"))),
                new Setter(TemplatedControl.BorderBrushProperty, new SolidColorBrush(Color.Parse("#5E81F4"))),
                new Setter(TemplatedControl.BorderThicknessProperty, new Thickness(1)),
                new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(6)),
                new Setter(TemplatedControl.PaddingProperty, new Thickness(10, 6)),
                new Setter(TemplatedControl.FontSizeProperty, 12.0)
            }
        };
        Styles.Add(tooltipStyle);

        var textBoxStyle = new Style(x => x.OfType<TextBox>())
        {
            Setters =
            {
                new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(6)),
                new Setter(TemplatedControl.PaddingProperty, new Thickness(10, 7))
            }
        };
        Styles.Add(textBoxStyle);

        var datePickerStyle = new Style(x => x.OfType<CalendarDatePicker>())
        {
            Setters =
            {
                new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(6))
            }
        };
        Styles.Add(datePickerStyle);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            _mainVm = new MainWindowViewModel();
            var window = new MainWindow(_mainVm);
            desktop.MainWindow = window;

            window.Closing += (_, e) =>
            {
                if (!_isExplicitExit && _mainVm.Settings.MinimizeToTrayOnClose)
                {
                    e.Cancel = true;
                    window.Hide();
                }
            };

            InitializeTrayIcon(desktop, window);

            desktop.Exit += async (_, _) =>
            {
                _trayIcon?.Dispose();
                if (_mainVm is not null)
                    await _mainVm.DisposeAsync();
            };

            if (Program.StartMinimized)
                window.Hide();

            _ = _mainVm.InitializeAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void InitializeTrayIcon(IClassicDesktopStyleApplicationLifetime desktop, MainWindow window)
    {
        try
        {
            WindowIcon? icon = null;
            using var stream = typeof(App).Assembly.GetManifestResourceStream("ScholarFlow.Assets.scholarflow.ico");
            if (stream is not null)
                icon = new WindowIcon(stream);

            var menu = new NativeMenu();
            var showItem = new NativeMenuItem("Show ScholarFlow");
            showItem.Click += (_, _) => RestoreMainWindow(window);
            menu.Items.Add(showItem);

            var syncItem = new NativeMenuItem("Sync eLMS Now");
            syncItem.Click += async (_, _) =>
            {
                if (_mainVm is not null)
                    await _mainVm.RunFullSyncAsync();
            };
            menu.Items.Add(syncItem);

            menu.Items.Add(new NativeMenuItemSeparator());

            var quitItem = new NativeMenuItem("Quit ScholarFlow");
            quitItem.Click += (_, _) =>
            {
                _isExplicitExit = true;
                desktop.Shutdown();
            };
            menu.Items.Add(quitItem);

            _trayIcon = new TrayIcon
            {
                ToolTipText = "ScholarFlow — Academic Workspace",
                Icon = icon,
                Menu = menu,
                IsVisible = true
            };
            _trayIcon.Clicked += (_, _) => RestoreMainWindow(window);
        }
        catch
        {
            // TrayIcon is optional on headless/unsupported window managers
        }
    }

    private static void RestoreMainWindow(MainWindow window)
    {
        if (!window.IsVisible)
            window.Show();
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        window.Activate();
    }
}
