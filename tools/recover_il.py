#!/usr/bin/env python3
"""
recover_il.py - read managed method bodies out of the (protected) Spanish Oak assembly.

The assembly embedded in "Spanish Oak Flash Tool.exe" is deliberately awkward:

  * type and method names are replaced by unprintable characters,
  * every method header declares a ~100 kB body that is mostly 0x00 padding,
  * the real IL for a method is stored at the *end* of its slot, immediately before the
    next method's header.

So the recovery is: for method i, take the bytes between header i and header i+1, drop the
trailing zero padding, and disassemble whatever non-zero tail is left. That yields the real
IL for 253 of the 378 methods, including the 0x27 seed/key generator.

Requirements (pip):
    pip install dnfile dncil

Usage:
    python3 recover_il.py dump   SpanishOak.dll --out il.txt
    python3 recover_il.py find   SpanishOak.dll --constant 0xC5
    python3 recover_il.py emulate SpanishOak.dll --method 264 --args 0x60,0x123456,0x08,0x30,0x61,0xA4,0xC5

Get the assembly out of the outer executable first:
    python3 pyinstaller_extract.py "Spanish Oak Flash Tool.exe" -o out
    # out/Spanish is a marshalled Python 3.10 code object; its single big constant is a
    # base64 PE image: decode it and you have the .NET assembly.
"""

import argparse
import base64
import re
import struct
import sys

try:
    import dnfile
    from dncil.cil.body import CilMethodBody
    from dncil.cil.body.reader import CilMethodBodyReaderBase
except ImportError:  # pragma: no cover - dependency hint
    sys.exit("Install the dependencies first:  pip install dnfile dncil")


class BufferReader(CilMethodBodyReaderBase):
    def __init__(self, buf, offset=0):
        self.buf = buf
        self.offset = offset

    def read(self, n):
        chunk = self.buf[self.offset:self.offset + n]
        self.offset += n
        if len(chunk) < n:
            raise EOFError("unexpected end of method body")
        return chunk

    def tell(self):
        return self.offset

    def seek(self, offset):
        self.offset = offset


class Assembly(object):
    def __init__(self, path):
        self.pe = dnfile.dnPE(path)
        self.md = self.pe.net.mdtables
        self.data = self.pe.__data__
        self.methods = sorted((m.Rva, i) for i, m in enumerate(self.md.MethodDef.rows) if m.Rva)
        self.slot = {idx: pos for pos, (_, idx) in enumerate(self.methods)}

    def name(self, index):
        try:
            return str(self.md.MethodDef.rows[index].Name)
        except Exception:
            return "?"

    def rva_to_offset(self, rva):
        return self.pe.get_offset_from_rva(rva)

    def body(self, index):
        """Real IL bytes for a method, recovered from the tail of its slot."""
        pos = self.slot[index]
        start = self.rva_to_offset(self.methods[pos][0])
        if pos + 1 < len(self.methods):
            end = self.rva_to_offset(self.methods[pos + 1][0])
        else:
            end = start + 0x20000
        segment = self.data[start:end]
        cut = segment.rfind(b"\x00" * 8)
        body = segment[cut + 1:] if cut >= 0 else segment
        return body.rstrip(b"\x00")

    def instructions(self, index):
        body = self.body(index)
        header = struct.pack("<HHII", 0x3013, 8, len(body), 0)   # fat header, maxstack 8
        return CilMethodBody(BufferReader(header + body)).instructions

    def resolve(self, operand):
        """Turns a metadata token into something readable."""
        if not isinstance(operand, int):
            return str(operand)
        table = operand >> 24
        rid = operand & 0xFFFFFF
        try:
            if table == 0x06:
                return self.name(rid - 1)
            if table == 0x0A:
                ref = self.md.MemberRef.rows[rid - 1]
                try:
                    owner = ref.Class.row
                    owner_name = f"{owner.TypeNamespace}.{owner.TypeName}".strip(".")
                except Exception:
                    owner_name = "?"
                return f"{owner_name}::{ref.Name}"
            if table == 0x04:
                return "field " + str(self.md.Field.rows[rid - 1].Name)
            if table == 0x01:
                t = self.md.TypeDef.rows[rid - 1]
                return f"type {t.TypeNamespace}.{t.TypeName}".strip(".")
            if table == 0x70:
                return f"user-string #{rid} (encrypted at rest)"
        except Exception:
            pass
        return hex(operand)


def cmd_dump(asm, out_path):
    with open(out_path, "w") as handle:
        for _, index in asm.methods:
            body = asm.body(index)
            handle.write(f"\n===== [{index}] {asm.name(index)} rva=0x{asm.md.MethodDef.rows[index].Rva:X} bytes={len(body)} =====\n")
            try:
                for ins in asm.instructions(index):
                    handle.write(f"   {ins.offset:6d} {str(ins.opcode):<12} {asm.resolve(ins.operand)}\n")
            except Exception as exc:
                handle.write(f"   <parse error: {exc!r}>\n")
    print(f"wrote {out_path}")


