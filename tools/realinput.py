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

pid=int(sys.argv[1]); mode=sys.argv[2]
hwnd=main_window(pid)
u.ShowWindow(hwnd, 9)
u.SetForegroundWindow(hwnd)
time.sleep(0.4)
if u.GetForegroundWindow()!=hwnd:
    print('NOT_FOREGROUND'); sys.exit(2)
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
elif mode=='key':
    vk=int(sys.argv[3])
    u.keybd_event(vk,0,0,0); time.sleep(0.06); u.keybd_event(vk,0,2,0)
elif mode=='dblclick':
    x=int(sys.argv[3]); y=int(sys.argv[4])
    u.SetCursorPos(r.left+x, r.top+y); time.sleep(0.15)
    for _ in range(2):
        u.mouse_event(0x0002,0,0,0,0); time.sleep(0.03); u.mouse_event(0x0004,0,0,0,0); time.sleep(0.08)
time.sleep(0.5)
u.SetCursorPos(old[0], old[1])
print('OK')
