import cadquery as cq
import math

length = 30
width = 20
height = 10

result = cq.Workplane("XY").box(length, width, height)