#!/usr/bin/env python3
"""No-SDK reference math distribution test. Not a C# build or world render."""
from pathlib import Path
source = (Path(__file__).resolve().parents[1] / "shards/Amethyst.Shards.Overworld/Generation/Terrain/AquiferSampler.cs").read_text()
for token in ("GeneratorVersion >= 7", "wet * 0.55 + wetCell * 0.8", "wetness > 0.20", "wetness > 0.11"):
    assert token in source, f"Expected sparse-aquifer rule changed: {token}"


from math import floor
MASK=(1<<64)-1

def mix(n):
    n&=MASK;n^=n>>30;n=(n*0xbf58476d1ce4e5b9)&MASK;n^=n>>27;n=(n*0x94d049bb133111eb)&MASK;return n^(n>>31)
def lerp(a,b,t):return a+(b-a)*t
def fade(t):return t*t*t*(t*(t*6-15)+10)
class Grad:
 def __init__(self,seed,domain):self.key=mix((seed&MASK)^mix(domain))
 def hash(self,x,y,z):return mix(self.key^((x*0x9e3779b185ebca87)&MASK)^((y*0xd6e8feb86659fd93)&MASK)^((z*0xc2b2ae3d27d4eb4f)&MASK))
 def dot2(self,h,x,z):
    d=0.7071067811865475
    return (x,-x,z,-z,d*(x+z),d*(-x+z),d*(x-z),d*(-x-z))[h&7]
 def dot3(self,h,x,y,z):return (x+y,-x+y,x-y,-x-y,x+z,-x+z,x-z,-x-z,y+z,-y+z,y-z,-y-z)[h%12]
 def s2(self,x,z):
    ix,iz=floor(x),floor(z);dx,dz=x-ix,z-iz;fx,fz=fade(dx),fade(dz)
    a=lerp(self.dot2(self.hash(ix,0,iz),dx,dz),self.dot2(self.hash(ix+1,0,iz),dx-1,dz),fx)
    b=lerp(self.dot2(self.hash(ix,0,iz+1),dx,dz-1),self.dot2(self.hash(ix+1,0,iz+1),dx-1,dz-1),fx)
    return max(-1,min(1,lerp(a,b,fz)*1.4142135623730951))
 def s3(self,x,y,z):
    ix,iy,iz=floor(x),floor(y),floor(z);dx,dy,dz=x-ix,y-iy,z-iz;fx,fy,fz=fade(dx),fade(dy),fade(dz)
    def row(tz):
      a=lerp(self.dot3(self.hash(ix,iy,iz+tz),dx,dy,dz-tz),self.dot3(self.hash(ix+1,iy,iz+tz),dx-1,dy,dz-tz),fx)
      b=lerp(self.dot3(self.hash(ix,iy+1,iz+tz),dx,dy-1,dz-tz),self.dot3(self.hash(ix+1,iy+1,iz+tz),dx-1,dy-1,dz-tz),fx)
      return lerp(a,b,fy)
    return max(-1,min(1,lerp(row(0),row(1),fz)))
class Frac:
 def __init__(self,seed,domain,oct=3,d=3):
    self.d=d;self.oct=[]
    for i in range(oct):
      salt=(domain+i*0x9e3779b97f4a7c15)&MASK
      g=Grad(seed,salt); key=mix((seed&MASK)^salt)
      ox=(key&0xffffffff)/4294967296*4096
      if d==3:
        key=mix(key^0xD3A5D3A5)
        oy=(key&0xffffffff)/4294967296*4096
        oz=(key>>32)/4294967296*4096
        self.oct.append((g,ox,oy,oz))
      else:
        oz=(key>>32)/4294967296*4096
        self.oct.append((g,ox,oz))
 def sample(self,x,y,z,w):
    f=1/w;amp=1;total=0;sm=0
    for group in self.oct:
      if self.d==2:
        g,ox,oz=group; n=g.s2(x*f+ox,z*f+oz)
      else:
        g,ox,oy,oz=group;n=g.s3(x*f+ox,y*f+oy,z*f+oz)
      sm+=n*amp;total+=amp;amp*=.5;f*=2
    return sm/total
seed=87420913
wet2=Frac(seed,0x4151554946455246,3,2)
water3=Frac(seed,0x4151554946455237,3,3)
lava3=Frac(seed,0x4C41564142415337,2,3)
stats=dict(dry=0,water=0,oldwater=0,lava=0)
for x in range(-640,641,53):
 for z in range(-640,641,59):
   wet=wet2.sample(x,0,z,300)
   c=wet*.55+water3.sample(x,0,z,110)*.8
   if c>.20:stats['water']+=1
   else:stats['dry']+=1
   if wet>-.16:stats['oldwater']+=1
   cdeep=wet*.55+water3.sample(x,-58,z,110)*.8
   if cdeep>.11 and lava3.sample(x,-58,z,96)>.09:stats['lava']+=1
print(stats)
assert stats['dry']>stats['water']>0
assert stats['lava']>0
assert stats['oldwater']>stats['water']