def cmd_find(asm, constant):
    pattern = re.compile(r"ldc\.i4(?:\.s)?\s+" + re.escape(constant), re.IGNORECASE)
    for _, index in asm.methods:
        text = "\n".join(f"{ins.offset} {ins.opcode} {ins.operand}" for ins in asm.instructions(index))
        if constant.lower() in text.lower() or pattern.search(text):
            print(f"[{index}] {asm.name(index)} (rva 0x{asm.md.MethodDef.rows[index].Rva:X}, {len(asm.body(index))} bytes)")


def emulate(asm, index, args):
    """Tiny IL interpreter - enough to run the 0x27 key generator and verify a port."""
    ins = asm.instructions(index)
    offsets = [i.offset for i in ins]
    stack, locs = [], {}
    pc = 0
    steps = 0

    def pop():
        return stack.pop()

    def key(operand):
        for attribute in ("index", "idx", "value"):
            if hasattr(operand, attribute):
                return int(getattr(operand, attribute))
        return operand

    while 0 <= pc < len(ins):
        steps += 1
        if steps > 500000:
            raise RuntimeError("emulation did not terminate")
        current = ins[pc]
        op = str(current.opcode)
        operand = current.operand

        if op == "nop":
            pass
        elif op == "ldarg.0":
            stack.append(args.get(0, 0))
        elif op == "ldarg.1":
            stack.append(args.get(1, 0))
        elif op == "ldarg.2":
            stack.append(args.get(2, 0))
        elif op == "ldarg.3":
            stack.append(args.get(3, 0))
        elif op == "ldarg.s":
            stack.append(args.get(key(operand), 0))
        elif op == "ldloc.s":
            stack.append(locs.get(key(operand), 0))
        elif op.startswith("ldloc."):
            stack.append(locs.get(int(op.split(".")[1]), 0))
        elif op == "stloc.s":
            locs[key(operand)] = pop()
        elif op.startswith("stloc."):
            locs[int(op.split(".")[1])] = pop()
        elif op == "ldc.i4.s":
            stack.append(int(operand) & 0xFFFFFFFF)
        elif op == "ldc.i4":
            stack.append(int(operand) & 0xFFFFFFFF)
        elif op.startswith("ldc.i4."):
            value = op.split(".")[-1]
            stack.append((-1 & 0xFFFFFFFF) if value == "m1" else int(value))
        elif op == "dup":
            stack.append(stack[-1])
        elif op == "pop":
            pop()
        elif op == "add":
            stack.append((pop() + pop()) & 0xFFFFFFFF)
        elif op == "mul":
            stack.append((pop() * pop()) & 0xFFFFFFFF)
        elif op == "and":
            stack.append(pop() & pop())
        elif op == "or":
            stack.append(pop() | pop())
        elif op == "xor":
            stack.append(pop() ^ pop())
        elif op == "shl":
            shift, value = pop(), pop()
            stack.append((value << (shift & 31)) & 0xFFFFFFFF)
        elif op == "shr":
            shift, value = pop(), pop()
            stack.append((value & 0xFFFFFFFF) >> (shift & 31))
        elif op == "conv.u1":
            stack.append(pop() & 0xFF)
        elif op == "clt":
            a, b = pop(), pop()
            stack.append(1 if b < a else 0)
        elif op == "ceq":
            a, b = pop(), pop()
            stack.append(1 if a == b else 0)
        elif op == "ret":
            return stack[-1] if stack else None
        elif op in ("br", "br.s"):
            pc = offsets.index(operand)
            continue
        elif op in ("brtrue", "brtrue.s"):
            pc = offsets.index(operand) if pop() else pc + 1
            continue
        elif op in ("brfalse", "brfalse.s"):
            pc = offsets.index(operand) if not pop() else pc + 1
            continue
        elif op in ("beq", "beq.s"):
            a, b = pop(), pop()
            pc = offsets.index(operand) if a == b else pc + 1
            continue
        else:
            raise RuntimeError(f"unsupported opcode {op} at IL offset {current.offset}")
        pc += 1
    return None


def main(argv):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("command", choices=["dump", "find", "emulate"])
    parser.add_argument("assembly")
    parser.add_argument("--out", default="il.txt")
    parser.add_argument("--constant", default="0xC5")
    parser.add_argument("--method", type=int, default=264)
    parser.add_argument("--args", default="0,0x123456,0x08,0x30,0x61,0xA4,0xC5")
    args = parser.parse_args(argv)

    asm = Assembly(args.assembly)
    if args.command == "dump":
        cmd_dump(asm, args.out)
        return 0
    if args.command == "find":
        cmd_find(asm, args.constant)
        return 0

    values = {}
    for position, token in enumerate(args.args.split(",")):
        values[position] = int(token, 0)
    result = emulate(asm, args.method, values)
    print(f"method [{args.method}] {asm.name(args.method)} -> 0x{result:06X}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
