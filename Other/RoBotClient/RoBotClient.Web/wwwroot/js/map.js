// Live top-down bot map. The component pushes a payload ~10x/s; an animation-frame loop paints it, gliding
// bots/monsters between cells and running the HP-trail and damage-number animations ported from the Unity client.
// World Y increases UPWARD, so canvas Y is flipped: canvasY = (height-1 - worldY) * cell.

const COLORS = {
    bg: "#10131a",
    walkable: "#3a4254",
    blocked: "#191c24",
    self: "#3b9dff",
    monster: "#e5484d",
    path: "#ffe066",
    text: "#cbd2dc",
    textDim: "#7c8493",
};

// Sprites, HP bars, damage numbers and shouts come from the simulation tab's module (the simulation is the source).
const Sim = await import("./sim-sprites.js" + new URL(import.meta.url).search);
const { font, clamp01, dpr, hpColor, drawShout, drawNumber, DAMAGE_PATH, COMBO_PATH, HIT_INTERVAL_MS, MISS_PATH, RED } = Sim;
const overlaps = (a, b) => a[0] < b[2] && a[2] > b[0] && a[1] < b[3] && a[3] > b[1]; // [left, top, right, bottom]

// ---------------------------------------------------------------------------------------------------------------
// Per-canvas state. Everything the canvas remembers between frames lives in one object.
// ---------------------------------------------------------------------------------------------------------------

const states = new WeakMap();

function stateOf(canvas) {
    let s = states.get(canvas);
    if (!s) states.set(canvas, s = {
        terrain: null,        // { map, img, w, h, spawns }
        payload: null,        // latest payload (without terrain cells)
        animating: false,
        hover: [],            // tooltip targets: points { x, y, name } and rects { left, top, width, height, name }
        glides: new Map(),    // "b:name" / "m:id" -> { x, y, seen }   drawn position, eased toward the server cell
        trails: new Map(),    // monster id -> Sim.hpTrail state + seen   HP bar damage trail
        plates: new Map(),    // bot name -> name-plate opacity
        lastHp: new Map(),    // monster id / "b:name" -> hp at the previous frame (a drop spawns damage numbers)
        lastMiss: new Map(),  // monster id / "b:name" -> misses taken so far (a rise spawns "Miss")
        numbers: [],          // flying damage numbers
        payloadAt: 0,         // performance.now() when the latest payload arrived (its atk/hit ages are as of then)
        deadAt: new Map(),    // monster id -> when it was first seen dead (the death animation plays from there)
    });
    return s;
}

// ---------------------------------------------------------------------------------------------------------------
// Entry point and animation loop
// ---------------------------------------------------------------------------------------------------------------

export function drawWorld(canvas, payload) {
    if (!canvas) return;
    const s = stateOf(canvas);
    if (!payload || !payload.ok) {
        s.payload = null;
        message(canvas, payload?.message || "no data");
        return;
    }
    if (payload.cells) s.terrain = buildTerrain(payload);
    s.payload = { ...payload, cells: undefined };
    s.payloadAt = performance.now();
    if (!s.animating) startLoop(canvas, s);
}

function startLoop(canvas, s) {
    s.animating = true;
    let last = performance.now();
    const step = now => {
        if (!s.payload || !canvas.isConnected) { s.animating = false; return; }
        const dt = Math.min(0.1, (now - last) / 1000);
        last = now;
        for (const [id, tr] of s.trails) if (now - tr.seen > 2000) s.trails.delete(id);
        render(canvas, s, glideAll(s, s.payload, dt, now), now);
        requestAnimationFrame(step);
    };
    requestAnimationFrame(step);
}

// ---------------------------------------------------------------------------------------------------------------
// Frame
// ---------------------------------------------------------------------------------------------------------------

