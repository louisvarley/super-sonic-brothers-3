// Offline level-generator CPU only. Browser gameplay never runs an NES emulator.
export class Cpu6502 {
  constructor(rom) {
    this.rom = rom;
    this.ram = new Uint8Array(0x800);
    this.prgRam = new Uint8Array(0x2000);
    this.banks = [30, 0, 0, 31];
    this.a = this.x = this.y = this.pc = this.steps = this.select = 0;
    this.s = 0xfd;
    this.c = this.z = this.n = this.v = this.d = false;
    this.i = true;
  }
  read(a) {
    a &= 65535;
    return a < 0x2000 ? this.ram[a & 0x7ff] : a < 0x6000 ? 0 :
      a < 0x8000 ? this.prgRam[a - 0x6000] : this.rom.prg(this.banks[(a - 0x8000) >> 13], a);
  }
  write(a, v) {
    a &= 65535;
    if (a < 0x2000) this.ram[a & 0x7ff] = v;
    else if (a >= 0x6000 && a < 0x8000) this.prgRam[a - 0x6000] = v;
    else if (a >= 0x8000 && a < 0xa000) {
      if (!(a & 1)) this.select = v;
      else if ((this.select & 7) === 6) this.banks[this.select & 0x40 ? 2 : 0] = v & 63;
      else if ((this.select & 7) === 7) this.banks[1] = v & 63;
    }
  }
  fetch() { const v = this.read(this.pc); this.pc = (this.pc + 1) & 65535; return v; }
  word() { return this.fetch() | this.fetch() << 8; }
  push(v) { this.write(0x100 + this.s, v); this.s = (this.s - 1) & 255; }
  pull() { this.s = (this.s + 1) & 255; return this.read(0x100 + this.s); }
  nz(v) { v &= 255; this.z = v === 0; this.n = !!(v & 128); return v; }
  flags() { return +this.c | +this.z << 1 | +this.i << 2 | +this.d << 3 | 0x30 | +this.v << 6 | +this.n << 7; }
  setFlags(v) { [this.c, this.z, this.i, this.d, this.v, this.n] = [1, 2, 4, 8, 64, 128].map(m => !!(v & m)); }
  addr(mode) {
    const zpword = a => this.read(a & 255) | this.read((a + 1) & 255) << 8;
    switch (mode) {
      case 'imm': { const a = this.pc; this.pc = (a + 1) & 65535; return a; }
      case 'zp': return this.fetch();
      case 'zx': return (this.fetch() + this.x) & 255;
      case 'zy': return (this.fetch() + this.y) & 255;
      case 'abs': return this.word();
      case 'ax': return (this.word() + this.x) & 65535;
      case 'ay': return (this.word() + this.y) & 65535;
      case 'ix': return zpword(this.fetch() + this.x);
      case 'iy': return (zpword(this.fetch()) + this.y) & 65535;
      default: throw new Error(`Unknown addressing mode ${mode}`);
    }
  }
  call(address, maxSteps = 2000000) {
    this.push(3); this.push(255); this.pc = address;
    while (this.pc !== 0x400) {
      if (++this.steps > maxSteps) throw new Error(`6502 instruction limit at $${this.pc.toString(16)}`);
      this.step();
    }
  }
  step() {
    const op = this.fetch(), entry = instructions.get(op);
    if (entry) {
      const [name, mode] = entry, address = this.addr(mode), value = this.read(address);
      if (name === 'sta' || name === 'stx' || name === 'sty') { this.write(address, this[name[2]]); return; }
      if (name.startsWith('ld')) { this[name[2]] = this.nz(value); return; }
      if (name === 'ora') this.a = this.nz(this.a | value);
      else if (name === 'and') this.a = this.nz(this.a & value);
      else if (name === 'eor') this.a = this.nz(this.a ^ value);
      else if (name === 'adc' || name === 'sbc') {
        const m = name === 'sbc' ? value ^ 255 : value, r = this.a + m + +this.c;
        this.v = !!((this.a ^ r) & (m ^ r) & 128); this.c = r > 255; this.a = this.nz(r);
      } else if (['cmp', 'cpx', 'cpy'].includes(name)) {
        const r = this[name === 'cmp' ? 'a' : name[2]]; this.c = r >= value; this.nz(r - value);
      } else if (name === 'bit') {
        this.z = !(this.a & value); this.n = !!(value & 128); this.v = !!(value & 64);
      } else this.write(address, this.modify(name, value));
      return;
    }
    const branches = { 0x10: !this.n, 0x30: this.n, 0x50: !this.v, 0x70: this.v,
      0x90: !this.c, 0xb0: this.c, 0xd0: !this.z, 0xf0: this.z };
    if (op in branches) { const v = this.fetch(); if (branches[op]) this.pc = (this.pc + (v < 128 ? v : v - 256)) & 65535; return; }
    switch (op) {
      case 0xaa: this.x = this.nz(this.a); break;
      case 0xa8: this.y = this.nz(this.a); break;
      case 0x8a: this.a = this.nz(this.x); break;
      case 0x98: this.a = this.nz(this.y); break;
      case 0xba: this.x = this.nz(this.s); break;
      case 0x9a: this.s = this.x; break;
      case 0x48: this.push(this.a); break;
      case 0x68: this.a = this.nz(this.pull()); break;
      case 0x08: this.push(this.flags()); break;
      case 0x28: this.setFlags(this.pull()); break;
      case 0xe8: this.x = this.nz(this.x + 1); break;
      case 0xc8: this.y = this.nz(this.y + 1); break;
      case 0xca: this.x = this.nz(this.x - 1); break;
      case 0x88: this.y = this.nz(this.y - 1); break;
      case 0x0a: this.a = this.modify('asl', this.a); break;
      case 0x4a: this.a = this.modify('lsr', this.a); break;
      case 0x2a: this.a = this.modify('rol', this.a); break;
      case 0x6a: this.a = this.modify('ror', this.a); break;
      case 0x4c: this.pc = this.word(); break;
      case 0x6c: { const a = this.word(); this.pc = this.read(a) | this.read((a & 0xff00) | ((a + 1) & 255)) << 8; break; }
      case 0x20: { const a = this.word(), ret = this.pc - 1; this.push(ret >> 8); this.push(ret); this.pc = a; break; }
      case 0x60: { const lo = this.pull(); this.pc = ((this.pull() << 8 | lo) + 1) & 65535; break; }
      case 0x40: { this.setFlags(this.pull()); const lo = this.pull(); this.pc = this.pull() << 8 | lo; break; }
      case 0x18: this.c = false; break;
      case 0x38: this.c = true; break;
      case 0x58: this.i = false; break;
      case 0x78: this.i = true; break;
      case 0xb8: this.v = false; break;
      case 0xd8: this.d = false; break;
      case 0xf8: this.d = true; break;
      case 0xea: break;
      default: throw new Error(`Unsupported 6502 opcode $${op.toString(16)} at $${((this.pc - 1) & 65535).toString(16)}`);
    }
  }
  modify(name, v) {
    const carry = +this.c;
    switch (name) {
      case 'inc': return this.nz(v + 1);
      case 'dec': return this.nz(v - 1);
      case 'asl': this.c = !!(v & 128); return this.nz(v << 1);
      case 'lsr': this.c = !!(v & 1); return this.nz(v >> 1);
      case 'rol': this.c = !!(v & 128); return this.nz(v << 1 | carry);
      case 'ror': this.c = !!(v & 1); return this.nz(v >> 1 | carry << 7);
      default: throw new Error(`Unknown instruction ${name}`);
    }
  }
}

