"""Maps action IDs + continuous parameters to CadQuery operations.

Parameters arrive in [-1, 1] and are mapped to physical dimensions
relative to the current part's bounding box.
"""

import cadquery as cq
import numpy as np
from typing import Tuple, Optional


class ActionExecutor:
    """Execute actions on the GeometryEngine."""

    def execute(self, action_id: int, params: np.ndarray, engine) -> Tuple[bool, Optional[str]]:
        """Dispatch to the appropriate action handler.

        Returns (success, error_message).
        """
        handlers = {
            0:  self._extrude,
            1:  self._cut,
            2:  self._revolve,
            3:  self._add_cylinder,
            4:  self._add_box,
            5:  self._fillet_edges,
            6:  self._chamfer_edges,
            7:  self._shell,
            8:  self._add_hole,
            9:  self._add_boss,
            10: self._add_rib,
            11: self._translate_feature,
            12: self._scale_feature,
            13: self._mirror,
            14: self._pattern_linear,
            15: self._thicken_wall,
            16: self._smooth_region,
            17: self._remove_feature,
            18: self._validate,
            19: self._finalize,
        }

        handler = handlers.get(action_id)
        if handler is None:
            return (False, f"Unknown action ID: {action_id}")

        try:
            return handler(params, engine)
        except Exception as e:
            return (False, str(e))

    # ------------------------------------------------------------------ #
    # Parameter mapping                                                    #
    # ------------------------------------------------------------------ #

    def _get_scale(self, engine) -> float:
        """Characteristic dimension of the current part (mm)."""
        if engine.current_mesh is None:
            return 10.0
        bbox = engine.current_mesh.bounding_box.extents
        return max(float(bbox[0]), float(bbox[1]), float(bbox[2]), 1.0)

    def _p(self, params: np.ndarray, i: int) -> float:
        """Safe parameter access — returns 0.0 if out of range."""
        if i < len(params):
            return float(params[i])
        return 0.0

    def _map_length(self, raw: float, scale: float) -> float:
        """[-1,1] → [0.1mm, scale×0.5mm]."""
        return max(0.1, 0.1 + (raw + 1.0) / 2.0 * scale * 0.5)

    def _map_position(self, raw: float, scale: float) -> float:
        """[-1,1] → [-scale×0.6, +scale×0.6]."""
        return raw * scale * 0.6

    def _map_radius(self, raw: float, scale: float) -> float:
        """[-1,1] → [0.05mm, scale×0.2mm]."""
        return max(0.05, 0.05 + (raw + 1.0) / 2.0 * scale * 0.2)

    def _map_angle(self, raw: float) -> float:
        """[-1,1] → [-180°, +180°]."""
        return raw * 180.0

    def _map_count(self, raw: float, lo: int = 2, hi: int = 6) -> int:
        """[-1,1] → [lo, hi] integer."""
        return int(round(lo + (raw + 1.0) / 2.0 * (hi - lo)))

    # ------------------------------------------------------------------ #
    # Primitive creation (0–4)                                             #
    # ------------------------------------------------------------------ #

    def _extrude(self, params: np.ndarray, engine) -> Tuple[bool, Optional[str]]:
        """Action 0: Extrude the top face of current geometry."""
        if engine.current_workplane is None:
            return (False, "No geometry to extrude — use add_box or add_cylinder first")
        scale = self._get_scale(engine)
        length = self._map_length(self._p(params, 3), scale)
        try:
            wp = engine.current_workplane.faces(">Z").wires().toPending().extrude(length)
            engine.current_workplane = wp
            return (True, None)
        except Exception as e:
            return (False, f"Extrude failed: {e}")

    def _cut(self, params: np.ndarray, engine) -> Tuple[bool, Optional[str]]:
        """Action 1: Cut (subtract) a box from the current geometry."""
        if engine.current_workplane is None:
            return (False, "No geometry to cut")
        scale = self._get_scale(engine)
        cx = self._map_position(self._p(params, 0), scale)
        cy = self._map_position(self._p(params, 1), scale)
        cz = self._map_position(self._p(params, 2), scale)
        depth = self._map_length(self._p(params, 3), scale)
        sx = self._map_length(self._p(params, 4), scale)
        sy = self._map_length(self._p(params, 5), scale)
        try:
            cutter = cq.Workplane("XY").box(sx, sy, depth).translate((cx, cy, cz + depth / 2))
            wp = engine.current_workplane.cut(cutter)
            engine.current_workplane = wp
            return (True, None)
        except Exception as e:
            return (False, f"Cut failed: {e}")

    def _revolve(self, params: np.ndarray, engine) -> Tuple[bool, Optional[str]]:
        """Action 2: Revolve a profile around an axis."""
        if engine.current_workplane is None:
            return (False, "No geometry for revolve")
        scale = self._get_scale(engine)
        angle = abs(self._map_angle(self._p(params, 3)))
        if angle < 1.0:
            angle = 360.0
        try:
            wp = engine.current_workplane.faces(">Z").wires().toPending().revolve(angle)
            engine.current_workplane = wp
            return (True, None)
        except Exception as e:
            return (False, f"Revolve failed: {e}")

    def _add_cylinder(self, params: np.ndarray, engine) -> Tuple[bool, Optional[str]]:
        """Action 3: Add a cylinder primitive."""
        scale = self._get_scale(engine)
        cx = self._map_position(self._p(params, 0), scale)
        cy = self._map_position(self._p(params, 1), scale)
        cz = self._map_position(self._p(params, 2), scale)
        radius = self._map_radius(self._p(params, 3), scale)
        height = self._map_length(self._p(params, 4), scale)
        try:
            if engine.current_workplane is None:
                wp = cq.Workplane("XY").cylinder(height, radius)
                if cx or cy or cz:
                    wp = wp.translate((cx, cy, cz + height / 2))
            else:
                cyl = cq.Workplane("XY").cylinder(height, radius)
                if cx or cy or cz:
                    cyl = cyl.translate((cx, cy, cz + height / 2))
                wp = engine.current_workplane.union(cyl)
            engine.current_workplane = wp
            return (True, None)
        except Exception as e:
            return (False, f"Add cylinder failed: {e}")

    def _add_box(self, params: np.ndarray, engine) -> Tuple[bool, Optional[str]]:
        """Action 4: Add a box primitive."""
        scale = self._get_scale(engine)
        cx = self._map_position(self._p(params, 0), scale)
        cy = self._map_position(self._p(params, 1), scale)
        cz = self._map_position(self._p(params, 2), scale)
        sx = self._map_length(self._p(params, 3), scale)
        sy = self._map_length(self._p(params, 4), scale)
        sz = self._map_length(self._p(params, 5), scale)
        try:
            if engine.current_workplane is None:
                wp = cq.Workplane("XY").box(sx, sy, sz)
                if cx or cy or cz:
                    wp = wp.translate((cx, cy, cz))
            else:
                box = cq.Workplane("XY").box(sx, sy, sz)
                if cx or cy or cz:
                    box = box.translate((cx, cy, cz))
                wp = engine.current_workplane.union(box)
            engine.current_workplane = wp
            return (True, None)
        except Exception as e:
            return (False, f"Add box failed: {e}")

    # ------------------------------------------------------------------ #
    # Feature operations (5–10)                                            #
    # ------------------------------------------------------------------ #

    def _fillet_edges(self, params: np.ndarray, engine) -> Tuple[bool, Optional[str]]:
        """Action 5: Fillet all edges or edges near a selector point."""
        if engine.current_workplane is None:
            return (False, "No geometry to fillet")
        scale = self._get_scale(engine)
        radius = self._map_radius(self._p(params, 0), scale)
        try:
            wp = engine.current_workplane.edges().fillet(radius)
            engine.current_workplane = wp
            return (True, None)
        except Exception as e:
            return (False, f"Fillet failed: {e}")

    def _chamfer_edges(self, params: np.ndarray, engine) -> Tuple[bool, Optional[str]]:
        """Action 6: Chamfer edges."""
        if engine.current_workplane is None:
            return (False, "No geometry to chamfer")
        scale = self._get_scale(engine)
        distance = self._map_radius(self._p(params, 0), scale)
        try:
            wp = engine.current_workplane.edges().chamfer(distance)
            engine.current_workplane = wp
            return (True, None)
        except Exception as e:
            return (False, f"Chamfer failed: {e}")

    def _shell(self, params: np.ndarray, engine) -> Tuple[bool, Optional[str]]:
        """Action 7: Shell the part (hollow out)."""
        if engine.current_workplane is None:
            return (False, "No geometry to shell")
        scale = self._get_scale(engine)
        thickness = self._map_radius(self._p(params, 0), scale)
        try:
            wp = engine.current_workplane.faces(">Z").shell(-thickness)
            engine.current_workplane = wp
            return (True, None)
        except Exception as e:
            return (False, f"Shell failed: {e}")

    def _add_hole(self, params: np.ndarray, engine) -> Tuple[bool, Optional[str]]:
        """Action 8: Drill a hole."""
        if engine.current_workplane is None:
            return (False, "No geometry for hole")
        scale = self._get_scale(engine)
        cx = self._map_position(self._p(params, 0), scale)
        cy = self._map_position(self._p(params, 1), scale)
        diameter = self._map_radius(self._p(params, 3), scale) * 2
        depth = self._map_length(self._p(params, 4), scale)
        try:
            wp = (
                engine.current_workplane
                .faces(">Z")
                .workplane()
                .center(cx, cy)
                .hole(diameter, depth)
            )
            engine.current_workplane = wp
            return (True, None)
        except Exception as e:
            return (False, f"Hole failed: {e}")

    def _add_boss(self, params: np.ndarray, engine) -> Tuple[bool, Optional[str]]:
        """Action 9: Add a cylindrical boss (protrusion)."""
        if engine.current_workplane is None:
            return (False, "No geometry for boss")
        scale = self._get_scale(engine)
        cx = self._map_position(self._p(params, 0), scale)
        cy = self._map_position(self._p(params, 1), scale)
        diameter = self._map_radius(self._p(params, 3), scale) * 2
        height = self._map_length(self._p(params, 4), scale)
        try:
            boss = (
                cq.Workplane("XY")
                .workplane()
                .center(cx, cy)
                .circle(diameter / 2)
                .extrude(height)
            )
            wp = engine.current_workplane.union(boss)
            engine.current_workplane = wp
            return (True, None)
        except Exception as e:
            return (False, f"Boss failed: {e}")

    def _add_rib(self, params: np.ndarray, engine) -> Tuple[bool, Optional[str]]:
        """Action 10: Add a rib (thin wall reinforcement)."""
        if engine.current_workplane is None:
            return (False, "No geometry for rib")
        scale = self._get_scale(engine)
        x0 = self._map_position(self._p(params, 0), scale)
        y0 = self._map_position(self._p(params, 1), scale)
        thickness = self._map_radius(self._p(params, 4), scale)
        height = self._map_length(self._p(params, 5), scale)
        length = scale * 0.3
        try:
            rib = cq.Workplane("XY").box(length, thickness, height).translate((x0, y0, height / 2))
            wp = engine.current_workplane.union(rib)
            engine.current_workplane = wp
            return (True, None)
        except Exception as e:
            return (False, f"Rib failed: {e}")

    # ------------------------------------------------------------------ #
    # Transformation operations (11–14)                                    #
    # ------------------------------------------------------------------ #

    def _translate_feature(self, params: np.ndarray, engine) -> Tuple[bool, Optional[str]]:
        """Action 11: Translate the entire workplane."""
        if engine.current_workplane is None:
            return (False, "No geometry to translate")
        scale = self._get_scale(engine)
        dx = self._map_position(self._p(params, 1), scale)
        dy = self._map_position(self._p(params, 2), scale)
        dz = self._map_position(self._p(params, 3), scale)
        try:
            wp = engine.current_workplane.translate((dx, dy, dz))
            engine.current_workplane = wp
            return (True, None)
        except Exception as e:
            return (False, f"Translate failed: {e}")

    def _scale_feature(self, params: np.ndarray, engine) -> Tuple[bool, Optional[str]]:
        """Action 12: Uniform scale of the workplane."""
        if engine.current_workplane is None:
            return (False, "No geometry to scale")
        # Use mean of scale params as uniform factor; map to [0.5, 1.5]
        raw = (self._p(params, 1) + self._p(params, 2) + self._p(params, 3)) / 3.0
        factor = max(0.1, 0.5 + (raw + 1.0) / 2.0)
        try:
            solid = engine.current_workplane.val()
            scaled = solid.scale(factor)
            wp = cq.Workplane("XY").newObject([scaled])
            engine.current_workplane = wp
            return (True, None)
        except Exception as e:
            return (False, f"Scale failed: {e}")

    def _mirror(self, params: np.ndarray, engine) -> Tuple[bool, Optional[str]]:
        """Action 13: Mirror across XY, XZ, or YZ plane."""
        if engine.current_workplane is None:
            return (False, "No geometry to mirror")
        # Choose plane based on dominant normal component
        nx = abs(self._p(params, 0))
        ny = abs(self._p(params, 1))
        nz = abs(self._p(params, 2))
        if nx >= ny and nx >= nz:
            plane = "YZ"
        elif ny >= nz:
            plane = "XZ"
        else:
            plane = "XY"
        try:
            mirrored = engine.current_workplane.mirror(plane)
            wp = engine.current_workplane.union(mirrored)
            engine.current_workplane = wp
            return (True, None)
        except Exception as e:
            return (False, f"Mirror failed: {e}")

    def _pattern_linear(self, params: np.ndarray, engine) -> Tuple[bool, Optional[str]]:
        """Action 14: Linear pattern of the last face feature."""
        if engine.current_workplane is None:
            return (False, "No geometry to pattern")
        scale = self._get_scale(engine)
        count = self._map_count(self._p(params, 3), lo=2, hi=5)
        spacing = self._map_length(self._p(params, 4), scale)
        # Pattern along X as default
        try:
            result_wp = engine.current_workplane
            base_solid = result_wp.val()
            for i in range(1, count):
                translated = base_solid.translate(cq.Vector(spacing * i, 0, 0))
                copy_wp = cq.Workplane("XY").newObject([translated])
                result_wp = result_wp.union(copy_wp)
            engine.current_workplane = result_wp
            return (True, None)
        except Exception as e:
            return (False, f"Pattern failed: {e}")

    # ------------------------------------------------------------------ #
    # Repair & refinement (15–17)                                          #
    # ------------------------------------------------------------------ #

    def _thicken_wall(self, params: np.ndarray, engine) -> Tuple[bool, Optional[str]]:
        """Action 15: Offset a face outward to thicken a wall."""
        if engine.current_workplane is None:
            return (False, "No geometry to thicken")
        scale = self._get_scale(engine)
        amount = self._map_radius(self._p(params, 3), scale)
        # Select the face nearest to selector point; fall back to >Z face
        try:
            wp = engine.current_workplane.faces(">Z").shell(amount)
            engine.current_workplane = wp
            return (True, None)
        except Exception as e:
            return (False, f"Thicken wall failed: {e}")

    def _smooth_region(self, params: np.ndarray, engine) -> Tuple[bool, Optional[str]]:
        """Action 16: Fillet a region (approximation — fillet small radius)."""
        if engine.current_workplane is None:
            return (False, "No geometry to smooth")
        scale = self._get_scale(engine)
        radius = self._map_radius(self._p(params, 3), scale) * 0.5
        try:
            wp = engine.current_workplane.edges().fillet(radius)
            engine.current_workplane = wp
            return (True, None)
        except Exception as e:
            return (False, f"Smooth region failed: {e}")

    def _remove_feature(self, params: np.ndarray, engine) -> Tuple[bool, Optional[str]]:
        """Action 17: Remove a small protrusion by subtracting a box at selector point."""
        if engine.current_workplane is None:
            return (False, "No geometry for remove_feature")
        scale = self._get_scale(engine)
        cx = self._map_position(self._p(params, 0), scale)
        cy = self._map_position(self._p(params, 1), scale)
        cz = self._map_position(self._p(params, 2), scale)
        size = scale * 0.1
        try:
            cutter = cq.Workplane("XY").box(size, size, size).translate((cx, cy, cz))
            wp = engine.current_workplane.cut(cutter)
            engine.current_workplane = wp
            return (True, None)
        except Exception as e:
            return (False, f"Remove feature failed: {e}")

    # ------------------------------------------------------------------ #
    # Control actions (18–19)                                              #
    # ------------------------------------------------------------------ #

    def _validate(self, params: np.ndarray, engine) -> Tuple[bool, Optional[str]]:
        """Action 18: Trigger full validation — no geometry change."""
        return (True, None)

    def _finalize(self, params: np.ndarray, engine) -> Tuple[bool, Optional[str]]:
        """Action 19: Declare part complete — no geometry change."""
        return (True, None)
