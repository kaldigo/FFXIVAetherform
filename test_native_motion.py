"""Source-carried relief invariants and the subdivision/skinning distinction."""
import unittest
import numpy as np
from scipy.spatial.transform import Rotation
from body_motion import Rig
from compare_native_motion import carry_surface,native_sample


class NativeMotionTests(unittest.TestCase):
    def test_rigid_motion_preserves_raised_relief(self):
        rest=np.array([[[0.,0,0],[2,0,0],[0,2,0]]]);t=np.array([[0,1,2]])
        bary=np.array([[[.5,.25,.25],[.2,.7,.1],[.2,.1,.7]]]);parents=np.zeros((1,3),int)
        v=np.einsum('fci,fij->fcj',bary,rest).reshape(-1,3);v[:,2]=[.2,.4,.1]
        rotation=Rotation.from_euler('xyz',[40,25,-70],degrees=True).as_matrix();shift=np.array([3,7,-2])
        out,_=carry_surface(rest,rest@rotation.T+shift,parents,bary,v,t)
        np.testing.assert_allclose(out,v@rotation.T+shift,atol=1e-12)

    def test_zero_relief_matches_posed_native_material_points(self):
        rest=np.array([[[0.,0,0],[2,0,0],[0,2,0]]]);posed=rest.copy();posed[0,1]=[1,1,.4]
        t=np.array([[0,1,2]]);bary=np.array([[[.5,.25,.25],[.2,.7,.1],[.2,.1,.7]]]);parents=np.zeros((1,3),int)
        v=np.einsum('fci,fij->fcj',bary,rest).reshape(-1,3)
        out,_=carry_surface(rest,posed,parents,bary,v,t)
        np.testing.assert_allclose(out,native_sample(posed,parents,bary,t,3),atol=1e-12)

    def test_interpolating_weights_does_not_preserve_a_native_edge(self):
        pose=np.array([[0,0,0,1,0,0,0,1,1,1,1,0],[.002,0,0,1,0,0,0,1,1,1,1,0]])
        rig=Rig(dict(Names=['root','hinge'],Parents=[-1,0],Pose=pose.ravel().tolist()))
        world=rig.world(dict(rotations={'hinge':[0,0,90]}),.5)
        endpoints,_=rig.skin(np.array([[0.,0,0],[2,0,0]]),np.eye(2),['root','hinge'],world)
        midpoint,_=rig.skin(np.array([[1.,0,0]]),np.array([[.5,.5]]),['root','hinge'],world)
        np.testing.assert_allclose(endpoints.mean(0),[1,0,0],atol=1e-12)
        np.testing.assert_allclose(midpoint,[[1.5,-.5,0]],atol=1e-12)


if __name__=='__main__':unittest.main()
