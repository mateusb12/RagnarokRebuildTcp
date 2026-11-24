// Simulation tab skill effects (visual only), from the Unity client's own data: its converted STR effects
// (Assets/Effects/{name}.asset, served as JSON by /sim-effect, SimEffects.cs) played the way Objects/RoEffectRenderer.cs
// plays them. Which effect a skill fires, on whom and when, is read from the client's skill handlers. Effects the client
// builds in C# code (DefaultSkillCastEffect, HitEffect, CastEffect, HealEffect, ...) are not drawn here.
// Everything is a function of replay time t, so seeking and playing backwards show the same frame.
const { lastLE } = await import("./sim-sprites.js" + new URL(import.meta.url).search);

// SkillHandlers/Handlers/**: ExecuteSkillTargeted -> CameraFollower.AttachEffectToEntity(name, target or caster).
const ON_EXECUTE = {
    Mammonite: ["Mammonite", "target"], LightningBolt: ["LightningBolt", "target"],        // MammoniteHandler, LightningBoltHandler
    ImproveConcentration: ["Concentration", "self"], Silence: ["SilenceAttack", "target"], // ImproveConcentrationHandler, SilenceAttackHandler
};
// OnHitEffect -> SendElementalHitEffect (one per hit, 0.2 s apart) -> ServerControllable.OnMessageElementalHit's STR;
// FireBolt: FireArrow attaches "firehit1" once per bolt, 12 frames apart.
const ON_HIT = { LightningBolt: "WindHit", FireBolt: "firehit1", FireAttack: "firehit1", WindAttack: "WindHit", EarthAttack: "EarthHit", HolyAttack: "HolyHit" };
const WINDOW = 4; // s; the longest effect wired here (LightningBolt) runs 130 frames at 60 fps

const cache = new Map();
function get(name) {
    if (!cache.has(name)) {
        cache.set(name, null);
        fetch("sim-effect/" + encodeURIComponent(name)).then(r => r.ok ? r.json() : null).then(fx => {
            if (!fx) return;
            const c = document.createElement("canvas"); c.width = fx.width; c.height = fx.height;
            const px = Uint8ClampedArray.from(atob(fx.rgba), ch => ch.charCodeAt(0));
            c.getContext("2d").putImageData(new ImageData(px, fx.width, fx.height), 0, 0);
            cache.set(name, { ...fx, rgba: null, img: c, tints: new Map() });
        }, () => {});
    }
    return cache.get(name);
}

/** The effects a replay fires: [{t0, name, on: 0 the bot | monster id}], by start time. */
export function index(rp) {
    const ev = rp.events, out = [];
    for (let i = 0; i < ev.length; i++) {
        const e = ev[i], ex = ON_EXECUTE[e.skill], hit = ON_HIT[e.skill];
        if (e.kind === "executed" && ex) {
            const h = ex[1] === "self" ? null : ev.slice(i + 1, i + 60).find(x => x.kind === "hit" && x.skill === e.skill && x.t - e.t < 3);
            const on = ex[1] === "self" ? 0 : h?.mob ?? ev.slice(0, i).reverse().find(x => x.kind === "pick")?.mob;
            if (on != null) out.push({ t0: e.t, name: ex[0], on });
        }
        if (e.kind === "hurt" && ex && e.mob) out.push({ t0: e.t - (e.detail?.motion || 0), name: ex[0], on: ex[1] === "self" ? e.mob : 0 }); // a monster's cast on the bot
        if ((e.kind === "hit" || e.kind === "hurt") && hit && e.amount > 0) {
            const on = e.kind === "hit" ? e.mob : 0, n = Math.max(1, e.detail?.hits || 1);
            for (let k = 0; k < n; k++) out.push({ t0: e.t + 0.2 * k, name: hit, on });
        }
    }
    return out.sort((a, b) => a.t0 - b.t0);
}

/** Draws the effects running at t. v: the viewer (posAt, mobAt, toScreen, zoom). One world unit (a cell) is
 *  max(zoom, 8) px, as the sprites; an STR's height above the feet is drawn screen-up. */
export function draw(g, v, list, t) {
    const U = Math.max(v.zoom, 8);
    for (let i = lastLE(list, t, f => f.t0); i >= 0 && list[i].t0 > t - WINDOW; i--) {
        const f = list[i], fx = get(f.name); if (!fx) continue;
        const frame = Math.floor((t - f.t0) * fx.frameRate) + 1; // RoEffectRenderer starts at time 1/FrameRate
        if (frame > fx.maxKey) continue;
        const p = f.on ? v.mobAt(f.on, t) : v.posAt(t); if (!p) continue;
        const s = v.toScreen(p.x, p.y);
        drawStr(g, fx, frame, s.x, s.y + 2, U);
    }
}

