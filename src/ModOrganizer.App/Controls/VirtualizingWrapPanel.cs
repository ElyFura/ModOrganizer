using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace ModOrganizer.App.Controls;

/// <summary>
/// A wrap panel that only realizes the item containers currently in view.
///
/// WPF ships no virtualizing wrap panel, so the gallery used ItemsControl + WrapPanel,
/// which realizes every card — 300 cards with 300 images, on every refresh. This panel
/// keeps that down to the ~20 that are actually on screen.
///
/// Items are assumed to be uniformly sized (the gallery binds every card to the same
/// ThumbnailSize). Set <see cref="ItemWidth"/> and <see cref="ItemHeight"/> to the card
/// size including margins; that lets the panel compute the full extent without measuring
/// anything it has not realized.
/// </summary>
public class VirtualizingWrapPanel : VirtualizingPanel, IScrollInfo
{
    public static readonly DependencyProperty ItemWidthProperty =
        DependencyProperty.Register(nameof(ItemWidth), typeof(double), typeof(VirtualizingWrapPanel),
            new FrameworkPropertyMetadata(200d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ItemHeightProperty =
        DependencyProperty.Register(nameof(ItemHeight), typeof(double), typeof(VirtualizingWrapPanel),
            new FrameworkPropertyMetadata(280d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double ItemWidth
    {
        get => (double)GetValue(ItemWidthProperty);
        set => SetValue(ItemWidthProperty, value);
    }

    public double ItemHeight
    {
        get => (double)GetValue(ItemHeightProperty);
        set => SetValue(ItemHeightProperty, value);
    }

    private Size _extent;
    private Size _viewport;
    private Point _offset;

    /// <summary>
    /// Column count from the last measure pass. Arrange must reuse it rather than
    /// recomputing from its own width: when a scrollbar appears the two widths differ for
    /// a pass, and a disagreement puts items in slots that measure never realized, which
    /// shows up as holes in the grid.
    /// </summary>
    private int _columns = 1;

    private double EffectiveItemWidth => Math.Max(1d, ItemWidth);
    private double EffectiveItemHeight => Math.Max(1d, ItemHeight);

    private int ColumnsFor(double availableWidth)
    {
        if (double.IsInfinity(availableWidth) || availableWidth <= 0) return 1;
        return Math.Max(1, (int)Math.Floor(availableWidth / EffectiveItemWidth));
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var itemsOwner = ItemsControl.GetItemsOwner(this);
        var itemCount = itemsOwner?.Items.Count ?? 0;

        // Touching InternalChildren is what forces the generator to come alive; without
        // reading it once, ItemContainerGenerator is null on the first pass.
        _ = InternalChildren;
        var generator = ItemContainerGenerator;

        if (itemCount == 0 || generator is null)
        {
            RemoveInternalChildRange(0, InternalChildren.Count);
            UpdateScrollInfo(availableSize, new Size(0, 0));
            return new Size(
                double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width,
                0);
        }

        var itemW = EffectiveItemWidth;
        var itemH = EffectiveItemHeight;

        var availableWidth = double.IsInfinity(availableSize.Width)
            ? itemW * itemCount
            : availableSize.Width;

        var columns = ColumnsFor(availableWidth);
        _columns = columns;
        var rows = (int)Math.Ceiling(itemCount / (double)columns);

        var extent = new Size(columns * itemW, rows * itemH);
        UpdateScrollInfo(availableSize, extent);

        var viewportHeight = double.IsInfinity(availableSize.Height) ? extent.Height : availableSize.Height;

        // One row of overscan above and below keeps scrolling from showing empty gaps.
        var firstRow = Math.Max(0, (int)Math.Floor(_offset.Y / itemH) - 1);
        var lastRow = Math.Min(rows - 1, (int)Math.Ceiling((_offset.Y + viewportHeight) / itemH) + 1);

        var firstIndex = firstRow * columns;
        var lastIndex = Math.Min(itemCount - 1, (lastRow + 1) * columns - 1);

        RealizeRange(generator, firstIndex, lastIndex, new Size(itemW, itemH));
        CleanupOutsideRange(generator, firstIndex, lastIndex);

        return new Size(
            double.IsInfinity(availableSize.Width) ? extent.Width : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? extent.Height : availableSize.Height);
    }

    private void RealizeRange(IItemContainerGenerator generator, int firstIndex, int lastIndex, Size itemSize)
    {
        if (lastIndex < firstIndex) return;

        var startPos = generator.GeneratorPositionFromIndex(firstIndex);
        // Offset 0 means the position points at a realized container, so the new child
        // belongs at that child index; otherwise it goes after it.
        var childIndex = startPos.Offset == 0 ? startPos.Index : startPos.Index + 1;

        using var _ = generator.StartAt(startPos, GeneratorDirection.Forward, true);
        for (var i = firstIndex; i <= lastIndex; i++, childIndex++)
        {
            if (generator.GenerateNext(out var isNewlyRealized) is not UIElement child)
                break;

            if (isNewlyRealized)
            {
                if (childIndex >= InternalChildren.Count) AddInternalChild(child);
                else InsertInternalChild(childIndex, child);
                generator.PrepareItemContainer(child);
            }

            child.Measure(itemSize);
        }
    }

    private void CleanupOutsideRange(IItemContainerGenerator generator, int firstIndex, int lastIndex)
    {
        var children = InternalChildren;
        // Walk backwards: removing a child shifts the ones after it.
        for (var childIndex = children.Count - 1; childIndex >= 0; childIndex--)
        {
            var pos = new GeneratorPosition(childIndex, 0);
            var itemIndex = generator.IndexFromGeneratorPosition(pos);
            if (itemIndex >= firstIndex && itemIndex <= lastIndex) continue;

            generator.Remove(pos, 1);
            RemoveInternalChildRange(childIndex, 1);
        }
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var generator = ItemContainerGenerator;
        if (generator is null) return finalSize;

        var itemW = EffectiveItemWidth;
        var itemH = EffectiveItemHeight;
        var columns = _columns;

        for (var childIndex = 0; childIndex < InternalChildren.Count; childIndex++)
        {
            var child = InternalChildren[childIndex];
            var itemIndex = generator.IndexFromGeneratorPosition(new GeneratorPosition(childIndex, 0));
            if (itemIndex < 0)
            {
                child.Arrange(new Rect(0, 0, 0, 0));
                continue;
            }

            var row = itemIndex / columns;
            var column = itemIndex % columns;

            child.Arrange(new Rect(
                column * itemW - _offset.X,
                row * itemH - _offset.Y,
                itemW,
                itemH));
        }

        return finalSize;
    }

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        switch (args.Action)
        {
            case System.Collections.Specialized.NotifyCollectionChangedAction.Remove:
            case System.Collections.Specialized.NotifyCollectionChangedAction.Replace:
            case System.Collections.Specialized.NotifyCollectionChangedAction.Move:
                RemoveInternalChildRange(args.Position.Index, args.ItemUICount);
                break;
            case System.Collections.Specialized.NotifyCollectionChangedAction.Reset:
                // A Clear() on the bound collection invalidates every realized container
                // and the scroll position they were laid out for.
                RemoveInternalChildRange(0, InternalChildren.Count);
                if (_offset.Y != 0)
                {
                    _offset.Y = 0;
                    ScrollOwner?.InvalidateScrollInfo();
                }
                break;
        }

        InvalidateMeasure();
    }

    // ---------------- IScrollInfo ----------------

    private void UpdateScrollInfo(Size availableSize, Size extent)
    {
        var viewport = new Size(
            double.IsInfinity(availableSize.Width) ? extent.Width : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? extent.Height : availableSize.Height);

        var changed = false;

        if (extent != _extent)
        {
            _extent = extent;
            changed = true;
        }
        if (viewport != _viewport)
        {
            _viewport = viewport;
            changed = true;
        }

        // Clamp: shrinking the extent (a filter that matches fewer mods) can leave the
        // offset past the end, which renders as a blank gallery.
        var maxOffsetY = Math.Max(0, _extent.Height - _viewport.Height);
        if (_offset.Y > maxOffsetY)
        {
            _offset.Y = maxOffsetY;
            changed = true;
        }
        var maxOffsetX = Math.Max(0, _extent.Width - _viewport.Width);
        if (_offset.X > maxOffsetX)
        {
            _offset.X = maxOffsetX;
            changed = true;
        }

        if (changed) ScrollOwner?.InvalidateScrollInfo();
    }

    public ScrollViewer? ScrollOwner { get; set; }
    public bool CanHorizontallyScroll { get; set; }
    public bool CanVerticallyScroll { get; set; } = true;

    public double ExtentWidth => _extent.Width;
    public double ExtentHeight => _extent.Height;
    public double ViewportWidth => _viewport.Width;
    public double ViewportHeight => _viewport.Height;
    public double HorizontalOffset => _offset.X;
    public double VerticalOffset => _offset.Y;

    private const double LineSize = 48;

    public void LineUp() => SetVerticalOffset(VerticalOffset - LineSize);
    public void LineDown() => SetVerticalOffset(VerticalOffset + LineSize);
    public void LineLeft() => SetHorizontalOffset(HorizontalOffset - LineSize);
    public void LineRight() => SetHorizontalOffset(HorizontalOffset + LineSize);

    public void PageUp() => SetVerticalOffset(VerticalOffset - ViewportHeight);
    public void PageDown() => SetVerticalOffset(VerticalOffset + ViewportHeight);
    public void PageLeft() => SetHorizontalOffset(HorizontalOffset - ViewportWidth);
    public void PageRight() => SetHorizontalOffset(HorizontalOffset + ViewportWidth);

    public void MouseWheelUp() => SetVerticalOffset(VerticalOffset - LineSize * 3);
    public void MouseWheelDown() => SetVerticalOffset(VerticalOffset + LineSize * 3);
    public void MouseWheelLeft() => SetHorizontalOffset(HorizontalOffset - LineSize * 3);
    public void MouseWheelRight() => SetHorizontalOffset(HorizontalOffset + LineSize * 3);

    public void SetVerticalOffset(double offset)
    {
        var clamped = Math.Max(0, Math.Min(offset, Math.Max(0, _extent.Height - _viewport.Height)));
        if (Math.Abs(clamped - _offset.Y) < 0.5) return;

        _offset.Y = clamped;
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateMeasure();
    }

    public void SetHorizontalOffset(double offset)
    {
        var clamped = Math.Max(0, Math.Min(offset, Math.Max(0, _extent.Width - _viewport.Width)));
        if (Math.Abs(clamped - _offset.X) < 0.5) return;

        _offset.X = clamped;
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateMeasure();
    }

    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        var child = InternalChildren.Cast<UIElement>()
            .FirstOrDefault(c => c == visual || c.IsAncestorOf(visual));
        if (child is null) return rectangle;

        var generator = ItemContainerGenerator;
        if (generator is null) return rectangle;

        var childIndex = InternalChildren.IndexOf(child);
        var itemIndex = generator.IndexFromGeneratorPosition(new GeneratorPosition(childIndex, 0));
        if (itemIndex < 0) return rectangle;

        var row = itemIndex / _columns;
        var top = row * EffectiveItemHeight;
        var bottom = top + EffectiveItemHeight;

        if (top < _offset.Y) SetVerticalOffset(top);
        else if (bottom > _offset.Y + _viewport.Height) SetVerticalOffset(bottom - _viewport.Height);

        return new Rect(0, top - _offset.Y, EffectiveItemWidth, EffectiveItemHeight);
    }
}