function render(canvas, s, payload, now) {
    const ctx = canvas.getContext("2d");
    if (!ctx) return;
    const t = s.terrain;
    if (!t || t.map !== payload.map) { message(canvas, "loading map…"); return; }

    const v = makeView(canvas, ctx, t, now);
    const bots = payload.bots || [];
    const mobs = layoutMobs(v, s, payload);

    drawTerrain(v, payload.minimap);
    const spawnHover = payload.showSpawnAreas !== false ? drawSpawnAreas(v, t.spawns) : [];
    const items = (payload.items || []).map(it => drawItem(v, it));
    mobs.forEach(m => drawMob(v, m));
    bots.forEach(b => drawPath(v, b));
    bots.forEach(b => drawWanderTarget(v, b));
    bots.forEach(b => drawBot(v, s, b, mobs));
    mobs.forEach(m => drawHpBar(v, s, m)); // over the name plates
    bots.forEach(b => b.shout && drawShout(v, b.shout, v.toX(b.x), v.toY(b.y) - v.r));
    spawnDamageNumbers(v, s, mobs, bots, now);
    drawDamageNumbers(v, s, mobs);
    drawLegend(v, payload, bots);
    if (payload.toast) drawToast(v, payload.toast);

    s.hover = [...spawnHover, ...items, ...mobs.map(m => m.hover), ...bots.flatMap(b => botHover(v, b))];
    attachHover(canvas, s);
}

/** Geometry and drawing context shared by every draw function for one frame. */
function makeView(canvas, ctx, t, now) {
    const { pw, ph } = fitCanvas(canvas);
    // Stretch the map to fill the canvas: x and y scale independently (a 400x400 map fills a wide card).
    const sx = pw / t.w, sy = ph / t.h;
    const fs = Math.max(11, Math.round(Math.min(pw, ph) * 0.022));
    const r = Math.max(4, Math.min(sx, sy) * 1.6);
    return {
        canvas, ctx, t, now, pw, ph, sx, sy, fs, r,
        mr: Math.max(3.5, r * 0.8),          // monster dot radius
        spriteHeight: 30 * dpr(),            // monster sprites ~30 css px tall
        toX: x => (x + 0.5) * sx,
        toY: y => (t.h - 1 - y + 0.5) * sy,
    };
}

function fitCanvas(canvas) {
    // Backing store matches the CSS box (any aspect), crisp on HiDPI.
    const pw = Math.round((canvas.clientWidth || canvas.width || 420) * dpr());
    const ph = Math.round((canvas.clientHeight || canvas.height || 420) * dpr());
    if (canvas.width !== pw || canvas.height !== ph) {
        canvas.width = pw;
        canvas.height = ph;
    }
    return { pw, ph };
}

function message(canvas, text) {
    const ctx = canvas.getContext("2d");
    if (!ctx) return;
    const { pw, ph } = fitCanvas(canvas);
    ctx.fillStyle = COLORS.bg;
    ctx.fillRect(0, 0, pw, ph);
    ctx.fillStyle = COLORS.textDim;
    ctx.font = font(Math.max(12, Math.round(Math.min(pw, ph) * 0.04)));
    ctx.textAlign = "center";
    ctx.textBaseline = "middle";
    ctx.fillText(text, pw / 2, ph / 2);
    ctx.textAlign = "start";
    ctx.textBaseline = "alphabetic";
}

// ---------------------------------------------------------------------------------------------------------------
// Images
// ---------------------------------------------------------------------------------------------------------------

// Images by URL (minimap art, monster atlases): undefined while loading, null if missing.
const images = new Map();

function image(url) {
    if (!url) return null;
    if (!images.has(url)) {
        images.set(url, undefined);
        const img = new Image();
        img.onload = () => images.set(url, img);
        img.onerror = () => images.set(url, null);
        img.src = url;
    }
    return images.get(url) || null;
}

// ---------------------------------------------------------------------------------------------------------------
// Terrain and spawn areas
// ---------------------------------------------------------------------------------------------------------------

const rgb = hex => [1, 3, 5].map(i => parseInt(hex.slice(i, i + 2), 16));

/** The terrain arrives once per map as a "0101..." string (row-major, world y=0 = bottom row); cache it as a
 *  1px-per-cell image, used until the minimap art loads (or when the map has none). */
