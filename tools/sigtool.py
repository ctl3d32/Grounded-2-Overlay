#!/usr/bin/env python3
"""
sigtool.py — regenera Config/signatures.json para uma nova build do Grounded 2.

Uso:
    python sigtool.py <Grounded2Steam-Win64-Shipping.exe> <dump.jsonl> [saida.json]

Como funciona (análise estática, sem dependências externas):
  1. Lê do dump (linha "meta") os endereços de GWorld/GNames/GObjects e a base do módulo → RVAs.
  2. Procura na seção .text instruções com operando RIP-relativo [rip+disp32]
     (ModRM mod=00 rm=101) que apontem para cada RVA.
  3. Para cada referência gera um padrão: bytes da instrução com o disp32 como curinga,
     mais os bytes seguintes (call/jmp rel32 e jcc rel32 também viram curinga),
     expandindo até o padrão ficar ÚNICO na .text.
  4. Verifica que cada padrão resolve exatamente para a RVA esperada e grava até 4 por global.
"""
import json
import re
import struct
import sys

PER_TARGET = 4
MAX_REFS = 40


def load_pe(path):
    data = open(path, "rb").read()
    e = struct.unpack_from("<I", data, 0x3C)[0]
    if data[e:e + 4] != b"PE\0\0":
        raise SystemExit("Arquivo não é PE.")
    nsec = struct.unpack_from("<H", data, e + 6)[0]
    tds = struct.unpack_from("<I", data, e + 8)[0]
    opt = struct.unpack_from("<H", data, e + 20)[0]
    secs = []
    for i in range(nsec):
        o = e + 24 + opt + 40 * i
        name = data[o:o + 8].rstrip(b"\0").decode(errors="replace")
        vs, va, rs, ra = struct.unpack_from("<IIII", data, o + 8)
        ch = struct.unpack_from("<I", data, o + 36)[0]
        secs.append((name, va, vs, ra, rs, ch))
    return data, secs, tds


def load_meta(dump_path):
    with open(dump_path, encoding="utf-8", errors="replace") as f:
        for line in f:
            if line.startswith('{"kind":"meta"'):
                m = json.loads(line)
                base = int(m["module_base"], 16)
                return {
                    "GWorld": int(m["gworld"], 16) - base,
                    "GNames": int(m["gnames"], 16) - base,
                    "GObjects": int(m["gobjects"], 16) - base,
                }, m.get("ue_version")
    raise SystemExit("Linha meta não encontrada no dump.")


# REX opcional + opcode de 1 byte (ou 0F xx) seguido de ModRM com mod=00 rm=101 (RIP-relativo).
OPC = re.compile(
    rb"(?:[\x40-\x4F])?(?:[\x8B\x89\x8D\x39\x3B\x03\x2B\x33\x63\x85\x87]|\x0F[\x44\x45\x10\x11\x28\xB6\xB7])[\x05\x0D\x15\x1D\x25\x2D\x35\x3D]",
    re.S,
)


def wildcard_tail(text, start, length):
    """Bytes após a instrução; curinga em rel32 de E8/E9 e 0F 8x."""
    out = []
    i = start
    end = start + length
    while i < end and i < len(text):
        b = text[i]
        if b in (0xE8, 0xE9) and i + 5 <= len(text):
            out += [b, None, None, None, None]
            i += 5
            continue
        if b == 0x0F and i + 1 < len(text) and 0x80 <= text[i + 1] <= 0x8F and i + 6 <= len(text):
            out += [b, text[i + 1], None, None, None, None]
            i += 6
            continue
        out.append(b)
        i += 1
    return out


def to_regex(parts):
    return re.compile(b"".join(b"." if p is None else re.escape(bytes([p])) for p in parts), re.S)


def fmt(parts):
    return " ".join("??" if p is None else f"{p:02X}" for p in parts)


def main():
    if len(sys.argv) < 3:
        print(__doc__)
        return 1
    exe, dump = sys.argv[1], sys.argv[2]
    out_path = sys.argv[3] if len(sys.argv) > 3 else "signatures.json"

    data, secs, tds = load_pe(exe)
    targets, uev = load_meta(dump)
    text = next(s for s in secs if s[0] == ".text")
    _, va, vs, ra, rs, _ = text
    T = data[ra:ra + rs]
    print(f"PE TimeDateStamp=0x{tds:08X}  .text RVA=0x{va:X} size=0x{rs:X}  UE={uev}")
    for k, v in targets.items():
        print(f"  {k}: RVA 0x{v:X}")

    refs = {k: [] for k in targets}
    inv = {v: k for k, v in targets.items()}
    for m in OPC.finditer(T):
        disp_pos = m.end()
        if disp_pos + 4 > len(T):
            continue
        disp = struct.unpack_from("<i", T, disp_pos)[0]
        # instruções sem imediato (as do padrão OPC): fim = disp + 4
        tgt = va + disp_pos + 4 + disp
        k = inv.get(tgt)
        if k and len(refs[k]) < MAX_REFS:
            refs[k].append((m.start(), disp_pos - m.start()))

    sigs = []
    for k, lst in refs.items():
        print(f"{k}: {len(lst)} referências RIP-relativas")
        found = 0
        for start, dofs in lst:
            if found >= PER_TARGET:
                break
            head = list(T[start:start + dofs]) + [None] * 4
            for tail in range(3, 14):
                parts = head + wildcard_tail(T, start + dofs + 4, tail)
                if sum(p is not None for p in parts) < 6:
                    continue
                hits = [x.start() for x in to_regex(parts).finditer(T)]
                if len(hits) == 1:
                    p = hits[0]
                    res = va + p + dofs + 4 + struct.unpack_from("<i", T, p + dofs)[0]
                    if res == targets[k]:
                        sigs.append({
                            "Target": k, "Pattern": fmt(parts), "DisplacementOffset": dofs,
                            "InstructionEnd": dofs + 4, "Disassembly": "", "FoundAtRva": f"0x{va + p:X}",
                        })
                        found += 1
                        print(f"  OK  {fmt(parts)}")
                    break
        if found == 0:
            print(f"  NENHUMA assinatura única para {k}!")

    json.dump({"GameBuild": f"TimeDateStamp 0x{tds:08X} / UE {uev}", "Signatures": sigs},
              open(out_path, "w"), indent=2)
    print(f"{len(sigs)} assinaturas gravadas em {out_path}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
