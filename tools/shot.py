import ctypes, sys, os
try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)
except Exception:
    pass
try:
    ctypes.windll.user32.SetProcessDpiAwarenessContext(-4)
except Exception:
    pass
try:
    ctypes.windll.user32.SetProcessDPIAware()
except Exception:
    pass
from ctypes import wintypes
from PIL import Image

user32 = ctypes.windll.user32
gdi32 = ctypes.windll.gdi32

def find_window(pid):
    result = []
    @ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    def enum(hwnd, lparam):
        p = wintypes.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(p))
        if p.value == pid and user32.IsWindowVisible(hwnd):
            n = user32.GetWindowTextLengthW(hwnd)
            if n:
                buf = ctypes.create_unicode_buffer(n+1)
                user32.GetWindowTextW(hwnd, buf, n+1)
                result.append((hwnd, buf.value))
        return True
    user32.EnumWindows(enum, 0)
    return result

def capture(hwnd, path):
    rect = wintypes.RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(rect))
    w, h = rect.right-rect.left, rect.bottom-rect.top
    hdc = user32.GetWindowDC(hwnd)
    mem = gdi32.CreateCompatibleDC(hdc)
    bmp = gdi32.CreateCompatibleBitmap(hdc, w, h)
    gdi32.SelectObject(mem, bmp)
    user32.PrintWindow(hwnd, mem, 2)
    class BITMAPINFOHEADER(ctypes.Structure):
        _fields_ = [("biSize", wintypes.DWORD),("biWidth", wintypes.LONG),("biHeight", wintypes.LONG),
                    ("biPlanes", wintypes.WORD),("biBitCount", wintypes.WORD),("biCompression", wintypes.DWORD),
                    ("biSizeImage", wintypes.DWORD),("biXPelsPerMeter", wintypes.LONG),("biYPelsPerMeter", wintypes.LONG),
                    ("biClrUsed", wintypes.DWORD),("biClrImportant", wintypes.DWORD)]
    bi = BITMAPINFOHEADER()
    bi.biSize = ctypes.sizeof(BITMAPINFOHEADER); bi.biWidth = w; bi.biHeight = -h
    bi.biPlanes = 1; bi.biBitCount = 32; bi.biCompression = 0
    buf = ctypes.create_string_buffer(w*h*4)
    gdi32.GetDIBits(mem, bmp, 0, h, buf, ctypes.byref(bi), 0)
    img = Image.frombuffer('RGBA', (w,h), buf, 'raw', 'BGRA', 0, 1)
    img.convert('RGB').save(path)
    gdi32.DeleteObject(bmp); gdi32.DeleteDC(mem); user32.ReleaseDC(hwnd, hdc)
    return w,h

pid = int(sys.argv[1]) if len(sys.argv)>1 else None
out = sys.argv[2] if len(sys.argv)>2 else 'shot.png'
ws = find_window(pid)
if not ws:
    print('no window for pid', pid); sys.exit(1)
def area(entry):
    r = wintypes.RECT(); user32.GetWindowRect(entry[0], ctypes.byref(r))
    return (r.right-r.left)*(r.bottom-r.top)
ws.sort(key=area, reverse=True)
hwnd, title = ws[0]
print('window:', title, hwnd)
print('size:', capture(hwnd, out))
