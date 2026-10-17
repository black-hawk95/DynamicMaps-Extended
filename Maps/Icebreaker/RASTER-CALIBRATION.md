# Original Icebreaker raster registration — beta 3

The previous rectangle was [-65.5,-64.5]..[77,67.4]. It placed the recorded
helipad position above the visible ship. Do not reuse that rectangle.

Artwork and high-resolution tile payloads remain unchanged. The raster canvas
is 9121 x 14774, including transparent padding; it is not the SVG canvas.
The corrected affine registration is:

    pixelX = 5024 - 47.4 * worldX
    pixelY = 8432 + 49 * worldZ

This registration was estimated from visible door hinges on the original
Accommodation Lower artwork and matching recorded Icebreaker door positions.
The three measurement residuals are approximately 0.03, 0.03 and 0.04 metres.
These are fitting residuals, not an independent accuracy guarantee. Artwork
pixel measurements are manual. Officers' Deck door/patrol overlays were also
visually inspected; no independent survey across every deck was performed.

Known limits: this is an offline calibration candidate, not a live-raid verified
alignment. The helipad zone describes a volume, not an exact painted pad centre.
It was used as a gross location check, not an exact control point. Raster floor
content can differ from the installed game's geometry. No claim of exact 1:1
registration or sub-metre accuracy across the entire ship is made.

RE3MR / QuestCodex uses its own unchanged coordinate bounds, with SVG full-canvas
compensation. Both styles share sixteen native floor slots; Engine and Fuel
have two raster subfloors mapped to one SVG deck each. Duplicate vector
underlays are suppressed without removing markers or changing their floor status.

Stable map viewport bounds keep transparent margins from changing zoom limits
when selecting another style. Markers, trails and quests remain owned by
DynamicMaps. No live game installation was modified by this build.
