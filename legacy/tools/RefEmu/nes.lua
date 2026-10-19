-- NES reference harness for MAME 0.242 (used for the SMB3 custom level; see docs/smb3-level.md).
--
-- Environment variables (set by nes.sh):
--   NES_INPUTS  input script: "<frame> <buttons>" per line, buttons from U D L R A B S (start) E (select) or "-".
--               The state holds until the next line. '#' starts a comment.
--   NES_FRAMES  total frames to run.
--   NES_OUT     output directory.
--   NES_DUMPS   comma-separated frames at which to dump CPU RAM ($0000-$07FF) + cartridge RAM ($6000-$7FFF) to
--               cpu_<frame>.bin (10 KiB: RAM then cart RAM), PPU space ($0000-$2FFF: pattern tables as the mapper
--               currently maps them, then nametables) to ppu_<frame>.bin, palette RAM (32 bytes) to pal_<frame>.bin
--               and sprite RAM (256 bytes) to oam_<frame>.bin.
--   NES_SNAPS   comma-separated frames at which to save a screenshot.
--   NES_HOLD    "from:addr:value,..." (hex addr/value): written to CPU RAM at the end of every frame from that frame on
--               (e.g. clearing Objects_State so no enemy reaches the player while music is recorded).
--   NES_APULOG  1 = log every write to the sound registers ($4000-$4017) to apu.csv as frame,address,value.
collectgarbage("stop")

local m = manager.machine
local cpu = m.devices[":maincpu"].spaces["program"]
local ppu = m.devices[":ppu"].spaces["videoram"]

local function env(name, default)
  local v = os.getenv(name)
  if v == nil or v == "" then return default end
  return v
end

local out_dir = env("NES_OUT", ".")
local total = tonumber(env("NES_FRAMES", "600"))

local button_names = { U = "P1 Up", D = "P1 Down", L = "P1 Left", R = "P1 Right", A = "A", B = "B", S = "Start", E = "Select" }
local fields = {}
for _, port in pairs(m.ioport.ports) do
  for _, f in pairs(port.fields) do fields[f.name] = f end
end
local function field(letter)
  local name = button_names[letter]
  return fields[name] or fields["P1 " .. name] or fields["P1 Button " .. (letter == "A" and "1" or "2")]
end

local script = {}
for line in io.lines(env("NES_INPUTS", "/dev/null")) do
  line = line:gsub("#.*", "")
  local f, b = line:match("^%s*(%d+)%s+(%S+)")
  if f then table.insert(script, { frame = tonumber(f), buttons = b }) end
end

local dumps, snaps = {}, {}
for f in env("NES_DUMPS", ""):gmatch("%d+") do dumps[tonumber(f)] = true end
for f in env("NES_SNAPS", ""):gmatch("%d+") do snaps[tonumber(f)] = true end

local function dump(path, space, first, last)
  local f = io.open(path, "wb")
  local chunk = {}
  for a = first, last do chunk[#chunk + 1] = string.char(space:read_u8(a)) end
  f:write(table.concat(chunk))
  f:close()
end

local holds = {}
for f, a, v in env("NES_HOLD", ""):gmatch("(%d+):(%x+):(%x+)") do
  table.insert(holds, { from = tonumber(f), addr = tonumber(a, 16), value = tonumber(v, 16) })
end

local frame, idx, current, done = 0, 1, "-", false
local apu = nil
local apu_tap = nil
if env("NES_APULOG", "") ~= "" then
  apu = io.open(out_dir .. "/apu.csv", "w")
  apu:write("frame,address,value\n")
  apu_tap = cpu:install_write_tap(0x4000, 0x4017, "apulog", function(offset, data)
    apu:write(frame .. "," .. string.format("%04X", offset) .. "," .. string.format("%02X", data & 0xFF) .. "\n")
  end)
end
local trace = io.open(out_dir .. "/trace.csv", "w")
trace:write("frame,buttons\n")
emu.register_frame_done(function()
  if done then return end
  while idx <= #script and script[idx].frame <= frame do current = script[idx].buttons; idx = idx + 1 end
  for letter, _ in pairs(button_names) do
    local fl = field(letter)
    if fl then fl:set_value(current:find(letter, 1, true) and 1 or 0) end
  end
  trace:write(frame .. "," .. current .. "\n")
  if dumps[frame] then
    local f = io.open(out_dir .. "/cpu_" .. frame .. ".bin", "wb")
    local chunk = {}
    for a = 0, 0x7FF do chunk[#chunk + 1] = string.char(cpu:read_u8(a)) end
    for a = 0x6000, 0x7FFF do chunk[#chunk + 1] = string.char(cpu:read_u8(a)) end
    f:write(table.concat(chunk)); f:close()
    dump(out_dir .. "/ppu_" .. frame .. ".bin", ppu, 0, 0x2FFF)
    local pal = {}
    local pitem = emu.item(m.devices[":ppu"].items["0/m_palette_ram"])
    for i = 0, 31 do pal[#pal + 1] = string.char(pitem:read(i) & 0x3F) end
    local pf = io.open(out_dir .. "/pal_" .. frame .. ".bin", "wb"); pf:write(table.concat(pal)); pf:close()
    local oam = {}
    local ok = pcall(function()
      local item = emu.item(m.devices[":ppu"].items["0/m_spriteram"])
      for i = 0, 255 do oam[#oam + 1] = string.char(item:read(i)) end
    end)
    if ok then local o = io.open(out_dir .. "/oam_" .. frame .. ".bin", "wb"); o:write(table.concat(oam)); o:close() end
  end
  if snaps[frame] then m.video:snapshot() end
  for _, h in ipairs(holds) do if frame >= h.from then cpu:write_u8(h.addr, h.value) end end
  frame = frame + 1
  if frame >= total then done = true; trace:close(); if apu then apu:close() end; m:exit() end
end)
