# Third-party art — provenance

Everything in this folder is **CC0** from [Poly Haven](https://polyhaven.com), which means it
is public domain: redistributable, no attribution owed, no credit debt to retrofit at ship
time. That is why these files are committed while the rest of the project's third-party art
is not.

The Mixamo rigs, the Sonniss audio and anything from the Asset Store are **not** here and must
not be — their licences cover shipping inside a build, not redistribution. See the asset
licence section in the repository's `.gitignore`.

## Why the hashes

Poly Haven revises assets, and a filename is not a guarantee of content. The SHA-256 below is
what this project actually built against. If you re-download and the hash differs, the art has
changed under you — that is worth knowing before you spend an afternoon wondering why the
terrain looks different.

It also means these files are the test case for the fetch-manifest idea: if the collection ever
grows past what belongs in git, this table becomes `tools/fetch-assets.ps1` and the files come
out of the repository.

## Sky — `Sky/`

| File | Source | SHA-256 |
| --- | --- | --- |
| `kloofendal_overcast_puresky_4k.hdr` | [kloofendal_overcast_puresky](https://polyhaven.com/a/kloofendal_overcast_puresky) | `77ade807bd208195073a51b970dea574fa70cd80cf666aeedf5cd416ab5c8abd` |

Chosen against D35's requirements, which rule out most of Poly Haven's overcast library: the
sky has to be **pure sky** (no baked treeline — fog and terrain own the horizon here) and
**neutral** (dusk comes from the colour grade, not the HDRI). A sunset overcast has a bright
patch where the sun is, which is a visible sun by implication, and §7 says there is no visible
sun or moon.

Used as the skybox on a `Skybox/Panoramic` material. **Texture Shape must stay 2D** — importing
it as a Cubemap is the classic way this breaks, and it fails by rendering something wrong rather
than by erroring. The lat-long setting lives on the material, not the texture.

## Terrain — `Terrain/`

All 2K (2048²), JPEG, which is §2's size for terrain and hero assets only. `TextureImportDefaults`
picks the size up automatically from `/terrain/` in the path. Imported as BC1 (diffuse) and BC5
(normal).

| Layer | Asset | Map | SHA-256 |
| --- | --- | --- | --- |
| 0 — forest floor | [forest_ground_04](https://polyhaven.com/a/forest_ground_04) | diffuse | `24b8aaf4c8547305d80b0e029ec365219ab1b90748d4ca5651bdc9e75bfde5e6` |
| | | normal | `b37ac799ed410976541e0011c246fca254e74eaa3f096ae7a7d74178db5a6d9b` |
| 1 — leaf litter | [forest_leaves_04](https://polyhaven.com/a/forest_leaves_04) | diffuse | `a4f964eb32909ef9b41dbf4000bde142995711b7b755850f6523eb4d99c4a5d4` |
| | | normal | `4870de640c043377b2f992337477cab9ff26f915b6cd5302cf2cb41d94b0c339` |
| 2 — mud | [brown_mud_02](https://polyhaven.com/a/brown_mud_02) | diffuse | `5c735b1df784ab1990f4e209c55d27fa4e7a19508e40484ed5aa783266783726` |
| | | normal | `3d6eec0adba8fd514d5634c56f806ad40206975c3ef17de1e2468b9e3ebcf719` |
| 3 — moss and rock | [mossy_rock](https://polyhaven.com/a/mossy_rock) | diffuse | `98ecbd1d19953433f5909b79958992b1da2b45addbee772d1d4c27287f368499` |
| | | normal | `78c05b52eb78617fcce4fde7a9ac4872a9725fb2d13aff33248cbdb9c6330563` |

`forest_ground_04` and `forest_leaves_04` are both from Poly Haven's curated
[pine_forest](https://polyhaven.com/collections) collection, which is why they sit together
without fighting.

### Normals are `nor_gl`, not `nor_dx`

Poly Haven ships both conventions. Unity expects **OpenGL** — green channel up. The DirectX
variant would light every surface inside out, and nothing would report an error.

### There is no mask map

Poly Haven's `arm` map packs AO/Roughness/Metallic, which is HDRP's layout. URP's mask map
expects Metallic/AO/Smoothness in a different channel order, so the ARM map cannot be dropped
in. M0 uses a scalar smoothness per layer instead — the terrain only needs one number, and a
wet forest floor's is near zero anyway. Revisit if terrain ever needs per-texel roughness.
