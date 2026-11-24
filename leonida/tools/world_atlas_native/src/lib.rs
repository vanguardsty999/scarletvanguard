use std::{mem, slice};

const MAX_PAIRS: usize = 8 * 1024 * 1024;
const MAX_MASK_WORDS: usize = 8 * 1024 * 1024;

#[repr(C)]
#[derive(Clone, Copy)]
pub struct Match {
    clear: i32,
    placed: i32,
    overlap: f64,
}

fn population(mask: &[u64]) -> u32 {
    mask.iter().map(|word| word.count_ones()).sum()
}

#[inline(always)]
fn intersection(tile: &[u64], cell: &[u64]) -> u32 {
    tile.iter()
        .zip(cell)
        .map(|(tile_word, cell_word)| (tile_word & cell_word).count_ones())
        .sum()
}

#[inline(always)]
fn overlap(tile: &[u64], cell: &[u64], total: u32) -> f64 {
    let shared = intersection(tile, cell);
    if shared == 0 {
        return 0.0;
    }
    f64::from(shared) / f64::from(total - shared)
}

#[inline(always)]
/// Scores every cell for ranking, but aligns only reachable slots. In row `i`, offset `k` is
/// absolute slot `i + k`, so a predecessor's offset can be at most `k`. Strict comparisons
/// keep the first tied predecessor and endpoint, like the Python reference.
fn match_masks(tiles: &[u64], cells: &[u64], words: usize, margin: f64, out: &mut [Match]) {
    let window = cells.len() / words - out.len() + 1;
    let cell_sizes: Vec<_> = cells.chunks_exact(words).map(population).collect();
    let tile_sizes: Vec<_> = tiles.chunks_exact(words).map(population).collect();
    let mut scores = vec![0.0; cell_sizes.len()];
    let mut prev = vec![0.0; window];
    let mut back = vec![0; (out.len() - 1) * window];

    for (row, tile) in tiles.chunks_exact(words).enumerate() {
        let (mut first, mut second, mut winner) = (-1.0, -1.0, 0);
        for (slot, cell) in cells.chunks_exact(words).enumerate() {
            let score = overlap(tile, cell, tile_sizes[row] + cell_sizes[slot]);
            scores[slot] = score;
            if score > first {
                (first, second, winner) = (score, first, slot);
            } else if score > second {
                second = score;
            }
        }
        out[row].clear = if first - second > margin {
            winner as i32
        } else {
            -1
        };

        let reachable = &scores[row..row + window];
        if row == 0 {
            prev.copy_from_slice(reachable);
        } else {
            let args = &mut back[(row - 1) * window..row * window];
            let (mut best, mut best_at) = (f64::NEG_INFINITY, 0);
            for (slot, ((previous, score), arg)) in
                prev.iter_mut().zip(reachable).zip(args).enumerate()
            {
                if *previous > best {
                    (best, best_at) = (*previous, slot);
                }
                *previous = best + score;
                *arg = best_at as u32;
            }
        }
    }

    let mut offset = 0;
    for (slot, score) in prev.iter().enumerate().skip(1) {
        if *score > prev[offset] {
            offset = slot;
        }
    }
    for row in (0..out.len()).rev() {
        let slot = row + offset;
        let tile = &tiles[row * words..][..words];
        let cell = &cells[slot * words..][..words];
        let score = overlap(tile, cell, tile_sizes[row] + cell_sizes[slot]);
        out[row].overlap = score;
        out[row].placed = if score > 0.0 { slot as i32 } else { -1 };
        if row > 0 {
            offset = back[(row - 1) * window + offset] as usize;
        }
    }
}

#[cfg(target_arch = "x86_64")]
#[target_feature(enable = "popcnt")]
unsafe fn match_masks_popcnt(tiles: &[u64], cells: &[u64], words: usize, margin: f64, out: &mut [Match]) {
    if words == 16 {
        match_masks(tiles, cells, 16, margin, out);
    } else {
        match_masks(tiles, cells, words, margin, out);
    }
}

fn valid_buffer<T>(pointer: *const T, len: usize) -> bool {
    !pointer.is_null() && pointer.is_aligned() && len <= isize::MAX as usize / mem::size_of::<T>()
}

/// # Safety
/// Both input pointers must point to readable buffers of the stated lengths.
/// The output must be initialized, writable, and not alias either input.
#[no_mangle]
pub unsafe extern "C" fn leonida_atlas_match_v1(
    tiles: *const u64,
    tiles_len: usize,
    cells: *const u64,
    cells_len: usize,
    words: usize,
    margin: f64,
    out: *mut Match,
    out_len: usize,
) -> i32 {
    if !(1..=4096).contains(&words)
        || tiles_len > MAX_MASK_WORDS
        || cells_len > MAX_MASK_WORDS
        || out_len == 0
        || out_len.checked_mul(words) != Some(tiles_len)
        || cells_len % words != 0
        || !margin.is_finite()
        || margin < 0.0
        || !valid_buffer(tiles, tiles_len)
        || !valid_buffer(cells, cells_len)
        || !valid_buffer(out, out_len)
    {
        return -1;
    }
    let slots = cells_len / words;
    if slots < 2 || slots < out_len || out_len > MAX_PAIRS / slots {
        return -1;
    }
    let tiles = unsafe { slice::from_raw_parts(tiles, tiles_len) };
    let cells = unsafe { slice::from_raw_parts(cells, cells_len) };
    let out = unsafe { slice::from_raw_parts_mut(out, out_len) };
    #[cfg(target_arch = "x86_64")]
    if std::is_x86_feature_detected!("popcnt") {
        unsafe { match_masks_popcnt(tiles, cells, words, margin, out) };
        return 0;
    }
    match_masks(tiles, cells, words, margin, out);
    0
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn ties_take_the_first_reachable_path_without_creating_anchors() {
        let mut out = [Match {
            clear: -1,
            placed: -1,
            overlap: 0.0,
        }; 2];
        match_masks(&[7, 7], &[1, 2, 4], 1, 0.2, &mut out);
        assert_eq!(
            out.map(|entry| (entry.clear, entry.placed, entry.overlap)),
            [(-1, 0, 1.0 / 3.0), (-1, 1, 1.0 / 3.0)]
        );
    }

    #[test]
    fn invalid_lengths_leave_output_alone() {
        let mut out = Match {
            clear: 7,
            placed: 8,
            overlap: 0.5,
        };
        let status = unsafe {
            leonida_atlas_match_v1([1].as_ptr(), 1, [2, 1].as_ptr(), 2, 2, 0.2, &mut out, 1)
        };
        assert_eq!(status, -1);
        assert_eq!((out.clear, out.placed, out.overlap), (7, 8, 0.5));
    }
}
