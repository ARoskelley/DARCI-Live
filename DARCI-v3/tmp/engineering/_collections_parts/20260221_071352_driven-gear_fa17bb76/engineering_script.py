import cadquery as cq
import math

teeth = 24
face_width = 10
bore_diameter = 10
pressure_angle_deg = 20
root_d = 43
outer_d = 52
tooth_h = 4.5
tooth_w = 2.827

root = cq.Workplane("XY").circle(root_d / 2.0).extrude(face_width)
tooth = cq.Workplane("XY").rect(tooth_w, tooth_h).extrude(face_width)
tooth = tooth.translate((root_d / 2.0 + tooth_h / 2.0, 0, 0))

gear = root
for i in range(teeth):
    gear = gear.union(tooth.rotate((0,0,0), (0,0,1), i * (360.0 / teeth)))

result = gear.faces(">Z").workplane().hole(bore_diameter)