"""Final-surface fairness checks using known quadratic and folded surfaces."""
import unittest
import numpy as np
from fit_curvature_envelope import curvature_operator,solve_curvature
from separate_body_shape import surface_operator


def grid(n=17):
    x,y=np.meshgrid(np.linspace(-8,8,n),np.linspace(-8,8,n));v=np.c_[x.ravel(),y.ravel(),np.zeros(n*n)]
    t=[]
    for j in range(n-1):
        for i in range(n-1):
            a=j*n+i;t.extend(((a,a+1,a+n),(a+1,a+n+1,a+n)))
    return v,np.array(t)


class CurvatureEnvelopeTests(unittest.TestCase):
    def test_quadratic_curvature_is_not_mistaken_for_fabric(self):
        v,t=grid();v[:,2]=.03*v[:,0]**2+.01*v[:,0]*v[:,1]+.02*v[:,1]**2
        n=np.tile([0.,0,1],(len(v),1));mass,_=surface_operator(v,t);free=(abs(v[:,0])<6)&(abs(v[:,1])<6)
        _,residual,_,_=curvature_operator(v,v,n,t,np.full(len(t),'legs_smallclothes'),mass,free,6.)
        np.testing.assert_allclose(residual,0,atol=1e-12)

    def test_fit_removes_a_relative_bump_without_outward_motion(self):
        v,t=grid();radius=np.linalg.norm(v[:,:2],axis=1)
        # Raw cloth has a narrow dip surrounded by raised fabric.
        detail=.3*np.exp(-radius**2/12)-.4*np.exp(-radius**2/1.5)
        v[:,2]=detail;n=np.tile([0.,0,1],(len(v),1));free=radius<6.5
        target=-detail;mass,lap=surface_operator(v,t)
        # This inward-only starting surface has a relative raised centre.
        initial=np.minimum(-.6*np.exp(-radius**2/12)+.55*np.exp(-radius**2/1.5),0);initial[~free]=0
        groups=np.full(len(t),'legs_smallclothes')
        op,base,w,ids=curvature_operator(v,v,n,t,groups,mass,free,6.)
        before=base+op@initial
        d,_=solve_curvature(target,mass,lap,free,2.,(v,t,n),groups,3000.,6.,1,initial)
        residual=base+op@d
        self.assertLess(np.average(residual**2,weights=w),.1*np.average(before**2,weights=w))
        centre=np.argmin(radius);row=np.flatnonzero(ids==centre)[0]
        self.assertLess(abs(residual[row]),.01)
        self.assertLessEqual(d.max(),0);np.testing.assert_array_equal(d[~free],0)


if __name__=='__main__':unittest.main()
