"""Broad-shape protection permits inset but detects altered curvature."""
import unittest
import numpy as np
from protect_underbreast import broad_operator,smooth_gate
from separate_body_shape import surface_operator


class ProtectedChestTests(unittest.TestCase):
    def test_constant_inset_is_allowed_but_curvature_change_is_detected(self):
        size=17;x,y=np.meshgrid(np.linspace(30,46,size),np.linspace(-8,8,size))
        v=np.c_[x.ravel(),y.ravel(),(.03*(x-38)**2+.02*y*y).ravel()];t=[]
        for j in range(size-1):
            for i in range(size-1):
                a=j*size+i;t.extend(((a,a+1,a+size),(a+1,a+size+1,a+size)))
        t=np.array(t);n=np.tile([0.,0,1],(len(v),1));mass,_=surface_operator(v,t)
        free=(abs(v[:,0]-38)<5)&(abs(v[:,1])<5)
        operator,weights=broad_operator(v,t,n,mass,free,np.ones(len(v)),radius=7.)
        np.testing.assert_allclose(operator@np.full(len(v),-.4),0,atol=1e-12)
        change=-.01*(v[:,0]-38)**2-.01*v[:,1]**2
        self.assertGreater(float(np.sqrt(np.average((operator@change)**2,weights=weights))),.05)

    def test_protection_tapers_to_zero_outside_chest_patch(self):
        x=np.array([0.,20,25,30,80,100,110,120,130])
        value=smooth_gate(x,20,30,100,120)
        np.testing.assert_array_equal(value[[0,1,7,8]],0)
        np.testing.assert_array_equal(value[[3,4,5]],1)
        self.assertTrue(np.all((value>0)[[2,6]]))


if __name__=='__main__':unittest.main()
