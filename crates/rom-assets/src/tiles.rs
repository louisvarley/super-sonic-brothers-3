//! Mega Drive 4bpp tiles (32 bytes each, two pixels per byte, high nibble first).

pub const BYTES_PER_TILE: usize = 32;

/// The inverse of the extractor's `Tiles.ToSheet`: rebuilds raw tile bytes from sheet pixels.
/// Pixel value = palette line * 16 + colour; the low nibble is the tile data.
pub fn from_sheet(pixels: &[u8], width: usize, tile_count: usize) -> Vec<u8> {
    let tiles_per_row = width / 8;
    let mut data = vec![0u8; tile_count * BYTES_PER_TILE];
    for t in 0..tile_count {
        let ox = (t % tiles_per_row) * 8;
        let oy = (t / tiles_per_row) * 8;
        for y in 0..8 {
            for x in (0..8).step_by(2) {
                let hi = pixels[(oy + y) * width + ox + x] & 0xF;
                let lo = pixels[(oy + y) * width + ox + x + 1] & 0xF;
                data[t * BYTES_PER_TILE + y * 4 + x / 2] = (hi << 4) | lo;
            }
        }
    }
    data
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn sheet_round_trip_of_one_tile() {
        // One tile, row 0: pixels 0,1,2,3,4,5,6,7 -> bytes 0x01, 0x23, 0x45, 0x67.
        let mut pixels = vec![0u8; 8 * 8];
        for (x, pixel) in pixels.iter_mut().take(8).enumerate() {
            *pixel = x as u8;
        }
        let data = from_sheet(&pixels, 8, 1);
        assert_eq!(&data[..4], &[0x01, 0x23, 0x45, 0x67]);
        // Palette line 2 does not leak into the raw nibbles.
        pixels[0] = 0x20 | 0x0A;
        let data = from_sheet(&pixels, 8, 1);
        assert_eq!(data[0], 0xA1);
    }
}