// RoEffectRenderer.UpdateAnimationLayer: the newest key (Type 0) at or before the frame, moved by the following delta key
// (Type 1) times the frames since; without a delta the key holds until the layer's last key.
function drawStr(g, fx, frame, x, y, U) {
    const base = g.getTransform();
    for (const layer of fx.layers) {
        const A = layer.animations; if (!A.length) continue;
        let start = -1, next = -1, lastFrame = 0, lastSource = 0;
        A.forEach((a, i) => { if (a.frame < frame) { if (a.type === 0) start = i; if (a.type === 1) next = i; } lastFrame = Math.max(lastFrame, a.frame); if (a.type === 0) lastSource = Math.max(lastSource, a.frame); });
        if (start < 0 || (next < 0 && lastFrame < frame)) continue;
        const from = A[start], to = next >= 0 ? A[next] : null, d = frame - from.frame;
        let xy = from.xy, pos = from.pos, angle = from.angle, col = from.color, tex = layer.textures[from.aniframe | 0];
        if (next !== start + 1 || to.frame !== from.frame) { if (to && lastSource <= from.frame) continue; }
        else {
            xy = from.xy.map((q, k) => q + to.xy[k] * d); pos = [pos[0] + to.pos[0] * d, pos[1] + to.pos[1] * d];
            angle += to.angle * d; col = col.map((c, k) => c + to.color[k] * d);
            const n = layer.textures.length, af = to.anitype === 1 ? from.aniframe + to.aniframe * d : to.anitype === 2 ? Math.min(from.aniframe + to.delay * d, n - 1)
                : to.anitype === 3 ? (from.aniframe + to.delay * d) % n : to.anitype === 4 ? (from.aniframe - to.delay * d) % n : 0;
            tex = layer.textures[Math.floor(af)] ?? layer.textures[0]; // a negative frame (anitype 4) has no texture: use the first
        }
        const r = fx.rects[tex], alpha = Math.min(1, col[3]); if (!r || alpha <= 0) continue;
        // Vertices 0..3 carry the atlas rect's top-left, top-right, bottom-left, bottom-right (UpdateMesh); STR pixels are 1/50 unit.
        const rad = -angle * Math.PI / 180, cs = Math.cos(rad), sn = Math.sin(rad), cx = x + (pos[0] - 320) / 50 * U, cy = y + (pos[1] - 320) / 50 * U;
        const pt = k => [cx + (xy[2 * k] * cs - xy[2 * k + 1] * sn) / 50 * U, cy - (xy[2 * k] * sn + xy[2 * k + 1] * cs) / 50 * U];
        const [p0, p1, p2] = [pt(0), pt(1), pt(2)], w = Math.max(1, Math.round(r[2])), h = Math.max(1, Math.round(r[3]));
        g.globalCompositeOperation = from.dstAlpha === 7 ? "lighter" : "source-over"; // SrcAlpha/DestAlpha: the client's additive (One, One, alpha-multiplied)
        g.globalAlpha = alpha;
        g.setTransform(base.multiply(new DOMMatrix([(p1[0] - p0[0]) / w, (p1[1] - p0[1]) / w, (p2[0] - p0[0]) / h, (p2[1] - p0[1]) / h, p0[0], p0[1]])));
        const [img, sx, sy] = tinted(fx, tex, r, w, h, col);
        g.drawImage(img, sx, sy, w, h, 0, 0, w, h);
    }
    g.setTransform(base); g.globalAlpha = 1; g.globalCompositeOperation = "source-over";
}

/** The atlas rect multiplied by the key's colour (the effect shader's _Color), cached per 1/32 step. */
function tinted(fx, tex, r, w, h, col) {
    const q = col.slice(0, 3).map(c => Math.round(Math.max(0, Math.min(1, c)) * 32));
    if (q.every(c => c === 32)) return [fx.img, Math.round(r[0]), Math.round(r[1])];
    const key = tex + "|" + q.join(",");
    let c = fx.tints.get(key);
    if (!c) {
        c = document.createElement("canvas"); c.width = w; c.height = h;
        const t = c.getContext("2d");
        t.drawImage(fx.img, Math.round(r[0]), Math.round(r[1]), w, h, 0, 0, w, h);
        t.globalCompositeOperation = "multiply"; t.fillStyle = "rgb(" + q.map(v => v * 255 / 32).join(",") + ")"; t.fillRect(0, 0, w, h);
        t.globalCompositeOperation = "destination-in"; t.drawImage(fx.img, Math.round(r[0]), Math.round(r[1]), w, h, 0, 0, w, h);
        fx.tints.set(key, c);
    }
    return [c, 0, 0];
}
