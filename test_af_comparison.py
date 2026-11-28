"""Check the complete surface search and native render-alias validation."""
import unittest

import numpy as np

from compare_af_bases import closest_surface, compatible_occurrences, point_triangle


class ComparisonTests(unittest.TestCase):
    def test_large_triangle_is_found_beyond_nearest_centroids(self):
        # Many nearby centroids distract a fixed-k search, while a distant
        # large triangle actually passes just beneath the query.
        p=np.array([[0.,0.,.1],[.2,.2,.3]])
        tiny=np.array([[[1+i*.01,0,2],[1.001+i*.01,0,2],[1+i*.01,.001,2]] for i in range(40)])
        large=np.array([[[-.5,-.5,0],[1000,-.5,0],[-.5,1000,0]]])
        triangles=np.concatenate((tiny,large))
        distance,face,bc=closest_surface(p,triangles)
        np.testing.assert_allclose(distance,[.1,.3],atol=1e-10)
        np.testing.assert_array_equal(face,[40,40])
        np.testing.assert_allclose(np.einsum('ni,nij->nj',bc,triangles[face])[:,:2],p[:,:2],atol=1e-9)
        brute,_=point_triangle(p[:,None],triangles[None])
        np.testing.assert_allclose(distance,brute.min(axis=1),atol=1e-10)

    def test_alias_merge_is_allowed_only_for_coincident_vertices(self):
        a=dict(baseIndices=np.array([[0,1,2],[3,2,1]]),
               vertices=np.array([[0.,0.,0.],[1.,0.,0.],[0.,1.,0.],[0.,0.,0.]]),
               vertexUV=np.array([[0.,0.],[1.,0.],[0.,1.],[0.,0.]]))
        b=dict(baseIndices=np.array([[0,1,2],[0,2,1]]),vertices=a['vertices'][:3].copy(),vertexUV=a['vertexUV'][:3].copy())
        self.assertTrue(compatible_occurrences(a,b))
        a['vertices'][3,2]=1.
        self.assertFalse(compatible_occurrences(a,b))


if __name__=='__main__':unittest.main()
