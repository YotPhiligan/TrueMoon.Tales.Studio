# Fixed Studio UI test fonts

Unmodified Noto Sans Regular and Noto Sans Symbols Regular, Noto Project Authors,
SIL Open Font License 1.1; the complete upstream license is [OFL.txt](OFL.txt).
Test assets only: not installed into Windows or shipped with the Studio app.

Upstream: https://github.com/notofonts/noto-fonts
Revision: `ffebf8c1ee449e544955a7e813c54f9b73848eac`.

| File | Upstream path | SHA256 |
|---|---|---|
| NotoSans-Regular.ttf | hinted/ttf/NotoSans/NotoSans-Regular.ttf | B85C38ECEA8A7CFB39C24E395A4007474FA5A4FC864F6EE33309EB4948D232D5 |
| NotoSansSymbols-Regular.ttf | hinted/ttf/NotoSansSymbols/NotoSansSymbols-Regular.ttf | 8F02F31959BBDF6061547A188248E13F84DC5FDD940326EC494675F453F072BB |

Downloaded directly from these paths at the pinned commit. All unregistered UI
families map to the fixed Noto Sans default; `Segoe UI Symbol` maps to Symbols.
No system fallback. Raster tests cover Cyrillic and the supplied symbols; they
do not establish emoji/shaping or physical display acceptance.
