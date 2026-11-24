// Simulation tab sprites (visual only): monsters and the bot drawn and posed as the Unity client does it, from what the
// replay recorded. Nothing here changes what the replay says; sim.js asks it for layers to draw. The minimap (map.js)
// imports its sprites, HP bars, damage numbers and shouts from here: the simulation is the source.
//  - Monsters: the imported sprite actions (/monster-anim, as the minimap). A swing starts the server's attack motion time
//    before its damage lands and is sped up so its attack frame connects then (ServerControllable.SetAttackAnimationSpeed);
//    a hit taken flinches; dead plays once and fades.
//  - The bot: imported body + head (hair colour), raw .spr/.act headgear, shield, weapon and swing trail (SimSprites.cs),
//    attached like RoSpriteAnimator children: same action and frame as the body, offset by the frames' "Pos" anchors, sorted
//    by SpriteOrder (behind the body on .imf foreground frames). Attack1/2/3 from the job + weapon class; skills pose
//    per the client's skill handlers; cast bars hold the cast pose.

// Small canvas helpers, shared with the minimap.
export const dpr = () => window.devicePixelRatio || 1;
export const font = (px, bold = false) => `${bold ? "bold " : ""}${px}px system-ui, sans-serif`;
export const clamp01 = v => Math.max(0, Math.min(1, v));
export const hpColor = pct => pct <= 0.25 ? "#dc3545" : pct <= 0.5 ? "#ffc107" : "#198754";

// Client motion layout (RoAnimationHelper.GetMotionIdForSprite): action = motion + facing.
export const MON = { idle: 0, walk: 8, attack: 16, hit: 24, dead: 32 };
export const PLR = { idle: 0, walk: 8, sit: 16, standby: 32, throw: 40, hit: 48, dead: 64, attack2: 80, attack1: 88, casting: 96 };

/** RO Direction (South 0, SouthWest 1 ... SouthEast 7) from a world offset, Y up. */
export function facing(dx, dy) { if (!dx && !dy) return 0; return [6, 5, 4, 3, 2, 1, 0, 7][((Math.round(Math.atan2(dy, dx) / (Math.PI / 4)) % 8) + 8) % 8]; }

export function drawLayers(g, layers) {
    g.imageSmoothingEnabled = true;
    for (const { img, r, left, top, w, h, mirror, alpha, angle } of layers) {
        if (alpha <= 0 || !img) continue;
        g.save(); g.globalAlpha = Math.min(1, alpha);
        if (angle) { g.translate(left + w / 2, top + h / 2); g.rotate(angle * Math.PI / 180); g.translate(-(left + w / 2), -(top + h / 2)); }
        if (mirror) { g.translate(left * 2 + w, 0); g.scale(-1, 1); }
        g.drawImage(img, r[0], r[1], r[2], r[3], left, top, w, h);
        g.restore();
    }
}

const loadImg = src => new Promise(ok => { const i = new Image(); i.onload = () => ok(i); i.onerror = () => ok(null); i.src = src; });

// ---- monsters ----
const anims = new Map();
/** Imported monster actions + atlas for a species; null while loading or with no sprite (sim.js then draws a dot). */
export function animOf(name) {
    if (!anims.has(name)) {
        anims.set(name, null);
        fetch("monster-anim/" + encodeURIComponent(name)).then(r => r.ok ? r.json() : null)
            .then(async a => { if (a) { const img = await loadImg(a.atlas); if (img) anims.set(name, { ...a, img }); } }, () => {});
    }
    return anims.get(name);
}

/** Per monster: flinches (hits landed on it) and swings ({t0: swing start, speed}) from the replay. */
export function indexMonsters(rp) {
    const hits = new Map(), swings = new Map(), push = (m, id, v) => { let a = m.get(id); if (!a) m.set(id, a = []); a.push(v); };
    for (const e of rp.events) {
        if (e.kind === "hit" && e.mob) push(hits, e.mob, e.t);
        else if (e.kind === "hurt" && e.mob) push(swings, e.mob, { t0: e.t - (e.detail?.motion || 0), motion: e.detail?.motion || 0 });
    }
    for (const l of swings.values()) l.sort((a, b) => a.t0 - b.t0);
    return { hits, swings };
}

