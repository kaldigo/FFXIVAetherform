"""Separate displacement detail without losing constant shape or physical scale."""
import unittest
import numpy as np
from separate_body_shape import surface_operator,low_frequency,compose
from test_clean_reference import patch


class BodySeparationTests(unittest.TestCase):
    def test_constant_shape_and_physical_scale_are_preserved(self):
        v,t,_=patch(0.);v[:,2]=0
        mass,lap=surface_operator(v,t)
        np.testing.assert_allclose(low_frequency(np.full(len(v),2.3),mass,lap,4.),2.3,atol=1e-12)
        signal=np.cos(v[:,0]/7)+.2*np.cos(v[:,0]*np.pi)
        result=low_frequency(signal,mass,lap,3.)
        scaled_mass,scaled_lap=surface_operator(v*2,t)
        np.testing.assert_allclose(result,low_frequency(signal,scaled_mass,scaled_lap,6.),atol=1e-12)

    def test_fine_variation_is_attenuated_more_than_broad_curve(self):
        v,t,_=patch(0.);v[:,2]=0;mass,lap=surface_operator(v,t)
        broad=np.cos(np.pi*v[:,0]/16);fine=np.cos(np.pi*v[:,0])
        def ratio(signal):
            filtered=low_frequency(signal,mass,lap,3.)
            return np.sqrt(np.average(filtered**2,weights=mass)/np.average(signal**2,weights=mass))
        self.assertLess(ratio(fine),ratio(broad)*.2)

    def test_composition_preserves_outer_skin_and_explicit_inward_step(self):
        neutral=np.zeros((3,3));raw=neutral+np.array([0.,0,2.]);direction=np.tile([0.,0,1.],(3,1))
        p=compose(raw,neutral,direction,np.full(3,1.5),np.array([0.,.5,1.]),.5)
        np.testing.assert_array_equal(p[0],raw[0])
        np.testing.assert_allclose(p[:,2],[2.,1.5,1.])


if __name__=='__main__':unittest.main()