function buildTerrain(payload) {
    const { width: w, height: h, cells } = payload;
    const img = document.createElement("canvas");
    img.width = w;
    img.height = h;
    const ictx = img.getContext("2d");
    const data = ictx.createImageData(w, h);
    const walk = rgb(COLORS.walkable), block = rgb(COLORS.blocked);
    for (let y = 0; y < h; y++) {
        const row = (h - 1 - y) * w; // flip Y
        for (let x = 0; x < w; x++) {
            const c = cells.charCodeAt(x + y * w) === 49 ? walk : block; // '1'
            data.data.set([...c, 255], (row + x) * 4);
        }
    }
    ictx.putImageData(data, 0, 0);
    return { map: payload.map, img, w, h, spawns: payload.spawns || [] };
}

function drawTerrain(v, minimapUrl) {
    const { ctx, t } = v;
    ctx.fillStyle = COLORS.bg;
    ctx.fillRect(0, 0, v.pw, v.ph);
    const art = image(minimapUrl);
    ctx.imageSmoothingEnabled = !!art;
    ctx.drawImage(art || t.img, 0, 0, t.w * v.sx, t.h * v.sy);
}

function hue(name) {
    let hash = 0;
    for (let i = 0; i < name.length; i++) hash = (hash * 31 + name.charCodeAt(i)) | 0;
    return (hash >>> 0) % 360;
}

/** Fixed spawn zones (inclusive world-cell rectangles), drawn lightly. Returns their hover rectangles. */
function drawSpawnAreas(v, spawns) {
    const { ctx, t, sx, sy } = v;
    return spawns.map(area => {
        const left = area.minX * sx, top = (t.h - 1 - area.maxY) * sy;
        const width = (area.maxX - area.minX + 1) * sx, height = (area.maxY - area.minY + 1) * sy;
        const h = hue(area.name || "");
        ctx.fillStyle = `hsla(${h}, 85%, 60%, 0.10)`;
        ctx.strokeStyle = `hsla(${h}, 90%, 68%, 0.82)`;
        ctx.lineWidth = Math.max(1, dpr());
        ctx.fillRect(left, top, width, height);
        ctx.strokeRect(left + 0.5, top + 0.5, Math.max(0, width - 1), Math.max(0, height - 1));
        return { left, top, width, height, name: `${area.name} spawn area · ${area.count} mob(s)` };
    });
}

// ---------------------------------------------------------------------------------------------------------------
// Monsters
// ---------------------------------------------------------------------------------------------------------------

/** Screen layout for each monster (nearer the bottom drawn on top): the sprite's layers for the current animation
 *  frame (or a single first-frame rect until the animation JSON loads), HP bar anchor, hover point, and the box it
 *  covers (for fading bot name plates). A red dot until the atlas loads, or with no sprite. */
function layoutMobs(v, s, payload) {
    for (const [id, t] of s.deadAt) if (v.now - t > 5000) s.deadAt.delete(id);
    return [...(payload.mobs || [])].sort((a, b) => b.y - a.y).map(m => {
        const x = v.toX(m.x), y = v.toY(m.y);
        const a = Sim.animOf(m.name);
        const barSpace = m.hp != null ? 12 : 0;
        const name = m.name || "monster";
        if (a) {
            const since = performance.now() - s.payloadAt; // the ages below are as of the last payload
            if (m.dead && !s.deadAt.has(m.id)) s.deadAt.set(m.id, v.now);
            // RoSpriteAnimator walks by distance: frame = cells * 4.6 * 0.37 * 4 / (delay / 24); idle loops on its delay.
            const loopMs = m.walk && m.cell > 0 ? v.now / 1000 / m.cell * 6.808 * 24 : v.now;
            const pose = Sim.monsterFrame(a, m.dir & 7, { dead: m.dead ? v.now - s.deadAt.get(m.id) : null, atk: m.atk + since, hit: m.hit + since, walk: m.walk }, loopMs);
            const r0 = a.rects[0], k = Math.min(1, v.spriteHeight / Math.max(r0[2], r0[3], 1)); // minimap: every monster ~30 css px
            const layers = pose ? Sim.place(a, pose.frame, 0, 0, x, y + v.mr, k, pose.alpha) : [];
            if (!layers.length) return null;
            const [left, top, right, bottom] = Sim.box(layers);
            return { m, x, y, layers, barY: top - 3,
                hover: { x, y: (top + bottom) / 2, name }, box: [left, top - barSpace, right, bottom] };
        }
        return { m, x, y, layers: null, barY: y - v.mr - 3,
            hover: { x, y, name }, box: [x - v.mr, y - v.mr - barSpace, x + v.mr, y + v.mr] };
    }).filter(Boolean);
}

