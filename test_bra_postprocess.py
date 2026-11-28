"""Local patch preservation and centreline estimates ignore replaced infill."""
import unittest
import numpy as np
from repair_surface import loop_of_patch
from postprocess_bra import valley_guide,build_patch


def grid(x,y,z):
    xx,yy=np.meshgrid(x,y);v=np.c_[xx.ravel(),yy.ravel(),z(xx,yy).ravel()];t=[];n=len(x)
    for j in range(len(y)-1):
        for i in range(n-1):
            a=j*n+i;t.extend(((a,a+1,a+n),(a+1,a+n+1,a+n)))
    return v,np.array(t)


class BraPostprocessTests(unittest.TestCase):
    def test_valley_guide_uses_surroundings_not_bad_central_infill(self):
        v,t=grid(np.arange(-40.,41.,4),np.arange(0.,101.,5),lambda x,y:100-.2*y+.4*np.sqrt(x*x+16))
        c=v[t].mean(1);mask=(abs(c[:,0])<20)&(c[:,1]>10)&(c[:,1]<90);loop=loop_of_patch(t,mask)
        damaged=v.copy();inside=(abs(v[:,0])<8)&(v[:,1]>20)&(v[:,1]<80)
        damaged[inside,2]+=7*np.sin(v[inside,1]/7)
        guide,_=valley_guide(v,t,loop);other,_=valley_guide(damaged,t,loop)
        np.testing.assert_allclose(guide,other,atol=1e-10)
        self.assertLess(guide[:,2].max(),120)

    def test_seam_reconstruction_preserves_tilted_plane_and_boundary(self):
        v,t=grid(np.arange(-5.,6.),np.arange(-5.,6.),lambda x,y:.2*x-.1*y)
        c=v[t].mean(1);mask=(abs(c[:,0])<2)&(abs(c[:,1])<2)
        p,tri,loop,_,_,_=build_patch(v,t,mask,'seam')
        np.testing.assert_array_equal(p[:len(loop)],v[loop])
        np.testing.assert_allclose(p[:,2],.2*p[:,0]-.1*p[:,1],atol=1e-10)


if __name__=='__main__':unittest.main()
