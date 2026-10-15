# Third-party art — provenance

Everything under `Sky/` and `Terrain/` is **CC0** from [Poly Haven](https://polyhaven.com),
which means it is public domain: redistributable, no attribution owed, no credit debt to
retrofit at ship time. That is why these files are committed while the rest of the project's
third-party art is not.

`Trees/` is the exception, and a deliberate one. It is **CC BY 4.0** — redistribution is
permitted, but attribution is a condition of the licence. Its section below is that
attribution, and unlike everything else in this file it carries an obligation that has to
reach the game's own credits before ship.

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

## Trees — `Trees/`

**Not CC0.** This is the only folder in the project whose contents are not public domain, and
the only place a redistributed third-party asset is committed to the repository.

| | |
| --- | --- |
| Work | *Pine trees pack (lowpoly, game ready, LODs)* |
| Creator | LOLIPOP ([@lolipop_1707](https://sketchfab.com/lolipop_1707)) |
| Source | [sketchfab.com/…/pine-trees-pack-lowpoly-game-ready-lods](https://sketchfab.com/3d-models/pine-trees-pack-lowpoly-game-ready-lods-e1e9c07b8e2e445c943fec660beefba2) |
| Licence | [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/) |
| Retrieved | 2026-10-15, via the Sketchfab "FBX" download option (delivered as a zip) |
| Changes | listed below — CC BY 4.0 requires this to be stated |

### What CC BY 4.0 obliges, and where it lands

Four conditions, not courtesies:

1. **Credit the creator** — name LOLIPOP as the author.
2. **Link the licence** — the CC BY 4.0 URL above.
3. **Indicate changes** — the list below is that indication, and it has to stay accurate as
   these assets are edited. A stale changes list is a licence breach, not a documentation bug.
4. **Do not imply endorsement** — LOLIPOP has no relationship with this game. Credits must not
   read as though these trees were made *for* Dead Pines.

The licence permits commercial use and redistribution, so a public repository and a sold build
are both fine. **The first three conditions must reach the game's own credits before ship.**
This file is provenance for whoever opens the repository; it is not a substitute for crediting
the work where players can actually see it.

### Changes made

- **Mesh** — committed unmodified. One FBX holding 15 trees (big / large / medium / small /
  sapling, three variants each), every one with `LOD0`, `LOD1`, `LOD2` and a
  `Billboard_LOD3`, plus `Bark`, `Cluster` and 15 billboard materials.
- **Textures not committed** — the 30 per-tree billboard *normal* and *subsurface* maps, plus
  `man_ref.png` (a scale-reference photograph, not an asset) and `Checker_basecolor.png` (a
  placeholder). Unity's terrain billboard shader samples a single colour texture, so those 30
  maps have no input to reach; at roughly 25 MB they would have been dead weight in git history
  permanently. They are still in the download if a custom foliage shader ever wants them.
- **Import settings** — not a change to the work; see `TextureImportDefaults`, which sizes
  these from `/trees/` in the path and gives the billboards BC7 so their alpha survives.
- **Materials and prefabs** — authored by this project, not by the pack.

### Files

`Pine_pack.fbx`

`ab26f767d863fb88681eb78b5da1ca8b6f55004f2baf3bc778e0f88a6eb09c59`

#### `Bark` — trunk and branches

| Map | File | SHA-256 |
| --- | --- | --- |
| albedo | `Bark_basecolor.png` | `4424635fbce9615debf833775bf3e955a37fbed80efa3400b6ee875ad5fff5f2` |
| normal | `Bark_normal.png` | `a58080f9a1cf4980bc30dc6d86f1e5f57e916ff0c66b79b1242432cf94b5444c` |
| roughness | `Bark_roughness.png` | `5666994cfeac221efd48c1e723839c7cfcab26308bc1f8b121b0052be04eab29` |
| occlusion | `Bark_ambientocclusion.png` | `2ffa413e1774ce2f183bfb9f2bdd7d1430a389624e5bd2dc55618dec13173252` |

#### `Cluster` — needle foliage

| Map | File | SHA-256 |
| --- | --- | --- |
| albedo | `Cluster_full_basecolor.png` | `9d72716b7d3959f91a62c8951d4dec45907f2486b9032a58a4bfad9411dbef65` |
| normal | `Cluster_full_normal.png` | `d054d8fab9f12ac3b21547d22ce49c199c28627ef037dc5eedaa046492fc7196` |
| roughness | `Cluster_full_roughness.png` | `4f79b78b463096b103a7ffd7f82ce4a369e617845e7292abf1b63e16650f6066` |
| occlusion | `Cluster_full_ambientocclusion.png` | `18e8041a93fae3fe2724f3f3983ab7037d673ac60ed4203602b13c803973bd3b` |
| **opacity** | `Cluster_full_Opacity.png` | `40db9dd0978f423be52b6a34e46e2231ae7102bc928dd0dc59fbb405a8f39d73` |
| subsurface | `Cluster_full_SubsurfaceAmount.png` | `8ae85cd24dae03b8807579abedaded2b16b3e3b5663493cf164b1736028d424c` |

Opacity is the one that makes the foliage read as needles rather than as a solid card, and
Unity's stock URP Lit material has nowhere to put it — its alpha clipping reads the *albedo's*
alpha channel, and here opacity is a separate grayscale map. Pairing the two is the shader
work Phase 4 has to do; nothing in the pack does it for us.

#### Billboards — colour only, one per tree

`Pine_big_1` `8ddacec44ebeb35ffb3ed309e549be1cfe4b01e5dde1ca0bddb512e5edd3a976`
`Pine_big_2` `6802a18f21eae214f89d116e9901113029d773868a091bec473330428bab312c`
`Pine_big_3` `7737480a682f31634b4898dbb1ae449fa53d85c8d96c9d789542e8dbd02c68bd`
`Pine_large_1` `922144d400f5cddce6e14bf09f232a5840334807be417317849eebd2fe4623f6`
`Pine_large_2` `4bd11566cc2d2b85078f0ccf5746b2b3ab9c302b53902af3c49e6b11b8d480ab`
`Pine_large_3` `4ee96cece1f66b5184f2e2586bb4715435dd9b16eeab28bfaf524da50bbbabe1`
`Pine_medium_1` `fbe2f5b6b8b62d270d5d4f29d7dc0e5ae1cda8377bffbfa5f013add927dfc59f`
`Pine_medium_2` `711a16ab099d775f32abbb57eef8d6c7b43b3fb733993eda7a5d978195bebc8a`
`Pine_medium_3` `3ee9ff2d306e43dc595c104666f38972c7e908cdc8a9c3d5a9df9a4af3894707`
`Pine_small_1` `29485c2a2e672ea09b0b5da091150c99fc5763bb4b9603f3cbcf90ebe3ad4e64`
`Pine_small_2` `20fcca9ac011da323f79fc45e84800943d02b05c2d1445c8819cc62ad255bdf0`
`Pine_small_3` `e9ae6f870aae5e59f66657d48c42667daa62d719978e8e668de9b603bb71723d`
`Pine_sapling_1` `c8a1f2d91af763007ab0d5391670ad77c1d8400b3ed58f5e6b63332af7d85e6b`
`Pine_sapling_2` `bd08dec6d53d7c65a789b1228b9fa35533724a8175b95f8120787f6ef15cdb98`
`Pine_sapling_3` `ecf9e0e6055e0dba07003aad5cd4c9a064975b17d5e3429cf938ebf14f5bf7a1`

Each is preceded by `Pine_<size>_<n>_Billboard_Color.png` in `Trees/Textures/`.

### Why these trees, and what they cost

The pack was chosen over two alternatives, and the reasons are worth keeping because the
trade-offs will come up again:

- Poly Haven has CC0 fir and pine — no attribution debt at all, the same licence as everything
  else here. But its tree meshes are 7.8M–17.4M triangles in 249 MB FBX files, they ship no
  LODs and no billboards, and **Unity has no mesh decimation API** (`MeshUtility.Optimize`
  reorders vertices, it does not reduce them). Taking that route meant installing Blender and
  building a decimate → LOD → imposter pipeline before a single tree landed.
- Asset Store conifer packs are Unity-native and need no import work at all, but the EULA
  permits shipping and not redistribution, so they could never be committed and the repository
  could never rebuild the forest.

This pack is neither. It is committable *and* terrain-ready, at the cost of one attribution
obligation. Its LOD chains and billboards are the reason it won: everything else on the list
needed that work done by hand.
