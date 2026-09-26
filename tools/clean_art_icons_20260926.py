"""Clean traced masks for the five generated UI icons (user authorized 2026-09-26).
Contours follow generated-2026-09-26/*.png; ragged edges and noisy holes are
reconstructed as smooth paths, then antialiased to pure-white 256px PNGs.
Does not call or modify tools/mission_icons.py.
"""
from pathlib import Path
import math, re
from PIL import Image, ImageDraw
ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / "art/space/mission-reputation"
S = 4

def path(draw, spec, fill=255):
    tokens = re.findall(r"[MLCQZ]|-?\d+(?:\.\d+)?", spec)
    pts=[]; i=0; x=y=0
    while i<len(tokens):
        cmd=tokens[i]; i+=1
        count={"M":2,"L":2,"C":6,"Q":4,"Z":0}[cmd]
        v=list(map(float,tokens[i:i+count])); i+=count
        if cmd in ("M","L"):
            x,y=v; pts.append((x*S,y*S))
        elif cmd in ("C","Q"):
            sx,sy=x,y
            for n in range(1,33):
                t=n/32; u=1-t
                if cmd=="C":
                    xx=u**3*sx+3*u*u*t*v[0]+3*u*t*t*v[2]+t**3*v[4]
                    yy=u**3*sy+3*u*u*t*v[1]+3*u*t*t*v[3]+t**3*v[5]
                else:
                    xx=u*u*sx+2*u*t*v[0]+t*t*v[2]
                    yy=u*u*sy+2*u*t*v[1]+t*t*v[3]
                pts.append((xx*S,yy*S))
            x,y=v[-2:]
    draw.polygon(pts,fill=fill)

def ellipse(draw, box, fill):
    draw.ellipse(tuple(v*S for v in box),fill=fill)

def rotated_ellipse(draw,cx,cy,rx,ry,degrees,fill):
    angle=math.radians(degrees)
    pts=[]
    for n in range(256):
        t=2*math.pi*n/256
        xx=rx*math.cos(t); yy=ry*math.sin(t)
        pts.append(((cx+xx*math.cos(angle)-yy*math.sin(angle))*S,
                    (cy+xx*math.sin(angle)+yy*math.cos(angle))*S))
    draw.polygon(pts,fill=fill)

