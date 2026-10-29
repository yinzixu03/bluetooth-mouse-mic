# libsiren (M110 decoder build)

Upstream: https://github.com/kakaroto/libsiren

Revision: `c7724c03d4a6daf4d6dff9e917c14ce5071dce66`.

Author: Youness Alaoui. Original author and licence headers are retained in the source.
Upstream permits GNU Library GPL version 2 or later. This distribution uses
GNU Lesser GPL version 2.1 or later; see `COPYING`.

## Changes

Changes by M110 Mic contributors, 2026-10-30:

- `decoder.c`: select standard G.722.1 mode 0 (16 kbit/s, 16 kHz mono,
  40 input bytes and 320 output samples); normalize by sqrt(512)/22; check
  allocation, arguments and envelope indexes. Input frames use network byte order.
- `huffman.c`: apply the MSN category 5 adjustment at scale factor 1;
  scale generated noise with decoded coefficients; bound frame reads to
  320 bits; check envelope indexes before table access.
- `huffman.h`: expose bounded-reader status.

## Build

Run `tools/build.ps1 -CodecOnly` from the project root, supplying `-ZigPath`
when needed. The build links `common.c`, `huffman.c`, `dct4.c`, `rmlt.c`
and `decoder.c` into `support/m110_g7221.dll`.

The C# adapter serializes initialization, decoding and cleanup across instances
to coordinate the library's global bit reader. The DLL is dynamically loaded
and can be replaced with a compatible modified build. Matching source and
build instructions are included in `support/source.zip` in the runtime package.