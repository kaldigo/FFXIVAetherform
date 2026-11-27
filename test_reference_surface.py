"""Geometric regression tests for reconstruction and shared-edge refinement."""
import unittest

import numpy as np

from reference_surface import areas, evaluate, patches, refine, triangulate_loop, unit


class ReconstructionTests(unittest.TestCase):
    def test_concave_root_cap_keeps_boundary_and_area(self):
        p = np.array([[0.,0.,0.], [3.,0.,0.], [3.,1.,0.],
                      [1.,1.,0.], [1.,3.,0.], [0.,3.,0.]])
        t = triangulate_loop(p)
        self.assertEqual(len(t), 4)
        self.assertAlmostEqual(areas(p[t]).sum(), 5.)
        edges = {}
        for face in t:
            for i, j in ((0,1), (1,2), (2,0)):
                key = tuple(sorted(face[[i,j]]))
                edges[key] = edges.get(key, 0)+1
        self.assertEqual({key for key, count in edges.items() if count == 1},
                         {tuple(sorted((i, (i+1)%6))) for i in range(6)})

    def test_planar_surface_stays_planar(self):
        v = np.array([[0., 0., 0.], [2., 0., 0.], [2., 2., 0.], [0., 2., 0.]])
        tri = np.array([[0, 1, 2], [0, 2, 3]])
        n = np.broadcast_to([0., 0., 1.], (2, 3, 3))
        cp, _ = patches(v[tri], n, tri)
        flat, t, owner, bc, _ = refine(v, tri, np.full(2, .05))
        actual, normal = evaluate(cp, owner[:, None], bc, True)
        np.testing.assert_allclose(actual, flat[t], atol=1e-12)
        np.testing.assert_allclose(normal, np.broadcast_to([0., 0., 1.], normal.shape), atol=1e-12)

    def test_curved_shared_edge_has_no_cracks(self):
        v = np.array([[0., 0., 1.], [1., 0., 0.], [0., 1., 0.], [-1., 0., 0.]])
        tri = np.array([[0, 1, 2], [0, 2, 3]])
        cp, _ = patches(v[tri], unit(v)[tri], tri)
        flat, t, owner, bc, _ = refine(v, tri, np.array([.02, .07]))
        curved = evaluate(cp, owner[:, None], bc)
        occurrences = {}
        for index, value in zip(t.ravel(), curved.reshape(-1, 3)):
            if index in occurrences:
                np.testing.assert_allclose(value, occurrences[index], atol=1e-12)
            occurrences[index] = value
        self.assertGreater(np.linalg.norm(curved-flat[t], axis=-1).max(), .05)
        edge_counts = {}
        for face in t:
            for i, j in ((0, 1), (1, 2), (2, 0)):
                key = tuple(sorted(face[[i, j]]))
                edge_counts[key] = edge_counts.get(key, 0)+1
        self.assertTrue(all(count in (1, 2) for count in edge_counts.values()))
        # Every open edge lies on an original boundary, never on the shared seam.
        for (a, b), count in edge_counts.items():
            if count == 1:
                self.assertFalse(abs(flat[a, 0]) < 1e-12 and abs(flat[b, 0]) < 1e-12)
        self.assertTrue(np.all(areas(flat[t]) <= np.array([.02, .07])[owner]*1.25+1e-12))


if __name__ == "__main__":
    unittest.main()