const instructions = new Map();
function group(name, modes, opcodes) {
  opcodes.forEach((op, i) => instructions.set(op, [name, modes[i]]));
}
const alu = ['ix', 'zp', 'imm', 'abs', 'iy', 'zx', 'ay', 'ax'];
for (const [name, base] of [['ora', 0], ['and', 0x20], ['eor', 0x40], ['adc', 0x60], ['lda', 0xa0], ['cmp', 0xc0], ['sbc', 0xe0]])
  group(name, alu, [1, 5, 9, 13, 17, 21, 25, 29].map(v => base + v));
group('sta', ['ix', 'zp', 'abs', 'iy', 'zx', 'ay', 'ax'], [0x81, 0x85, 0x8d, 0x91, 0x95, 0x99, 0x9d]);
group('ldx', ['imm', 'zp', 'zy', 'abs', 'ay'], [0xa2, 0xa6, 0xb6, 0xae, 0xbe]);
group('ldy', ['imm', 'zp', 'zx', 'abs', 'ax'], [0xa0, 0xa4, 0xb4, 0xac, 0xbc]);
group('stx', ['zp', 'zy', 'abs'], [0x86, 0x96, 0x8e]);
group('sty', ['zp', 'zx', 'abs'], [0x84, 0x94, 0x8c]);
group('cpx', ['imm', 'zp', 'abs'], [0xe0, 0xe4, 0xec]);
group('cpy', ['imm', 'zp', 'abs'], [0xc0, 0xc4, 0xcc]);
group('bit', ['zp', 'abs'], [0x24, 0x2c]);
for (const [name, base] of [['asl', 0], ['rol', 0x20], ['lsr', 0x40], ['ror', 0x60], ['dec', 0xc0], ['inc', 0xe0]])
  group(name, ['zp', 'zx', 'abs', 'ax'], [6, 22, 14, 30].map(v => base + v));
