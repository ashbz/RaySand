
-- graphics feature showcase (60fps)
local t = 0

function _update60()
  t = t + 1
end

function _draw()
  cls(1)

  -- ── fill pattern dithering ──────────────────────────────────
  -- primary = color 8 (red), secondary = color 9 (orange)
  -- pattern 0x5555 = checkerboard; +0.5 enables secondary color
  fillp(0x5555 + 0.5)
  rectfill(2, 2, 62, 30, 0x89)  -- high nibble=secondary(8=red? no, 8=red), low=primary
  fillp()  -- reset

  -- ── ovals ───────────────────────────────────────────────────
  ovalfill(2, 34, 62, 58, 10)   -- yellow filled ellipse
  oval(2, 34, 62, 58, 7)        -- white outline

  -- ── connected lines ─────────────────────────────────────────
  local lx, ly = 66, 2
  line(lx, ly, 7)
  for i = 0, 16 do
    local a = (t * 0.01 + i / 16)
    local nx = lx + cos(a) * 28
    local ny = ly + 14 + sin(a) * 12
    line(nx, ny, 14 - i)    -- continues from last endpoint
  end

  -- ── palette swap ────────────────────────────────────────────
  -- display palette: remap color 11 (green) → 12 (blue) during this frame
  pal(11, 12, 1)
  circfill(97, 16, 12, 11)  -- drawn as green in buffer, shown as blue
  pal(nil, nil, 1)          -- reset display palette

  -- ── circ / circfill ─────────────────────────────────────────
  for i = 1, 6 do
    local a = t * 0.02 + i / 6
    local cx = 97 + cos(a) * 20
    local cy = 50 + sin(a) * 14
    circfill(cx, cy, 3, i + 8)
  end

  -- ── peek/poke: write directly to screen RAM ──────────────────
  -- draw a diagonal strip via poke into 0x6000
  for i = 0, 30 do
    local px = 2 + i
    local py = 62 + i
    if px < 128 and py < 128 then
      local addr = 0x6000 + flr(py) * 64 + flr(px / 2)
      local cur = peek(addr)
      if px % 2 == 0 then
        poke(addr, (cur & 0xf0) | 7)   -- white in lo nibble
      else
        poke(addr, (cur & 0x0f) | (7 << 4))
      end
    end
  end

  -- ── text ────────────────────────────────────────────────────
  local x = print("fillp ", 2, 107, 7)
  x = print("oval ",  x,  107, 10)
  x = print("tline",  x,  107, 11)

  print("emu ok  t=" .. t, 2, 114, 6)

  -- ── border ──────────────────────────────────────────────────
  rect(0, 0, 127, 127, 5)
end