/** An item on the ground: its inventory icon (half a monster sprite), else a small gold diamond. Returns its hover point. */
function drawItem(v, it) {
    const { ctx } = v;
    const x = v.toX(it.x), y = v.toY(it.y), icon = image(it.icon), k = v.spriteHeight * 0.5;
    if (icon) {
        ctx.imageSmoothingEnabled = true;
        ctx.drawImage(icon, x - k / 2, y - k / 2, k, k);
    } else {
        const d = v.mr * 0.8;
        ctx.fillStyle = "#ffd54a";
        ctx.strokeStyle = "#000000cc";
        ctx.lineWidth = 1;
        ctx.beginPath();
        ctx.moveTo(x, y - d); ctx.lineTo(x + d, y); ctx.lineTo(x, y + d); ctx.lineTo(x - d, y);
        ctx.closePath();
        ctx.fill();
        ctx.stroke();
    }
    return { x, y, name: it.count > 1 ? `${it.name} x${it.count}` : it.name };
}

function drawMob(v, { x, y, layers }) {
    const { ctx } = v;
    if (layers) { Sim.drawLayers(ctx, layers); return; }
    ctx.fillStyle = COLORS.monster;
    ctx.strokeStyle = "#000000cc";
    ctx.lineWidth = 1.5;
    ctx.beginPath();
    ctx.arc(x, y, v.mr, 0, Math.PI * 2);
    ctx.fill();
    ctx.stroke();
}

/** Dueled monsters only (hp is null otherwise): a small bar above the sprite, with the client's white damage trail. */
function drawHpBar(v, s, { m, x, barY }) {
    if (m.hp == null) return;
    const pct = clamp01(m.hp / m.maxHp);
    let tr = s.trails.get(m.id);
    if (!tr) s.trails.set(m.id, tr = {});
    tr.seen = v.now;
    Sim.drawHpBar(v.ctx, x, barY, Math.max(18, v.spriteHeight * 1.2), Math.max(3, v.spriteHeight * 0.16), pct, Sim.hpTrail(tr, pct, v.now / 1000));
}

// ---------------------------------------------------------------------------------------------------------------
// Bots
// ---------------------------------------------------------------------------------------------------------------

/** Planned walk of a traveling bot: dashed line along its A* path, ring on the goal cell. */
function drawPath(v, b) {
    const p = b.path || [];
    if (p.length < 4) return;
    const { ctx, toX, toY } = v;
    ctx.strokeStyle = COLORS.path;
    ctx.lineWidth = 2;
    ctx.setLineDash([6, 4]);
    ctx.beginPath();
    ctx.moveTo(toX(b.x), toY(b.y));
    for (let i = 0; i < p.length; i += 2) ctx.lineTo(toX(p[i]), toY(p[i + 1]));
    ctx.stroke();
    ctx.setLineDash([]);
    ctx.beginPath();
    ctx.arc(toX(p[p.length - 2]), toY(p[p.length - 1]), Math.max(4, v.r), 0, Math.PI * 2);
    ctx.stroke();
}

/** Wander A* destination: a gold dot, separate from the bot's current position. */
function drawWanderTarget(v, b) {
    if (!b.target) return;
    const { ctx } = v;
    ctx.fillStyle = COLORS.path;
    ctx.strokeStyle = "#171717";
    ctx.lineWidth = 2;
    ctx.beginPath();
    ctx.arc(v.toX(b.target.x), v.toY(b.target.y), Math.max(4, v.r * 0.9), 0, Math.PI * 2);
    ctx.fill();
    ctx.stroke();
}

