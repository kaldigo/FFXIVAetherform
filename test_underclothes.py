"""Independent shell topology and complete static crossing checks."""
import unittest
import numpy as np
from extract_underclothes import make_shell
from mesh_crossings import paired_intersections,crossing_pairs
from repair_surface import boundaries


class UnderclothesTests(unittest.TestCase):
    def test_open_patch_becomes_oriented_closed_shell(self):
        inner=np.array([[0.,0,0],[1,0,0],[1,1,0],[0,1,0]]);outer=inner+[0,0,.2]
        t=np.array([[0,1,2],[0,2,3]]);uv=inner[t][...,:2]
        weights=np.ones((2,3,1));d=make_shell(outer,inner,t,uv,weights,'cloth')
        self.assertEqual(boundaries(d['triangles'])['openBoundaryLoops'],0)
        p=d['position'][d['triangles']];volume=np.einsum('ij,ij->i',p[:,0],np.cross(p[:,1],p[:,2])).sum()/6
        self.assertAlmostEqual(volume,.2)
        np.testing.assert_array_equal(d['uv'][:2],uv);np.testing.assert_allclose(d['weights'].sum(-1),1)
        self.assertEqual(len(crossing_pairs(p,p,self_faces=d['triangles'])),0)

    def test_crossings_and_coplanar_overlap_are_detected(self):
        a=np.array([[0.,0,0],[2,0,0],[0,2,0]])
        b=np.array([[[.5,.5,-1],[.5,.5,1],[1.5,.5,0]],a+[0,0,1],a*.2+[.2,.2,0],a+[3,0,0]])
        np.testing.assert_array_equal(paired_intersections(np.repeat(a[None],4,axis=0),b),[True,False,True,False])

    def test_large_triangle_candidate_is_not_missed(self):
        big=np.array([[[0.,0,0],[100,0,0],[0,100,0]]])
        small=np.array([[[.5,.5,-1],[.5,.5,1],[1,.5,0]]])
        np.testing.assert_array_equal(crossing_pairs(small,big),[[0,0]])


if __name__=='__main__':unittest.main()