export const lastLE = (arr, t, key = x => x) => { let lo = 0, hi = arr.length - 1, r = -1; while (lo <= hi) { const m = (lo + hi) >> 1; if (key(arr[m]) <= t) { r = m; lo = m + 1; } else hi = m - 1; } return r; };
/** Frame i of an action played once from age (ms) at speed (delay multiplier); null once it's over unless hold. */
function once(act, age, speed, hold) {
    if (!act?.frames.length || !(age >= 0) || !isFinite(age)) return null;
    const i = Math.floor(age / Math.max(1, act.delay * speed));
    return i < act.frames.length ? i : hold ? act.frames.length - 1 : null;
}
const loopFrame = (act, ms) => act?.frames.length ? Math.floor(ms / Math.max(1, act.delay)) % act.frames.length : 0;

/** The monster's pose, like RoSpriteAnimator: dead plays once and holds its last frame while the corpse fades; a swing
 *  or a flinch plays once from when it happened (the newer wins; the swing at `speed`); else walk or idle loop on loopMs.
 *  Ages in ms (Infinity: none), dir the RO facing. The minimap poses its monsters with this too. */
export function monsterFrame(a, dir, { dead = null, atk = Infinity, hit = Infinity, speed = 1, walk = false }, loopMs) {
    const act = mo => a.actions[mo + dir] || a.actions[mo];
    const one = (mo, age, sp, hold) => { const ac = act(mo), i = once(ac, age, sp, hold); return i != null ? ac.frames[i] : null; };
    let frame, alpha = 1;
    if (dead != null) { frame = one(MON.dead, dead, 1, true); alpha = clamp01((1900 - dead) / 700); }
    else frame = atk <= hit ? one(MON.attack, atk, speed) || one(MON.hit, hit, 1) : one(MON.hit, hit, 1) || one(MON.attack, atk, speed);
    if (!frame) { const ac = act(walk ? MON.walk : MON.idle); if (!ac?.frames.length) return null; frame = ac.frames[loopFrame(ac, loopMs)]; }
    return { frame, alpha };
}

/** Layers for a monster at t. The simulator has no monster facing: it faces the bot while fighting it (a swing or flinch
 *  within 3 s), else south. px: screen pixels per sprite pixel; the client draws sprites at 50 px per tile, scaled 1.5
 *  (x Size for monsters). */
export function monsterLayers(ix, a, id, m, t, me, s, px) {
    const flinchAt = (() => { const l = ix.hits.get(id); const i = l ? lastLE(l, t) : -1; return i >= 0 ? l[i] : -Infinity; })();
    const sw = (() => { const l = ix.swings.get(id); const i = l ? lastLE(l, t, x => x.t0) : -1; return i >= 0 ? l[i] : null; })();
    const dir = (sw && t - sw.t0 < 3) || t - flinchAt < 3 || !m.alive ? facing(me.x - m.x, me.y - m.y) : 0;
    // ServerControllable.SetAttackAnimationSpeed: AnimSpeed = motionTime / (AttackFrameTime / 1000), so the attack frame lands with the damage.
    const speed = sw && sw.motion > 0 && a.attackFrameTime > 0 ? sw.motion * 1000 / a.attackFrameTime : 1;
    const pose = monsterFrame(a, dir, { dead: m.alive ? null : (t - m.diedAt) * 1000, atk: sw ? (t - sw.t0) * 1000 : Infinity, hit: (t - flinchAt) * 1000, speed }, t * 1000);
    const layers = pose ? place(a, pose.frame, 0, 0, s.x, s.y + 2, px * (a.size || 1), pose.alpha) : [];
    return layers.length ? { layers, box: box(layers) } : null;
}