function drawBot(v, s, b, mobs) {
    const { ctx } = v;
    const x = v.toX(b.x), y = v.toY(b.y);
    ctx.fillStyle = COLORS.self;
    ctx.beginPath();
    ctx.arc(x, y, v.r, 0, Math.PI * 2);
    ctx.fill();
    ctx.strokeStyle = "#ffffffaa";
    ctx.lineWidth = 1.5;
    ctx.stroke();
    drawNamePlate(v, s, b.name || "?", x, y + v.r, mobs);
}

/** Unity's ItemObtainedToast: icon + "Jellopy - 1 obtained." at the top middle, name-plate style. */
function drawToast(v, { text, icon }) {
    const { ctx } = v;
    const size = Math.max(10, Math.round(v.fs * 0.9)), img = image(icon), k = img ? size * 1.6 : 0;
    ctx.font = font(size, true);
    ctx.textBaseline = "middle";
    const w = ctx.measureText(text).width + (k ? k + 6 : 0), h = Math.max(size, k) + 8;
    const left = ctx.canvas.width / 2 - w / 2, top = 8 * dpr(), mid = top + h / 2;
    ctx.fillStyle = "#000000aa";
    ctx.fillRect(left - 6, top, w + 12, h);
    if (img) ctx.drawImage(img, left, mid - k / 2, k, k);
    ctx.fillStyle = COLORS.text;
    ctx.fillText(text, left + (k ? k + 6 : 0), mid);
}

/** Name centred under the bot dot. A monster under the plate fades it so the monster shows through. */
function drawNamePlate(v, s, label, x, below, mobs) {
    const { ctx } = v;
    const size = Math.max(9, Math.round(v.fs * 0.75));
    ctx.font = font(size);
    ctx.textBaseline = "middle";
    const w = ctx.measureText(label).width;
    const y = below + 3 + size / 2 + 2;
    const box = [x - w / 2 - 3, y - size / 2 - 2, x + w / 2 + 3, y + size / 2 + 2];
    const covered = mobs.some(m => overlaps(m.box, box));
    ctx.globalAlpha = plateFade(s, label, covered ? 0.65 : 1);
    ctx.fillStyle = "#000000aa";
    ctx.fillRect(box[0], box[1], box[2] - box[0], box[3] - box[1]);
    ctx.fillStyle = COLORS.text;
    ctx.fillText(label, x - w / 2, y);
    ctx.globalAlpha = 1;
}

/** Name-plate opacity per bot, eased toward its target each frame (~0.15 s) so the fade isn't a flicker. */
function plateFade(s, key, target) {
    const a = s.plates.get(key) ?? 1;
    const next = a + (target - a) * 0.2;
    s.plates.set(key, next);
    return next;
}

function botHover(v, b) {
    const name = b.name || "bot";
    const points = [{ x: v.toX(b.x), y: v.toY(b.y), name }];
    if (b.target) points.push({ x: v.toX(b.target.x), y: v.toY(b.target.y), name: `${name} A* target` });
    return points;
}

