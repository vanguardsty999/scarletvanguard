const MIN_NAME: usize = 3;
const MAX_NAME: usize = 200;
const TAIL: usize = 18;

fn printable(byte: u8) -> bool {
    (0x21..=0x7e).contains(&byte)
}

fn match_start(bytes: &[u8], start: usize, nul: usize) -> Option<usize> {
    if bytes[nul + 2..nul + 6] != bytes[nul + 10..nul + 14] {
        return None;
    }
    let floor = start.max(nul.saturating_sub(MAX_NAME));
    Some(
        bytes[floor..nul]
            .iter()
            .rposition(|&byte| !printable(byte))
            .map_or(floor, |previous| floor + previous + 1),
    )
}

#[cfg(target_arch = "x86_64")]
fn scan_blocks(bytes: &[u8], start: usize, end: usize) -> (Option<usize>, usize) {
    use std::arch::x86_64::*;

    let mut cursor = start;
    let mut carry = 0;
    while end - cursor >= 16 {
        let (nuls, names) = unsafe {
            let block = _mm_loadu_si128(bytes.as_ptr().add(cursor).cast());
            let nuls = _mm_movemask_epi8(_mm_cmpeq_epi8(block, _mm_setzero_si128()));
            let printable = _mm_and_si128(
                _mm_cmpgt_epi8(block, _mm_set1_epi8(0x20)),
                _mm_cmpgt_epi8(_mm_set1_epi8(0x7f), block),
            );
            (nuls as u32, _mm_movemask_epi8(printable) as u32)
        };
        let history = (names << MIN_NAME) | carry;
        carry = names >> (16 - MIN_NAME);
        let mut candidates = nuls & history & (history >> 1) & (history >> 2);
        while candidates != 0 {
            let nul = cursor + candidates.trailing_zeros() as usize;
            if let Some(found) = match_start(bytes, start, nul) {
                return (Some(found), cursor);
            }
            candidates &= candidates - 1;
        }
        cursor += 16;
    }
    (None, cursor)
}

fn find(bytes: &[u8], start: usize) -> Option<usize> {
    let end = bytes.len().checked_sub(TAIL)? + 1;
    if end.saturating_sub(start) <= MIN_NAME {
        return None;
    }
    #[cfg(target_arch = "x86_64")]
    let cursor = {
        let (found, cursor) = scan_blocks(bytes, start, end);
        if found.is_some() {
            return found;
        }
        cursor
    };
    #[cfg(not(target_arch = "x86_64"))]
    let cursor = start;

    (cursor.max(start + MIN_NAME)..end).find_map(|nul| {
        if bytes[nul] == 0 && bytes[nul - MIN_NAME..nul].iter().copied().all(printable) {
            match_start(bytes, start, nul)
        } else {
            None
        }
    })
}

#[no_mangle]
pub unsafe extern "C" fn leonida_shader_carve_find_v1(
    bytes: *const u8,
    len: usize,
    start: usize,
) -> usize {
    if bytes.is_null() || len > isize::MAX as usize || start > len {
        return usize::MAX;
    }
    let bytes = unsafe { std::slice::from_raw_parts(bytes, len) };
    find(bytes, start).unwrap_or(len)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn checks_every_block_alignment_and_tail_length() {
        for offset in 0..32 {
            let mut bytes = vec![0x80; offset];
            bytes.extend_from_slice(b"!~A");
            bytes.extend_from_slice(&[0; TAIL]);
            assert_eq!(find(&bytes, 0), Some(offset));
            assert_eq!(find(&bytes, offset), Some(offset));
            assert_eq!(find(&bytes, offset + 1), None);
            bytes.pop();
            assert_eq!(find(&bytes, 0), None);
        }
    }

    #[test]
    fn checks_max_name_and_backreference() {
        let mut bytes = vec![b'A'; MAX_NAME + 1];
        bytes.extend_from_slice(&[0; TAIL]);
        assert_eq!(find(&bytes, 0), Some(1));
        bytes[MAX_NAME + 1 + 10] = 1;
        assert_eq!(find(&bytes, 0), None);
    }

    #[test]
    fn distinguishes_no_match_from_refusal() {
        let bytes = b"abc";
        let scan =
            |start| unsafe { leonida_shader_carve_find_v1(bytes.as_ptr(), bytes.len(), start) };
        assert_eq!(scan(0), bytes.len());
        assert_eq!(scan(bytes.len()), bytes.len());
        assert_eq!(scan(bytes.len() + 1), usize::MAX);
        assert_eq!(
            unsafe { leonida_shader_carve_find_v1(std::ptr::null(), 0, 0) },
            usize::MAX
        );
    }
}