export function place(spr, frame, ox, oy, x, feet, k, alpha) {
    return frame.filter(l => l[0] >= 0 && spr.rects[l[0]]).map(([i, lx, ly, mirror, scx, scy, al, angle]) => {
        const r = spr.rects[i], w = r[2] * Math.abs(scx) * k, h = r[3] * Math.abs(scy) * k;
        return { img: spr.img, r, left: x + (lx + ox) * k - w / 2, top: feet + (ly + oy) * k - h / 2, w, h, mirror: !!mirror !== scx < 0, alpha: al * alpha, angle: angle || 0 };
    });
}
export const box = ls => [Math.min(...ls.map(l => l.left)), Math.min(...ls.map(l => l.top)), Math.max(...ls.map(l => l.left + l.w)), Math.max(...ls.map(l => l.top + l.h))];

// ---- raw .spr / .act (port of the client's importer: Editor/RagnarokSpriteLoader.cs, RagnarokAnimLoader.cs) ----
function decodeSpr(buf) {
    const d = new DataView(buf), u8 = new Uint8Array(buf);
    const ver = d.getUint8(3) * 10 + d.getUint8(2);
    const indexCount = d.getUint16(4, true), rgbaCount = ver > 11 ? d.getUint16(6, true) : 0;
    let p = ver > 11 ? 8 : 6;
    const frames = [];
    for (let i = 0; i < indexCount; i++) {
        const w = d.getUint16(p, true), h = d.getUint16(p + 2, true); p += 4;
        const data = new Uint8Array(w * h);
        if (ver < 21) { data.set(u8.subarray(p, p + w * h)); p += w * h; }
        else {
            const end = d.getUint16(p, true) + p + 2; p += 2;
            let o = 0;
            while (p < end) {
                const c = u8[p++]; data[o++] = c;
                if (c === 0) { const n = u8[p++]; if (n === 0) data[o++] = n; else for (let j = 1; j < n; j++) data[o++] = c; }
            }
        }
        frames.push({ w, h, data, indexed: true });
    }
    for (let i = 0; i < rgbaCount; i++) {
        const w = d.getInt16(p, true), h = d.getInt16(p + 2, true); p += 4;
        frames.push({ w, h, data: u8.subarray(p, p + w * h * 4), indexed: false }); p += w * h * 4;
    }
    const pal = ver > 10 ? u8.subarray(buf.byteLength - 1024) : null;
    return { frames, indexCount, pal };
}

function decodeAct(buf, indexCount) {
    const d = new DataView(buf); let p = 2;
    const ver = d.getUint8(3) * 10 + d.getUint8(2); p = 4;
    const i32 = () => { const v = d.getInt32(p, true); p += 4; return v; }, f32 = () => { const v = d.getFloat32(p, true); p += 4; return v; };
    const count = d.getUint16(p, true); p += 2 + 10;
    const actions = [];
    for (let a = 0; a < count; a++) {
        const act = { delay: 150, frames: [], anchors: [], sounds: [] };
        const fc = d.getUint32(p, true); p += 4;
        for (let f = 0; f < fc; f++) {
            p += 32;
            const lc = d.getUint32(p, true); p += 4;
            const layers = [];
            for (let l = 0; l < lc; l++) {
                const x = i32(), y = i32(); let index = i32(); const mirror = i32() !== 0 ? 1 : 0;
                let alpha = 1, sx = 1, sy = 1, angle = 0;
                if (ver >= 20) {
                    alpha = d.getUint8(p + 3) / 255; p += 4;
                    sx = f32(); sy = ver > 23 ? f32() : sx; angle = i32();
                    if (i32() === 1) index += indexCount;
                    if (ver >= 25) p += 8;
                }
                layers.push([index, x, y, mirror, sx, sy, alpha, angle]);
            }
            act.sounds.push(ver >= 20 ? i32() : -1);
            let anchor = [0, 0];
            if (ver >= 23) { const pc = i32(); for (let k = 0; k < pc; k++) { i32(); const ax = i32(), ay = i32(); i32(); if (k === 0) anchor = [ax, ay]; } }
            act.frames.push(layers); act.anchors.push(anchor);
        }
        actions.push(act);
    }
    if (ver >= 21) { const n = i32(); p += 40 * n; }
    if (ver >= 22) for (const act of actions) act.delay = Math.trunc(f32() * 24);
    return actions;
}

