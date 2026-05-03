# -*- coding: utf-8 -*-
"""
把键盘谱（原神诗琴字母谱）转成标准 MIDI 文件。

键位映射按「风物之诗琴 / 镜花之琴」的 C 大调 21 键布局
（与 Services/Instruments.cs 里的 CMajor21Keys 完全一致）：

  低音 Z X C V B N M  ->  C3 D3 E3 F3 G3 A3 B3
  中音 A S D F G H J  ->  C4 D4 E4 F4 G4 A4 B4
  高音 Q W E R T Y U  ->  C5 D5 E5 F5 G5 A5 B5

谱面解析规则：
  * 每一个「字母」或「括号组」算作 1 个节奏单位（默认八分音符）。
  * 括号里的字母同时弹响，形成和弦。
  * 行内的空格只是视觉分隔，不产生额外停顿。
  * 连续两个空行 / 新段落标题行之间会插入 1 个四分音符的停顿。
  * `#` 开头的行是注释，不会发声。
"""

import os
import struct

# ---------------------------------------------------------------------------
# 键位 -> MIDI 音高
# ---------------------------------------------------------------------------
KEYMAP = {
    'Z': 48, 'X': 50, 'C': 52, 'V': 53, 'B': 55, 'N': 57, 'M': 59,
    'A': 60, 'S': 62, 'D': 64, 'F': 65, 'G': 67, 'H': 69, 'J': 71,
    'Q': 72, 'W': 74, 'E': 76, 'R': 77, 'T': 79, 'Y': 81, 'U': 83,
}

# ---------------------------------------------------------------------------
# 谱面内容
# ---------------------------------------------------------------------------
TAB = r"""
# === 前奏 A ===
(CN)(CN)(XB) (CN)(CN)(XB) (CN)(CN)(XB) (CN) (CA)
(CN)(CN)(XB) (CN)(CN)(XB) (CN) (CA) (CS) (CD)

# === 前奏 B 第一段 ===
SD NBNB SD NBNB
SD NBNB A MAM NB
SD NBNB SD NBNB
SDGQ JQJ HGD
SD NBNB SD NBNB
SD NBNB A MAM NB
N BNA NAS SDGQDG Q JQJ HGH GHQ

# === 前奏 B 第二段 ===
WE HGHG WE HGHG
WE HGHG Q JQJ HG
WE HGHG WE HGHG
W E T Y U Y T E
WE HGHG WE HGHG
WEHGH G QJQJ HG
EWET YTEW H Q W E
(DH)(DH)(SG)(DH)

# === 主歌 ===
N N BNAAS N N BNBCB
N N BNASD D SDS A N
N NN BBNAS N N BNBCB
N N BBNAS D SDS A N

# === Pre-Chorus ===
AAAA MMMM NNNN BBBB B NB CXC
VV(VN) (BS)(BM) (NA) MB (NS)D
AAAA MMMM NNNN BBBB B NB CXC CB
(VN)(VN) (VN) A S MMMMMMMMM NA
(NS)(NS)(ND) (ND)DDDD (ND) (SG) HG SAD NA
(NS)(NS)(ND) (ND)DDDD (ND) F DFD SAA NA
(NS)(NS)(ND) (ND)DDDD (ND) (SG) HG SAD NA
(NF)FFF (ND)DDD (NS)SSS (NA)AAA (NS) DS NBN NA
(NS)(NS)(ND) (ND)DDDD (ND) (SG) HG SAD NA
(NS)(NS)(ND) (ND)DDDD (ND) F DFD SAA NA
(NS)(NS)(ND) (ND)DDDD (ND) (SG) HG SAD NA
(NF)FFF (ND)DDD (NS)SSS (NA)AAA (NS)(NA)(ND)(NG)(NH)

# === 尾奏 (同前奏 B) ===
SD NBNB SD NBNB
SD NBNB A MAM NB
SD NBNB SD NBNB
SDGQ JQJ HGD
SD NBNB SD NBNB
SD NBNB A MAM NB
N BNA NAS SDGQDG Q JQJ HGH GHQ

WE HGHG WE HGHG
WE HGHG Q JQJ HG
WE HGHG WE HGHG
W E T Y U Y T E
WE HGHG WE HGHG
WEHGH G QJQJ HG
EWET YTEW H Q W E
(DH)(DH)(SG)(DH)

# === 收尾句 ===
(CN)(CN)(CA)(CN)
"""

