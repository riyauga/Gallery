using Microsoft.UI.Xaml.Controls;

namespace Gallery.Controls.Catalog;

/// <summary>
/// 宿主无关的选择模式状态机，按 UI_INTERACTION.md「选择模式（通用）」实现。
/// 只管选中集合、锚点与键盘/右键语义；勾选框、按钮、确认和删除由宿主接。
/// </summary>
internal sealed class LocalSelectionController
{
    private readonly HashSet<string> _selected = new(StringComparer.Ordinal);
    private string? _anchor;

    public bool IsActive { get; private set; }
    public int Count => _selected.Count;
    public IReadOnlyCollection<string> Selected => _selected;
    private Action? _syncButtons;
    public event Action? Changed;

    /// <summary>绑定一次性的按钮同步委托，重复绑定先解绑，避免 ShowDetail 重建时累积。</summary>
    public void BindButtons(Button select, Button delete, Action<LocalSelectionController, Button, Button> sync)
    {
        if (_syncButtons is not null) Changed -= _syncButtons;
        _syncButtons = () => sync(this, select, delete);
        Changed += _syncButtons;
        _syncButtons();
    }

    public bool Enter()
    {
        if (IsActive) return false;
        IsActive = true;
        Changed?.Invoke();
        return true;
    }

    public bool Exit()
    {
        if (!IsActive) return false;
        IsActive = false;
        _selected.Clear();
        _anchor = null;
        Changed?.Invoke();
        return true;
    }

    public void Clear()
    {
        _selected.Clear();
        _anchor = null;
        Changed?.Invoke();
    }

    public bool Contains(string key) => _selected.Contains(key);

    /// <summary>右键：未选中则清选区并只选它；已选中则保留整组。</summary>
    public void RightSelect(string key)
    {
        if (!IsActive) IsActive = true;
        if (!_selected.Contains(key))
        {
            _selected.Clear();
            _selected.Add(key);
            _anchor = key;
        }
        Changed?.Invoke();
    }

    /// <summary>单击/Ctrl/Shift/Ctrl+Shift；order 是当前筛选后的完整列表顺序。</summary>
    public void Toggle(string key, bool ctrl, bool shift, IReadOnlyList<string> order)
    {
        if (!IsActive) IsActive = true;
        if (shift && _anchor is { } anchor)
        {
            int first = -1, last = -1;
            for (int i = 0; i < order.Count; i++)
            {
                if (order[i] == anchor) first = i;
                if (order[i] == key) last = i;
            }
            if (first < 0 || last < 0) return;
            // 选择模式已激活时，Shift 区间追加；进入模式的第一次点击由 RightSelect/SelectAll 处理。
            for (int i = Math.Min(first, last); i <= Math.Max(first, last); i++) _selected.Add(order[i]);
        }
        else
        {
            // 选择模式下单击是「切换」：追加或取消，不清空已有选区（否则永远只能单选）。
            if (!_selected.Add(key)) _selected.Remove(key);
            _anchor = key;
        }
        Changed?.Invoke();
    }

    public void SelectAll(IEnumerable<string> keys)
    {
        IsActive = true;
        _selected.Clear();
        foreach (var key in keys) _selected.Add(key);
        Changed?.Invoke();
    }

    /// <summary>数据源变化后剔除已失效的 key；锚点失效则清空。</summary>
    public void Retain(IEnumerable<string> validKeys)
    {
        var valid = validKeys.ToHashSet(StringComparer.Ordinal);
        _selected.RemoveWhere(key => !valid.Contains(key));
        if (_anchor is { } anchor && !valid.Contains(anchor)) _anchor = null;
        Changed?.Invoke();
    }
}
