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
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _mainVm = new MainWindowViewModel();
            var window = new MainWindow(_mainVm);
            desktop.MainWindow = window;

            desktop.Exit += async (_, _) =>
            {
                if (_mainVm is not null)
                    await _mainVm.DisposeAsync();
            };

            _ = _mainVm.InitializeAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
