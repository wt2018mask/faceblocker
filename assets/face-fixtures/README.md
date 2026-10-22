# Licensed face fixture

`astronaut.png`: NASA Eileen Collins portrait, public domain according to scikit-image's authoritative dataset documentation:
https://github.com/scikit-image/scikit-image/blob/v0.24.0/skimage/data/_fetchers.py#L355-L373

Source: https://raw.githubusercontent.com/scikit-image/scikit-image/v0.24.0/skimage/data/astronaut.png
SHA-256: 88431CD9653CCD539741B555FB0A46B61558B301D4110412B5BC28B5E3EA6CB5

Manual fixture face box: x178,y64,w94,h112 in 512x512 original. This is a coarse face annotation, not per-pixel segmentation ground truth. Generated copies/scales/crops/video use the same subject. They do not measure demographic, pose or real-world accuracy.

`tools/prepare_face_fixture.py` uses development-only OpenCV/NumPy to create local VP8 video (160 frames/20fps) and a browser page under ignored artifacts. The .NET application has no OpenCV runtime dependency. Preparing media never opens a window. The bounded interactive script requires explicit `-Interactive` and a browser path; user authorization is required before running it.