/** A raw sprite as {img (an atlas canvas), rects, actions} - the imported format. */
async function loadRaw(url) {
    const get = u => fetch(u).then(r => r.ok ? r.arrayBuffer() : null, () => null);
    const [sb, ab] = await Promise.all([get(url + ".spr"), get(url + ".act")]);
    if (!sb || !ab) return null;
    const spr = decodeSpr(sb), actions = decodeAct(ab, spr.indexCount);
    const W = 1024, rects = []; let x = 0, y = 0, rowH = 0;
    for (const f of spr.frames) { if (x + f.w > W) { x = 0; y += rowH; rowH = 0; } rects.push([x, y, f.w, f.h]); x += f.w; rowH = Math.max(rowH, f.h); }
    const c = document.createElement("canvas"); c.width = W; c.height = Math.max(1, y + rowH);
    const g = c.getContext("2d");
    spr.frames.forEach((f, i) => {
        if (!f.w || !f.h) return;
        const im = g.createImageData(f.w, f.h), o = im.data;
        for (let yy = 0; yy < f.h; yy++) for (let xx = 0; xx < f.w; xx++) {
            const q = (xx + yy * f.w) * 4;
            if (f.indexed) { const ci = f.data[xx + yy * f.w]; if (ci && spr.pal) { o[q] = spr.pal[ci * 4]; o[q + 1] = spr.pal[ci * 4 + 1]; o[q + 2] = spr.pal[ci * 4 + 2]; o[q + 3] = 255; } }
            else { const s = (xx + (f.h - yy - 1) * f.w) * 4; o[q] = f.data[s + 3]; o[q + 1] = f.data[s + 2]; o[q + 2] = f.data[s + 1]; o[q + 3] = f.data[s]; } // RGBA frames are stored bottom-up, ABGR
        }
        g.putImageData(im, rects[i][0], rects[i][1]);
    });
    return { img: c, rects, actions };
}

// ---- the bot ----
/** spec: SimSprites.Player (body, head, parts, attackMotion, skills, look). Resolves to the drawable player or null. */
export async function loadPlayer(spec) {
    if (!spec?.body || !spec?.head) return null;
    const [bi, hi] = await Promise.all([loadImg(spec.body.atlas), loadImg(spec.head.atlas)]);
    if (!bi || !hi) return null;
    const parts = (await Promise.all(spec.parts.map(async p => { const s = await loadRaw(p.url); return s && { ...s, order: p.order, what: p.what }; }))).filter(Boolean);
    return {
        body: { ...spec.body, img: bi }, children: [{ ...spec.head, img: hi, order: 1, what: "head" }, ...parts],
        attack: spec.attackMotion === 2 ? PLR.attack2 : PLR.attack1, skills: spec.skills || {}, look: spec.look,
        parts: spec.parts.map(p => p.what + (parts.some(q => q.what === p.what) ? "" : " (failed to load)")),
    };
}

// Player attack speed (PacketAttack + ServerControllable.SetAttackAnimationSpeed): motionTime padded by 10% (at most +0.1 s),
// capped at 1.5 s, against the 0.6 s a player sprite's attack is drawn for.
const playerSpeed = m => m > 0 ? Math.min(1.5, Math.min(m * 1.1, m + 0.1)) / 0.6 : 1;

/** The bot's one-shot motions from the replay, by start time: swings (auto-attack hits started motion seconds before
 *  they landed; skills on execution, per the client's handler), cast bars (held), flinches, deaths. */
