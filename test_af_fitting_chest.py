"""Check curvature continuation without a planar cap or exterior movement."""
import unittest
import numpy as np
from prepare_af_fitting_chest import continue_curve,continue_collar_surface


class FittingChestTests(unittest.TestCase):
    def test_recovers_curved_breast_beneath_a_local_bump(self):
        x,y=np.meshgrid(np.linspace(-30,30,81),np.linspace(-30,30,81));x,y=x.ravel(),y.ravel();radius=np.hypot(x,y)
        breast=-.015*x*x-.02*y*y+.05*x-.03*y
        bump=4*np.maximum(1-(radius/8)**2,0)**2
        q=np.c_[x,y,breast+bump]
        for method in (continue_curve,continue_collar_surface):
            delta,report=method(q,np.ones(len(q)),np.ones(len(q),bool));inner=radius<9
            np.testing.assert_allclose((q[:,2]+delta)[inner],breast[inner],atol=1e-9)
            np.testing.assert_array_equal(delta[radius>=18],0)
            self.assertTrue(np.all(np.array(report['centrePrincipalGraphCurvaturesPerMm'])<-.01))

    def test_inner_feature_does_not_determine_the_replacement_shape(self):
        x,y=np.meshgrid(np.linspace(-30,30,61),np.linspace(-30,30,61));x,y=x.ravel(),y.ravel();r=np.hypot(x,y)
        q=np.c_[x,y,-.02*x*x-.017*y*y];a=q.copy();b=q.copy();a[r<8,2]+=4;b[r<8,2]+=10
        for method in (continue_curve,continue_collar_surface):
            da,_=method(a,np.ones(len(q)),np.ones(len(q),bool));db,_=method(b,np.ones(len(q)),np.ones(len(q),bool))
            np.testing.assert_allclose((a[:,2]+da)[r<9],(b[:,2]+db)[r<9],atol=1e-12)


if __name__=='__main__':unittest.main()
