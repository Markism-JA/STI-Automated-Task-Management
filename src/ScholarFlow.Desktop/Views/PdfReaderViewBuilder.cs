using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Material.Icons;
using ScholarFlow.Desktop.Theme;
using ScholarFlow.Desktop.UI;
using ScholarFlow.Desktop.ViewModels;

namespace ScholarFlow.Desktop.Views;

/// <summary>
/// Builds the Built-in PDF Reader view with a collapsible nested tree outline sidebar, responsive toolbar, and Fit-to-Width page canvas.
/// </summary>
public static class PdfReaderViewBuilder
{
    public static Control Build(MainWindowViewModel vm)
    {
        var reader = vm.PdfReader;
        var activeNode = reader.FindActiveBookmarkNode();

        var root = new Grid
        {
            RowDefinitions = RowDefinitions.Parse("Auto,*"),
            Margin = new Thickness(12, 8)
        };

        // 1. Responsive Top Toolbar
        var toolbarBorder = new Border
        {
            Background = ThemeService.Brush(ThemeService.BgSurface),
            BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 6),
            Margin = new Thickness(0, 0, 0, 8)
        };

        var toolbarGrid = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto")
        };

        var leftGroup = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center
        };

        leftGroup.Children.Add(UiBuilder.GhostButton(
            "Back",
            MaterialIconKind.ArrowLeft,
            () =>
            {
                if (vm.ActiveCourse is not null)
                    vm.SelectCourse(vm.ActiveCourse, showMaterials: true);
                else
                    vm.SelectSmartView(vm.ActiveSmartView);
            }));

        if (reader.BookmarkRoots.Count > 0)
        {
            leftGroup.Children.Add(UiBuilder.GhostButton(
                "Outline",
                MaterialIconKind.FileTreeOutline,
                () => reader.ToggleOutline(),
                isActive: reader.IsOutlineVisible));
        }
        toolbarGrid.Children.Add(leftGroup);

        var titleText = new TextBlock
        {
            Text = reader.Title,
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = ThemeService.Brush(ThemeService.TextPrimary),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(titleText, 1);
        toolbarGrid.Children.Add(titleText);

        var rightControls = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };

        var navRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        };
        navRow.Children.Add(UiBuilder.IconButton(MaterialIconKind.PageFirst, () => reader.JumpToPage(1)));
        navRow.Children.Add(UiBuilder.IconButton(MaterialIconKind.ChevronLeft, () => reader.PreviousPage()));
        navRow.Children.Add(new Border
        {
            Background = ThemeService.Brush(ThemeService.BgElevated),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(8, 3.5),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = $"{reader.CurrentPage} / {Math.Max(1, reader.PageCount)}",
                FontSize = 12,
                FontWeight = FontWeight.SemiBold,
                Foreground = ThemeService.Brush(ThemeService.TextPrimary)
            }
        });
        navRow.Children.Add(UiBuilder.IconButton(MaterialIconKind.ChevronRight, () => reader.NextPage()));
        navRow.Children.Add(UiBuilder.IconButton(MaterialIconKind.PageLast, () => reader.JumpToPage(reader.PageCount)));
        rightControls.Children.Add(navRow);

        var zoomRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center
        };
        zoomRow.Children.Add(UiBuilder.GhostButton(
            "Fit Width",
            MaterialIconKind.ArrowExpandHorizontal,
            () => reader.ToggleFitToWidth(),
            isActive: reader.FitToWidth));
        zoomRow.Children.Add(UiBuilder.IconButton(MaterialIconKind.MagnifyMinusOutline, () => reader.ZoomOut()));
        zoomRow.Children.Add(UiBuilder.GhostButton(
            reader.FitToWidth ? "Auto" : $"{reader.ZoomPercent}%",
            null,
            () => reader.ResetZoom()));
        zoomRow.Children.Add(UiBuilder.IconButton(MaterialIconKind.MagnifyPlusOutline, () => reader.ZoomIn()));
        zoomRow.Children.Add(UiBuilder.GhostButton(
            "External",
            MaterialIconKind.OpenInNew,
            () => reader.OpenExternal()));
        rightControls.Children.Add(zoomRow);

        Grid.SetColumn(rightControls, 2);
        toolbarGrid.Children.Add(rightControls);
        toolbarBorder.Child = toolbarGrid;
        root.Children.Add(toolbarBorder);

        // 2. Body: Left Nested Tree Outline + Center Page Canvas
        bool showOutline = reader.IsOutlineVisible && reader.BookmarkRoots.Count > 0;
        var body = new Grid
        {
            ColumnDefinitions = showOutline
                ? ColumnDefinitions.Parse("250,10,*")
                : ColumnDefinitions.Parse("0,0,*")
        };

        if (showOutline)
        {
            var outlineStack = new StackPanel { Spacing = 2, Margin = new Thickness(4) };
            outlineStack.Children.Add(UiBuilder.SectionHeader("Document Outline"));

            foreach (var rootNode in reader.BookmarkRoots)
            {
                outlineStack.Children.Add(BuildBookmarkTreeNode(reader, rootNode, activeNode));
            }

            body.Children.Add(new Border
            {
                Background = ThemeService.Brush(ThemeService.BgSurface),
                BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(6),
                Child = new ScrollViewer { Content = outlineStack }
            });
        }

        // 3. Center Page Canvas
        var bmp = reader.GetCurrentPageBitmap();
        Control canvasContent;
        if (!string.IsNullOrEmpty(reader.ErrorMessage))
        {
            canvasContent = new TextBlock
            {
                Text = reader.ErrorMessage,
                Foreground = ThemeService.Brush(ThemeService.PriorityP1Fg),
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(24),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }
        else if (bmp is not null)
        {
            var img = new Image
            {
                Source = bmp,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top
            };

            if (!reader.FitToWidth)
            {
                img.Width = 720 * (reader.ZoomPercent / 100.0);
            }

            var pageSheet = new Border
            {
                Background = Brushes.White,
                BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(16, 12),
                Padding = new Thickness(4),
                HorizontalAlignment = reader.FitToWidth ? HorizontalAlignment.Stretch : HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                MaxWidth = reader.FitToWidth ? 860 : double.PositiveInfinity,
                Child = img
            };

            canvasContent = new ScrollViewer
            {
                HorizontalScrollBarVisibility = reader.FitToWidth
                    ? Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
                    : Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                Content = pageSheet
            };
        }
        else
        {
            canvasContent = new TextBlock
            {
                Text = "No PDF page loaded.",
                Foreground = ThemeService.Brush(ThemeService.TextSecondary),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        var canvasBorder = new Border
        {
            Background = ThemeService.Brush(ThemeService.BgBase),
            BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = canvasContent
        };
        Grid.SetColumn(canvasBorder, 2);
        body.Children.Add(canvasBorder);

        Grid.SetRow(body, 1);
        root.Children.Add(body);

        return root;
    }

    private static Control BuildBookmarkTreeNode(
        PdfReaderViewModel reader,
        PdfBookmarkNode node,
        PdfBookmarkNode? activeNode)
    {
        bool isCurrent = ReferenceEquals(node, activeNode);
        bool hasChildren = node.Children.Count > 0;

        var container = new StackPanel { Spacing = 1 };

        var rowGrid = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("20,*,Auto")
        };

        if (hasChildren)
        {
            var chevronBtn = new Button
            {
                Content = UiBuilder.Icon(
                    node.IsExpanded ? MaterialIconKind.ChevronDown : MaterialIconKind.ChevronRight,
                    14,
                    ThemeService.Brush(ThemeService.TextSecondary)),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(2),
                Cursor = new Cursor(StandardCursorType.Hand),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            chevronBtn.Click += (_, e) =>
            {
                e.Handled = true;
                reader.ToggleNodeExpanded(node);
            };
            rowGrid.Children.Add(chevronBtn);
        }
        else
        {
            var dot = UiBuilder.Icon(
                MaterialIconKind.FileDocumentOutline,
                12,
                isCurrent ? ThemeService.Brush(ThemeService.AccentPrimary) : ThemeService.Brush(ThemeService.TextMuted));
            dot.HorizontalAlignment = HorizontalAlignment.Center;
            rowGrid.Children.Add(dot);
        }

        var normalBg = isCurrent ? ThemeService.Brush(ThemeService.AccentSoftBg) : Brushes.Transparent;
        var hoverBg = isCurrent ? ThemeService.Brush(ThemeService.AccentSoftBg) : ThemeService.Brush(ThemeService.BgHover);

        var titleBtn = new Button
        {
            Content = new Grid
            {
                ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"),
                Children =
                {
                    new TextBlock
                    {
                        Text = node.Title,
                        FontSize = node.Level == 1 ? 12.5 : 12,
                        FontWeight = isCurrent || node.Level == 1 ? FontWeight.SemiBold : FontWeight.Normal,
                        Foreground = isCurrent
                            ? ThemeService.Brush(ThemeService.AccentPrimary)
                            : node.Level == 1
                                ? ThemeService.Brush(ThemeService.TextPrimary)
                                : ThemeService.Brush(ThemeService.TextSecondary),
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        VerticalAlignment = VerticalAlignment.Center
                    },
                    new TextBlock
                    {
                        Text = node.PageNumber.ToString(),
                        FontSize = 11,
                        Foreground = isCurrent
                            ? ThemeService.Brush(ThemeService.AccentPrimary)
                            : ThemeService.Brush(ThemeService.TextMuted),
                        Margin = new Thickness(6, 0, 0, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                        [Grid.ColumnProperty] = 1
                    }
                }
            },
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(4, 4.5, 6, 4.5),
            Cursor = new Cursor(StandardCursorType.Hand),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        titleBtn.Click += (_, e) =>
        {
            e.Handled = true;
            reader.JumpToPage(node.PageNumber);
        };
        Grid.SetColumn(titleBtn, 1);
        Grid.SetColumnSpan(titleBtn, 2);
        rowGrid.Children.Add(titleBtn);

        var rowBorder = new Border
        {
            Background = normalBg,
            CornerRadius = new CornerRadius(5),
            Child = rowGrid
        };
        rowBorder.PointerEntered += (_, _) => rowBorder.Background = hoverBg;
        rowBorder.PointerExited += (_, _) => rowBorder.Background = normalBg;

        container.Children.Add(rowBorder);

        // Nested Tree Children with vertical guide line
        if (hasChildren && node.IsExpanded)
        {
            var childrenStack = new StackPanel { Spacing = 1 };
            foreach (var child in node.Children)
            {
                childrenStack.Children.Add(BuildBookmarkTreeNode(reader, child, activeNode));
            }

            container.Children.Add(new Border
            {
                BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
                BorderThickness = new Thickness(1, 0, 0, 0),
                Margin = new Thickness(10, 1, 0, 3),
                Padding = new Thickness(8, 0, 0, 0),
                Child = childrenStack
            });
        }

        return container;
    }
}
