"""Skinning invariants and deformation tests that do not mislabel rigid rotation."""
import unittest
import numpy as np
from scipy.spatial.transform import Rotation
from body_motion import Rig,deformation_metrics


class BodyMotionTests(unittest.TestCase):
    def test_rest_and_parent_rotation_use_bind_pivots(self):
        pose=np.array([[0,0,0,1,0,0,0,1,1,1,1,0],[.001,0,0,1,0,0,0,1,1,1,1,0]])
        rig=Rig(dict(Names=['root','child'],Parents=[-1,0],Pose=pose.ravel().tolist()))
        v=np.array([[2.,0,0]]);weights=np.array([[1.]])
        rest,_=rig.skin(v,weights,['child'],rig.rest);np.testing.assert_allclose(rest,v)
        moved,_=rig.skin(v,weights,['child'],rig.world(dict(rotations={'root':[0,0,90]}),.5))
        np.testing.assert_allclose(moved,[[0,2,0]],atol=1e-12)

    def test_rigid_half_turn_is_not_a_fold_or_stretch(self):
        v=np.array([[0.,0,0],[1,0,0],[0,1,0]]);t=np.array([[0,1,2]])
        rotation=Rotation.from_euler('x',180,degrees=True).as_matrix();q=v@rotation.T
        metrics=deformation_metrics(v,q,t,np.repeat(rotation[None],3,axis=0))
        for key in ('signedArea','areaRatio','minStretch','maxStretch'):np.testing.assert_allclose(metrics[key],1,atol=1e-12)

    def test_collapsed_triangle_is_detected(self):
        v=np.array([[0.,0,0],[1,0,0],[0,1,0]]);q=v.copy();q[2]=[.5,0,0]
        metrics=deformation_metrics(v,q,np.array([[0,1,2]]),np.repeat(np.eye(3)[None],3,axis=0))
        self.assertEqual(metrics['signedArea'][0],0);self.assertEqual(metrics['minStretch'][0],0)


if __name__=='__main__':unittest.main()
