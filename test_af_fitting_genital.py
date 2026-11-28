"""Unfold connected folds; preserve holes and ignore buried shape as a donor."""
import unittest
import numpy as np
from prepare_af_fitting_genital import disk_chart,side_slope_model


def grid():
    x,y=np.meshgrid(np.linspace(-2,2,7),np.linspace(-2,2,7));v=np.c_[x.ravel(),y.ravel(),np.zeros(x.size)];t=[]
    for j in range(6):
        for i in range(6):
            a=j*7+i;t.extend(((a,a+1,a+8),(a,a+8,a+7)))
    return v,np.array(t)


class GenitalFittingTests(unittest.TestCase):
    def test_unfolds_recessed_disk_without_changing_boundary_or_faces(self):
        v,t=grid();v[24]=[-1.7,0,-1.]
        chart=disk_chart(v,t,np.ones(len(t),bool),np.eye(3),np.zeros(3),rings=0)
        self.assertGreater((chart['originalProjectedArea']<=0).sum(),0)
        self.assertGreater(chart['projectedArea'].min(),0)
        np.testing.assert_array_equal(chart['vertices'][chart['triangles']],t)
        np.testing.assert_array_equal(chart['uv'][chart['boundary']],chart['local'][chart['boundary'],:2])

    def test_rejects_a_genuine_hole_instead_of_sealing_it(self):
        v,t=grid();selected=np.ones(len(t),bool);cell=2*6+2;selected[2*cell:2*cell+2]=False
        with self.assertRaisesRegex(ValueError,'single disk'):disk_chart(v,t,selected,np.eye(3),np.zeros(3),rings=0)

    def test_side_slopes_recover_curve_and_ignore_deep_central_detail(self):
        x,s=np.meshgrid(np.linspace(-24,24,25),np.linspace(-30,30,31));x,s=x.ravel(),s.ravel()
        h=2-.02*x*x+.01*s*s;local=np.c_[x,s,h];normal=np.c_[.04*x,-.02*s,np.ones(len(x))];normal/=np.linalg.norm(normal,axis=1,keepdims=True)
        model,_,_=side_slope_model(local,normal,np.ones(len(x)));query=np.array([[0.,0],[1,5],[-3,-2]])
        expected=2-.02*query[:,0]**2+.01*query[:,1]**2
        np.testing.assert_allclose(model(query),expected,atol=1e-10)
        local[abs(x)<8,2]-=30;normal[abs(x)<8]=[0,0,-1]
        changed,_,_=side_slope_model(local,normal,np.ones(len(x)))
        np.testing.assert_allclose(changed(query),expected,atol=1e-10)


if __name__=='__main__':unittest.main()
