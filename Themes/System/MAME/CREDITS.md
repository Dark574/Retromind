# Arcade (MAME) system theme assets

## Original arcade cabinet artwork

- File: `Images/arcade-cabinet-frame.png`
- Creation: generated with OpenAI's built-in image generation tool from an original
  prompt; no external reference images were used.
- Design: a classic dark two-player upright cabinet with cyan and amber trim, red
  ball-top joysticks, six buttons per player, an abstract marquee, and a coin door.
  The background and screen opening use PNG transparency. The artwork contains no
  manufacturer logos, game artwork, or characters.
- License: distributed as part of Retromind under GPL-3.0-only, to the extent copyright
  applies.

All remaining visual elements and the localized game count are drawn in Avalonia XAML.
The system logo and description come from the user's library node.

## Generation prompt

Create a production-ready transparent PNG asset for a desktop game library's arcade/MAME
system theme. Use case: product-mockup. Asset: a single classic dark upright two-player
arcade cabinet, isolated, complete body and base fully visible, no environment. Portrait
image 1024 x 1536. Straight-on centered frontal view, symmetric, minimal perspective so
the screen is a perfectly front-facing rectangle, but the top of the control panel and
joystick bases are visible. Handsome photorealistic 3D product render, textured charcoal
black laminate, black molded screen bezel, restrained luminous cyan T-molding on the left
edge, warm amber-orange accents on the right, two red ball-top joysticks, six round buttons
per player (cyan, orange, cream), coin door and coin slots below. Broad cabinet proportions
with a large landscape 4:3 CRT screen, compact lower cabinet, subtle wear, premium soft
studio lighting, crisp material details. Top marquee contains ONLY abstract cyan and amber
geometric bands with a blank dark central area, no text or logos. Side panels have original
understated diagonal colored stripes. No branded game imagery, no characters, no lettering,
no logos, no watermark. Critical technical requirements: actual transparent alpha background
around the complete cabinet, no opaque background or checkerboard painted into the image.
The screen opening itself must ALSO be truly transparent alpha, an empty hole through the
cabinet graphic where the application will play video behind it; no glass, reflection,
screen image, glow, black fill or checkerboard in the screen hole. Screen hole should be a
large axis-aligned landscape 4:3 rectangle with lightly rounded corners, ideally spanning
approximately x=185..839, y=330..820 in the 1024x1536 image. Keep the bezel surrounding the
hole solid and detailed. Cabinet should occupy most of the image, roughly x=95..930,
y=45..1480, with transparent margins on every side. Keep the lower cabinet compact enough
to give the screen prominence. All physical parts, especially the joysticks, control panel,
and base must be fully inside the image. Return just the isolated cabinet asset.
