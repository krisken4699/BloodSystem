# Wound soak / bleed-down stains — spec

Ken's spec, 2026-08-06. Verbatim intent captured before implementation. Not yet built.

## The rejection

Flat quads are out. A quad is straight; armour and bodies are curved, so a quad reads
as "a PNG stuck on a curved surface". Wants the stain to actually follow the surface —
painted into the texture / UV rather than floated on a card.

## Look

- **Not opaque.** Soft circle, and the *middle* is not fully opaque either — a soak,
  not a sticker.
- Blood spreads outward from the entry wound and keeps growing slowly.

## Clothed / armoured target

1. Stain grows outward into a circle around the entry wound.
2. It also slowly drips DOWN.
3. Final shape: circle at the top with a taper running down — an upside-down teardrop.
4. Reads as fabric soaking through.

## Bare / unclothed target

- No stain on clothes, because there are none — the stain goes on the body itself.
- Not a wide soak. A **clearer, narrower trickle** running down the skin like a river.
- Explicitly NOT the current behaviour of a physical blood droplet popping out.
- The trickle must **continue across body segments** — it flows from one segment onto
  the next rather than stopping at a seam.

## Direction

- Runs along **world down**, not body-local down.
- If the sosig is lying down, the blood trickles down its side, not along its body axis.
- So direction has to be re-evaluated against gravity, not baked from the bone's
  orientation at spawn.

## Open technical questions (must be answered before building)

1. Can a hit's UV be obtained? `RaycastHit.textureCoord` needs a `MeshCollider`;
   sosig wearables and links may use primitive colliders, which would rule it out.
2. Are wearable/body renderers `SkinnedMeshRenderer`? That decides whether a
   mesh-conforming decal has to re-bake as the body moves.
3. Is per-instance texture painting affordable — cloned material + `RenderTexture`
   per sosig — or does a mesh-conforming decal built from the target's own triangles
   give the same "follows the surface" result far cheaper?
4. How is "is this link clothed?" determined at the hit site, to pick soak vs trickle?

## Config

One toggle, consistent with the thermal section. No knobs.
