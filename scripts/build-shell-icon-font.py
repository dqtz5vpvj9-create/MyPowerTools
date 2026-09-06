"""Build our portable shell pictograms (requires fonttools; not needed to build the app).

Original geometric artwork, using the existing shell codepoints so every platform
renders the same icons. The generated font is checked in as an Avalonia resource.
"""
from pathlib import Path
from math import sin, cos, pi, hypot
from fontTools.fontBuilder import FontBuilder
from fontTools.pens.ttGlyphPen import TTGlyphPen

# Coordinates use a 24-unit drawing grid, with Y pointing down.
icons = {}
def icon(code, lines): icons[code] = lines
def circle(x=12,y=12,r=8):
    return [(x+r*cos(i*pi/16),y+r*sin(i*pi/16)) for i in range(33)]
def box(x,y,w,h): return [(x,y),(x+w,y),(x+w,y+h),(x,y+h),(x,y)]
icon(0xE71D,[box(3,3,7,7),box(14,3,7,7),box(3,14,7,7),box(14,14,7,7)])
icon(0xE72C,[[(19,7),(16,4),(10,3),(5,6),(3,12),(5,18),(11,21),(17,19),(20,15)],[(19,2),(19,8),(13,8)]])
icon(0xE713,[circle(r=4),[(12+ (10 if i%2==0 else 8)*cos(i*pi/8),12+(10 if i%2==0 else 8)*sin(i*pi/8)) for i in range(17)]])
icon(0xE9D9,[[(2,12),(7,12),(10,5),(14,19),(17,12),(22,12)]])
icon(0xEA8F,[[(4,17),(6,14),(6,9),(8,5),(12,3),(16,5),(18,9),(18,14),(20,17),(4,17)],[(9,20),(12,21),(15,20)]])
icon(0xE968,[[(3,7),(20,7),(16,3)],[(21,17),(4,17),(8,21)]])
icon(0xE7F4,[box(3,4,18,13),[(12,17),(12,21)],[(7,21),(17,21)]])
icon(0xE756,[[(8,4),(20,12),(8,20),(8,4)]])
icon(0xE8A7,[[(10,4),(18,12),(10,20)],[(3,12),(18,12)]])
icon(0xE945,[[(14,2),(5,14),(11,14),(10,22),(20,9),(14,9),(14,2)]])
icon(0xEA39,[box(3,3,18,7),box(3,14,18,7),[(6,6.5),(8,6.5)],[(6,17.5),(8,17.5)]])
icon(0xE711,[[(5,5),(19,19)],[(19,5),(5,19)]])
icon(0xE8BB,icons[0xE711])
icon(0xE721,[circle(10,10,7),[(15,15),(22,22)]])
icon(0xE72B,[[(14,4),(6,12),(14,20)],[(6,12),(22,12)]])
icon(0xE7BA,[[(12,3),(22,21),(2,21),(12,3)],[(12,9),(12,14)],[(12,17),(12,18)]])
icon(0xE7E7,[circle(r=9),[(12,6),(12,12),(17,15)]])
# Standard Unicode symbols used by the shell navigation.
icon(0x2302,[[(3,11),(12,3),(21,11)],[(6,9),(6,21),(18,21),(18,9)],[(10,21),(10,15),(14,15),(14,21)]])
icon(0x2699,icons[0xE713]); icon(0x25A6,icons[0xE71D]); icon(0x21BB,icons[0xE72C])
icon(0x25C9,[circle(r=9),circle(r=3)])
icon(0x2630,[[(4,6),(20,6)],[(4,12),(20,12)],[(4,18),(20,18)]])

fb=FontBuilder(1024,isTTF=True)
names=['.notdef']+[f'uni{c:04X}' for c in icons]
fb.setupGlyphOrder(names);fb.setupCharacterMap({c:f'uni{c:04X}' for c in icons})
glyphs={'.notdef':TTGlyphPen(None).glyph()}
for code,lines in icons.items():
    pen=TTGlyphPen(None)
    for points in lines:
        for a,b in zip(points,points[1:]):
            dx,dy=b[0]-a[0],b[1]-a[1];length=hypot(dx,dy)
            if not length: continue
            ox,oy=-dy/length*.8,dx/length*.8
            vertices=[(a[0]+ox,a[1]+oy),(b[0]+ox,b[1]+oy),(b[0]-ox,b[1]-oy),(a[0]-ox,a[1]-oy)]
            scaled=[(round(x*40+32),round(928-y*40)) for x,y in vertices]
            pen.moveTo(scaled[0])
            for point in scaled[1:]:pen.lineTo(point)
            pen.closePath()
    glyphs[f'uni{code:04X}']=pen.glyph()
fb.setupGlyf(glyphs);fb.setupHorizontalMetrics({n:(1024,0) for n in names})
fb.setupHorizontalHeader(ascent=960,descent=-64)
fb.setupNameTable({'familyName':'MyPowerTools Icons','styleName':'Regular','uniqueFontIdentifier':'MyPowerToolsIcons-Regular-1','fullName':'MyPowerTools Icons','psName':'MyPowerToolsIcons-Regular','version':'Version 1.0'})
fb.setupOS2(sTypoAscender=960,sTypoDescender=-64,usWinAscent=960,usWinDescent=64)
fb.setupPost();fb.setupMaxp()
fb.font['head'].created=fb.font['head'].modified=3861000000
out=Path(__file__).resolve().parents[1]/'src/MyPowerTools.UI/Assets/Fonts/MyPowerToolsIcons.ttf'
out.parent.mkdir(parents=True,exist_ok=True);fb.save(out)
print(out)