# ---------------------------------------------------------------------------
# 解析：返回 [(token_chord_or_None_for_rest), ...]
# None 代表一个休止符
# ---------------------------------------------------------------------------
def parse_tab(text):
    lines = text.splitlines()
    tokens = []
    prev_blank = True  # 开头不需要额外 rest
    for raw in lines:
        line = raw.strip()
        if not line:
            if not prev_blank:
                tokens.append(None)      # 段落停顿 (1 个单位)
                tokens.append(None)
            prev_blank = True
            continue
        if line.startswith('#'):
            if not prev_blank:
                tokens.append(None)
                tokens.append(None)
            prev_blank = True
            continue
        prev_blank = False

        i = 0
        while i < len(line):
            ch = line[i]
            if ch.isspace():
                i += 1
                continue
            if ch == '(':
                j = line.find(')', i)
                if j < 0:
                    # 未闭合括号，尽量容错
                    j = len(line)
                chord = tuple(c for c in line[i+1:j].upper() if c in KEYMAP)
                if chord:
                    tokens.append(chord)
                i = j + 1
            else:
                up = ch.upper()
                if up in KEYMAP:
                    tokens.append((up,))
                # 其它字符直接忽略
                i += 1
    return tokens


# ---------------------------------------------------------------------------
# MIDI 写出
# ---------------------------------------------------------------------------
PPQ = 480
UNIT_TICKS = PPQ // 2            # 八分音符 = 240 ticks
NOTE_OFF_PAD = 20                # 让相邻音符之间有一点点空隙
TEMPO_BPM = 108
TEMPO_US = int(60_000_000 / TEMPO_BPM)  # 每四分音符微秒数
VELOCITY = 90


def vlq(n):
    """Variable-length quantity (MIDI delta-time 编码)"""
    if n < 0:
        n = 0
    out = [n & 0x7F]
    n >>= 7
    while n > 0:
        out.insert(0, (n & 0x7F) | 0x80)
        n >>= 7
    return bytes(out)


def build_midi(tokens, out_path):
    events = []  # (tick, priority, bytes)    priority 保证同 tick 内 note-off 先于 note-on

    # Tempo
    events.append((0, 0, b'\xff\x51\x03' + TEMPO_US.to_bytes(3, 'big')))

    tick = 0
    for tok in tokens:
        if tok is None:
            tick += UNIT_TICKS
            continue
        note_on_tick = tick
        note_off_tick = tick + UNIT_TICKS - NOTE_OFF_PAD
        if note_off_tick <= note_on_tick:
            note_off_tick = note_on_tick + 1
        for k in tok:
            p = KEYMAP[k]
            events.append((note_on_tick, 1, bytes([0x90, p, VELOCITY])))
            events.append((note_off_tick, 0, bytes([0x80, p, 0])))
        tick += UNIT_TICKS

    # End-of-track
    events.append((tick, 2, b'\xff\x2f\x00'))

    # 按 (tick, priority) 排序
    events.sort(key=lambda e: (e[0], e[1]))

    track = bytearray()
    prev_tick = 0
    for t, _, payload in events:
        delta = t - prev_tick
        track += vlq(delta)
        track += payload
        prev_tick = t

    header = (
        b'MThd'
        + (6).to_bytes(4, 'big')
        + (1).to_bytes(2, 'big')      # format 1
        + (1).to_bytes(2, 'big')      # 1 track
        + PPQ.to_bytes(2, 'big')
    )
    chunk = b'MTrk' + len(track).to_bytes(4, 'big') + bytes(track)

    with open(out_path, 'wb') as f:
        f.write(header + chunk)


def main():
    tokens = parse_tab(TAB)
    n_sound = sum(1 for t in tokens if t is not None)
    n_rest = sum(1 for t in tokens if t is None)
    out_dir = os.path.dirname(os.path.abspath(__file__))
    out_path = os.path.join(out_dir, 'keyboard_song.mid')
    build_midi(tokens, out_path)
    total_secs = len(tokens) * (UNIT_TICKS / PPQ) * (60.0 / TEMPO_BPM)
    print(f'tokens  = {len(tokens)}  (sounding={n_sound}, rests={n_rest})')
    print(f'tempo   = {TEMPO_BPM} BPM, unit = 1/8 note ({UNIT_TICKS} ticks)')
    print(f'length  = {total_secs:0.1f} s')
    print(f'written -> {out_path}')


if __name__ == '__main__':
    main()