def make(name,fn):
    mask=Image.new("L",(256*S,256*S),0)
    fn(ImageDraw.Draw(mask))
    box=mask.getbbox()
    crop=mask.crop(box)
    factor=194/max(crop.size)
    size=tuple(round(v*factor) for v in crop.size)
    crop=crop.resize(size,Image.Resampling.LANCZOS)
    alpha=Image.new("L",(256,256),0)
    alpha.paste(crop,((256-size[0])//2,(256-size[1])//2))
    icon=Image.new("RGBA",(256,256),(255,255,255,0))
    icon.putalpha(alpha)
    icon.save(OUT/(name+".png"))
    return icon

def skull(d):
    path(d,"M128 22 C69 22 30 62 30 116 C30 139 40 151 39 166 C32 178 41 190 59 190 L72 190 L76 215 Q78 224 88 224 L91 224 L91 205 Q91 200 96 200 Q101 200 101 205 L101 227 L119 227 L119 205 Q119 200 124 200 Q129 200 129 205 L129 227 L147 227 L147 205 Q147 200 152 200 Q157 200 157 205 L157 224 L168 224 Q178 224 180 214 L183 190 L197 190 C214 189 223 178 216 166 C214 151 226 139 226 116 C226 62 186 22 128 22 Z")
    path(d,"M57 111 C49 108 46 119 49 136 C52 152 66 161 83 160 C101 160 113 149 106 138 C100 127 72 114 57 111 Z",0)
    path(d,"M199 111 C207 108 210 119 207 136 C204 152 190 161 173 160 C155 160 143 149 150 138 C156 127 184 114 199 111 Z",0)
    path(d,"M128 153 C122 153 116 169 112 178 Q110 187 119 187 L128 181 L137 187 Q146 187 144 178 C140 169 134 153 128 153 Z",0)

def collect(d):
    path(d,"M37 31 C91 18 143 45 173 71 C210 103 225 140 225 164 C211 145 198 126 184 112 L55 228 Q49 234 42 227 L32 217 Q27 211 34 204 L157 83 C132 64 88 44 37 39 Q27 34 37 31 Z")
    path(d,"M112 175 L134 158 L161 191 L151 228 L123 221 Z")
    path(d,"M142 151 L179 133 L201 146 L183 178 L164 184 Z")
    path(d,"M190 183 L207 155 L228 190 L219 207 Z")
    path(d,"M168 195 L185 189 L214 217 L184 230 L159 228 Z")

def deliver(d):
    path(d,"M27 67 L132 32 L213 77 L213 200 L106 233 L27 190 Z")
    path(d,"M49 71 L89 58 L154 91 L115 104 Z",0)
    path(d,"M107 52 L130 44 L190 78 L169 85 Z",0)
    path(d,"M44 94 L58 101 L58 174 L44 166 Z",0)
    path(d,"M73 108 L87 115 L87 191 L73 181 Z",0)
    path(d,"M122 121 L140 115 L140 207 L122 213 Z",0)
    path(d,"M154 111 L172 105 L172 197 L154 203 Z",0)
    path(d,"M186 101 L200 96 L200 188 L186 193 Z",0)
    # A clear gap separates the arrow from the crate, as in the generated artwork.
    path(d,"M147 133 L208 133 L208 105 L250 153 L208 203 L208 173 L147 173 Z",0)
    path(d,"M157 142 L217 142 L217 126 L244 153 L217 182 L217 165 L157 165 Z")

def defend(d):
    path(d,"M128 19 C153 41 183 51 220 57 L220 122 C220 177 185 210 128 237 C71 210 36 177 36 122 L36 57 C73 51 103 41 128 19 Z")
    path(d,"M128 43 C152 61 176 68 199 72 L199 124 C199 166 171 195 128 214 C85 195 57 166 57 124 L57 72 C80 68 104 61 128 43 Z",0)
    rotated_ellipse(d,128,130,64,21,-22,255)
    rotated_ellipse(d,128,130,50,10,-22,0)
    ellipse(d,(91,91,165,165),255)
    path(d,"M90 139 C111 144 145 132 169 116 L172 125 C147 143 112 155 90 148 Z",0)

def helmet(d):
    path(d,"M35 177 C36 117 60 79 88 63 Q88 49 108 46 L149 37 L166 47 Q180 48 181 64 C213 84 232 124 234 177 Z")
    path(d,"M26 183 L240 183 Q250 184 250 196 C249 215 226 224 205 224 L62 224 C42 224 20 212 18 198 Q16 187 26 183 Z")
    path(d,"M82 87 Q89 86 88 98 C81 122 78 146 77 169 L65 169 C66 134 71 102 77 91 Q79 88 82 87 Z",0)
    path(d,"M191 87 Q185 86 186 98 C193 122 196 146 197 169 L209 169 C208 134 203 102 197 91 Q194 88 191 87 Z",0)
    ellipse(d,(101,151,166,216),0)
    ellipse(d,(110,160,157,207),255)
    path(d,"M139 32 L159 32 L136 91 L157 88 L137 129 L149 129 L132 168 L143 170 L124 227 L113 227 L128 181 L117 178 L130 139 L117 136 L135 101 L111 105 Z",0)

icons=[make("mission-kill",skull),make("mission-collect",collect),make("mission-deliver",deliver),make("mission-defend",defend),make("campaign-quiet-war",helmet)]
# Inspection sheet, not a game asset.
sheet=Image.new("RGB",(1000,340),(20,29,44))
d=ImageDraw.Draw(sheet)
names=["kill","collect","deliver","defend","quiet-war"]
for i,(icon,name) in enumerate(zip(icons,names)):
    x=i*200
    d.text((x+12,12),name,fill="white")
    for size,y in [(160,45),(48,224),(24,294)]:
        sample=icon.resize((size,size),Image.Resampling.LANCZOS)
        sheet.paste(sample,(x+(200-size)//2,y),sample)
sheet.save(OUT/"review-clean-2026-09-26.png")
print("Five pure-white RGBA icons saved at 256x256.")

