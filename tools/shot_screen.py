import ctypes, sys
try: ctypes.windll.shcore.SetProcessDpiAwareness(2)
except: pass
from PIL import ImageGrab
img = ImageGrab.grab(all_screens=True)
img.save(sys.argv[1] if len(sys.argv)>1 else 'screen.png')
print(img.size)