export function indexPlayer(rp, P) {
    const out = [], ev = rp.events, how = s => P.skills[s] || P.skills["*"] || { execute: "attack", cast: "standby" };
    for (let i = 0; i < ev.length; i++) {
        const e = ev[i];
        if (e.kind === "hit" && e.mob && !e.skill) out.push({ t0: e.t - (e.detail?.motion || 0), motion: P.attack, speed: playerSpeed(e.detail?.motion) });
        else if (e.kind === "executed" && e.skill) {
            const h = how(e.skill).execute;
            if (h === "attack") { const hit = ev.slice(i + 1, i + 40).find(x => x.kind === "hit" && x.skill === e.skill && x.t - e.t < 3); out.push({ t0: e.t, motion: P.attack, speed: playerSpeed(hit?.detail?.motion || 0.6) }); }
            else if (h === "skill") out.push({ t0: e.t, motion: PLR.casting, speed: 1 });
            else if (h === "throw") out.push({ t0: e.t, motion: PLR.throw, speed: 1 });
        }
        else if (e.kind === "castbar" && e.skill) {
            const c = how(e.skill).cast, end = ev.slice(i + 1).find(x => x.t > e.t && ["executed", "failed", "rejected", "death"].includes(x.kind));
            out.push({ t0: e.t, until: Math.min(e.t + (e.amount || 0) / 1000, end ? end.t : Infinity), motion: c === "attack" ? P.attack : c === "standby" ? PLR.standby : PLR.casting, hold: c !== "standby" });
        }
        else if (e.kind === "hurt" && e.amount > 0) out.push({ t0: e.t, motion: PLR.hit, speed: 1 });
        else if (e.kind === "death") out.push({ t0: e.t, until: e.t + 2, motion: PLR.dead, speed: 1, hold: true, dead: true });
    }
    return out.sort((a, b) => a.t0 - b.t0);
}

/** The bot's layers at t (b: its screen point). Facing: toward the target while fighting, else its last step. */
export function playerLayers(P, moves, rp, t, me, b, act, tm, px) {
    const tr = rp.track; let dir = 0;
    const moving = me.x !== me.cx || me.y !== me.cy;
    if (act === "fight" && tm) dir = facing(tm.x - me.x, tm.y - me.y);
    else if (moving) dir = facing(me.x - me.cx, me.y - me.cy);
    else for (let i = lastLE(tr, t, s => s.t); i > 0; i--) if (tr[i].x !== tr[i - 1].x || tr[i].y !== tr[i - 1].y) { dir = facing(tr[i].x - tr[i - 1].x, tr[i].y - tr[i - 1].y); break; }
    const A = P.body.actions, action = m => A[m + dir] || A[m];
    let motion = null, fi = 0;
    // The newest one-shot motion still playing wins (a cast pose is frozen on its first frame, a death holds its last).
    for (let i = lastLE(moves, t, m => m.t0), n = 0; i >= 0 && n < 12; i--, n++) {
        const m = moves[i], age = (t - m.t0) * 1000;
        if (m.until != null && t >= m.until) continue;
        if (m.until != null && m.hold && !m.dead) { motion = m.motion; fi = 0; break; }
        const f = once(action(m.motion), age, m.speed || 1, m.hold);
        if (f != null) { motion = m.motion; fi = f; break; }
    }
    if (motion == null) {
        // Idle and sit hold the head-facing frame (0: straight ahead); walk and battle stance loop.
        motion = moving ? PLR.walk : act === "rest" ? PLR.sit : act === "fight" ? PLR.standby : PLR.idle;
        fi = motion === PLR.walk || motion === PLR.standby ? loopFrame(action(motion), t * 1000) : 0;
    }
    const ba = action(motion); if (!ba?.frames.length) return null;
    fi = Math.min(fi, ba.frames.length - 1);
    const k = px, feet = b.y + 2;
    const fg = ba.foreground?.[fi], anchor = ba.anchors?.[fi] || [0, 0];
    const layers = [{ order: 0, ls: place(P.body, ba.frames[fi], 0, 0, b.x, feet, k, 1) }];
    for (const c of P.children) {
        // RoSpriteAnimator.ChildSetFrameData / ChildUpdate: the body's action (wrapped) and frame; offset body anchor - own anchor.
        let ai = motion + dir; if (ai > c.actions.length - 1) ai %= c.actions.length;
        const ca = c.actions[ai]; if (!ca?.frames.length) continue;
        const cf = Math.min(fi, ca.frames.length - 1), own = ca.anchors?.[cf] || [0, 0];
        const [ox, oy] = own[0] || own[1] ? [anchor[0] - own[0], anchor[1] - own[1]] : [0, 0];
        layers.push({ order: fg ? c.order - 10 : c.order, ls: place(c, ca.frames[cf], ox, oy, b.x, feet, k, 1) });
    }
    return layers.sort((x, y) => x.order - y.order).flatMap(x => x.ls);
}

