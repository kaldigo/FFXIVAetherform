"""Tight coverage reduces inflated height while respecting the outer shape."""
import unittest
import numpy as np
from scipy.sparse import csr_matrix,eye
from tighten_af_genital import coverage_rows,qp_cover,overlay_samples,crest_heights,hidden_peak_mask
from prepare_af_fitting_genital import side_slope_model


class TightCoverTests(unittest.TestCase):
    def test_lowers_an_inflated_bridge_to_the_coverage_limit(self):
        A=csr_matrix([[1.,0],[0,1],[.5,.5]])
        x,report=qp_cover(eye(2,format='csc'),np.zeros(2),A,np.array([1.,1,2]),np.array([3.,3.]))
        self.assertTrue(report['converged']);np.testing.assert_allclose(x,[2,2],atol=.002)

    def test_outermost_layer_controls_coverage(self):
        x,report=qp_cover(eye(1,format='csc'),np.zeros(1),csr_matrix([[1.],[1.]]),np.array([-10.,1.]),np.array([3.]))
        self.assertTrue(report['converged']);self.assertAlmostEqual(x[0],1,delta=.002)

    def test_coverage_uses_the_actual_mesh_triangles(self):
        uv=np.array([[0.,0],[1,0],[1,1],[0,1]]);t=np.array([[0,1,2],[0,2,3]]);points=np.array([[.2,.1],[.5,.5],[.1,.8],[2,2]])
        A,missing=coverage_rows(uv,t,points);np.testing.assert_array_equal(missing,[3])
        np.testing.assert_allclose((A@(2+3*uv[:,0]-5*uv[:,1]))[:3],2+3*points[:3,0]-5*points[:3,1],atol=1e-12)

    def test_flat_prior_does_not_invent_a_central_dome(self):
        x,s=np.meshgrid(np.linspace(-24,24,25),np.linspace(-30,30,31));x,s=x.ravel(),s.ravel();p=np.c_[x,s,2-.02*x*x+.01*s*s]
        n=np.c_[.04*x,-.02*s,np.ones(len(x))];n/=np.linalg.norm(n,axis=1,keepdims=True)
        model,_,_=side_slope_model(p,n,np.ones(len(x)),transverse_curvature=False)
        values=model(np.array([[-5.,0],[0,0],[5,0]]));np.testing.assert_allclose(values,values[1],atol=1e-10);self.assertLess(values[1],2)

    def test_overlay_includes_crossing_edge_heights(self):
        p=np.array([[0.,0,0],[1,0,0],[1,1,0],[0,1,0],[.3,.5,1.]])
        uv=p[:,:2].copy();uv[4]=[.7,.5];t=np.array([[0,1,4],[1,2,4],[2,3,4],[3,0,4]])
        points=overlay_samples(p,t,uv);expected=np.array([.5,5/14,5/7])
        self.assertLess(np.linalg.norm(points-expected,axis=1).min(),1e-8)

    def test_crest_clips_sections_to_the_requested_strip(self):
        p=np.array([[-2.,-1,0],[2,-1,4],[0,1,0]])
        np.testing.assert_allclose(crest_heights(p,np.array([[0,1,2]]),[0.],half_width=.5),[1.5],atol=1e-12)

    def test_hidden_peak_can_clip_without_forcing_a_surface_bump(self):
        points=np.array([[0.,0,0],[0,20,1.]])
        required=~hidden_peak_mask(points);A=eye(2,format='csr');P=csr_matrix([[2.,-1],[-1,2.]])
        h,report=qp_cover(P,np.zeros(2),A[required],points[required,2],np.ones(2))
        self.assertTrue(report['converged']);np.testing.assert_allclose(h,0,atol=.002)
        self.assertLess(h[1],points[1,2]-.9)


if __name__=='__main__':unittest.main()
