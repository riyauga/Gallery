import ctypes, sys, time
from ctypes import wintypes
u = ctypes.windll.user32
try: ctypes.windll.shcore.SetProcessDpiAwareness(2)
except: pass

def main_window(pid):
    best=[0,0]
    @ctypes.WINFUNCTYPE(wintypes.BOOL,wintypes.HWND,wintypes.LPARAM)
    def cb(h,l):
        p=wintypes.DWORD(); u.GetWindowThreadProcessId(h,ctypes.byref(p))
        if p.value==pid and u.IsWindowVisible(h):
            r=wintypes.RECT(); u.GetWindowRect(h,ctypes.byref(r))
            a=(r.right-r.left)*(r.bottom-r.top)
            if a>best[0]: best[0]=a; best[1]=h
        return True
    u.EnumWindows(cb,0)
    return best[1]

def force_foreground(hwnd):
    """拿到前台：SetForegroundWindow 会被系统拒绝，先用 AttachThreadInput 把输入队列接到目标线程再设。"""
    u.ShowWindow(hwnd, 9)
    # 系统只在「当前进程最近收到过输入」时允许抢前台，发一次 ALT 抬起骗过这条限制。
    u.keybd_event(0x12, 0, 0, 0); time.sleep(0.03); u.keybd_event(0x12, 0, 2, 0)
    for _ in range(8):
        u.SetForegroundWindow(hwnd); time.sleep(0.15)
        if u.GetForegroundWindow()==hwnd: return True
        fg=u.GetForegroundWindow()
        if fg:
            t1=u.GetWindowThreadProcessId(fg,0); t2=u.GetWindowThreadProcessId(hwnd,0)
            if t1 and t2:
                u.AttachThreadInput(t1,t2,True)
                u.SetForegroundWindow(hwnd)
                u.AttachThreadInput(t1,t2,False)
                time.sleep(0.15)
                if u.GetForegroundWindow()==hwnd: return True
        u.SwitchToThisWindow(hwnd, True); time.sleep(0.2)
    return u.GetForegroundWindow()==hwnd

pid=int(sys.argv[1]); mode=sys.argv[2]
hwnd=main_window(pid)
if not force_foreground(hwnd):
    print('NOT_FOREGROUND'); sys.exit(2)
time.sleep(0.4)
r=wintypes.RECT(); u.GetWindowRect(hwnd,ctypes.byref(r))
pt=wintypes.POINT(); u.GetCursorPos(ctypes.byref(pt))
old=(pt.x,pt.y)
if mode=='wheel':
    notches=int(sys.argv[3]); x=int(sys.argv[4]); y=int(sys.argv[5])
    u.SetCursorPos(r.left+x, r.top+y); time.sleep(0.15)
    for _ in range(abs(notches)):
        u.mouse_event(0x0800,0,0, 120 if notches>0 else -120, 0); time.sleep(0.05)
elif mode=='click':
    x=int(sys.argv[3]); y=int(sys.argv[4])
    u.SetCursorPos(r.left+x, r.top+y); time.sleep(0.15)
    u.mouse_event(0x0002,0,0,0,0); time.sleep(0.05); u.mouse_event(0x0004,0,0,0,0)
elif mode=='rclick':
    x=int(sys.argv[3]); y=int(sys.argv[4])
    u.SetCursorPos(r.left+x, r.top+y); time.sleep(0.15)
    u.mouse_event(0x0008,0,0,0,0); time.sleep(0.05); u.mouse_event(0x0010,0,0,0,0)
elif mode=='hotkey':
    mods=[int(m) for m in sys.argv[3].split('+')]
    vk=int(sys.argv[4])
    for m in mods: u.keybd_event(m,0,0,0); time.sleep(0.05)
    u.keybd_event(vk,0,0,0); time.sleep(0.06); u.keybd_event(vk,0,2,0)
    for m in reversed(mods): u.keybd_event(m,0,2,0); time.sleep(0.05)
elif mode=='key':
    vk=int(sys.argv[3])
    u.keybd_event(vk,0,0,0); time.sleep(0.06); u.keybd_event(vk,0,2,0)
elif mode=='dblclick':
    x=int(sys.argv[3]); y=int(sys.argv[4])
    u.SetCursorPos(r.left+x, r.top+y); time.sleep(0.15)
    for _ in range(2):
        u.mouse_event(0x0002,0,0,0,0); time.sleep(0.03); u.mouse_event(0x0004,0,0,0,0); time.sleep(0.08)
elif mode=='drag':
    # drag sx sy ex ey [steps]：按住左键从 (sx,sy) 拖到 (ex,ey)，坐标相对窗口左上角
    sx=int(sys.argv[3]); sy=int(sys.argv[4]); ex=int(sys.argv[5]); ey=int(sys.argv[6])
    steps=int(sys.argv[7]) if len(sys.argv)>7 else 24
    u.SetCursorPos(r.left+sx, r.top+sy); time.sleep(0.25)
    u.mouse_event(0x0002,0,0,0,0); time.sleep(0.2)
    for i in range(1, steps+1):
        u.SetCursorPos(r.left+sx+(ex-sx)*i//steps, r.top+sy+(ey-sy)*i//steps)
        time.sleep(0.02)
    time.sleep(0.3)
    u.mouse_event(0x0004,0,0,0,0); time.sleep(0.2)
time.sleep(0.5)
u.SetCursorPos(old[0], old[1])
print('OK')
