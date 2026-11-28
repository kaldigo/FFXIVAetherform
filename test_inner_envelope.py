"""Curvature averaging and a skin-anchored obstacle solve on synthetic data."""
import unittest
import numpy as np
from fit_inner_envelope import quadratic_intercept,constrained_offset,orientation_penalty
from separate_body_shape import surface_operator


class InnerEnvelopeTests(unittest.TestCase):
    def test_orientation_penalty_gradient_matches_finite_difference(self):
        v=np.array([[0.,0,0],[1,0,0],[0,1,0]]);t=np.array([[0,1,2]])
        n=np.array([[0.,0,1],[-1,0,0],[0,0,1]]);d=np.array([0.,.9,0.])
        energy,g=orientation_penalty(v,t,n,d);self.assertGreater(energy,0)
        numerical=[]
        for i in range(3):
            step=np.zeros(3);step[i]=1e-6
            numerical.append((orientation_penalty(v,t,n,d+step)[0]-orientation_penalty(v,t,n,d-step)[0])/2e-6)
        np.testing.assert_allclose(g,numerical,rtol=1e-7,atol=1e-7)

    def test_average_recovers_quadratic_beneath_balanced_texture(self):
        x,y=np.meshgrid(np.linspace(-1,1,15),np.linspace(-1,1,15));x=x.ravel();y=y.ravel()
        design=np.c_[np.ones(len(x)),x,y,x*x,x*y,y*y]
        noise=.08*np.sin(17*x+9*y)
        # Remove polynomial moments so this is balanced fine texture.
        noise-=design@np.linalg.lstsq(design,noise,rcond=None)[0]
        z=.3+.2*x-.1*y+.4*x*x+.15*x*y+.1*y*y+noise
        intercept,_=quadratic_intercept(np.c_[x,y,z],np.ones(len(x)))
        self.assertAlmostEqual(intercept,.3,places=12)

    def test_obstacle_offset_is_inward_and_skin_remains_fixed(self):
        n=17;x,y=np.meshgrid(np.arange(n,dtype=float),np.arange(n,dtype=float));v=np.c_[x.ravel(),y.ravel(),np.zeros(n*n)]
        triangles=[]
        for j in range(n-1):
            for i in range(n-1):
                a=j*n+i;triangles.extend(((a,a+1,a+n),(a+1,a+n+1,a+n)))
        mass,lap=surface_operator(v,np.array(triangles));free=(v[:,0]>2)&(v[:,0]<14)&(v[:,1]>2)&(v[:,1]<14)
        target=.35*np.cos(v[:,0]*1.9)*np.sin(v[:,1]*1.7)
        d,report=constrained_offset(target,mass,lap,free,2.)
        self.assertLessEqual(d.max(),0);np.testing.assert_array_equal(d[~free],0)
        self.assertLess(d.min(),-.05);self.assertLess(report['relativeProjectedGradient'],1e-4)
        # A smooth correction couples neighbouring points; this is not clamp.
        self.assertGreater(np.max(abs(d[free]-np.minimum(target[free],0))),.02)


if __name__=='__main__':unittest.main()