/** Top-left box: map title, then one row per bot with tiny HP (green/yellow/red) and SP bars. */
function drawLegend(v, payload, bots) {
    const { ctx, canvas, t, fs } = v;
    ctx.font = font(fs);
    const aspect = (canvas.clientWidth / Math.max(1, canvas.clientHeight)).toFixed(2);
    const title = `${payload.map}  ${t.w}×${t.h} cells  ·  shown ${canvas.clientWidth}×${canvas.clientHeight}px (${aspect}:1)`
        + `  ·  ${bots.length} bot(s)  ·  ${(payload.mobs || []).length} mob(s) in view`;
    const rowHeight = fs + 6, barWidth = Math.round(fs * 6), barHeight = Math.max(4, Math.round(fs * 0.4));
    const numbers = b => `${b.hp}/${b.maxHp}  ${b.sp}/${b.maxSp}`;
    const widest = f => Math.max(0, ...bots.map(b => ctx.measureText(f(b)).width));
    const nameWidth = widest(b => b.name || "?");
    const boxWidth = Math.max(ctx.measureText(title).width, bots.length ? nameWidth + barWidth + widest(numbers) + 20 : 0) + 10;

    ctx.textBaseline = "top";
    ctx.fillStyle = "#000000aa";
    ctx.fillRect(4, 4, boxWidth, fs + 8 + bots.length * rowHeight);
    ctx.fillStyle = COLORS.text;
    ctx.fillText(title, 9, 8);
    const barX = 9 + nameWidth + 8;
    const bar = (y, pct, color) => {
        ctx.fillStyle = "#ffffff22";
        ctx.fillRect(barX, y, barWidth, barHeight);
        ctx.fillStyle = color;
        ctx.fillRect(barX, y, barWidth * pct, barHeight);
    };
    bots.forEach((b, i) => {
        const top = 8 + fs + 4 + i * rowHeight;
        const hp = b.maxHp > 0 ? Math.min(1, b.hp / b.maxHp) : 0;
        const sp = b.maxSp > 0 ? Math.min(1, b.sp / b.maxSp) : 0;
        ctx.fillStyle = COLORS.text;
        ctx.fillText(b.name || "?", 9, top);
        bar(top + 1, hp, hpColor(hp));
        bar(top + 1 + barHeight + 2, sp, "#0dcaf0");
        ctx.fillStyle = COLORS.text;
        ctx.fillText(numbers(b), barX + barWidth + 8, top);
    });
    ctx.textBaseline = "alphabetic";
}

// ---------------------------------------------------------------------------------------------------------------
// Smooth movement
// ---------------------------------------------------------------------------------------------------------------

// The server sends cell positions ~10x/s, so drawn as-is bots hop cell to cell. Each entity glides toward its
// latest cell at walking pace; jumps over 12 cells (warp, Fly Wing, a new mob) snap instead of sliding.
function glide(s, key, x, y, dt, now) {
    let g = s.glides.get(key);
    if (!g || Math.max(Math.abs(x - g.x), Math.abs(y - g.y)) > 12) s.glides.set(key, g = { x, y });
    else {
        const dx = x - g.x, dy = y - g.y, d = Math.hypot(dx, dy);
        const len = Math.max(7, d / 0.2) * dt; // ~7 cells/s, faster if we fell behind
        if (d <= len) { g.x = x; g.y = y; } else { g.x += dx / d * len; g.y += dy / d * len; }
    }
    g.seen = now;
    return { x: g.x, y: g.y };
}

function glideAll(s, p, dt, now) {
    const out = {
        ...p,
        bots: (p.bots || []).map(b => ({ ...b, ...glide(s, "b:" + b.name, b.x, b.y, dt, now) })),
        mobs: (p.mobs || []).map(m => ({ ...m, ...glide(s, "m:" + m.id, m.x, m.y, dt, now) })),
    };
    for (const [k, g] of s.glides) if (now - g.seen > 2000) s.glides.delete(k);
    return out;
}

// ---------------------------------------------------------------------------------------------------------------
// Damage numbers, ported from the Unity client
// ---------------------------------------------------------------------------------------------------------------

function nearestBot(bots, m) {
    let near = null, best = Infinity;
    for (const b of bots) {
        const d = Math.hypot(b.x - m.x, b.y - m.y);
        if (d < best) { best = d; near = b; }
    }
    return near;
}

/** A dueled monster's HP dropped since last frame: one white number per hit (0.2 s apart), flying away from the
 *  nearest bot (the attacker), plus the yellow running total on multi-hits. */
