using System.Runtime.InteropServices;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Gallery.Controls;

/// <summary>可选文字与输入框共用的鼠标选词、整行选择；宿主根节点接入一次。</summary>
public static class TextSelectionBehavior
{
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(object), typeof(TextSelectionBehavior), new PropertyMetadata(null));

    [DllImport("user32.dll")] private static extern uint GetDoubleClickTime();
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);

    public static void Attach(UIElement root)
    {
        if (root.GetValue(StateProperty) is not null) return;
        root.SetValue(StateProperty, new SelectionState(root));
    }

    public static string WebScript { get; } = LoadWebScript();
    private static string LoadWebScript()
    {
        using var stream = typeof(TextSelectionBehavior).Assembly.GetManifestResourceStream("Gallery.Controls.TextSelection.js")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private sealed class SelectionState
    {
        private readonly UIElement _root;
        private UIElement? _target;
        private Point _lastPoint, _pressedPoint;
        private long _lastAt;
        private int _count;

        public SelectionState(UIElement root)
        {
            _root = root;
            root.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(Pressed), true);
            root.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(Released), true);
        }

        private UIElement? FindTarget(object source)
        {
            for (var node = source as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
            {
                if (!ReferenceEquals(node, _root) && node.GetValue(StateProperty) is not null) return null;
                if (node is TextBox box) return box;
                if (node is TextBlock { IsTextSelectionEnabled: true } text) return text;
                if (node is ButtonBase or PasswordBox || ReferenceEquals(node, _root)) break;
            }
            return null;
        }

        private void Pressed(object sender, PointerRoutedEventArgs e)
        {
            var point = e.GetCurrentPoint(_root);
            if (point.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Mouse || !point.Properties.IsLeftButtonPressed) { _count = 0; return; }
            var target = FindTarget(e.OriginalSource);
            var now = Environment.TickCount64;
            var scale = _root.XamlRoot.RasterizationScale;
            _count = target is not null && ReferenceEquals(target, _target) && now - _lastAt <= GetDoubleClickTime()
                && Math.Abs(point.Position.X - _lastPoint.X) <= GetSystemMetrics(36) / scale
                && Math.Abs(point.Position.Y - _lastPoint.Y) <= GetSystemMetrics(37) / scale ? _count + 1 : 1;
            _target = target; _lastAt = now; _lastPoint = point.Position; _pressedPoint = point.Position;
        }

        private void Released(object sender, PointerRoutedEventArgs e)
        {
            if (_target is not { } target) return;
            var point = e.GetCurrentPoint(_root);
            if (point.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Mouse) return;
            var scale = _root.XamlRoot.RasterizationScale;
            if (Math.Abs(point.Position.X - _pressedPoint.X) > GetSystemMetrics(36) / scale
                || Math.Abs(point.Position.Y - _pressedPoint.Y) > GetSystemMetrics(37) / scale) { _count = 0; return; }
            if (_count is not (2 or 3)) return;
            var count = _count;
            if (count == 3) _count = 0;
            var position = e.GetCurrentPoint(target).Position;
            target.DispatcherQueue.TryEnqueue(() =>
            {
                if (target.XamlRoot is not null) SelectAtPoint(target, position, count);
            });
        }
    }

    internal static void SelectAtPoint(UIElement target, Point point, int clicks)
    {
        var value = target is TextBox box ? box.Text : ((TextBlock)target).Text;
        if (value.Length == 0) return;
        var nativeStart = target is TextBox input ? input.SelectionStart
            : Math.Max(0, ((TextBlock)target).SelectionStart.Offset - ((TextBlock)target).Inlines[0].ContentStart.Offset);
        var nativeLength = target is TextBox edit ? edit.SelectionLength : ((TextBlock)target).SelectedText.Length;
        var from = clicks == 2 && nativeLength > 0 ? nativeStart : 0;
        var to = clicks == 2 && nativeLength > 0 ? Math.Min(value.Length, nativeStart + nativeLength) : value.Length;
        if (target is TextBox textInput && TextBoxView(textInput) is { } view)
            point = textInput.TransformToVisual(view).TransformPoint(point);
        var glyphs = new List<(int Index, Rect Rect)>();
        var characters = StringInfo.ParseCombiningCharacters(value);
        for (var character = 0; character < characters.Length; character++)
        {
            var i = characters[character];
            if (i < from || i >= to || value[i] is '\r' or '\n') continue;
            var end = character + 1 < characters.Length ? characters[character + 1] : value.Length;
            Rect rect;
            if (target is TextBox textBox)
            {
                var leading = textBox.GetRectFromCharacterIndex(i, false);
                var trailing = textBox.GetRectFromCharacterIndex(end - 1, true);
                rect = new Rect(Math.Min(leading.X, trailing.X), leading.Y, Math.Abs(trailing.X - leading.X), leading.Height);
            }
            else
            {
                var text = (TextBlock)target;
                var origin = text.Inlines[0].ContentStart;
                var first = origin.GetPositionAtOffset(i, LogicalDirection.Forward).GetCharacterRect(LogicalDirection.Forward);
                var last = origin.GetPositionAtOffset(end, LogicalDirection.Forward).GetCharacterRect(LogicalDirection.Forward);
                rect = new Rect(Math.Min(first.X, last.X), first.Y, Math.Abs(last.X - first.X), first.Height);
            }
            if (rect.Height <= 0) continue;
            glyphs.Add((i, rect));
        }
        var tops = glyphs.Select(g => g.Rect.Top).Distinct().Order().ToArray();
        var index = from;
        var distance = double.MaxValue;
        foreach (var glyph in glyphs)
        {
            var rect = glyph.Rect;
            if (target is TextBox && tops.Length > 1)
            {
                var row = Array.IndexOf(tops, rect.Top);
                rect.Height = row + 1 < tops.Length ? tops[row + 1] - rect.Top : rect.Top - tops[row - 1];
            }
            var dx = Math.Max(0, Math.Max(rect.Left - point.X, point.X - rect.Right));
            var dy = Math.Max(0, Math.Max(rect.Top - point.Y, point.Y - rect.Bottom));
            var d = dx * dx + dy * dy;
            if (d < distance) { distance = d; index = glyph.Index; }
            if (d == 0) break;
        }
        var (start, length) = SelectionRange(value, index, clicks, from, to);
        if (target is TextBox field) field.Select(start, length);
        else
        {
            var text = (TextBlock)target;
            var first = text.Inlines[0].ContentStart.GetPositionAtOffset(start, LogicalDirection.Forward);
            var last = text.Inlines[0].ContentStart.GetPositionAtOffset(start + length, LogicalDirection.Forward);
            if (first is not null && last is not null) text.Select(first, last);
        }
    }

    private static UIElement? TextBoxView(DependencyObject node)
    {
        if (node is ScrollViewer { Name: "ContentElement", Content: UIElement view }) return view;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            if (TextBoxView(VisualTreeHelper.GetChild(node, i)) is { } found) return found;
        return null;
    }

    internal static (int Start, int Length) SelectionRange(string text, int index, int clicks, int from, int to)
    {
        index = Math.Clamp(index, 0, text.Length - 1);
        var start = index; var end = index + 1;
        if (clicks == 3)
        {
            while (start > 0 && text[start - 1] is not ('\r' or '\n')) start--;
            while (end < text.Length && text[end] is not ('\r' or '\n')) end++;
            if (text[index] is '\r' or '\n') end = index;
        }
        else
        {
            static bool Separator(char c) => char.IsWhiteSpace(c) || char.IsPunctuation(c) || char.IsSymbol(c);
            bool Same(char c) => Separator(text[index]) ? char.IsWhiteSpace(text[index]) ? char.IsWhiteSpace(c) : c == text[index] : !Separator(c);
            while (start > from && Same(text[start - 1])) start--;
            while (end < to && Same(text[end])) end++;
        }
        return (start, end - start);
    }
}