// ---- HP bars, damage numbers and skill shouts (ported from the Unity client; the minimap draws them with these too) ----

// SliderBar's damage trail: on a drop the bar falls at once and a white chunk stays behind, draining with accel += 20/s
// each second (10·τ² after τ s) until it meets the bar; drops while it drains don't restart it. tr: per-bar state
// ({} to start), fed the bar's fraction in time order (now in s). Returns where the white chunk ends.
export function hpTrail(tr, pct, now) {
    let d = tr.at == null ? (tr.last ?? pct) : tr.from - 10 * (now - tr.at) ** 2;
    if (d <= pct) { tr.at = null; d = pct; }
    else if (tr.at == null) { tr.from = d; tr.at = now; }
    tr.last = pct;
    return d;
}

/** An HP bar centred on x with its bottom at `bottom`, coloured by fraction, with the white trail up to `delayed`. */
export function drawHpBar(g, x, bottom, w, h, pct, delayed) {
    const left = x - w / 2, top = bottom - h;
    pct = clamp01(pct); delayed = clamp01(delayed ?? pct);
    g.fillStyle = "#000000cc"; g.fillRect(left - 1, top - 1, w + 2, h + 2);
    if (delayed > pct) { g.fillStyle = "#ffffff"; g.fillRect(left, top, w * delayed, h); }
    g.fillStyle = hpColor(pct); g.fillRect(left, top, w * pct, h);
}

// DamageIndicator.OnUpdate driven by Data/FlyingText/*.asset. Keys are Unity AnimationCurve keys
// [time, value, inSlope, outSlope] over a 0..1 lifetime of `tween` seconds.
// Damage: starts where it spawned, drifts sideways away from the attacker while arcing up and down, punches in at
// 5x size then settles, and fades out.
export const DAMAGE_PATH = {
    trajectory: [[0.0019874573, -0.0014190674, 4.052195, 4.052195], [0.9942106, 0.011132479, -3.9042158, -3.9042158]],
    size: [[-0.0009186715, 5.0193124, -14.1374855, -14.1374855], [0.3964636, 0.99757385, -0.60127693, -0.3272466], [1, 0.8000686, -0.3272466, -0.3272466]],
    alpha: [[0, 1, -0.00026645948, -0.00026645948], [0.11452991, 0.9999695, -0.00026645948, -0.075074494], [0.56060225, 0.70585805, -1.3047085, -1.4544252], [1.0042865, 0.006022349, -0.2006653, -0.2006653]],
    tween: 1, height: 4, distance: 4, color: "#ffffff",
    maxSize: 2.5, // minimap: Unity pops to 5x, capped so the yellow total stays the biggest number
};
// Multi-hit (Double Attack): EntityMessageQueue.SendDamageEvent shows each hit 0.2 s apart, and ServerControllable
// .AttachDamageIndicator adds a yellow running total (ComboPath.asset) that rides with the monster, each new hit
// replacing the previous total (ComboIndicator.EndDamageIndicator).
export const COMBO_PATH = {
    trajectory: [[0.0039138794, 0.51712036, 0.4858895, 0.4858895], [0.9932951, 0.9978503, 0.4858895, 0.4858895]],
    size: [[0.0007741172, 0.0036451616, 18.02707, 18.02707], [0.10320092, 1.4825153, 2.0349398, 0.004584544], [0.9957344, 1.4866072, 0.004584544, 0.004584544]],
    alpha: [[0, 1, -0.00006110869, -0.00006110869], [0.49939838, 0.9999695, -0.00006110869, -0.075074494], [0.75252, 0.70585805, -2.4358597, -2.8889782], [1.0042865, 0.006022349, -0.2006653, -0.2006653]],
    tween: 3, height: 6, distance: 0, color: "#ffff00",
    scale: 2, // minimap: settles at ~3x, above any (capped) white hit
};
export const HIT_INTERVAL_MS = 200;
// MissPath.asset: rises straight up at constant size, holds, then fades over 1.5 s.
export const MISS_PATH = {
    trajectory: [[-0.0012550354, -0.0013427734, 1.0056698, 1.0056698], [1.0009416, 1.0065361, 1.0056698, 1.0056698]],
    size: [[0, 1, 0, 0], [1, 1, 0, 0]],
    alpha: [[0, 1, 0.009446392, 0.009446392], [0.4999364, 1.0047226, 0.009446392, -0.42355192], [1.0024122, -0.0012597442, -4.6746316, -4.6746316]],
    tween: 1.5, height: 5, distance: 0, color: "#ffffff", scale: 1.5,
};
// ServerControllable: damage and misses on a player (an ally) are red.
export const RED = "#ff3b3b";

