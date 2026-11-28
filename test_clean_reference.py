"""Filtering must remove detail while preserving form and fixed geometry."""
import unittest

import numpy as np

from clean_reference import constraints, graph, local_quadratic


def patch(ripple):
    x,y=np.meshgrid(np.arange(-8.,9.),np.arange(-8.,9.))
    z=.002*(x*x+y*y)+ripple*np.cos(np.pi*x/2)*np.cos(np.pi*y/2)
    v=np.c_[x.ravel(),y.ravel(),z.ravel()]
    triangles=[]
    for j in range(16):
        for i in range(16):
            a=j*17+i;triangles.extend(((a,a+1,a+17),(a+1,a+18,a+17)))
    t=np.array(triangles);pinned=(abs(v[:,0])==8)|(abs(v[:,1])==8)
    return v,t,pinned


class CleanupTests(unittest.TestCase):
    def test_transition_does_not_release_distant_skin(self):
        v,t,_=patch(0.);_,metric=graph(v,t)
        groups=np.where(v[t].mean(axis=1)[:,0]>0,'top_smallclothes','top_skin')
        pinned,_,_,_=constraints(v,t,groups,metric,transition_mm=2.)
        self.assertTrue(pinned[v[:,0]<-3].all())
        self.assertTrue((~pinned[v[:,0]>2]).any())

    def filter(self,ripple):
        v,t,pinned=patch(ripple);average,metric=graph(v,t)
        clean,_,_=local_quadratic(v,t,average,metric,pinned,(~pinned).astype(float),7.,1.,
                                  np.array(['top_smallclothes']*len(t)))
        np.testing.assert_array_equal(clean[pinned],v[pinned])
        return v,clean

    def test_smooth_quadratic_centre_is_preserved(self):
        before,after=self.filter(0.)
        np.testing.assert_allclose(after[144],before[144],atol=1e-6)

    def test_oscillation_is_attenuated_with_fixed_boundary(self):
        before,after=self.filter(.5)
        self.assertLess(abs(after[144,2]),abs(before[144,2])*.8)
        self.assertLessEqual(np.linalg.norm(after-before,axis=1).max(),1.+1e-9)


if __name__=='__main__':unittest.main()
