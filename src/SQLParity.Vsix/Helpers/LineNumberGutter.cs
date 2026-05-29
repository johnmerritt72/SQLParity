using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace SQLParity.Vsix.Helpers
{
    /// <summary>
    /// Paints right-aligned line numbers for a target <see cref="RichTextBox"/>.
    /// Line numbers are read from each <see cref="Paragraph.Tag"/> (an int) — paragraphs
    /// with a null Tag (alignment-padding rows) draw nothing. Numbers are positioned by
    /// the pixel rect of each paragraph's start, so alignment is correct whether or not
    /// the document wraps (a wrapped line's continuation rows simply get no number).
    /// Because the numbers live here and not in the RichTextBox, copied SQL excludes them.
    /// </summary>
    public class LineNumberGutter : FrameworkElement
    {
        private static readonly Typeface MonoTypeface =
            new Typeface(new FontFamily("Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        private static readonly Brush NumberBrush = CreateFrozen(Color.FromRgb(140, 140, 140));
        private static readonly Pen DividerPen = CreateFrozenPen(Color.FromRgb(200, 200, 200), 1);

        private const double LeftPadding = 6;
        private const double RightPadding = 8;

        private RichTextBox _target;
        private ScrollViewer _scrollViewer;
        private double _fontSize = 12;
        private bool _showNumbers = true;

        private static Brush CreateFrozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        private static Pen CreateFrozenPen(Color c, double thickness)
        {
            var p = new Pen(CreateFrozen(c), thickness);
            p.Freeze();
            return p;
        }

        /// <summary>The RichTextBox whose paragraphs this gutter numbers.</summary>
        public RichTextBox Target
        {
            get => _target;
            set
            {
                if (ReferenceEquals(_target, value)) return;
                DetachScroll();
                if (_target != null)
                {
                    _target.SizeChanged -= OnTargetSizeChanged;
                    _target.TextChanged -= OnTargetTextChanged;
                }

                _target = value;

                if (_target != null)
                {
                    _target.SizeChanged += OnTargetSizeChanged;
                    _target.TextChanged += OnTargetTextChanged;
                }

                // The RichTextBox template (and its inner ScrollViewer) may not exist yet;
                // defer hookup until WPF has applied the template.
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                    new Action(AttachScroll));
                Refresh();
            }
        }

        /// <summary>Font size used both for measuring the gutter width and drawing numbers. Match the document.</summary>
        public double FontSize
        {
            get => _fontSize;
            set
            {
                if (Math.Abs(_fontSize - value) < 0.01) return;
                _fontSize = value;
                Refresh();
            }
        }

        /// <summary>When false, the gutter collapses to zero width and draws nothing.</summary>
        public bool ShowNumbers
        {
            get => _showNumbers;
            set
            {
                if (_showNumbers == value) return;
                _showNumbers = value;
                Refresh();
            }
        }

        /// <summary>Re-measure and repaint. Call after the target's document, font, or wrap changes.</summary>
        public void Refresh()
        {
            InvalidateMeasure();
            InvalidateVisual();
        }

        private void OnTargetSizeChanged(object sender, SizeChangedEventArgs e) => InvalidateVisual();
        private void OnTargetTextChanged(object sender, TextChangedEventArgs e) => Refresh();
        private void OnScrollChanged(object sender, ScrollChangedEventArgs e) => InvalidateVisual();

        private void AttachScroll()
        {
            if (_target == null) return;
            var sv = FindScrollViewer(_target);
            if (ReferenceEquals(sv, _scrollViewer)) return;
            DetachScroll();
            _scrollViewer = sv;
            if (_scrollViewer != null)
                _scrollViewer.ScrollChanged += OnScrollChanged;
            InvalidateVisual();
        }

        private void DetachScroll()
        {
            if (_scrollViewer != null)
                _scrollViewer.ScrollChanged -= OnScrollChanged;
            _scrollViewer = null;
        }

        private static ScrollViewer FindScrollViewer(DependencyObject parent)
        {
            if (parent == null) return null;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is ScrollViewer sv) return sv;
                var result = FindScrollViewer(child);
                if (result != null) return result;
            }
            return null;
        }

        private double GutterWidth()
        {
            if (!_showNumbers || _target == null) return 0;
            int maxLine = MaxLineNumber();
            int digits = maxLine > 0 ? maxLine.ToString(CultureInfo.InvariantCulture).Length : 1;
            // Consolas digit advance is ~0.55em; pad generously so numbers never clip.
            double digitWidth = _fontSize * 0.62;
            return LeftPadding + digits * digitWidth + RightPadding;
        }

        private int MaxLineNumber()
        {
            var doc = _target?.Document;
            if (doc == null) return 0;
            int max = 0;
            foreach (Block b in doc.Blocks)
                if (b is Paragraph p && p.Tag is int n && n > max)
                    max = n;
            return max;
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            // Desired height is 0 by design: this control is meant to be docked Left in a
            // DockPanel (or stretched in a Grid cell), where the parent supplies the height.
            return new Size(GutterWidth(), 0);
        }

        protected override void OnRender(DrawingContext dc)
        {
            double width = ActualWidth;
            double height = ActualHeight;
            if (!_showNumbers || _target == null || width <= 0 || height <= 0)
                return;

            var doc = _target.Document;
            if (doc == null) return;

            // Make sure scroll is hooked (covers the case where the template applied late).
            if (_scrollViewer == null) AttachScroll();

            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

            // Find the first paragraph at/after the top of the viewport, so we only
            // GetCharacterRect/draw the visible range rather than every paragraph.
            // GetPositionFromPoint may return a pointer inside a nested paragraph (e.g.
            // within a List/Table), which would not be a direct child of doc.Blocks; walk
            // up to the top-level block so the ReferenceEquals skip below can match. If we
            // can't resolve a top-level Paragraph, fall back to iterating all paragraphs.
            Paragraph startPara = null;
            var topPointer = _target.GetPositionFromPoint(new Point(2, 2), true);
            if (topPointer != null)
            {
                FrameworkContentElement el = topPointer.Paragraph;
                while (el != null && !(el is Paragraph fp && fp.Parent is FlowDocument))
                    el = el.Parent as FrameworkContentElement;
                startPara = el as Paragraph;
            }

            bool started = startPara == null; // couldn't locate one → iterate all
            foreach (Block b in doc.Blocks)
            {
                if (!(b is Paragraph p)) continue;

                if (!started)
                {
                    if (!ReferenceEquals(p, startPara)) continue;
                    started = true;
                }

                if (!(p.Tag is int lineNum)) continue;

                Rect rect;
                try
                {
                    rect = p.ContentStart.GetCharacterRect(LogicalDirection.Forward);
                }
                catch
                {
                    continue;
                }

                // Map the rect (relative to the RichTextBox) into this gutter's coordinates.
                double y;
                try
                {
                    y = _target.TransformToVisual(this).Transform(new Point(0, rect.Top)).Y;
                }
                catch
                {
                    continue;
                }

                if (y > height) break;                 // past the bottom of the viewport — done
                if (y + _fontSize < 0) continue;        // above the viewport — skip drawing

                var ft = new FormattedText(
                    lineNum.ToString(CultureInfo.InvariantCulture),
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    MonoTypeface,
                    _fontSize,
                    NumberBrush,
                    dpi);

                double x = width - RightPadding - ft.Width;
                dc.DrawText(ft, new Point(x, y));
            }

            // Divider on the right edge.
            dc.DrawLine(DividerPen, new Point(width - 0.5, 0), new Point(width - 0.5, height));
        }
    }
}