/** Unity's AnimationCurve.Evaluate: cubic Hermite between keys, clamped at the ends. */
function evalCurve(keys, t) {
    if (t <= keys[0][0]) return keys[0][1];
    for (let i = 0; i < keys.length - 1; i++) {
        const [t0, v0, , o0] = keys[i], [t1, v1, i1] = keys[i + 1];
        if (t > t1) continue;
        const d = t1 - t0, u = (t - t0) / d, u2 = u * u, u3 = u2 * u;
        return (2 * u3 - 3 * u2 + 1) * v0 + (u3 - 2 * u2 + u) * d * o0 + (-2 * u3 + 3 * u2) * v1 + (u3 - u2) * d * i1;
    }
    return keys[keys.length - 1][1];
}

export function drawNumber(v, n, life) { // v: { ctx, spriteHeight } in device pixels
    const { ctx } = v, P = n.path;
    const unit = v.spriteHeight * 0.5; // one Unity world unit on the minimap, relative to the sprite
    const size = Math.min(evalCurve(P.size, life), P.maxSize ?? Infinity) * (P.scale ?? 1);
    const x = n.x + (n.dir || 0) * P.distance * unit * life * 0.5;
    const y = n.y - evalCurve(P.trajectory, life) * P.height * unit * 0.6; // minimap: Unity's full arc (4 units) read too high, 0.3 too low
    ctx.globalAlpha = clamp01(evalCurve(P.alpha, life));
    ctx.font = font(Math.round(9 * dpr() * size), true);
    ctx.lineWidth = Math.max(2, 2.5 * size);
    ctx.strokeStyle = "#000000";
    ctx.strokeText(String(n.value), x, y);
    ctx.fillStyle = n.color || P.color;
    ctx.fillText(String(n.value), x, y);
}


/** Skill shout ("Bash!!") above the dot, styled like the name plate. */
export function drawShout(v, text, x, above) {
    const { ctx } = v;
    const size = Math.max(9, Math.round(v.fs * 0.75));
    ctx.font = font(size, true);
    ctx.textBaseline = "middle";
    const w = ctx.measureText(text).width;
    const y = above - 3 - size / 2 - 2;
    ctx.fillStyle = "#000000aa";
    ctx.fillRect(x - w / 2 - 3, y - size / 2 - 2, w + 6, size + 4);
    ctx.fillStyle = "#cbd2dc";
    ctx.fillText(text, x - w / 2, y);
}

