"""Body-only filtering and flap closure must protect unrelated evidence."""
import unittest
from types import SimpleNamespace
import numpy as np
from body_cleanup import filter_cloth_field,build_disk
from normal_field import tangent_normal
from test_clean_reference import patch
from repair_surface import boundaries


class BodyCleanupTests(unittest.TestCase):
    def make_field(self):
        x,y=np.meshgrid(np.arange(16),np.arange(16));a=.5+.2*((x+y)%2*2-1)
        texture=np.stack((a,a),axis=-1)
        return SimpleNamespace(maps=[texture.copy(),texture.copy(),texture.copy()],strength=[1.,1.,1.])

    def test_neutralisation_only_removes_cloth_relief(self):
        f=self.make_field();original=[m.copy() for m in f.maps];filter_cloth_field(f,'neutral')
        self.assertEqual(f.strength,[1.,0.,0.])
        for a,b in zip(f.maps,original):np.testing.assert_array_equal(a,b)
        np.testing.assert_allclose(tangent_normal(f.maps[1],0),np.broadcast_to([0,0,1.],(16,16,3)))
        self.assertEqual(f.settings['offsetMm'],0)

    def test_blur_preserves_skin_and_constant_cloth_field(self):
        f=self.make_field();skin=f.maps[0].copy();f.maps[2][:]=.61
        before=np.std(f.maps[1]);filter_cloth_field(f,'blur',2.)
        np.testing.assert_array_equal(f.maps[0],skin)
        np.testing.assert_allclose(f.maps[2],.61)
        self.assertLess(np.std(f.maps[1]),before*.1)

    def test_closure_keeps_boundary_and_medial_valley_pin(self):
        v,t,_=patch(0.);v[144,2]=-1.
        mask=np.max(abs(v[t][...,:2]),axis=(1,2))<=3
        p,nt,fixed,loop,_,_=build_disk(v,t,mask,1.,[144])
        np.testing.assert_array_equal(p[:len(fixed)],v[fixed])
        self.assertIn(144,fixed)
        self.assertEqual(boundaries(nt)['openBoundaryLoops'],1)


if __name__=='__main__':unittest.main()
