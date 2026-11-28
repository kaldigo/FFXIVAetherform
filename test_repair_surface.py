"""Local replacement must preserve boundaries and point identity."""
import unittest
import numpy as np
from repair_surface import patch_geometry,boundaries
from polish_repair import bind_points
from test_clean_reference import patch


class RepairTests(unittest.TestCase):
    def test_patch_preserves_boundary_and_quadratic_form(self):
        v,t,_=patch(0.)
        mask=np.max(abs(v[t][...,:2]),axis=(1,2))<=3
        p,nt,loop,origin,basis=patch_geometry(v,t,mask,1.)
        np.testing.assert_array_equal(p[:len(loop)],v[loop])
        np.testing.assert_allclose(p[:,2],.002*(p[:,0]**2+p[:,1]**2),atol=1e-10)
        self.assertEqual(boundaries(nt)['openBoundaryLoops'],1)
        # Every boundary edge survived without an inserted vertex.
        from reference_surface import boundary_edges
        self.assertEqual(len(boundary_edges(nt)),len(loop))

    def test_invalid_winding_is_rejected(self):
        with self.assertRaises(ValueError):boundaries(np.array([[0,1,2],[0,1,3]]))

    def test_rebinds_chart_and_surviving_points_after_deformation(self):
        old={'position':np.array([[0.,0,0],[1,0,0],[0,1,0],[1,1,0]]),
             'triangles':np.array([[0,1,2],[1,3,2]])}
        data=dict(old,originalDenseFace=np.array([-1,1]))
        patches=[dict(oldFaces=[0],firstNewFace=0,newFaceCount=1,origin=[0,0,0],basis=np.eye(3))]
        lm=dict(names=np.array(['a','b']),sourceFace=np.array([0,1]),
                sourceBary=np.array([[.2,.3,.5],[.4,.1,.5]]),targetPosition=np.zeros((2,3)),
                sourceParent=np.array([8,9]))
        moved=old['position']+np.array([0,0,2.])
        result,names=bind_points(old,data,patches,lm,moved)
        np.testing.assert_allclose(result['sourceBary'],lm['sourceBary'])
        np.testing.assert_allclose(result['sourcePosition'][:,2],2.)
        np.testing.assert_array_equal(result['targetPosition'],lm['targetPosition'])
        self.assertNotIn('sourceParent',result)
        self.assertEqual(names,['a'])


if __name__=='__main__':unittest.main()
