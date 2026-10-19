//! Reads the extractor's 8-bit indexed PNGs and returns their palette indices.
//!
//! The legacy `IndexedPng.Read` only accepts 8-bit indexed PNGs with filter 0. The `png` crate handles the filters,
//! but the colour type/bit depth are checked here so an accidentally re-encoded sheet fails loudly.

pub fn read_indexed(bytes: &[u8]) -> Result<(u32, u32, Vec<u8>), String> {
    let decoder = png::Decoder::new(bytes);
    let mut reader = decoder
        .read_info()
        .map_err(|e| format!("PNG header: {e}"))?;
    if reader.info().color_type != png::ColorType::Indexed
        || reader.info().bit_depth != png::BitDepth::Eight
    {
        return Err(format!(
            "expected an 8-bit indexed PNG, got {:?}/{:?}",
            reader.info().color_type,
            reader.info().bit_depth
        ));
    }
    let mut buffer = vec![0; reader.output_buffer_size()];
    let info = reader
        .next_frame(&mut buffer)
        .map_err(|e| format!("PNG data: {e}"))?;
    let pixels = buffer[..(info.width as usize * info.height as usize)].to_vec();
    Ok((info.width, info.height, pixels))
}
