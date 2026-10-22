"""Prepare licensed local face media only. Never launches a browser or captures a screen."""
import cv2, numpy as np, json, shutil, hashlib
from pathlib import Path
root=Path(__file__).resolve().parents[1]
out=root/'artifacts/browser-face-fixture'; out.mkdir(parents=True,exist_ok=True)
src=root/'assets/face-fixtures/astronaut.png'
assert hashlib.sha256(src.read_bytes()).hexdigest()=='88431cd9653ccd539741b555fb0a46b61558b301d4110412b5bc28b5e3ea6cb5'
shutil.copy2(src,out/'astronaut.png')
shutil.copy2(root/'tools/browser-face/index.html',out/'index.html')
shutil.copy2(root/'tools/browser-face/feed.html',out/'feed.html')
im=cv2.imread(str(src))
writer=cv2.VideoWriter(str(out/'moving.webm'),cv2.VideoWriter_fourcc(*'VP80'),20,(640,360))
assert writer.isOpened(),'VP8 encoder unavailable'
for i in range(160):
    canvas=np.full((360,640,3),235,np.uint8)
    x=int(70+200*(1+np.sin(i/25))/2)
    canvas[30:286,x:x+256]=cv2.resize(im,(256,256))
    writer.write(canvas)
writer.release()
cap=cv2.VideoCapture(str(out/'moving.webm')); count=0
while True:
    ok,frame=cap.read()
    if not ok: break
    if count%20==0: cv2.imwrite(str(out/f'video-frame-{count:03}.png'),frame)
    count+=1
cap.release(); assert count==160, count
(out/'fixture.json').write_text(json.dumps({'license':'NASA public domain, via scikit-image v0.24.0','frames':count,'fps':20,'sourceSha256':hashlib.sha256(src.read_bytes()).hexdigest()},indent=2))
print(f'Prepared {out}; decoded {count} frames. No browser opened.')
