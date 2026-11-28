"""Normal decoding, material-coordinate lineage and geometric integration."""
import unittest
import numpy as np
from normal_field import bilinear,tangent_normal,stored_frame
from normal_tessellation import refine
from reconstruct_normal_map import geometric,integrate_height


class NormalReconstructionTests(unittest.TestCase):
    def test_stored_handedness_flips_tangent_on_mirrored_island(self):
        n,t,b=stored_frame(np.array([[0.,0,1],[0.,0,1]]),np.array([[.5,0,.5,0],[.5,0,.5,1]]))
        np.testing.assert_allclose(t,[[1,0,0],[-1,0,0]])
        np.testing.assert_allclose(b,[[0,-1,0],[0,-1,0]])

    def test_rg_decoding_reconstructs_z_without_blue(self):
        rg=np.array([[.5,.5],[.8,.5],[.5,.2]])
        np.testing.assert_allclose(tangent_normal(rg),[[0,0,1],[.6,0,.8],[0,-.6,.8]],atol=1e-12)

    def test_texture_sampling_uses_texel_centres_and_repeat(self):
        image=np.array([[[0.,0],[1.,0]],[[0.,1],[1.,1]]])
        uv=np.array([[.25,.25],[.75,.75],[1.25,-.75],[.5,.5]])
        np.testing.assert_allclose(bilinear(image,uv),[[0,0],[1,1],[0,0],[.5,.5]])

    def test_edge_refinement_retains_exact_native_material_locations(self):
        v=np.array([[0.,0,0],[4,0,0],[0,4,0],[4,4,0]])
        t=np.array([[0,1,2],[1,3,2]])
        p=v[t];nv,nt,parent,bc=refine(v,t,np.array([.8,1.1]))
        np.testing.assert_allclose(nv[nt],np.einsum('fci,fij->fcj',bc,p[parent]))
        self.assertLessEqual(np.linalg.norm(nv[nt]-np.roll(nv[nt],1,axis=1),axis=-1).max(),1.1+1e-12)
        from repair_surface import boundaries
        self.assertEqual(boundaries(nt)['openBoundaryLoops'],1)

    def test_integration_reconstructs_unfiltered_relief(self):
        x,y=np.meshgrid(np.arange(9.),np.arange(9.))
        v=np.c_[x.ravel(),y.ravel(),np.zeros(81)];t=[]
        for j in range(8):
            for i in range(8):
                a=j*9+i;t.extend([[a,a+1,a+9],[a+1,a+10,a+9]])
        t=np.array(t);expected=v.copy()
        expected[:,2]=.15*np.sin(np.pi*x.ravel()/2)*np.sin(np.pi*y.ravel()/2)
        target=geometric(expected,t);directions=np.tile([0.,0,1.],(len(v),1))
        result=integrate_height(v,t,target,np.ones(len(t),bool),directions,1e-10)
        np.testing.assert_allclose(result,expected,atol=1e-7)


if __name__=='__main__':unittest.main()
