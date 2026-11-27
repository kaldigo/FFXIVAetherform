"""Material point identity must survive a change in subdivision topology."""
import unittest

import numpy as np

from reference_surface import refine
from transfer_landmarks import child_address


class LandmarkTests(unittest.TestCase):
    def test_interior_edges_and_vertices_keep_material_address(self):
        v = np.array([[0.,0.,0.], [4.,0.,0.], [0.,3.,0.], [4.,3.,0.]])
        tri = np.array([[0,1,2], [1,3,2]])
        flat, new_tri, parent, bary, _ = refine(v, tri, np.array([.08,.3]))
        by_parent = {i: np.flatnonzero(parent == i) for i in (0,1)}
        for owner in (0,1):
            for material in ([.2,.3,.5], [0.,.5,.5], [1.,0.,0.], [0.,0.,1.]):
                material = np.array(material)
                face, bc = child_address(owner, material, bary, by_parent)
                self.assertEqual(parent[face], owner)
                self.assertGreaterEqual(bc.min(), 0)
                self.assertAlmostEqual(bc.sum(), 1.)
                np.testing.assert_allclose(bc@flat[new_tri[face]], material@v[tri[owner]], atol=1e-12)


if __name__ == "__main__":
    unittest.main()