function spawnDamageNumbers(v, s, mobs, bots, now) {
    const missed = (key, count, x, y, color) => {
        const before = s.lastMiss.get(key);
        s.lastMiss.set(key, count || 0);
        if (before != null && count > before) s.numbers.push({ path: MISS_PATH, value: "Miss", x, y, born: now, color });
    };
    for (const { m, x, barY } of mobs) missed(m.id, m.misses, x, barY - 6);
    for (const b of bots) {
        const key = "b:" + b.name, x = v.toX(b.x), y = v.toY(b.y) - v.r - 6;
        missed(key, b.misses, x, y, RED);
        const before = s.lastHp.get(key);
        s.lastHp.set(key, b.hp);
        if (before != null && b.hp < before) // ponytail: one number per HP drop; regen ticks between hits can net them
            s.numbers.push({ path: DAMAGE_PATH, value: before - b.hp, x, y, dir: Math.random() < 0.5 ? -1 : 1, born: now, color: RED });
    }
    for (const { m, x, barY } of mobs) {
        if (m.hp == null) continue;
        const before = s.lastHp.get(m.id);
        s.lastHp.set(m.id, m.hp);
        if (before == null || m.hp >= before) continue;
        const near = nearestBot(bots, m);
        const dir = near && near.x !== m.x ? Math.sign(m.x - near.x) : (Math.random() < 0.5 ? -1 : 1);
        const hits = Math.max(1, m.hits || 1), per = Math.round((before - m.hp) / hits);
        for (let h = 0; h < hits; h++) {
            const born = now + HIT_INTERVAL_MS * h;
            s.numbers.push({ path: DAMAGE_PATH, value: per, x, y: barY - 6, dir, born });
            if (hits > 1) s.numbers.push({ path: COMBO_PATH, value: per * (h + 1), id: m.id, born });
        }
    }
}

function drawDamageNumbers(v, s, mobs) {
    const { ctx, now } = v;
    const barAt = new Map(mobs.map(({ m, x, barY }) => [m.id, [x, barY - 6]]));
    // A combo total that has started ends the older totals on the same monster.
    const newestCombo = new Map();
    for (const n of s.numbers)
        if (n.id != null && n.born <= now && n.born > (newestCombo.get(n.id) ?? -1)) newestCombo.set(n.id, n.born);
    const lifeOf = n => (now - n.born) / 1000 / n.path.tween;
    s.numbers = s.numbers.filter(n => lifeOf(n) <= 1 && !(n.id != null && n.born < (newestCombo.get(n.id) ?? -1)));

    ctx.textAlign = "center";
    ctx.textBaseline = "alphabetic";
    for (const n of s.numbers) {
        const life = lifeOf(n);
        if (life < 0) continue; // a later hit of a multi-hit, not shown yet
        if (n.id != null && barAt.has(n.id)) [n.x, n.y] = barAt.get(n.id); // the total rides with the monster
        drawNumber(v, n, life);
    }
    ctx.globalAlpha = 1;
    ctx.textAlign = "start";
}

// ---------------------------------------------------------------------------------------------------------------
// Hover tooltips
// ---------------------------------------------------------------------------------------------------------------

/** The native tooltip names the nearest dot under the pointer (monster or bot), else the spawn areas under it. */
function attachHover(canvas, s) {
    if (s.hoverAttached) return;
    s.hoverAttached = true;
    canvas.addEventListener("mousemove", e => {
        const rect = canvas.getBoundingClientRect();
        const k = canvas.width / rect.width;
        const px = (e.clientX - rect.left) * k, py = (e.clientY - rect.top) * k;
        canvas.title = pointAt(s.hover, px, py, 10 * k)?.name ?? spawnAreasAt(s.hover, px, py);
    });
}

function pointAt(targets, px, py, radius) {
    let best = null, bestDistance = radius;
    for (const h of targets) {
        if (h.left != null) continue;
        const d = Math.hypot(h.x - px, h.y - py);
        if (d < bestDistance) { bestDistance = d; best = h; }
    }
    return best;
}

// Overlapping spawn zones can cover most of the map: prefer the smallest, and list a few.
function spawnAreasAt(targets, px, py) {
    const areas = targets.filter(h => h.left != null
        && px >= h.left && px <= h.left + h.width && py >= h.top && py <= h.top + h.height)
        .sort((a, b) => a.width * a.height - b.width * b.height);
    if (!areas.length) return "";
    return areas.slice(0, 3).map(h => h.name).join("\n") + (areas.length > 3 ? `\n+${areas.length - 3} more spawn areas` : "");
}
